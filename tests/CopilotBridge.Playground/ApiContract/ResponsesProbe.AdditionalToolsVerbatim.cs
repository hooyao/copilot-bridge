using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// One-shot verification probe: replay the EXACT <c>additional_tools</c> input
/// item from the real desktop capture (all four nested tools with Copilot's
/// reserved built-in schemas intact) to confirm faithful passthrough 200s. This
/// isolates the <see cref="ResponsesProbe.AdditionalTools_AsInputItem_FullZoo"/>
/// 400 as a hand-written-schema artifact (my stub for <c>collaboration</c> didn't
/// match Copilot's reserved schema) rather than an <c>additional_tools</c>
/// rejection. Reads the fixture from the source tree (not copied to output) so no
/// 20 KB blob is committed to the test bundle.
/// </summary>
/// <remarks>
/// Run:
/// <code>dotnet test tests/CopilotBridge.Playground --filter "FullyQualifiedName~AdditionalToolsVerbatim" --logger "console;verbosity=detailed"</code>
/// </remarks>
public partial class ResponsesProbe
{
    [Fact]
    public async Task AdditionalToolsVerbatim_ExactCaptureRoundTrips()
    {
        var path = FindVerbatimFixture();
        var payload = await File.ReadAllTextAsync(path);
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"[gpt-5.6-sol] verbatim additional_tools capture → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 600)}");
    }

    private static string FindVerbatimFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "CopilotBridge.Playground",
                "Fixtures", "codex-additional-tools-verbatim.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("codex-additional-tools-verbatim.json not found from " + AppContext.BaseDirectory);
    }
}
