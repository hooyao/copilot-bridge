using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotBridge.Cli.Models.Anthropic.Common;

namespace CopilotBridge.Cli.Models;

/// <summary>
/// Mutable accumulator for token counts a request produces. The endpoint
/// initialises one per request and feeds it via <see cref="UsageProbe"/>
/// during response handling, then snapshots it into the
/// <see cref="Endpoints.ClaudeCode.RequestSummary"/> emitted in the
/// <c>finally</c> block.
/// </summary>
internal sealed class UsageSnapshot
{
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CacheReadInputTokens { get; set; }
    public int? CacheCreationInputTokens { get; set; }

    public bool HasAny =>
        InputTokens is not null
        || OutputTokens is not null
        || CacheReadInputTokens is not null
        || CacheCreationInputTokens is not null;

    /// <summary>Compact one-line rendering for the INFO summary log.</summary>
    public string Display => HasAny
        ? $"{{in:{InputTokens?.ToString() ?? "?"} out:{OutputTokens?.ToString() ?? "?"} cache_read:{CacheReadInputTokens?.ToString() ?? "0"} cache_creation:{CacheCreationInputTokens?.ToString() ?? "0"}}}"
        : "(none)";
}

/// <summary>
/// Pure parsers that pull <see cref="Usage"/> out of the three response shapes
/// the bridge sees:
/// <list type="bullet">
///   <item>Non-streaming Anthropic body (<see cref="TryReadBuffered"/>).</item>
///   <item>Streaming SSE events (<see cref="TryUpdateFromStreamEvent"/>),
///         which Anthropic emits cumulatively on <c>message_start</c> and
///         <c>message_delta</c> (so the latest value overwrites the snapshot,
///         not adds).</item>
///   <item><c>count_tokens</c> response body (<see cref="TryReadCountTokens"/>),
///         which is just <c>{"input_tokens":N}</c>.</item>
/// </list>
/// All parsers swallow JSON parse failures — the audit pipeline already
/// captures bad bodies; missing usage just leaves the snapshot empty.
/// </summary>
internal static class UsageProbe
{
    public static void TryReadBuffered(ReadOnlySpan<byte> body, UsageSnapshot snapshot)
    {
        if (body.IsEmpty) return;
        try
        {
            var envelope = JsonSerializer.Deserialize(body, JsonContext.Default.UsageProbeEnvelope);
            if (envelope?.Usage is { } usage)
            {
                snapshot.InputTokens = usage.InputTokens;
                snapshot.OutputTokens = usage.OutputTokens;
                snapshot.CacheReadInputTokens = usage.CacheReadInputTokens;
                snapshot.CacheCreationInputTokens = usage.CacheCreationInputTokens;
            }
        }
        catch (JsonException) { /* malformed body — already audited */ }
    }

    public static void TryReadCountTokens(ReadOnlySpan<byte> body, UsageSnapshot snapshot)
    {
        if (body.IsEmpty) return;
        try
        {
            var resp = JsonSerializer.Deserialize(body, JsonContext.Default.CountTokensResponse);
            if (resp is not null)
            {
                snapshot.InputTokens = resp.InputTokens;
            }
        }
        catch (JsonException) { /* malformed body — already audited */ }
    }

    /// <summary>
    /// Update <paramref name="snapshot"/> from one SSE event. Anthropic emits
    /// the full message envelope on <c>message_start</c> (initial counters)
    /// and the cumulative-to-date counters on <c>message_delta</c> — both
    /// overwrite rather than add (per
    /// <c>Models/Anthropic/Common/Usage.cs:30-40</c> comment).
    /// </summary>
    public static void TryUpdateFromStreamEvent(string? eventType, string data, UsageSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(eventType) || string.IsNullOrEmpty(data)) return;
        try
        {
            if (eventType.Equals("message_start", StringComparison.Ordinal))
            {
                var env = JsonSerializer.Deserialize(data, JsonContext.Default.MessageStartUsageEnvelope);
                if (env?.Message?.Usage is { } u)
                {
                    snapshot.InputTokens = u.InputTokens;
                    snapshot.OutputTokens = u.OutputTokens;
                    snapshot.CacheReadInputTokens = u.CacheReadInputTokens;
                    snapshot.CacheCreationInputTokens = u.CacheCreationInputTokens;
                }
            }
            else if (eventType.Equals("message_delta", StringComparison.Ordinal))
            {
                var env = JsonSerializer.Deserialize(data, JsonContext.Default.MessageDeltaUsageEnvelope);
                if (env?.Usage is { } u)
                {
                    if (u.InputTokens is not null) snapshot.InputTokens = u.InputTokens;
                    snapshot.OutputTokens = u.OutputTokens;
                    if (u.CacheReadInputTokens is not null) snapshot.CacheReadInputTokens = u.CacheReadInputTokens;
                    if (u.CacheCreationInputTokens is not null) snapshot.CacheCreationInputTokens = u.CacheCreationInputTokens;
                }
            }
        }
        catch (JsonException) { /* event payload not parseable — skip */ }
    }
}

// --- Minimal POCO envelopes for source-generated JSON ------------------------
// Kept here (not under Models/Anthropic/...) because they only exist for the
// usage probe — they intentionally skip every other field of the upstream
// payload, so they're not part of the real wire shape.

/// <summary>Envelope for the non-streaming Anthropic message body: only
/// <c>usage</c> is read; every other field is ignored.</summary>
internal sealed record UsageProbeEnvelope
{
    public Usage? Usage { get; init; }
}

/// <summary>Envelope for <c>count_tokens</c> responses (<c>{"input_tokens":N}</c>).</summary>
internal sealed record CountTokensResponse
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }
}

/// <summary>Envelope for the <c>message_start</c> SSE event:
/// <c>{"type":"message_start","message":{"usage":{...}}}</c>.</summary>
internal sealed record MessageStartUsageEnvelope
{
    public MessageStartMessage? Message { get; init; }
}

/// <summary>Inner <c>message</c> object of a <c>message_start</c> envelope.</summary>
internal sealed record MessageStartMessage
{
    public Usage? Usage { get; init; }
}

/// <summary>Envelope for the <c>message_delta</c> SSE event:
/// <c>{"type":"message_delta","usage":{...}}</c>.</summary>
internal sealed record MessageDeltaUsageEnvelope
{
    public MessageDeltaUsage? Usage { get; init; }
}
