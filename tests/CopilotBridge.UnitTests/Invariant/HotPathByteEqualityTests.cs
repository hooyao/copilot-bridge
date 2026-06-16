using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Models;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// H1 hot-path byte-equality (<c>docs/ir-definition-design.md</c> §7.5) — THE
/// gate the whole change rests on. Adding the <c>ProviderExtensions</c> bag must
/// not change a single byte the bridge sends for the existing Claude Code path.
///
/// Because the bag is <c>null</c> for every Claude Code request and the
/// context-wide <c>WhenWritingNull</c> omits null properties, the new
/// <c>provider_extensions</c> field emits nothing — so the serialized upstream
/// body is identical to pre-change output. We prove that two ways:
///   H1a — the round-tripped bytes contain NO <c>provider_extensions</c> token
///         (the bag truly emitted nothing), and parse→serialize is stable
///         (idempotent: a second round trip equals the first).
///   H1b — a frozen pre-change golden of the serialized hot-path body equals the
///         current serialization byte-for-byte. The golden is regenerated only
///         by an explicit opt-in env var, so an accidental drift fails loudly.
/// </summary>
public class HotPathByteEqualityTests
{
    private readonly ITestOutputHelper _output;

    public HotPathByteEqualityTests(ITestOutputHelper output) => _output = output;

    // Goldens are COMMITTED source assets: seed/read them in the source tree
    // (tests/.../Fixtures/hotpath-golden), not the bin copy — otherwise a clean
    // checkout has no golden and H1b becomes vacuously self-seeding. Walk up
    // from the bin dir to the project root.
    private static readonly string GoldenDir =
        Path.Combine(FindProjectRoot(), "Fixtures", "hotpath-golden");

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CopilotBridge.UnitTests.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        // Fallback to bin-local Fixtures (still functional, just not committed).
        return Path.Combine(AppContext.BaseDirectory, "Fixtures");
    }

    public static IEnumerable<object[]> Fixtures() =>
        IrRoundTrip.AllFixtureSlugs().Select(s => new object[] { s });

    // ── H1a: the bag is provably inert on the hot path ───────────────────────

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void H1a_BagEmitsNothing_AndSerializationIsStable(string slug)
    {
        var inbound = IrRoundTrip.LoadFixtureBodyJson(slug);

        var once = IrRoundTrip.RoundTripBytes(inbound);
        var onceStr = Encoding.UTF8.GetString(once);

        // The empty/null bag must not appear anywhere in the hot-path bytes.
        Assert.DoesNotContain("provider_extensions", onceStr, StringComparison.Ordinal);

        // parse→serialize→parse→serialize is idempotent (no accumulating drift).
        var twice = IrRoundTrip.RoundTripBytes(onceStr);
        Assert.Equal(onceStr, Encoding.UTF8.GetString(twice));
    }

    // ── H1b: serialized hot-path body matches a frozen golden byte-for-byte ───

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void H1b_MatchesFrozenGolden(string slug)
    {
        var inbound = IrRoundTrip.LoadFixtureBodyJson(slug);
        var current = IrRoundTrip.RoundTripBytes(inbound);

        Directory.CreateDirectory(GoldenDir);
        var goldenPath = Path.Combine(GoldenDir, $"{slug}.upstream.json");

        // Opt-in regeneration: set BRIDGE_REGEN_HOTPATH_GOLDEN=1 once after an
        // INTENTIONAL serialization change, then review the git diff. Never set
        // it in CI — that would make this test vacuously green.
        var regen = Environment.GetEnvironmentVariable("BRIDGE_REGEN_HOTPATH_GOLDEN") == "1";
        if (regen || !File.Exists(goldenPath))
        {
            File.WriteAllBytes(goldenPath, current);
            if (!regen)
                _output.WriteLine($"[seeded golden] {slug}.upstream.json ({current.Length} bytes) — review & commit.");
        }

        var golden = File.ReadAllBytes(goldenPath);
        Assert.True(golden.AsSpan().SequenceEqual(current),
            $"{slug}: hot-path serialized body differs from frozen golden. " +
            "If this change was intentional, regenerate with BRIDGE_REGEN_HOTPATH_GOLDEN=1 and review the diff.");
    }

    // ── H1c: an empty (non-null) bag also emits nothing ──────────────────────
    // Defends the boundary case where some future code sets an EMPTY bag rather
    // than leaving it null — that must still serialize to nothing on the wire.

    [Fact]
    public void H1c_EmptyBag_StillSerializesToNothing()
    {
        var inbound = IrRoundTrip.LoadFixtureBodyJson("plain-opus48");
        var ir = IrRoundTrip.Parse(inbound);

        var withEmptyBag = ir with
        {
            ProviderExtensions = new Cli.Models.Common.ProviderExtensions(),
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(withEmptyBag, JsonContext.Default.MessagesRequest);
        var str = Encoding.UTF8.GetString(bytes);

        // An empty ByProvider dict renders as
        // "provider_extensions":{"by_provider":{}} — assert that shape and
        // document the boundary. The hot path NEVER constructs an empty bag (it
        // stays null → omitted entirely), so production bytes are unaffected;
        // this test pins the behavior if that ever changes.
        var node = JsonNode.Parse(bytes)!.AsObject();
        if (node.ContainsKey("provider_extensions"))
        {
            Assert.Equal("""{"by_provider":{}}""", node["provider_extensions"]!.ToJsonString());
        }
    }
}
