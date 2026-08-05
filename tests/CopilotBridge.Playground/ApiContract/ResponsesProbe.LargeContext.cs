using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CopilotBridge.Playground.Contract;
using Xunit;

namespace CopilotBridge.Playground;

public partial class ResponsesProbe
{
    private const int FormerCodexCatalogCeiling = 272_000;
    private const int PaddingTokenCandidates = 300_000;

    /// <summary>
    /// Replays a real Codex 0.144.x upstream request, changing only the target
    /// model and prompt-length axis. The repeated <c>" x"</c> sequence tokenizes
    /// as one token per repetition under the advertised o200k tokenizer; the
    /// response usage is still asserted so that assumption cannot manufacture a
    /// false pass. This is the captured-byte confirmation required before the
    /// bridge advertises a context limit above Codex's former 272k catalog cap.
    /// </summary>
    /// <remarks>
    /// Requires at least one local, gitignored <c>Kind=ClientBehavior</c> trace
    /// produced by real <c>codex.exe</c>. The probe deliberately refuses a
    /// synthetic fallback: full instructions, tools, metadata, streaming, and
    /// system blocks are part of the evidence.
    /// </remarks>
    [Theory]
    [InlineData("gpt-5.4")]
    [InlineData("gpt-5.5")]
    [InlineData("gpt-5.6-luna")]
    [InlineData("gpt-5.6-sol")]
    [InlineData("gpt-5.6-terra")]
    public async Task OneMillionClass_RealCodexBytes_AcceptBeyondFormer272kCeiling(string model)
    {
        var (capturePath, body) = LoadNewestRealCodexBody();
        body["model"] = model;

        var input = body["input"]?.AsArray()
            ?? throw new InvalidDataException($"Captured Codex body has no input array: {capturePath}");
        var lastUser = input
            .OfType<JsonObject>()
            .LastOrDefault(item => string.Equals(item["type"]?.GetValue<string>(), "message", StringComparison.Ordinal)
                                && string.Equals(item["role"]?.GetValue<string>(), "user", StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Captured Codex body has no user message: {capturePath}");
        var content = lastUser["content"]?.AsArray()
            ?? throw new InvalidDataException($"Captured Codex user message has no content array: {capturePath}");
        content.Add(new JsonObject
        {
            ["type"] = "input_text",
            ["text"] = string.Concat(Enumerable.Repeat(" x", PaddingTokenCandidates)),
        });

        using var client = new PlaygroundClient();
        var (status, raw) = await client.TryPostResponsesRawStreamAsync(body.ToJsonString());
        var accepted = WireAcceptance.IsAccepted(status, raw, $"{model} real-Codex >272k probe");
        var inputTokens = ReadInputTokens(raw);

        _output.WriteLine($"[{model}] real Codex bytes + {PaddingTokenCandidates:N0} token candidates → {(int)status} {status}");
        _output.WriteLine($"  source capture: {capturePath}");
        _output.WriteLine($"  reported input_tokens: {inputTokens:N0}");
        if (!accepted) _output.WriteLine($"  rejection: {WireAcceptance.ErrorMessage(raw)}");

        Assert.True(accepted, $"{model} rejected a real Codex-shaped prompt beyond the old catalog ceiling: {WireAcceptance.ErrorMessage(raw)}");
        Assert.True(inputTokens > FormerCodexCatalogCeiling,
            $"Probe did not cross the former {FormerCodexCatalogCeiling:N0}-token ceiling; Copilot reported {inputTokens:N0} input tokens.");
    }

    private static long ReadInputTokens(string raw)
    {
        var matches = Regex.Matches(raw, "\\\"input_tokens\\\"\\s*:\\s*(?<value>\\d+)", RegexOptions.CultureInvariant);
        return matches.Count == 0 ? 0 : long.Parse(matches[^1].Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (string Path, JsonObject Body) LoadNewestRealCodexBody()
    {
        var root = FindRepoRoot();
        var runs = Path.Combine(root, "tests", "behavior-runs");
        if (!Directory.Exists(runs))
            throw new InvalidOperationException($"No real-client evidence directory exists at {runs}. Run a Codex Kind=ClientBehavior scenario first.");

        foreach (var path in Directory.EnumerateFiles(runs, "*-upstream-req.json", SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            JsonObject? wrapper;
            try { wrapper = JsonNode.Parse(File.ReadAllText(path))?.AsObject(); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { continue; }

            var target = wrapper?["target"]?.GetValue<string>();
            var userAgent = wrapper?["headers"]?["User-Agent"]?.GetValue<string>();
            if (!string.Equals(target, "/responses", StringComparison.Ordinal) ||
                userAgent?.Contains("codex", StringComparison.OrdinalIgnoreCase) != true ||
                wrapper?["body"] is not JsonObject body)
                continue;

            return (path, (JsonObject)body.DeepClone());
        }

        throw new InvalidOperationException(
            $"No real codex.exe /responses capture was found under {runs}. Run a Codex Kind=ClientBehavior scenario first.");
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CopilotBridge.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"Could not find repository root from {AppContext.BaseDirectory}.");
    }
}
