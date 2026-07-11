using System.Runtime.Versioning;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// GROUND-TRUTH replay of the REAL production request that 400'd with
/// <c>Missing namespace for function_call 'list_agents'</c>. A minimized synthetic
/// probe (<see cref="NamespacedToolEchoProbe"/>) failed to reproduce the 400 —
/// because the real request registers <c>list_agents</c> via an
/// <c>additional_tools</c> developer preamble (the <c>collaboration</c> namespace),
/// NOT the top-level <c>tools[]</c>, and only that registration path enforces the
/// namespace round-trip. So this test replays the ACTUAL captured upstream bytes:
/// <list type="bullet">
///   <item><b>A</b> — the request VERBATIM as the bridge forwarded it (namespace
///   dropped): must 400 with "Missing namespace".</item>
///   <item><b>B</b> — the same bytes with <c>"namespace":"collaboration"</c>
///   injected into the echoed <c>list_agents</c> function_call: must 200.</item>
/// </list>
/// This is what actually proves the fix hypothesis — that re-emitting the namespace
/// on the echoed function_call is necessary AND sufficient. The two fixtures are
/// generated from
/// <c>request-traces/20260711-111649-0010-upstream-req.json</c> and live under
/// <c>tmp-namespace-repro/</c> (de-identified enough for a local run; NOT checked
/// in — this test is skipped when they're absent).
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class NamespaceRealReplayProbe
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public NamespaceRealReplayProbe(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private static readonly string ReproDir =
        Path.Combine("Q:\\", "MyProjects", "cc-copilot-bridge", "tmp-namespace-repro");

    [Fact]
    public async Task A_Verbatim_Reproduces400_MissingNamespace()
    {
        var path = Path.Combine(ReproDir, "repro-A-verbatim.json");
        Assert.True(File.Exists(path), $"replay fixture absent: {path}");
        var body = await File.ReadAllTextAsync(path);

        using var client = new PlaygroundClient();
        var (status, resp) = await client.TryPostResponsesAsync(body);
        _output.WriteLine($"[A verbatim] → {(int)status} {status}");
        _output.WriteLine($"  body: {(resp.Length <= 400 ? resp : resp[..400])}");

        // Contract: the real dropped-namespace request 400s with the exact error.
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, status);
        Assert.Contains("Missing namespace", resp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task B_WithNamespaceInjected_Is200()
    {
        var path = Path.Combine(ReproDir, "repro-B-namespace.json");
        Assert.True(File.Exists(path), $"replay fixture absent: {path}");
        var body = await File.ReadAllTextAsync(path);

        using var client = new PlaygroundClient();
        var (status, resp) = await client.TryPostResponsesAsync(body);
        _output.WriteLine($"[B +namespace] → {(int)status} {status}");
        _output.WriteLine($"  body: {(resp.Length <= 400 ? resp : resp[..400])}");

        // Contract: re-emitting namespace on the echoed function_call is SUFFICIENT.
        Assert.Equal(System.Net.HttpStatusCode.OK, status);
        Assert.DoesNotContain("Missing namespace", resp, StringComparison.Ordinal);
    }
}
