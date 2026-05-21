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
/// Model mapping: bridge-side variants like <c>claude-opus-4-7-1m-internal</c>
/// have no native equivalent; we use <c>claude-opus-4-7</c> with the
/// <c>context-1m-2025-08-07</c> beta header on the native side and label the
/// case accordingly.
/// </summary>
[SupportedOSPlatform("windows")]
public class ApiComparisonTests
{
    private readonly ITestOutputHelper _output;

    public ApiComparisonTests(ITestOutputHelper output) => _output = output;

    [Theory]
    // model_copilot, model_native, native_betas, label
    [InlineData("claude-sonnet-4.6", "claude-sonnet-4-5",    null,                    "sonnet-4.6 (Copilot) vs sonnet-4.5 (native — closest available)")]
    [InlineData("claude-haiku-4.5",  "claude-haiku-4-5",     null,                    "haiku-4.5 (both)")]
    [InlineData("claude-opus-4.7",   "claude-opus-4-1",      null,                    "opus-4.7 (Copilot) vs opus-4.1 (native — closest)")]
    [InlineData("claude-opus-4.7-1m-internal", "claude-opus-4-1", "context-1m-2025-08-07", "opus-4.7 1m variant (Copilot) vs opus-4.1 + 1m beta (native)")]
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
