using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// End-to-end regression for the gpt-5.6 <b>agent_message</b> (multi-agent) 400: the
/// production failure <c>Polymorphism_UnrecognizedTypeDiscriminator, agent_message
/// Path: $.input[17]</c> — the bridge's closed input[] whitelist rejected the
/// inter-agent message type BEFORE T1 even ran. Replays the REAL captured inbound body
/// (a genuine multi-agent turn carrying an agent_message with an encrypted_content
/// blob) through the fixed in-process bridge (<c>POST /codex/responses</c>) to Copilot
/// and asserts: no Polymorphism 400, upstream accepts it, and the forwarded upstream
/// request still carries the agent_message with its encrypted_content byte-intact.
/// </summary>
/// <remarks>
/// Fixture: <c>tmp-namespace-repro/inbound-0014-agent-message.json</c> (de-identified
/// real bytes; NOT checked in — the test asserts it exists). Tagged Integration
/// (live Copilot); ephemeral port.
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class CodexAgentMessageHeadlessTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    private static readonly string ReproDir =
        Path.Combine("Q:\\", "MyProjects", "cc-copilot-bridge", "tmp-namespace-repro");

    public CodexAgentMessageHeadlessTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    [Fact]
    public async Task AgentMessageInbound_ThroughBridge_NoPolymorphism400_EncryptedContentIntact()
    {
        var path = Path.Combine(ReproDir, "inbound-0014-agent-message.json");
        Assert.True(File.Exists(path), $"replay fixture absent: {path}");
        var payload = await File.ReadAllTextAsync(path);

        // Pull the original agent_message's encrypted_content blob out of the fixture so
        // we can assert it survived byte-identically on the upstream request.
        var originalBlob = ExtractAgentMessageEncryptedBlob(payload);
        Assert.False(string.IsNullOrEmpty(originalBlob), "fixture carried no agent_message encrypted_content");

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

        // The production failure signature must be gone — the bridge accepted the
        // agent_message instead of 400'ing at deserialization.
        Assert.DoesNotContain("Polymorphism_UnrecognizedTypeDiscriminator", body, StringComparison.Ordinal);
        Assert.DoesNotContain("agent_message Path", body, StringComparison.Ordinal);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("response.completed", body, StringComparison.Ordinal);

        // The audit must show the bridge FORWARDED the agent_message to Copilot with its
        // encrypted_content byte-intact (the whole point of the byte-faithful passthrough).
        var entry = await PollForUpstreamEntryAsync(reader, TimeSpan.FromSeconds(15));
        Assert.NotNull(entry);
        var forwardedBlob = ExtractAgentMessageEncryptedBlob(
            (entry!.UpstreamBody as JsonObject)?.ToJsonString() ?? "");
        Assert.Equal(originalBlob, forwardedBlob);
        _output.WriteLine("[audit] upstream agent_message encrypted_content == original (byte-intact).");
    }

    /// <summary>Pull the first agent_message's encrypted_content string out of a body JSON.</summary>
    private static string ExtractAgentMessageEncryptedBlob(string bodyJson)
    {
        if (string.IsNullOrEmpty(bodyJson)) return "";
        var input = JsonNode.Parse(bodyJson)?["input"] as JsonArray;
        if (input is null) return "";
        foreach (var n in input)
        {
            if (n is not JsonObject o || o["type"]?.GetValue<string>() != "agent_message") continue;
            if (o["content"] is not JsonArray content) continue;
            foreach (var part in content)
                if (part is JsonObject p && p["type"]?.GetValue<string>() == "encrypted_content")
                    return p["encrypted_content"]?.GetValue<string>() ?? "";
        }
        return "";
    }

    private static async Task<BridgeLogEntry?> PollForUpstreamEntryAsync(BridgeLogReader reader, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var entry = reader.ReadNew()
                .FirstOrDefault(e => e.InboundPath.EndsWith("/responses", StringComparison.Ordinal)
                                     && e.UpstreamBody is not null);
            if (entry is not null || DateTime.UtcNow >= deadline)
                return entry;
            await Task.Delay(250);
        }
    }
}
