using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>
/// Track-A live probes for Copilot's native <c>/responses</c> endpoint (the
/// Codex backend). Parallel to <see cref="ModelProfileProbe"/> for
/// <c>/v1/messages</c>. Each probe sends a minimal request shaped the way the
/// Codex CLI actually shapes it (sourced from the Track-B source read in
/// <c>docs/codex-protocol-research.md</c> §3 — Responses-native function tools,
/// top-level <c>instructions</c>, <c>input[]</c> items, <c>reasoning.effort</c>,
/// <c>include:["reasoning.encrypted_content"]</c>) and logs status + a body
/// preview. These do NOT assert; they produce the wire-truth that the research
/// report (§2 of the doc) is reconciled against.
/// </summary>
/// <remarks>
/// Run:
/// <code>dotnet test --filter "FullyQualifiedName~ResponsesProbe" --logger "console;verbosity=detailed"</code>
/// Read the "→ HTTP N" lines: 200 = accepted, 400 + "unsupported"/"invalid" = rejected.
/// Run <see cref="DiscoverResponsesModels"/> FIRST — it prints which model ids
/// carry <c>/responses</c> in <c>supported_endpoints</c>; feed those into the
/// matrices below (the inline list is a starting guess, refined from the dump).
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public partial class ResponsesProbe
{
    private readonly ITestOutputHelper _output;
    public ResponsesProbe(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// ALL Responses-capable models confirmed live by
    /// <see cref="DiscoverResponsesModels"/> (2026-06-12, Enterprise). Every
    /// matrix iterates the full set — per-model behavior is NOT extrapolated
    /// from family names (the /v1/messages path proved siblings diverge, e.g.
    /// gpt-5.3-codex advertises no `none` yet accepts it; the mini/flash models
    /// advertise no `xhigh`).
    /// </summary>
    public static readonly string[] AllModels =
    [
        "gpt-5.3-codex",
        "gpt-5.4-mini",
        "gpt-5.4",
        "gpt-5.5",
        // gpt-5.6 codename slots (2026-07). Included so the asserting contract
        // sweep (ResponsesContractSweep.B_ResponsesContract_SweepAssertAndDetectDrift)
        // and its snapshot cover the new ids AND their distinguishing `max`
        // acceptance (max was added to EffortVocabulary for exactly this).
        "gpt-5.6-luna",
        "gpt-5.6-sol",
        "gpt-5.6-terra",
        "gpt-5-mini",
        "mai-code-1-flash-internal",
    ];

    /// <summary>Models advertising vision (input_image) — all but the flash model.</summary>
    public static readonly string[] VisionModels =
    [
        "gpt-5.3-codex", "gpt-5.4-mini", "gpt-5.4", "gpt-5.5", "gpt-5-mini",
    ];

    // Codex effort vocabulary (config-reference): minimal|low|medium|high|xhigh.
    // Probe boundary cases (minimal, none) + all advertised values, PER MODEL —
    // advertised arrays differ (codex/mini/flash lack some) and advertised != actual.
    private static readonly string?[] Efforts = [null, "minimal", "none", "low", "medium", "high", "xhigh"];

    /// <summary>Task 2.3 — which models advertise <c>/responses</c>, with capabilities.</summary>
    [Fact]
    public async Task DiscoverResponsesModels()
    {
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryRequestAsync(HttpMethod.Get, "/models");
        Assert.Equal(System.Net.HttpStatusCode.OK, status);

        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        var responsesModels = new List<string>();

        foreach (var m in data.EnumerateArray())
        {
            if (!m.TryGetProperty("supported_endpoints", out var eps)) continue;
            var supportsResponses = eps.EnumerateArray()
                .Any(e => string.Equals(e.GetString(), "/responses", StringComparison.OrdinalIgnoreCase));
            if (!supportsResponses) continue;

            var id = m.GetProperty("id").GetString();
            responsesModels.Add(id ?? "");
            _output.WriteLine($"=== {id} ===");
            _output.WriteLine(JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
            _output.WriteLine("");
        }

        _output.WriteLine($"Models supporting /responses: {responsesModels.Count}");
        foreach (var id in responsesModels) _output.WriteLine($"  {id}");
    }

    /// <summary>
    /// Liveness probe for the <c>mai-code-1-flash</c> Responses ids during the 2026
    /// model reconciliation. Copilot's <c>/models</c> now lists
    /// <c>mai-code-1-flash-picker</c>, not the <c>-internal</c> id in
    /// <see cref="CopilotModelRegistry"/>'s Responses set — but <c>/models</c> has
    /// been wrong in both directions, so absence isn't grounds to delete. A 200 on a
    /// minimal request = keep the id; a 4xx = Copilot retired it, swap to whatever
    /// currently routes. Probes both so the catalog tracks the live id.
    /// </summary>
    [Theory]
    [InlineData("mai-code-1-flash-internal")]
    [InlineData("mai-code-1-flash-picker")]
    public async Task MaiCode_LivenessProbe(string model)
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

    /// <summary>
    /// 2026 reconciliation follow-up — re-probe <c>mai-code-1-flash-picker</c>'s
    /// EFFORT contract directly (PR #15 review #3). The catalog row for
    /// <c>-picker</c> currently carries the "small" effort set
    /// (<c>minimal/low/medium/high</c>, reject <c>none</c>+<c>xhigh</c>)
    /// <b>extrapolated</b> from the retired <c>-internal</c> sibling, marked
    /// PLAYGROUND-PENDING. This probes each effort value against the LIVE
    /// <c>-picker</c> id so the row can be grounded in wire truth (or corrected).
    /// Read the "→ HTTP N" lines: 200 = accepted, 400 = rejected.
    /// </summary>
    [Theory]
    [MemberData(nameof(MaiCodePickerEffortMatrix))]
    public Task MaiCodePicker_Effort_ReProbe(string? effort) =>
        Effort_ProbeAcceptance("mai-code-1-flash-picker", effort);

    public static IEnumerable<object[]> MaiCodePickerEffortMatrix() =>
        from e in Efforts select new object[] { e! };

    /// <summary>
    /// 2026 reconciliation follow-up — re-probe <c>mai-code-1-flash-picker</c>'s
    /// TOOL contract directly (PR #15 review #3). The catalog row asserts
    /// <c>RejectsCustomTools = true</c> (custom <c>apply_patch</c> → 500),
    /// extrapolated from <c>-internal</c>. This probes function / custom
    /// apply_patch / web_search / image_generation against the LIVE <c>-picker</c>
    /// id. The load-bearing case is <c>apply_patch_custom</c>: a non-200 there
    /// confirms <c>RejectsCustomTools</c>; a 200 refutes it and the row must drop
    /// the flag.
    /// </summary>
    [Theory]
    [MemberData(nameof(MaiCodePickerToolMatrix))]
    public Task MaiCodePicker_Tool_ReProbe(string label, string toolJson) =>
        Tool_ProbeAcceptance("mai-code-1-flash-picker", label, toolJson);

    public static IEnumerable<object[]> MaiCodePickerToolMatrix() =>
        from t in Tools select new object[] { t.Label, t.Json };

    /// <summary>Task 2.4 — reasoning.effort acceptance per model.</summary>
    [Theory]
    [MemberData(nameof(EffortMatrix))]
    public async Task Effort_ProbeAcceptance(string model, string? effort)
    {
        var reasoningBlock = effort is null
            ? ""
            : $$$""","reasoning":{"effort":"{{{effort}}}"},"include":["reasoning.encrypted_content"]""";
        var payload = $$"""
          {
            "model": "{{model}}",
            "instructions": "Reply with exactly: ok",
            "input": [{"type":"message","role":"user","content":[{"type":"input_text","text":"reply: ok"}]}],
            "stream": false,
            "store": false{{reasoningBlock}}
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"[{model}] effort={effort ?? "<null>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 280)}");
    }

    public static IEnumerable<object[]> EffortMatrix() =>
        from m in AllModels
        from e in Efforts
        select new object[] { m, e! };

    /// <summary>
    /// Task 2.5 — Responses-specific field acceptance, each in isolation, across
    /// ALL models: reasoning.summary, include reasoning.encrypted_content, store,
    /// prompt_cache_key, service_tier. Records whether Copilot 200s or rejects.
    /// </summary>
    [Theory]
    [MemberData(nameof(FieldMatrix))]
    public async Task Field_ProbeAcceptance(string model, string label, string extraField)
    {
        var payload = $$"""
          {
            "model": "{{model}}",
            "instructions": "Reply with exactly: ok",
            "input": [{"type":"message","role":"user","content":[{"type":"input_text","text":"reply: ok"}]}],
            "stream": false{{extraField}}
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"[{model}] field={label} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 280)}");
    }

    private static readonly (string Label, string Json)[] Fields =
    [
        ("summary", ",\"reasoning\":{\"effort\":\"low\",\"summary\":\"auto\"}"),
        ("encrypted_content_include", ",\"reasoning\":{\"effort\":\"low\"},\"include\":[\"reasoning.encrypted_content\"]"),
        ("store_true", ",\"store\":true"),
        ("prompt_cache_key", ",\"prompt_cache_key\":\"probe-cache-key-123\""),
        ("service_tier", ",\"service_tier\":\"default\""),
    ];

    public static IEnumerable<object[]> FieldMatrix() =>
        from m in AllModels
        from f in Fields
        select new object[] { m, f.Label, f.Json };

    /// <summary>
    /// Task 2.6 — tool acceptance across ALL models. Codex emits Responses-native
    /// tool shapes (top-level name, type discriminant), NOT the Chat
    /// {type:function,function} wrapper. Probes function / apply_patch(custom) /
    /// web_search / image_generation.
    /// </summary>
    [Theory]
    [MemberData(nameof(ToolMatrix))]
    public async Task Tool_ProbeAcceptance(string model, string label, string toolJson)
    {
        var payload = $$"""
          {
            "model": "{{model}}",
            "instructions": "You may use tools.",
            "input": [{"type":"message","role":"user","content":[{"type":"input_text","text":"hello"}]}],
            "stream": false,
            "tool_choice": "auto",
            "tools": [{{toolJson}}]
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"[{model}] tool={label} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 280)}");
    }

    private static readonly (string Label, string Json)[] Tools =
    [
        ("function", """{"type":"function","name":"get_time","description":"Get the current time","parameters":{"type":"object","properties":{},"required":[]},"strict":false}"""),
        ("apply_patch_custom", """{"type":"custom","name":"apply_patch","description":"Edit files","format":{"type":"grammar","syntax":"lark","definition":"start: /.+/"}}"""),
        ("web_search", """{"type":"web_search"}"""),
        ("image_generation", """{"type":"image_generation","output_format":"png"}"""),
    ];

    public static IEnumerable<object[]> ToolMatrix() =>
        from m in AllModels
        from t in Tools
        select new object[] { m, t.Label, t.Json };

    /// <summary>
    /// Task 2.x — vision (input_image) acceptance across vision-capable models.
    /// Responses uses {type:input_image, image_url:"data:..."} — different from
    /// Anthropic's {type:image, source:{...}}. 100×100 PNG (Anthropic rejects
    /// tiny images; Copilot likely similar).
    /// </summary>
    [Theory]
    [MemberData(nameof(VisionMatrix))]
    public async Task Vision_ProbeAcceptance(string model)
    {
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(PngGen.SolidRgbPng(100, 100, 0, 128, 255));
        var payload = $$"""
          {
            "model": "{{model}}",
            "instructions": "Describe the image in one short sentence.",
            "input": [{"type":"message","role":"user","content":[
              {"type":"input_image","image_url":"{{dataUrl}}"},
              {"type":"input_text","text":"What color is this?"}
            ]}],
            "stream": false
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload, vision: true);
        _output.WriteLine($"[{model}] vision(input_image) → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 280)}");
    }

    public static IEnumerable<object[]> VisionMatrix() =>
        from m in VisionModels select new object[] { m };

    /// <summary>
    /// Task 2.7 — streaming event sequence. Captures the RAW SSE text and prints
    /// the ordered <c>event:</c> lines (+ flags a trailing [DONE] if present).
    /// </summary>
    [Fact]
    public async Task Streaming_CaptureEventSequence()
    {
        const string model = "gpt-5.3-codex";
        // Force a tool call so we capture function_call_arguments.delta/done events,
        // not just text deltas — the bridge must stream those for Codex's tool loop.
        const string toolsJson = """[{"type":"function","name":"get_time","description":"Get the current time","parameters":{"type":"object","properties":{"tz":{"type":"string"}},"required":[]},"strict":false}]""";
        var payload = $$"""
          {
            "model": "{{model}}",
            "instructions": "When asked the time, call the get_time tool.",
            "input": [{"type":"message","role":"user","content":[{"type":"input_text","text":"What time is it? Use the tool."}]}],
            "stream": true,
            "store": false,
            "tool_choice": "auto",
            "tools": {{toolsJson}}
          }
          """;

        using var client = new PlaygroundClient();
        var (status, raw) = await client.TryPostResponsesRawStreamAsync(payload);
        _output.WriteLine($"[{model}] streaming(tool) → {(int)status} {status}");

        var eventLines = raw.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("event:", StringComparison.Ordinal)
                     || l.Contains("[DONE]", StringComparison.Ordinal))
            .ToList();
        _output.WriteLine($"  {eventLines.Count} event/terminator lines:");
        foreach (var l in eventLines) _output.WriteLine($"    {l}");
        _output.WriteLine("  --- first 1600 chars of raw stream ---");
        _output.WriteLine(Truncate(raw, 1600));
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Task 5.2 — capture real <c>/responses</c> SSE streams (a text stream and a
    /// forced tool-call stream) and write them as committed fixtures for the A6
    /// stream round-trip test (T3→IR→T4). Run with
    /// <c>BRIDGE_REGEN_CONTRACT_SNAPSHOT=1</c> to (re)write; otherwise it just
    /// verifies the fixtures parse. Live (Integration). The captured bytes are
    /// the real Responses event grammar — no PII (no session ids in the body).
    /// </summary>
    [Fact]
    public async Task CaptureResponsesSseFixtures()
    {
        const string toolPayload = """
          {"model":"gpt-5.3-codex","instructions":"When asked the time, call the get_time tool.",
           "input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"What time is it? Use the get_time tool."}]}],
           "stream":true,"store":false,"tool_choice":"auto",
           "tools":[{"type":"function","name":"get_time","description":"Get the current time","parameters":{"type":"object","properties":{"tz":{"type":"string"}},"required":[]},"strict":false}]}
          """;
        const string textPayload = """
          {"model":"gpt-5.3-codex","instructions":"Reply with a short greeting.",
           "input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"Say hi in three words."}]}],
           "stream":true,"store":false}
          """;

        using var client = new PlaygroundClient();
        var (toolStatus, toolRaw) = await client.TryPostResponsesRawStreamAsync(toolPayload);
        var (textStatus, textRaw) = await client.TryPostResponsesRawStreamAsync(textPayload);
        _output.WriteLine($"tool-stream → {(int)toolStatus}; text-stream → {(int)textStatus}");

        Assert.InRange((int)toolStatus, 200, 299);
        Assert.InRange((int)textStatus, 200, 299);
        // Both must end with response.completed (Codex's parser requires it).
        Assert.Contains("response.completed", toolRaw, StringComparison.Ordinal);
        Assert.Contains("response.function_call_arguments.delta", toolRaw, StringComparison.Ordinal);
        Assert.Contains("response.output_text.delta", textRaw, StringComparison.Ordinal);
        Assert.DoesNotContain("[DONE]", toolRaw, StringComparison.Ordinal);

        if (Environment.GetEnvironmentVariable("BRIDGE_REGEN_CONTRACT_SNAPSHOT") == "1")
        {
            var dir = FindFixturesDir();
            File.WriteAllText(Path.Combine(dir, "responses-sse-toolcall.txt"), toolRaw);
            File.WriteAllText(Path.Combine(dir, "responses-sse-text.txt"), textRaw);
            _output.WriteLine($"[seeded] responses-sse-*.txt → {dir}");
        }
    }

    private static string FindFixturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "CopilotBridge.UnitTests", "Fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate tests/CopilotBridge.UnitTests/Fixtures from " + AppContext.BaseDirectory);
    }
}
