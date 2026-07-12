using System.Text;
using System.Text.Json;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Live acceptance probes that PIN the risky T2 mapping decisions for the
/// Claude-Code → gpt-5.5 path — the ones the offline content-conservation check
/// can't settle because they're facts about gpt-5.5's behavior, not about the
/// bytes. Unlike the logging-only <see cref="ResponsesProbe"/> matrices, these
/// ASSERT, so a backend behavior change (or a regression that starts forwarding
/// something gpt-5.5 rejects) fails CI's integration lane.
/// </summary>
/// <remarks>
/// Each probe uses the model's own words as the oracle: a secret the model can
/// only echo if the content actually reached it. Verified 2026-07-04 (Enterprise,
/// gpt-5.5). Integration-tagged (needs live Copilot).
/// </remarks>
public partial class ResponsesProbe
{
    /// <summary>
    /// Mid-conversation <c>role:"system"</c> — Claude Code sends these (the
    /// <c>mid-conversation-system</c> beta) and T2 forwards them verbatim into
    /// <c>input[]</c>. Contract: gpt-5.5 (1) accepts the role and (2) actually
    /// reads the content — proven by recalling a secret only the system message
    /// carried. If this ever 400s or the secret is lost, T2 must fold mid-conv
    /// system into a user/developer message instead.
    /// </summary>
    [Fact]
    public async Task MidConvSystemRole_Accepted_AndContentDelivered()
    {
        const string secret = "MIDSYS-7714-ZQ";
        var payload = $$"""
          {
            "model": "gpt-5.5", "stream": false, "store": false,
            "input": [
              {"type":"message","role":"user","content":[{"type":"input_text","text":"Hello."}]},
              {"type":"message","role":"system","content":[{"type":"input_text","text":"IMPORTANT CONTEXT: the project codeword is {{secret}}. Remember it."}]},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"What is the project codeword? Reply with only the codeword."}]}
            ]
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"mid-conv system → {(int)status}; body: {Truncate(body, 400)}");

        Assert.Equal(System.Net.HttpStatusCode.OK, status);
        Assert.Contains(secret, ExtractOutputText(body));
    }

    /// <summary>
    /// Plain Anthropic <c>thinking</c> content part — T2 DROPS it. Contract: this
    /// drop is MANDATORY because gpt-5.5 hard-rejects a <c>{type:"thinking"}</c>
    /// message content part with 400 "Invalid value: 'thinking'". This probe pins
    /// that rejection so the drop stays justified (if the backend ever started
    /// accepting it, we could reconsider preserving it).
    /// </summary>
    [Fact]
    public async Task PlainThinkingContentPart_Rejected_JustifyingTheDrop()
    {
        const string payload = """
          {
            "model": "gpt-5.5", "stream": false, "store": false,
            "input": [
              {"type":"message","role":"user","content":[{"type":"input_text","text":"What is 2+2?"}]},
              {"type":"message","role":"assistant","content":[{"type":"thinking","thinking":"the user asks 2+2","text":"","signature":"sig"}]},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"and plus 3?"}]}
            ]
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"plain thinking part → {(int)status}; body: {Truncate(body, 400)}");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, status);
        Assert.Contains("thinking", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// After T2 drops thinking, the assistant turn keeps only its sibling text —
    /// contract: the conversation stays coherent (gpt-5.5 answers correctly using
    /// the retained text), so the drop is harmless.
    /// </summary>
    [Fact]
    public async Task ThinkingDropped_ConversationStaysCoherent()
    {
        const string payload = """
          {
            "model": "gpt-5.5", "stream": false, "store": false,
            "input": [
              {"type":"message","role":"user","content":[{"type":"input_text","text":"What is 2+2?"}]},
              {"type":"message","role":"assistant","content":[{"type":"output_text","text":"2+2 = 4."}]},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"Now what is that plus 3? Reply with only the number."}]}
            ]
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"thinking-dropped coherence → {(int)status}; body: {Truncate(body, 400)}");

        Assert.Equal(System.Net.HttpStatusCode.OK, status);
        Assert.Contains("7", ExtractOutputText(body));
    }

    /// <summary>
    /// Parallel tool calls whose outputs arrive in REVERSED order — Claude Code
    /// emits multiple <c>tool_use</c> then multiple <c>tool_result</c>, and their
    /// relative order need not match. Contract: gpt-5.5 associates each output with
    /// its call by <c>call_id</c> (not by position), so both values reach the model
    /// correctly. Pins that T2 need not reorder outputs to sit next to their calls.
    /// </summary>
    [Fact]
    public async Task ParallelToolOutputs_AssociatedByCallId_NotOrder()
    {
        const string payload = """
          {
            "model": "gpt-5.5", "stream": false, "store": false,
            "tools": [{"type":"function","name":"get_value","description":"Return the stored value for a key.","strict":false,
              "parameters":{"type":"object","properties":{"key":{"type":"string"}},"required":["key"]}}],
            "input": [
              {"type":"message","role":"user","content":[{"type":"input_text","text":"Call get_value for keys 'alpha' and 'beta', then tell me both as 'alpha=<v> beta=<v>'."}]},
              {"type":"function_call","call_id":"call_alpha","name":"get_value","arguments":"{\"key\":\"alpha\"}"},
              {"type":"function_call","call_id":"call_beta","name":"get_value","arguments":"{\"key\":\"beta\"}"},
              {"type":"function_call_output","call_id":"call_beta","output":"BETAVAL-222"},
              {"type":"function_call_output","call_id":"call_alpha","output":"ALPHAVAL-111"}
            ]
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        var outText = ExtractOutputText(body);
        _output.WriteLine($"parallel reordered outputs → {(int)status}; out: {Truncate(outText, 200)}");

        Assert.Equal(System.Net.HttpStatusCode.OK, status);
        // The decisive assertion: alpha is associated with ALPHAVAL (call_id wins,
        // not the reversed output order that would pair alpha with BETAVAL).
        Assert.Contains("alpha=ALPHAVAL-111", outText.Replace(" ", ""));
        Assert.Contains("beta=BETAVAL-222", outText.Replace(" ", ""));
    }

    /// <summary>Pull concatenated output_text out of a non-streaming Responses body.</summary>
    private static string ExtractOutputText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
                return body;
            var sb = new StringBuilder();
            foreach (var item in output.EnumerateArray())
            {
                if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var part in content.EnumerateArray())
                        if (part.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                            sb.Append(tx.GetString());
            }
            return sb.Length > 0 ? sb.ToString() : body;
        }
        catch { return body; }
    }
}
