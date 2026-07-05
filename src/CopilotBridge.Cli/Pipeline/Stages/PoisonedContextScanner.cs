using System;
using System.Collections.Generic;
using System.Text.Json;
using CopilotBridge.Cli.Models.Anthropic.Request;

namespace CopilotBridge.Cli.Pipeline.Stages;

/// <summary>
/// Result of a poisoned-context scan: how badly one tool is stuck in a
/// repeated-failure loop this request, and the total failure debris count.
/// </summary>
/// <param name="WorstToolFailures">The highest number of failed <c>tool_result</c>s
/// attributable to a single tool name in this request — the structural "same tool
/// keeps failing and being replayed" signal the stage thresholds on.</param>
/// <param name="WorstTool">The tool name responsible for <paramref name="WorstToolFailures"/>
/// (null when there are no failures), so the warning can name it.</param>
/// <param name="TotalFailures">Total failed <c>tool_result</c>s across all tools —
/// the raw failure-debris count recorded for telemetry.</param>
internal readonly record struct PoisonScanResult(int WorstToolFailures, string? WorstTool, int TotalFailures)
{
    public static readonly PoisonScanResult None = new(0, null, 0);
}

/// <summary>
/// Detects a <b>poisoned transcript</b>: earlier <b>failed</b> tool / sub-agent
/// calls whose error the client (Claude Code) keeps in the session and replays
/// every turn. In the gpt-5.5 runaway (<c>docs/gpt55-runaway-diagnosis.md</c>) a
/// single tool — the <c>Agent</c> sub-agent launcher — failed 50 times in one
/// request (Copilot rejected the sub-agent's model with a 400), and that debris
/// derailed the weak backend model.
/// </summary>
/// <remarks>
/// <para>
/// The signal is <b>structural, not lexical</b>: <em>one tool name accumulating many
/// failed <c>tool_result</c>s in a single request</em> — the fingerprint of a client
/// retrying the same failing call over and over. This is deliberately NOT a match on
/// a specific error phrase: the wording varies with the underlying cause (a
/// region-restricted 400, a quota 429, a timeout, "model is not supported", or
/// whatever a future Copilot returns), and chasing phrases is a losing arms race.
/// Aggregating failures by originating tool catches the replay loop regardless of the
/// error text.
/// </para>
/// <para>
/// A <c>tool_result</c> counts as a failure when <see cref="ToolResultBlockParam.IsError"/>
/// is true OR its content <b>begins</b> (anchored, ignoring leading whitespace) with
/// an API-failure marker (<c>API Error:</c> / <c>Error:</c>). The is_error flag alone
/// is unreliable — in the runaway only 2 of 137 blocks set it, while all 50 poison
/// blocks began with <c>API Error:</c>, which is the fixed wrapper Claude Code puts on
/// any propagated backend failure. Anchoring at the content start avoids miscounting a
/// legitimate result that merely <em>mentions</em> an error mid-text (e.g. a web-search
/// hit about "overloaded_error", seen 3× in the same request).
/// </para>
/// <para>
/// DETECT ONLY — never mutate the transcript. Dropping a <c>tool_result</c> without
/// its paired <c>tool_use</c> breaks protocol and 400s upstream, and the bridge cannot
/// un-poison the client's history anyway; only the user compacting the session can. So
/// <see cref="PoisonedContextScanStage"/> records the counts and, when a tool crosses
/// the threshold, logs a "compact" WARNING. Runs over the already-deserialized IR body
/// (no re-parse).
/// </para>
/// </remarks>
internal static class PoisonedContextScanner
{
    // Sentinel tool name for a failed tool_result whose tool_use we can't resolve
    // (defensive — should not happen for a well-formed transcript).
    private const string UnknownTool = "<unknown>";

    /// <summary>
    /// Scan the request body: pair failed <c>tool_result</c>s back to the tool that
    /// produced them and return the worst single-tool failure count (the replay-loop
    /// signal) plus the total failure count.
    /// </summary>
    public static PoisonScanResult Scan(MessagesRequest body)
    {
        var messages = body.Messages;
        if (messages is null || messages.Count == 0) return PoisonScanResult.None;

        // Pass 1: map every tool_use id → its tool name, so a failed tool_result can
        // be attributed to the tool that issued it.
        Dictionary<string, string>? nameById = null;
        foreach (var msg in messages)
        {
            foreach (var block in msg.Content)
            {
                if (block is ToolUseBlockParam use)
                {
                    (nameById ??= new(StringComparer.Ordinal))[use.Id] = use.Name;
                }
            }
        }

        // Pass 2: tally FAILED tool_results per originating tool name.
        Dictionary<string, int>? failuresByTool = null;
        var total = 0;
        foreach (var msg in messages)
        {
            foreach (var block in msg.Content)
            {
                if (block is ToolResultBlockParam tr && IsFailure(tr))
                {
                    var tool = nameById is not null && nameById.TryGetValue(tr.ToolUseId, out var n)
                        ? n
                        : UnknownTool;
                    failuresByTool ??= new(StringComparer.Ordinal);
                    failuresByTool.TryGetValue(tool, out var c);
                    failuresByTool[tool] = c + 1;
                    total++;
                }
            }
        }

        if (failuresByTool is null) return PoisonScanResult.None;

        var worst = 0;
        string? worstTool = null;
        foreach (var (tool, c) in failuresByTool)
        {
            if (c > worst)
            {
                worst = c;
                worstTool = tool;
            }
        }
        return new PoisonScanResult(worst, worstTool, total);
    }

    /// <summary>
    /// A <c>tool_result</c> is a failure when it is flagged <c>is_error</c> OR its
    /// content begins with an API-failure marker. Both are checked because the flag is
    /// unreliable in practice (rarely set) and the anchored prefix is what actually
    /// distinguishes a propagated backend failure from ordinary tool output.
    /// </summary>
    private static bool IsFailure(ToolResultBlockParam tr)
    {
        if (tr.IsError == true) return true;
        if (tr.Content is { } content && LeadingText(content) is { } text)
        {
            var t = text.AsSpan().TrimStart();
            return t.StartsWith("API Error:".AsSpan(), StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("Error:".AsSpan(), StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// The leading text of a tool_result's content: the string itself for the string
    /// form, or the first text part for the array-of-blocks form. Read opaquely off the
    /// <see cref="JsonElement"/> so both content shapes work; returns null when there
    /// is no textual content to anchor on.
    /// </summary>
    private static string? LeadingText(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString();

            case JsonValueKind.Array:
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("text", out var t)
                        && t.ValueKind == JsonValueKind.String)
                    {
                        return t.GetString();
                    }
                }
                return null;

            default:
                return null;
        }
    }
}
