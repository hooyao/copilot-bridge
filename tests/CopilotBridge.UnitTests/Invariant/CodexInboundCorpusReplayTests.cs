using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Models;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// The VERIFICATION GATE that closes the "I can't reproduce it, you hit it every run"
/// gap: replay EVERY real <c>/codex/responses</c> inbound body captured in a live
/// session through the actual deserializer + T1→T2, and assert not one throws
/// <c>Polymorphism_UnrecognizedTypeDiscriminator</c> (the 400 that shipped four
/// gpt-5.6 tool bugs). A single synthetic sample is NOT enough — the bugs were all
/// unmodeled <c>input[]</c> item types the author never imagined, so only the real
/// corpus surfaces them.
/// </summary>
/// <remarks>
/// <para>OFFLINE — this exercises the exact layer where the production 400 happens
/// (STJ polymorphic deserialization of <c>ResponsesRequest.Input</c>, then the T1→T2
/// round trip), WITHOUT calling Copilot. It's opt-in: set
/// <c>CODEX_CORPUS_DIR</c> to a request-traces directory. Skips (passes) when unset so
/// CI stays green; run it locally against the real captures.</para>
/// <para>It also ENUMERATES every distinct <c>input[]</c> item type in the corpus and
/// prints them, so a reviewer can see the coverage the fix must hold — including the
/// types the bridge doesn't model (which must route through the unknown-item
/// passthrough, not a 400).</para>
/// </remarks>
public class CodexInboundCorpusReplayTests
{
    private readonly ITestOutputHelper _output;
    public CodexInboundCorpusReplayTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EveryCapturedCodexInbound_DeserializesAndRoundTrips_WithoutPolymorphism400()
    {
        var dir = Environment.GetEnvironmentVariable("CODEX_CORPUS_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            _output.WriteLine($"CODEX_CORPUS_DIR not set or missing ('{dir}') — skipping corpus replay.");
            return; // opt-in; CI-safe
        }

        var files = Directory.EnumerateFiles(dir, "*-inbound-req.json").ToList();
        _output.WriteLine($"scanning {files.Count} inbound files under {dir}");

        var itemTypes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var codexCount = 0;
        var failures = new List<string>();

        foreach (var file in files)
        {
            string bodyJson;
            try
            {
                var envelope = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                var target = envelope["target"]?.GetValue<string>() ?? "";
                // Only codex /responses inbound bodies carry input[] and go through T1.
                if (!target.EndsWith("/responses", StringComparison.Ordinal)) continue;
                if (envelope["body"] is not JsonObject body || body["input"] is not JsonArray) continue;
                bodyJson = body.ToJsonString();
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  [skip] {Path.GetFileName(file)}: envelope parse — {ex.Message}");
                continue;
            }

            codexCount++;
            var name = Path.GetFileName(file);

            // (1) The exact layer the production 400 occurs at: STJ polymorphic
            // deserialization of ResponsesRequest (input[] items). Must NOT throw.
            ResponsesRequestShim? req = null;
            try
            {
                var parsed = JsonSerializer.Deserialize(bodyJson, JsonContext.Default.ResponsesRequest);
                Assert.NotNull(parsed);
                // Enumerate item types actually present (from the raw JSON so unknowns count).
                foreach (var n in JsonNode.Parse(bodyJson)!["input"]!.AsArray())
                {
                    var t = n?["type"]?.GetValue<string>() ?? "<none>";
                    itemTypes[t] = itemTypes.TryGetValue(t, out var c) ? c + 1 : 1;
                }
                req = new ResponsesRequestShim(parsed);
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: DESERIALIZE threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            // (2) The full T1→T2 round trip must also not throw (the translation layer).
            try
            {
                _ = CodexRoundTrip.RoundTrip(bodyJson);
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: T1→T2 threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        _output.WriteLine($"codex inbound replayed: {codexCount}");
        _output.WriteLine("distinct input[] item types seen:");
        foreach (var kv in itemTypes) _output.WriteLine($"  {kv.Key,-24} {kv.Value}");

        if (failures.Count > 0)
        {
            _output.WriteLine($"FAILURES ({failures.Count}):");
            foreach (var f in failures) _output.WriteLine($"  {f}");
        }

        Assert.True(codexCount > 0, "corpus contained no codex /responses inbound bodies — check CODEX_CORPUS_DIR");
        Assert.Empty(failures);
    }

    // Tiny shim so the compiler doesn't complain about the unused strongly-typed result
    // (we only need that deserialization SUCCEEDED; the round trip re-parses from JSON).
    private sealed record ResponsesRequestShim(object Parsed);
}
