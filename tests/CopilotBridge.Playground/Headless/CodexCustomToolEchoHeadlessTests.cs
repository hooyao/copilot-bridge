using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// End-to-end regression for the REQUEST-side custom-tool argument bug: a Codex
/// follow-up turn that echoes a prior <c>exec</c> (custom grammar) call back as a
/// <c>function_call</c> whose <c>arguments</c> is RAW JavaScript (not JSON). The
/// bridge used to 400 this at T1 (`ExpectedStartOfValueNotFound`) — the real
/// gpt-5.6 exec loop died on its SECOND turn. Drives the exact shape through the
/// real in-process bridge (`POST /codex/responses`) to Copilot and asserts it now
/// completes (no 400, upstream 200), and that the audit shows the raw-JS arguments
/// reached Copilot verbatim.
/// </summary>
/// <remarks>
/// This is the multi-turn wire shape a unit test can't fully stand in for — it
/// exercises T1 (accept raw args) → routing → T2 (re-emit verbatim) → real Copilot
/// acceptance in one shot. Tagged Integration (live Copilot); ephemeral port.
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class CodexCustomToolEchoHeadlessTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public CodexCustomToolEchoHeadlessTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    [Fact]
    public async Task ExecCallEchoedWithRawJsArguments_NoLonger400s()
    {
        const string execJs =
            "const r = await tools.shell_command({\n  command: \"echo hi\",\n  workdir: \"Q:\\\\proj\"\n});\ntext(r);";
        var template = """
          {
            "model":"gpt-5.6-sol",
            "instructions":"You have an exec tool that runs JavaScript.",
            "input":[
              {"type":"message","role":"user","content":[{"type":"input_text","text":"run echo hi via exec"}]},
              {"type":"function_call","name":"exec","call_id":"call_echo_1","arguments":__ARGS__},
              {"type":"function_call_output","call_id":"call_echo_1","output":"hi"},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"good — now reply with exactly: done"}]}
            ],
            "stream":true,"store":false,
            "tools":[{"type":"custom","name":"exec","description":"Run JS.","format":{"type":"grammar","syntax":"lark","definition":"start: /[\\s\\S]+/"}}]
          }
          """;
        var payload = template.Replace("__ARGS__", Json(execJs));

        var reader = new BridgeLogReader(_bridge.LogDirectory);

        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/codex/responses");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);

        _output.WriteLine($"/codex/responses → {(int)resp.StatusCode} {resp.StatusCode}");
        _output.WriteLine($"  first 300: {(body.Length <= 300 ? body : body[..300])}");

        // The old failure signature must be gone.
        Assert.DoesNotContain("ExpectedStartOfValueNotFound", body, StringComparison.Ordinal);
        Assert.DoesNotContain("malformed JSON arguments", body, StringComparison.Ordinal);
        // The turn completes: 200 + a terminal Responses event.
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("response.completed", body, StringComparison.Ordinal);

        // The audit must show the raw-JS arguments reached Copilot verbatim (not "{}"
        // and not a double-encoded string). Read the upstream /responses body.
        var entry = reader.ReadNew()
            .FirstOrDefault(e => e.InboundPath.EndsWith("/responses", StringComparison.Ordinal));
        Assert.NotNull(entry);
        var upstream = entry!.UpstreamBody?.ToJsonString() ?? "";
        // The distinctive exec token must be present as the function_call arguments.
        Assert.Contains("tools.shell_command", upstream, StringComparison.Ordinal);
        _output.WriteLine("[audit] upstream carried the raw exec arguments verbatim.");
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);
}
