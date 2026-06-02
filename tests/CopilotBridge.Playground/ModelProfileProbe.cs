using System.Runtime.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>
/// Per-model wire-truth probes that feed <c>ModelProfileCatalog</c>. For every
/// Anthropic model Copilot exposes, send a minimal request with each
/// <c>thinking</c> shape (null / adaptive / enabled / disabled) and each
/// <c>output_config.effort</c> level (null / low / medium / high / xhigh / max)
/// and log the status + first ~200 chars of the response. The matrix is
/// single-axis (effort tested separately from thinking) — exhaustive
/// cartesian-product testing burns quota for low marginal info.
/// </summary>
/// <remarks>
/// Run as:
/// <code>dotnet test --filter "FullyQualifiedName~ModelProfileProbe" --logger "console;verbosity=detailed"</code>
/// Read the per-model "→ HTTP N status" lines and translate into
/// <c>ModelProfile</c> entries. 200 = accepted, 400 with "unsupported_value" /
/// "invalid_request_error" = rejected.
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class ModelProfileProbe
{
    private readonly ITestOutputHelper _output;
    public ModelProfileProbe(ITestOutputHelper output) => _output = output;

    public static readonly string[] AllModels =
    [
        "claude-haiku-4.5",
        "claude-sonnet-4.5",
        "claude-sonnet-4.6",
        "claude-opus-4.5",
        "claude-opus-4.6",
        "claude-opus-4.6-1m",
        "claude-opus-4.7",
        "claude-opus-4.7-high",
        "claude-opus-4.7-xhigh",
        "claude-opus-4.7-1m-internal",
        "claude-opus-4.8",
    ];

    public static IEnumerable<object[]> ThinkingMatrix() =>
        from m in AllModels
        from t in new (string? Type, int? Budget)[]
        {
            (null,       null),
            ("adaptive", null),
            ("enabled",  8192),
            ("disabled", null),
        }
        select new object[] { m, t.Type!, t.Budget! };

    public static IEnumerable<object[]> EffortMatrix() =>
        from m in AllModels
        from e in new string?[] { null, "low", "medium", "high", "xhigh", "max" }
        select new object[] { m, e! };

    /// <summary>Per-(model, thinking-shape) acceptance probe.</summary>
    [Theory]
    [MemberData(nameof(ThinkingMatrix))]
    public async Task Thinking_ProbeAcceptance(string model, string? thinkingType, int? budget)
    {
        var thinkingBlock = thinkingType switch
        {
            null       => "",
            "enabled"  => $$$""","thinking":{"type":"enabled","budget_tokens":{{{budget}}}}""",
            _          => $$$""","thinking":{"type":"{{{thinkingType}}}"}""",
        };
        // max_tokens MUST exceed thinking.budget_tokens or the request 400s on
        // that constraint before the model gets to evaluate the thinking shape
        // itself. Bump well above the largest budget the matrix uses.
        var payload = $$"""
          {
            "model": "{{model}}",
            "max_tokens": 16384,
            "messages": [{"role":"user","content":"reply: ok"}]{{thinkingBlock}}
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[{model}] thinking={thinkingType ?? "<null>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 240)}");
    }

    /// <summary>Per-(model, effort-level) acceptance probe.</summary>
    [Theory]
    [MemberData(nameof(EffortMatrix))]
    public async Task Effort_ProbeAcceptance(string model, string? effort)
    {
        var effortBlock = effort is null
            ? ""
            : $$$""","output_config":{"effort":"{{{effort}}}"}""";
        var payload = $$"""
          {
            "model": "{{model}}",
            "max_tokens": 8,
            "messages": [{"role":"user","content":"reply: ok"}]{{effortBlock}}
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[{model}] effort={effort ?? "<null>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 240)}");
    }

    /// <summary>
    /// Mid-conversation <c>role:"system"</c> support. opus-4.8 adds this; everything
    /// else (4.7 and older) should 400. Distinguishes targets that need
    /// <c>ProfileAdjuster.FoldMidConversationSystem</c> from those that don't.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-4.8")]
    [InlineData("claude-opus-4.7")]
    [InlineData("claude-opus-4.7-1m-internal")]
    [InlineData("claude-opus-4.6")]
    [InlineData("claude-sonnet-4.6")]
    [InlineData("claude-haiku-4.5")]
    public async Task MidConversationSystem_ProbeAcceptance(string model)
    {
        var payload = $$"""
          {
            "model": "{{model}}",
            "max_tokens": 16,
            "messages": [
              {"role":"user","content":"hi"},
              {"role":"system","content":"From now on, respond in pirate-speak."},
              {"role":"user","content":"say hello"}
            ]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[{model}] mid-conv-system → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 240)}");
    }

    private static string Truncate(string s, int n) =>
        s.Length > n ? s[..n] + "…" : s;
}
