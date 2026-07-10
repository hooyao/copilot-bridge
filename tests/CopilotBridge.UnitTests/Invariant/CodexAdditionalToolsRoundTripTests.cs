using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Contract tests for the gpt-5.6 <c>additional_tools</c> input-item carriage
/// (change <c>add-codex-additional-tools-item</c>). These assert the REQUIRED
/// behaviour derived from the live probe truth — Copilot's native <c>/responses</c>
/// accepts the item verbatim (200, <c>ResponsesProbe.AdditionalToolsVerbatim</c>) —
/// not the implementation: the bridge must (1) deserialize the new discriminator
/// instead of 400-ing, and (2) round-trip the item byte-faithfully to the wire,
/// ahead of the conversation messages, because the nested <c>collaboration.*</c>
/// tools carry Copilot's reserved schemas that a rewrite would corrupt.
/// </summary>
public class CodexAdditionalToolsRoundTripTests
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static string LoadBody()
    {
        var path = Path.Combine(FixturesDir, "codex-additional-tools-req.json");
        var env = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        return env["body"]!.ToJsonString();
    }

    // ── Contract 3: the new discriminator deserializes instead of throwing ──────
    // Before the fix STJ threw Polymorphism_UnrecognizedTypeDiscriminator here.
    [Fact]
    public void AdditionalToolsItem_Deserializes_NoPolymorphismThrow()
    {
        var body = LoadBody();
        var ex = Record.Exception(() => CodexRoundTrip.ParseRequest(body));
        Assert.Null(ex);

        var req = CodexRoundTrip.ParseRequest(body);
        Assert.Equal("gpt-5.6-sol", req.Model);
        Assert.NotEmpty(req.Input);
    }

    // ── Contract 1: round-trip fidelity — bytes preserved AND positioned first ──
    [Fact]
    public void AdditionalToolsItem_RoundTrips_ByteFaithful_AheadOfMessages()
    {
        var body = LoadBody();
        var original = JsonNode.Parse(body)!.AsObject();

        var req = CodexRoundTrip.ParseRequest(body);
        var ir = CodexRoundTrip.ToIr(req);
        var wire = CodexRoundTrip.ToResponsesWire(ir);
        var emitted = JsonNode.Parse(wire)!.AsObject();

        // Locate the additional_tools item in the ORIGINAL and the EMITTED input[].
        var origItem = FindAdditionalToolsItem(original["input"]!.AsArray());
        Assert.NotNull(origItem);

        var emittedInput = emitted["input"]!.AsArray();
        var emittedItem = FindAdditionalToolsItem(emittedInput);
        Assert.NotNull(emittedItem);

        // (a) It is emitted FIRST — ahead of every conversation message (the only
        // order live-probed 200; matches every capture).
        Assert.Equal("additional_tools", emittedInput[0]!["type"]!.GetValue<string>());

        // (b) role preserved.
        Assert.Equal(
            origItem!["role"]?.GetValue<string>(),
            emittedItem!["role"]?.GetValue<string>());

        // (c) the tools payload is BYTE-IDENTICAL — Copilot's reserved schemas
        // (grammar definition, collaboration.* namespace, encrypted flags) must not
        // be altered. Compare the RAW JSON TEXT (GetRawText) of the tools array from
        // the original body bytes vs the emitted wire bytes — NOT JsonNode canonical
        // text, which would normalize whitespace/escaping/number spelling on BOTH
        // sides and hide exactly the lexical drift WriteRawValue is there to prevent.
        var origToolsRaw = ExtractAdditionalToolsToolsRaw(System.Text.Encoding.UTF8.GetBytes(body));
        var emittedToolsRaw = ExtractAdditionalToolsToolsRaw(wire);
        Assert.NotNull(origToolsRaw);
        Assert.Equal(origToolsRaw, emittedToolsRaw);

        // (d) it must NOT also leak as a stray TOP-LEVEL request field (sibling of
        // input/tools). The item rides the openai bag, and WriteBagFields must skip
        // that key so it is emitted ONLY inside input[]. Without this assertion a
        // deleted skip-case would double-emit the item and the suite would stay green
        // (mutation-verified during review).
        Assert.Null(emitted["additional_tools"]);
    }

    // ── Contract 1b: the item is NOT smeared into messages/system ───────────────
    // It is a tool-registration preamble, not conversation content. If T1 had
    // folded it into the system prompt or a message, the emitted input[] would
    // carry its tool schemas as text — assert it does not.
    [Fact]
    public void AdditionalToolsItem_NotFoldedIntoSystemOrMessages()
    {
        var body = LoadBody();
        var req = CodexRoundTrip.ParseRequest(body);
        var ir = CodexRoundTrip.ToIr(req);
        var wire = CodexRoundTrip.ToResponsesWire(ir);
        var emitted = JsonNode.Parse(wire)!.AsObject();

        // The exec grammar's distinctive token only exists inside the tools payload.
        // It must NOT appear in instructions (system) — that would mean T1 folded
        // the preamble into the system prompt.
        var instructions = emitted["instructions"]?.GetValue<string>() ?? "";
        Assert.DoesNotContain("pragma_source", instructions);

        // And exactly one additional_tools item survives (no duplication into a
        // message item).
        var count = emitted["input"]!.AsArray()
            .Count(n => n?["type"]?.GetValue<string>() == "additional_tools");
        Assert.Equal(1, count);
    }

    // ── Negative / no-op case: a Codex request WITHOUT additional_tools ─────────
    // The carriage machinery (bag key + WriteAdditionalToolsItems) must be a true
    // no-op: no additional_tools key anywhere in the emitted wire, no throw, and
    // input[] carries only the real message. Guards the empty-guard branches so a
    // future change can't start emitting a spurious key on ordinary traffic.
    [Fact]
    public void NoAdditionalTools_RoundTrips_WithNoStrayKey()
    {
        const string body = """
          {
            "model": "gpt-5.6-sol",
            "instructions": "You are Codex.",
            "input": [{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}],
            "stream": false,
            "store": false
          }
          """;
        var req = CodexRoundTrip.ParseRequest(body);
        var ir = CodexRoundTrip.ToIr(req);
        var wire = CodexRoundTrip.ToResponsesWire(ir);
        var wireText = System.Text.Encoding.UTF8.GetString(wire);
        var emitted = JsonNode.Parse(wire)!.AsObject();

        // No additional_tools anywhere — not a top-level key, not the token in the
        // raw bytes (which also covers the input[] items).
        Assert.Null(emitted["additional_tools"]);
        Assert.DoesNotContain("additional_tools", wireText, StringComparison.Ordinal);
    }

    private static JsonObject? FindAdditionalToolsItem(JsonArray input)
    {
        foreach (var n in input)
            if (n is JsonObject o && o["type"]?.GetValue<string>() == "additional_tools")
                return o;
        return null;
    }

    /// <summary>
    /// Pull the RAW JSON text of the first <c>additional_tools</c> item's
    /// <c>tools</c> value straight from the given UTF-8 JSON bytes, via
    /// <see cref="JsonDocument"/> + <see cref="JsonElement.GetRawText"/>. This
    /// preserves the ORIGINAL lexical form (whitespace, escaping, number spelling)
    /// — unlike a <c>JsonNode</c> round trip, which canonicalizes. Used to assert
    /// the carriage is byte-faithful, not merely value-equal. Returns null if no
    /// such item/tools is found.
    /// </summary>
    private static string? ExtractAdditionalToolsToolsRaw(byte[] utf8Json)
    {
        using var doc = JsonDocument.Parse(utf8Json);
        if (!doc.RootElement.TryGetProperty("input", out var input)
            || input.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("type", out var t)
                && t.ValueKind == JsonValueKind.String
                && t.GetString() == "additional_tools"
                && item.TryGetProperty("tools", out var tools))
            {
                return tools.GetRawText();
            }
        }
        return null;
    }
}
