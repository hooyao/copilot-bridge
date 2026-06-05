using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>
/// Investigates the origin of the "Invalid tool parameters" corruption seen in
/// production (trace <c>20260605-145016-0079</c>): an <c>AskUserQuestion</c>
/// tool call arrived at Claude Code with the required <c>question</c> field of
/// every question object missing, so Claude Code's schema validation rejected
/// it.
///
/// The bridge's streaming path is byte-faithful by design:
///   upstream bytes → SseParser.Create(stream) → SseItem&lt;string&gt;
///   → WriteSseEventAsync (re-serialize as data: lines) → downstream bytes
///
/// Two pieces of evidence localize the fault to <b>upstream</b>, not the bridge:
/// <list type="number">
///   <item><c>SseRoundTripTests</c> (unit) proves the bridge's parse +
///         re-serialize round trip preserves tool-call <c>partial_json</c>
///         fragments byte-for-byte, including the <c>question</c> field, across
///         CRLF endings and empty fragments.</item>
///   <item>This test reads Copilot's response as RAW bytes (no SseParser) and
///         measures how often the upstream stream itself contains the
///         <c>question</c> field. A &lt;100% rate is upstream non-determinism:
///         opus-4.8 occasionally omits a required field from its own tool-call
///         output, and the bridge faithfully relays the broken result.</item>
/// </list>
///
/// The production audit across 6 bridge-produced AskUserQuestion calls found
/// the field present in 4, partially missing in 1, and fully missing in 1 — a
/// distribution incompatible with a deterministic bridge bug. This probe lets
/// that rate be re-measured on demand. It does not assert (the point is to
/// OBSERVE the upstream rate); it only fails on transport errors.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class InvalidToolParamsReplayTests
{
    private readonly ITestOutputHelper _output;
    public InvalidToolParamsReplayTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Force opus-4.8 to emit an <c>AskUserQuestion</c> tool call (via
    /// <c>tool_choice</c>) several times, reading each response as RAW bytes
    /// (bypassing SseParser), and report how often the upstream stream actually
    /// contains the required <c>question</c> field.
    /// </summary>
    [Fact]
    public async Task ForceAskUserQuestion_RawStream_MeasureQuestionFieldRate()
    {
        // Minimal AskUserQuestion tool schema (same shape as the real one:
        // questions[] each with question/header/options/multiSelect required).
        const string body = """
          {
            "model": "claude-opus-4.8",
            "max_tokens": 4096,
            "stream": true,
            "tool_choice": {"type":"tool","name":"AskUserQuestion"},
            "tools": [{
              "name": "AskUserQuestion",
              "description": "Ask the user one or more multiple-choice questions.",
              "input_schema": {
                "type":"object",
                "properties": {
                  "questions": {
                    "type":"array",
                    "items": {
                      "type":"object",
                      "properties": {
                        "question": {"type":"string"},
                        "header": {"type":"string"},
                        "multiSelect": {"type":"boolean"},
                        "options": {"type":"array","items":{"type":"object","properties":{"label":{"type":"string"},"description":{"type":"string"}}}}
                      },
                      "required": ["question","header","options","multiSelect"]
                    }
                  }
                },
                "required": ["questions"]
              }
            }],
            "messages": [{"role":"user","content":"I'm choosing a database and a deploy target for a new web app. Ask me two questions to narrow it down, each with 3 options."}]
          }
          """;

        const int runs = 8;
        var present = 0;
        var ran = 0;
        for (var i = 1; i <= runs; i++)
        {
            try
            {
                var (questionPresent, deltaCount, joinedLen, sample) = await ReplayOnceRaw(body);
                ran++;
                if (questionPresent) present++;
                _output.WriteLine(
                    $"[run {i}] deltas={deltaCount} joined_len={joinedLen} question_present={questionPresent}");
                if (!questionPresent)
                {
                    _output.WriteLine($"  CORRUPT (upstream omitted 'question'): {Truncate(sample, 300)}");
                }
            }
            catch (HttpRequestException ex)
            {
                _output.WriteLine($"[run {i}] upstream error: {ex.Message}");
            }
        }

        _output.WriteLine(
            $"SUMMARY: opus-4.8 raw stream contained 'question' field in {present}/{ran} runs. "
            + (ran > 0 && present < ran
                ? "UPSTREAM intermittently omits the field — reproduces the Invalid-tool-parameters corruption at the source (the bridge relays it faithfully)."
                : "Field present in every run this batch (the omission is low-frequency; the production rate was ~1-2 in 6)."));
    }

    /// <summary>
    /// POST the body to Copilot and read the SSE response with a raw
    /// StreamReader (no SseParser). Returns whether the literal token
    /// <c>"question"</c> appears in the reassembled tool input.
    /// </summary>
    private static async Task<(bool QuestionPresent, int DeltaCount, int JoinedLen, string Sample)>
        ReplayOnceRaw(string bodyJson)
    {
        using var client = new PlaygroundClient();
        var raw = await client.PostMessagesRawStreamAsync(bodyJson);

        var joined = new StringBuilder();
        var deltaCount = 0;
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = trimmed.Length > 5 ? trimmed[5..].TrimStart() : "";
            if (payload.Length == 0 || payload[0] != '{') continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(payload); }
            catch { continue; }
            using (doc)
            {
                if (doc.RootElement.TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("type", out var dt)
                    && dt.GetString() == "input_json_delta"
                    && delta.TryGetProperty("partial_json", out var pj))
                {
                    joined.Append(pj.GetString());
                    deltaCount++;
                }
            }
        }

        var s = joined.ToString();
        var present = s.Contains("\"question\"", StringComparison.Ordinal);
        return (present, deltaCount, s.Length, s);
    }

    private static string Truncate(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
