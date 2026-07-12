using System.Runtime.Versioning;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Live probe for the gpt-5.6 <b>custom-tool streaming contract</b> — the response
/// side of the exec/grammar tool loop. Grounds the T3 fix for the custom-tool
/// argument-loss bug: Copilot streams a <c>custom</c> tool's input via
/// <c>response.custom_tool_call_input.delta</c>/<c>.done</c> events (NOT
/// <c>response.function_call_arguments.*</c>), and the bridge's T3
/// (<c>ResponsesToAnthropicStream</c>) only handled the latter — so a custom
/// tool's arguments were dropped, reaching Codex as <c>arguments:""</c> →
/// <c>aborted</c>. This probe confirms the exact event names + field shapes live
/// before the fix edits them.
/// </summary>
/// <remarks>
/// Run:
/// <code>dotnet test tests/CopilotBridge.Playground --filter "FullyQualifiedName~CustomToolStreaming" --logger "console;verbosity=detailed"</code>
/// Read the printed event lines: we expect an <c>output_item.added</c> with
/// <c>item.type=custom_tool_call</c>, then <c>custom_tool_call_input.delta</c>
/// (field <c>delta</c>) fragments, then <c>custom_tool_call_input.done</c> (field
/// <c>input</c>, NOT <c>arguments</c>), then <c>output_item.done</c>. Probe only
/// logs. Integration-tagged (live Copilot).
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class CustomToolStreamingProbe
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public CustomToolStreamingProbe(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task CustomToolStreaming_Gpt56Sol_EmitsCustomToolCallInputEvents()
    {
        // A custom (grammar) tool shaped like Codex's `exec`, plus a prompt that
        // forces a call — so Copilot must stream the tool's input back.
        const string payload = """
          {
            "model": "gpt-5.6-sol",
            "instructions": "You have an `exec` tool that runs JavaScript. When asked to list files, you MUST call exec with a short JS snippet. Do not answer in prose.",
            "input": [{"type":"message","role":"user","content":[{"type":"input_text","text":"Use the exec tool to print the current directory listing. Call the tool now."}]}],
            "stream": true,
            "store": false,
            "tool_choice": "required",
            "tools": [
              {"type":"custom","name":"exec","description":"Run JavaScript to orchestrate tool calls.",
               "format":{"type":"grammar","syntax":"lark","definition":"start: /[\\s\\S]+/"}}
            ]
          }
          """;

        using var client = new PlaygroundClient();
        var (status, raw) = await client.TryPostResponsesRawStreamAsync(payload);
        _output.WriteLine($"[gpt-5.6-sol] custom-tool stream → {(int)status} {status}");

        var eventLines = raw.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("event:", StringComparison.Ordinal))
            .ToList();
        _output.WriteLine($"  {eventLines.Count} event lines. Distinct types:");
        foreach (var g in eventLines.GroupBy(l => l).OrderByDescending(g => g.Count()))
            _output.WriteLine($"    {g.Count(),4}x {g.Key}");

        // Signal presence of the custom-tool events the fix depends on.
        _output.WriteLine($"  has custom_tool_call_input.delta: {raw.Contains("custom_tool_call_input.delta", StringComparison.Ordinal)}");
        _output.WriteLine($"  has custom_tool_call_input.done:  {raw.Contains("custom_tool_call_input.done", StringComparison.Ordinal)}");
        _output.WriteLine($"  has function_call_arguments:       {raw.Contains("function_call_arguments", StringComparison.Ordinal)}");
        _output.WriteLine($"  item.type custom_tool_call present: {raw.Contains("\"custom_tool_call\"", StringComparison.Ordinal)}");

        // Print the first .done payload so we can read the field name (input vs arguments).
        // The type string appears first in the `event:` line; the payload is the NEXT
        // `data:` line AFTER it — search forward, not backward (a backward search from
        // the event line lands on the PRECEDING delta's data line).
        var doneIdx = raw.IndexOf("custom_tool_call_input.done", StringComparison.Ordinal);
        if (doneIdx >= 0)
        {
            var lineStart = raw.IndexOf("data:", doneIdx, StringComparison.Ordinal);
            if (lineStart >= 0)
            {
                var nl = raw.IndexOf('\n', lineStart);
                var lineEnd = nl >= 0 ? nl : raw.Length;
                // Trim FIRST, then bound against the trimmed string's own length —
                // Trim() drops the SSE line's trailing \r (and any padding), so the
                // pre-trim segment length can exceed the trimmed length and index out
                // of range for a short .done payload.
                var doneLine = raw[lineStart..lineEnd].Trim();
                _output.WriteLine($"  .done payload: {doneLine[..Math.Min(500, doneLine.Length)]}");
            }
        }
    }
}
