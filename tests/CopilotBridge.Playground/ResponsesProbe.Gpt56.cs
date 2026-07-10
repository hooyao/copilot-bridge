using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Targeted live probes for the three <c>gpt-5.6-*</c> Responses ids Copilot's
/// <c>/models</c> surfaced during the 2026-07 reconciliation
/// (<c>gpt-5.6-luna</c>, <c>gpt-5.6-sol</c>, <c>gpt-5.6-terra</c> — codename
/// slots, all <c>endpoints=[/responses,ws:/responses]</c>, <c>ctx=1050000</c>).
/// Parallel in intent to <see cref="ResponsesProbe.MaiCodePicker_Effort_ReProbe"/> —
/// ground every <see cref="CopilotBridge.Cli.Pipeline.Routing.CodexModelProfile"/>
/// field in a live status before editing the catalog.
/// </summary>
/// <remarks>
/// <para>The load-bearing question these answer: <c>/models</c> advertises
/// <c>effort=[none,low,medium,high,xhigh,max]</c> for all three — a <c>max</c>
/// tier NO existing Codex profile accepts (the "large" set tops out at
/// <c>xhigh</c>). Per the sync skill's one rule, <c>/models</c> lies in both
/// directions, so whether <c>max</c> is really accepted decides whether these get
/// the existing "large" profile or a NEW "xlarge" one. Hence the effort axis
/// here EXTENDS <see cref="ResponsesProbe"/>'s matrix with <c>max</c> (the shared
/// <c>Efforts</c> array stops at <c>xhigh</c>).</para>
/// <para>Run:
/// <code>dotnet test tests/CopilotBridge.Playground --filter "FullyQualifiedName~Gpt56_" --logger "console;verbosity=detailed"</code>
/// Read the "→ HTTP N" lines: 200 = accepted, 400 = rejected. The probes only
/// LOG — they don't assert — so a matrix cell 400ing doesn't fail the run;
/// interpret the printed statuses and encode them in the catalog.</para>
/// </remarks>
public partial class ResponsesProbe
{
    /// <summary>The three new codename ids under test.</summary>
    public static readonly string[] Gpt56Models =
    [
        "gpt-5.6-luna",
        "gpt-5.6-sol",
        "gpt-5.6-terra",
    ];

    /// <summary>
    /// Effort vocabulary to probe — the shared <see cref="Efforts"/> boundary set
    /// PLUS <c>max</c>. <c>max</c> is the decisive case: it is what Anthropic's
    /// top tier maps to and what <c>/models</c> claims these accept, but no Codex
    /// model has ever accepted it. <c>null</c> (no effort field) and the two
    /// inverted boundaries (<c>none</c> vs <c>minimal</c>) pin which effort family
    /// these belong to.
    /// </summary>
    private static readonly string?[] Gpt56Efforts =
        [null, "minimal", "none", "low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// Effort acceptance for the gpt-5.6 codenames. Reuses
    /// <see cref="ResponsesProbe.Effort_ProbeAcceptance"/> (same minimal request
    /// shape Codex sends). Decides <c>AcceptedEfforts</c> + <c>DefaultEffort</c>:
    /// if <c>max</c> → 200 these need a new effort set; if <c>max</c> → 400 they
    /// match the existing "large" profile and <c>max</c> falls back to <c>xhigh</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Gpt56EffortMatrix))]
    public Task Gpt56_Effort_ReProbe(string model, string? effort) =>
        Effort_ProbeAcceptance(model, effort);

    public static IEnumerable<object[]> Gpt56EffortMatrix() =>
        from m in Gpt56Models
        from e in Gpt56Efforts
        select new object[] { m, e! };

    /// <summary>
    /// Custom-tool acceptance for the gpt-5.6 codenames — grounds
    /// <c>RejectsCustomTools</c>. Reuses <see cref="ResponsesProbe.Tool_ProbeAcceptance"/>
    /// (function / custom apply_patch / web_search / image_generation). The
    /// load-bearing case is <c>apply_patch_custom</c>: a non-200 there sets
    /// <c>RejectsCustomTools = true</c> (like <c>mai-code-1-flash-picker</c>); a
    /// 200 leaves it false (like the gpt-5.x family).
    /// </summary>
    [Theory]
    [MemberData(nameof(Gpt56ToolMatrix))]
    public Task Gpt56_Tool_ReProbe(string model, string label, string toolJson) =>
        Tool_ProbeAcceptance(model, label, toolJson);

    public static IEnumerable<object[]> Gpt56ToolMatrix() =>
        from m in Gpt56Models
        from t in Tools
        select new object[] { m, t.Label, t.Json };

    /// <summary>
    /// Liveness sanity: a bare minimal request per codename id. A 200 confirms the
    /// id routes at all (guards against a <c>/models</c> phantom that 404s on a real
    /// call); a 4xx that isn't an effort/tool rejection means the id isn't actually
    /// serveable and must NOT be added to the catalog.
    /// </summary>
    [Theory]
    [InlineData("gpt-5.6-luna")]
    [InlineData("gpt-5.6-sol")]
    [InlineData("gpt-5.6-terra")]
    public async Task Gpt56_LivenessProbe(string model)
    {
        var payload = $$"""
          {
            "model": "{{model}}",
            "instructions": "Reply with exactly: ok",
            "input": [{"type":"message","role":"user","content":[{"type":"input_text","text":"reply: ok"}]}],
            "stream": false,
            "store": false
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"[{model}] liveness → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 300)}");
    }
}
