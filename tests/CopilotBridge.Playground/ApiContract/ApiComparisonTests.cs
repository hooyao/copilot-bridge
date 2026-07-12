using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>
/// 1:1 comparison of the Anthropic native API and Copilot's <c>/v1/messages</c>.
/// Sends the same request body to both, captures both responses, and reports
/// every structural difference (extra/missing fields, divergent values).
///
/// This is the canonical empirical source for "where do Copilot and Anthropic
/// differ on the wire" — feeds the research doc and informs bridge passthrough
/// vs translation decisions.
///
/// Anthropic API key comes from <c>tests/CopilotBridge.Playground/appsettings.local.json</c>
/// (gitignored). Tests skip cleanly when the key is absent or rate-limited (429).
///
/// Model mapping: sonnet-4.6 / haiku-4.5 / opus-4.7 exist on both sides with
/// the same canonical id (dot/dash format differs — Anthropic native uses
/// <c>claude-opus-4-7</c>, Copilot uses <c>claude-opus-4.7</c>). The
/// Copilot-only variant <c>claude-opus-4-7-1m-internal</c> maps to
/// <c>claude-opus-4-7</c> + <c>context-1m-2025-08-07</c> beta header on native.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class ApiComparisonTests
{
    private readonly ITestOutputHelper _output;

    public ApiComparisonTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Empirical observation from the haiku-4.5 native 1:1 comparison
    /// (2026-05-21). <b>Only `copilot_usage` is truly platform-invariant.</b>
    /// `stop_details`, `usage.inference_geo`, `usage.service_tier` vary by
    /// Copilot backend — see <see cref="CopilotShape_ObserveAcrossModels"/>
    /// for the reason: Copilot routes models through different upstream
    /// providers (msg_bdrk_* = AWS Bedrock, msg_vrtx_* = Google Vertex,
    /// msg_01* = direct Anthropic), each with slightly different response
    /// shaping. The Anthropic SDK ignores unknown fields and tolerates
    /// missing optional fields, so the bridge needs no transformation — but
    /// don't treat haiku-vs-native as a universal reverse-derivation key.
    /// </summary>
    private static readonly string[] CopilotInvariantTopLevelKeys = { "copilot_usage" };

    [Theory]
    // model_copilot, model_native, native_betas, label
    [InlineData("claude-sonnet-4.6", "claude-sonnet-4-6",    null,                    "sonnet-4.6 (both)")]
    [InlineData("claude-haiku-4.5",  "claude-haiku-4-5",     null,                    "haiku-4.5 (both)")]
    [InlineData("claude-opus-4.7",   "claude-opus-4-7",      null,                    "opus-4.7 (both)")]
    [InlineData("claude-opus-4.7-1m-internal", "claude-opus-4-7", "context-1m-2025-08-07", "opus-4.7 1m variant (Copilot) vs opus-4.7 + 1m beta (native)")]
    public async Task CompareMinimalPrompt_AcrossBackends(
        string modelCopilot,
        string modelNative,
        string? nativeBeta,
        string label)
    {
        const string prompt = "Reply with the single word: ok";

        var copilotBody = BuildBody(modelCopilot, prompt);
        var nativeBody = BuildBody(modelNative, prompt);

        // ── Copilot side ──
        using var copilotClient = new PlaygroundClient();
        var (copilotStatus, copilotResp) = await copilotClient.TryPostMessagesAsync(copilotBody);
        _output.WriteLine($"=== {label} ===");
        _output.WriteLine($"[Copilot] HTTP {(int)copilotStatus} model={modelCopilot}");

        // ── Anthropic native side ──
        string? skipReason = null;
        if (LocalConfig.AnthropicApiKey is null)
        {
            skipReason = "AnthropicApiKey missing in appsettings.local.json";
        }

        string? nativeResp = null;
        System.Net.HttpStatusCode nativeStatus = 0;
        if (skipReason is null)
        {
            using var nativeClient = new AnthropicNativeClient();
            var betas = nativeBeta is null ? null : new[] { nativeBeta };
            (nativeStatus, nativeResp) = await nativeClient.TryPostMessagesAsync(nativeBody, betas);
            _output.WriteLine($"[Native]  HTTP {(int)nativeStatus} model={modelNative}{(nativeBeta is null ? "" : $" beta={nativeBeta}")}");

            if ((int)nativeStatus == 429)
            {
                skipReason = $"Anthropic native returned 429 (rate-limited): {Truncate(nativeResp, 200)}";
            }
        }

        if (skipReason is not null)
        {
            _output.WriteLine($"[skip native side] {skipReason}");
            _output.WriteLine($"[Copilot response, first 800 chars]");
            _output.WriteLine(Truncate(copilotResp, 800));
            return;
        }

        Assert.InRange((int)copilotStatus, 200, 299);
        Assert.InRange((int)nativeStatus, 200, 299);

        var copilotJson = JsonNode.Parse(copilotResp)!.AsObject();
        var nativeJson = JsonNode.Parse(nativeResp!)!.AsObject();

        ReportDiff(copilotJson, nativeJson);
    }

    /// <summary>
    /// Observational probe of Copilot-side shape across priority Claude
    /// models. Asserts only the truly invariant invariants
    /// (<see cref="CopilotInvariantTopLevelKeys"/> + base Anthropic fields) —
    /// dumps everything else for human review. The ID prefix in each response
    /// (<c>msg_bdrk_</c>, <c>msg_vrtx_</c>, <c>msg_01</c>) reveals which
    /// upstream provider Copilot routed through, which is the actual driver
    /// of "does this model emit stop_details / inference_geo / service_tier".
    /// </summary>
    [Theory]
    [InlineData("claude-haiku-4.5")]
    [InlineData("claude-sonnet-4.6")]
    [InlineData("claude-opus-4.7")]
    [InlineData("claude-opus-4.7-1m-internal")]
    public async Task CopilotShape_ObserveAcrossModels(string model)
    {
        var body = BuildBody(model, "Reply with the single word: ok");
        using var client = new PlaygroundClient();
        var (status, raw) = await client.TryPostMessagesAsync(body);
        Assert.InRange((int)status, 200, 299);

        var resp = JsonNode.Parse(raw)!.AsObject();
        var topKeys = resp.Select(p => p.Key).OrderBy(s => s).ToList();
        var usageKeys = resp["usage"]?.AsObject().Select(p => p.Key).OrderBy(s => s).ToList() ?? new();
        var msgId = resp["id"]?.GetValue<string>() ?? "?";
        var provider = msgId.StartsWith("msg_bdrk_") ? "Bedrock"
            : msgId.StartsWith("msg_vrtx_") ? "Vertex"
            : msgId.StartsWith("msg_01") ? "AnthropicDirect"
            : "Unknown";

        _output.WriteLine($"[Copilot] {model}");
        _output.WriteLine($"  id          : {msgId}  (provider={provider})");
        _output.WriteLine($"  top-level   : {string.Join(", ", topKeys)}");
        _output.WriteLine($"  usage keys  : {string.Join(", ", usageKeys)}");
        _output.WriteLine($"  has stop_details          : {topKeys.Contains("stop_details")}");
        _output.WriteLine($"  has usage.inference_geo   : {usageKeys.Contains("inference_geo")}");
        _output.WriteLine($"  has usage.service_tier    : {usageKeys.Contains("service_tier")}");

        // Truly invariant: copilot_usage extension and the base Anthropic fields.
        foreach (var k in CopilotInvariantTopLevelKeys)
            Assert.True(topKeys.Contains(k), $"{model}: expected '{k}'.");
        foreach (var k in new[] { "content", "id", "model", "role", "stop_reason", "type", "usage" })
            Assert.True(topKeys.Contains(k), $"{model}: missing core Anthropic field '{k}'.");
        foreach (var k in new[] { "input_tokens", "output_tokens" })
            Assert.True(usageKeys.Contains(k), $"{model}: missing 'usage.{k}'.");
    }

    private static string BuildBody(string model, string prompt) =>
        new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 16,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = prompt },
            },
        }.ToJsonString();

    private void ReportDiff(JsonObject copilot, JsonObject native)
    {
        _output.WriteLine("");
        _output.WriteLine("--- top-level keys ---");
        var copilotKeys = copilot.Select(p => p.Key).ToHashSet();
        var nativeKeys = native.Select(p => p.Key).ToHashSet();
        var copilotOnly = copilotKeys.Except(nativeKeys).OrderBy(s => s).ToList();
        var nativeOnly = nativeKeys.Except(copilotKeys).OrderBy(s => s).ToList();
        var both = copilotKeys.Intersect(nativeKeys).OrderBy(s => s).ToList();
        _output.WriteLine($"copilot-only: {string.Join(", ", copilotOnly)}");
        _output.WriteLine($"native-only : {string.Join(", ", nativeOnly)}");
        _output.WriteLine($"both        : {string.Join(", ", both)}");

        _output.WriteLine("");
        _output.WriteLine("--- usage diff ---");
        DiffNode("usage", copilot["usage"], native["usage"]);

        _output.WriteLine("");
        _output.WriteLine("--- stop_reason / content shapes ---");
        _output.WriteLine($"[Copilot] stop_reason={copilot["stop_reason"]} content_types=[{ContentTypes(copilot)}]");
        _output.WriteLine($"[Native]  stop_reason={native["stop_reason"]} content_types=[{ContentTypes(native)}]");

        _output.WriteLine("");
        _output.WriteLine("--- raw copilot (first 1200 chars) ---");
        _output.WriteLine(Truncate(copilot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), 1200));
        _output.WriteLine("");
        _output.WriteLine("--- raw native (first 1200 chars) ---");
        _output.WriteLine(Truncate(native.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), 1200));
    }

    private void DiffNode(string path, JsonNode? a, JsonNode? b)
    {
        if (a is JsonObject ao && b is JsonObject bo)
        {
            var ak = ao.Select(p => p.Key).ToHashSet();
            var bk = bo.Select(p => p.Key).ToHashSet();
            foreach (var k in ak.Except(bk).OrderBy(s => s))
                _output.WriteLine($"  {path}.{k}: copilot-only ({ao[k]?.ToJsonString()})");
            foreach (var k in bk.Except(ak).OrderBy(s => s))
                _output.WriteLine($"  {path}.{k}: native-only ({bo[k]?.ToJsonString()})");
            foreach (var k in ak.Intersect(bk).OrderBy(s => s))
                DiffNode(path + "." + k, ao[k], bo[k]);
            return;
        }
        var as_ = a?.ToJsonString();
        var bs = b?.ToJsonString();
        if (as_ != bs)
        {
            _output.WriteLine($"  {path}: copilot={as_} | native={bs}");
        }
    }

    private static string ContentTypes(JsonObject body)
    {
        if (body["content"] is not JsonArray arr) return "";
        return string.Join(",", arr.OfType<JsonObject>().Select(b => b["type"]?.GetValue<string>() ?? "?"));
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"...[+{s.Length - max} chars]";
}
