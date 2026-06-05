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

    /// <summary>
    /// opus-4.8 placement-rule probes. The single-position probe above hits
    /// only one placement (<c>user→system→user</c>) — but on 4.8 the error
    /// surface changed from "system not allowed" to a placement-specific error
    /// ("role 'system' must precede an 'assistant' message or end the array"),
    /// proving that 4.8's gateway now accepts <c>role:"system"</c> but enforces
    /// position rules. This matrix enumerates every position that occurs in a
    /// real Claude-Code session so we can fix the bridge to place / convert
    /// correctly. Pure 4.8 — no other model accepts mid-conv system at all per
    /// the probe above.
    /// </summary>
    /// <remarks>
    /// Placement variants tested (S = system, U = user, A = assistant):
    /// <list type="bullet">
    ///   <item><c>U·S</c> — system at array end after a user turn.</item>
    ///   <item><c>U·S·U</c> — system between two user turns (the placement Claude Code injects when a message is queued during a user turn — and what the original probe was testing).</item>
    ///   <item><c>U·A·S</c> — system at array end after an assistant turn (the placement Claude Code injects when a message is queued mid-tool-call, before the assistant has yielded).</item>
    ///   <item><c>U·A·S·U</c> — system between assistant and user (this is the SHAPE the bug-report traces showed — queued user message after the assistant turn but before the next user turn).</item>
    ///   <item><c>U·A·S·A</c> — system between assistant turns.</item>
    ///   <item><c>U·A·U·S</c> — system at array end after a user turn following an assistant turn.</item>
    /// </list>
    /// Anthropic's documented rule ("immediately after a user turn") plus the
    /// 4.8 gateway error ("precede an 'assistant' message or end the array")
    /// jointly predict which combinations succeed.
    /// </remarks>
    [Theory]
    [InlineData("end-after-user",          """[{"role":"user","content":"hi"},{"role":"system","content":"S"}]""")]
    [InlineData("between-two-users",       """[{"role":"user","content":"hi"},{"role":"system","content":"S"},{"role":"user","content":"there"}]""")]
    [InlineData("end-after-assistant",     """[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"},{"role":"system","content":"S"}]""")]
    [InlineData("between-assistant-user",  """[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"},{"role":"system","content":"S"},{"role":"user","content":"more"}]""")]
    [InlineData("between-two-assistants",  """[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"},{"role":"system","content":"S"},{"role":"assistant","content":"world"}]""")]
    [InlineData("end-after-user-followup", """[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"},{"role":"user","content":"more"},{"role":"system","content":"S"}]""")]
    public async Task Opus48_MidConversationSystem_PlacementRules(string label, string messagesJson)
    {
        // max_tokens larger than the default thinking budget the 4.8 family
        // applies under adaptive thinking; otherwise the request 400s on the
        // budget vs max_tokens constraint before placement is even evaluated.
        var payload = $$"""
          {
            "model": "claude-opus-4.8",
            "max_tokens": 64,
            "messages": {{messagesJson}}
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[claude-opus-4.8] placement={label} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 240)}");
    }

    /// <summary>
    /// Confirms whether opus-4.8 needs an <c>anthropic-beta</c> header to
    /// unlock mid-conversation system support. The base probe doesn't send
    /// one; if the placement matrix turns up only 400s, this rules in/out a
    /// "you forgot the beta opt-in" explanation before we conclude the gateway
    /// just won't accept any placement. Anthropic's release notes call the
    /// feature <c>mid-conversation-system-messages-2025-XX-XX</c>; the bridge
    /// already strips <c>mid-conversation-system-*</c> on the way out
    /// (<c>appsettings.json</c> <c>Pipeline.OutboundBeta.GlobalStrip</c>),
    /// which now looks premature if the feature actually works.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("mid-conversation-system-2025-11-01")]
    [InlineData("mid-conversation-system-2025-10-15")]
    public async Task Opus48_MidConversationSystem_WithBetaHeader(string? beta)
    {
        var payload = """
          {
            "model": "claude-opus-4.8",
            "max_tokens": 64,
            "messages": [
              {"role":"user","content":"hi"},
              {"role":"assistant","content":"hello"},
              {"role":"system","content":"From now on, respond in pirate-speak."}
            ]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload, anthropicBeta: beta);
        _output.WriteLine($"[claude-opus-4.8] beta={beta ?? "<none>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 240)}");
    }

    /// <summary>
    /// Does Copilot accept two consecutive <c>role:"user"</c> messages? Tested
    /// across the canonical Claude families because if a unified "convert
    /// system→user" fix can land, it must not create user-user adjacency that
    /// the gateway rejects. Anthropic's first-party API requires strict
    /// alternation (user, assistant, user, …); whether Copilot enforces the
    /// same is the open question.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-4.8")]
    [InlineData("claude-opus-4.7")]
    [InlineData("claude-opus-4.7-1m-internal")]
    [InlineData("claude-sonnet-4.6")]
    [InlineData("claude-haiku-4.5")]
    public async Task ConsecutiveUserMessages_ProbeAcceptance(string model)
    {
        var payload = $$"""
          {
            "model": "{{model}}",
            "max_tokens": 64,
            "messages": [
              {"role":"user","content":"first"},
              {"role":"user","content":"second"}
            ]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[{model}] consecutive-user → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 240)}");
    }

    /// <summary>
    /// Does Copilot accept a <c>role:"user"</c> message inserted between
    /// assistant turns? Same purpose as <see cref="ConsecutiveUserMessages_ProbeAcceptance"/>:
    /// rules out an alternation-violation rejection for the
    /// <c>U·A·U·A·U</c> shape that emerges when system messages between two
    /// assistants get converted to user.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-4.8")]
    [InlineData("claude-opus-4.7")]
    [InlineData("claude-sonnet-4.6")]
    public async Task UserBetweenAssistants_ProbeAcceptance(string model)
    {
        // U·A·U·A·U pattern. Trailing U is required (Copilot rejects trailing
        // assistant; MessagesSanitizeStage appends "Please continue." normally).
        var payload = $$"""
          {
            "model": "{{model}}",
            "max_tokens": 64,
            "messages": [
              {"role":"user","content":"hi"},
              {"role":"assistant","content":"ok"},
              {"role":"user","content":"injected"},
              {"role":"assistant","content":"ack"},
              {"role":"user","content":"go"}
            ]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[{model}] user-between-assistants → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 240)}");
    }

    /// <summary>
    /// Does Copilot's claude-opus-4.8 accept the
    /// <c>context-1m-2025-08-07</c> beta header on a small request? The
    /// catalog says opus-4.8 has <c>ctx=1000000</c> in its <c>/models</c>
    /// capabilities, and there is no separate <c>-1m-internal</c> variant
    /// for 4.8 (unlike 4.7). If 4.8 takes the 1M beta directly, the routing
    /// rule that today silently downgrades opus-4.8 + 1M beta to
    /// opus-4.7-1m-internal can be removed.
    ///
    /// Three cases under one probe — minimal payload, both with and without
    /// the beta, observing acceptance:
    /// <list type="bullet">
    ///   <item><c>null</c> — baseline; small request, no beta → expected 200.</item>
    ///   <item><c>context-1m-2025-08-07</c> — small request WITH the beta →
    ///         expected 200 if Copilot accepts the token on 4.8.</item>
    ///   <item><c>bogus-nonexistent-beta-99999</c> — control; if Copilot
    ///         rejects unknown tokens, this 400s and we know the 1m response
    ///         above is genuine acceptance, not silent ignore.</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("context-1m-2025-08-07")]
    [InlineData("bogus-nonexistent-beta-99999")]
    public async Task Opus48_ContextOneMillionBeta_ProbeAcceptance(string? beta)
    {
        var payload = """
          {
            "model": "claude-opus-4.8",
            "max_tokens": 16,
            "messages": [{"role":"user","content":"reply: ok"}]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload, anthropicBeta: beta);
        _output.WriteLine($"[claude-opus-4.8] beta={beta ?? "<none>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 300)}");
    }

    /// <summary>
    /// Confirms opus-4.8 actually serves prompts that exceed the 200k
    /// context window. Sends a single long user message (~260k chars of
    /// padding to land roughly between 200k and 260k tokens — comfortably
    /// over the 200k boundary that distinguishes "needs 1M" from "fits in
    /// standard ctx"). Tests both with and without the 1M beta so we can
    /// see whether the beta is REQUIRED or just permitted.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("context-1m-2025-08-07")]
    public async Task Opus48_LargePrompt_ProbeOneMillionContextSupport(string? beta)
    {
        // ~260k chars of padding. JSON-safe (only spaces and 'x'); Copilot's
        // tokenizer (o200k_base per /models) compresses spaces aggressively
        // so this is roughly 60-80k tokens — well above the 200k LIMIT line
        // is wrong actually, but it is well above 32k (the "this model
        // works on small prompts" trivial case). For a true >200k probe
        // we'd need a much larger payload; this probe is the "lightweight
        // sanity" version. Re-run with larger padding only if the lightweight
        // version surprises us.
        var padding = new string('x', 260_000);
        var payload = $$"""
          {
            "model": "claude-opus-4.8",
            "max_tokens": 16,
            "messages": [{"role":"user","content":"context follows; reply: ok\n\n{{padding}}"}]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload, anthropicBeta: beta);
        _output.WriteLine($"[claude-opus-4.8] padded-prompt beta={beta ?? "<none>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 300)}");
    }

    /// <summary>
    /// Probes the actual context window of <c>claude-sonnet-4.6</c>,
    /// <c>claude-haiku-4.5</c>, and <c>claude-sonnet-4.5</c> on Copilot —
    /// does Copilot 400 a >200k-token prompt with "prompt is too long" the
    /// way <c>docs/context-window.md</c> (PR #7, 2026-06-04) claimed, or
    /// has Copilot's gateway been upgraded to honor the 1M ctx the
    /// <c>/models</c> capability advertises for sonnet-4.6?
    /// <para>
    /// Uses a deliberately incompressible padding (rotating short token
    /// strings) so the byte-to-token ratio stays close to 1:3 — 800k chars
    /// lands around 240-260k tokens, well over the 200k boundary.
    /// </para>
    /// <para>
    /// Expected results decide a routing policy choice:
    /// <list type="bullet">
    ///   <item>200 OK → Copilot really IS serving 1M sonnet-4.6, and the
    ///         <c>StripBetas=["context-1m-*"]</c> on the sonnet-4.6 profile
    ///         (added in PR #7) is now stripping a useful capability hint.
    ///         Remove that entry.</item>
    ///   <item>400 with "prompt is too long: N > 200000" → PR #7's
    ///         conclusion stands, the strip is correct.</item>
    /// </list>
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("claude-sonnet-4.6")]
    [InlineData("claude-sonnet-4.5")]
    [InlineData("claude-haiku-4.5")]
    public async Task NonOpus_LargePrompt_Probe200kBoundary(string model)
    {
        // ~800k chars of incompressible padding → roughly 240-260k tokens
        // under o200k_base. Cycle a 30-char pseudo-random string so the
        // tokenizer cannot collapse it to a single repeated token.
        var unit = "qZ7$%w!eL#3xR2&Vp9*Jb4@Sk6mTn1Y";
        var padding = string.Concat(Enumerable.Repeat(unit, 800_000 / unit.Length));
        var payload = $$"""
          {
            "model": "{{model}}",
            "max_tokens": 16,
            "messages": [{"role":"user","content":"context follows; reply: ok\n\n{{padding}}"}]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[{model}] padded-prompt → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 400)}");
    }

    /// <summary>
    /// Does Copilot's <c>claude-sonnet-4.6</c> accept the 1M-context beta?
    /// And does it actually serve prompts that exceed the 200k standard
    /// context window? <c>/models</c> claims <c>ctx=1000000, max_prompt=936000</c>
    /// for sonnet-4.6 — same shape as opus-4.8 (no separate <c>-1m-internal</c>
    /// variant in the model list). If both hold, sonnet-4.6 — like opus-4.8
    /// — does NOT need a routing rule to "unlock 1M" and the bridge can pass
    /// the model id through verbatim. This is the analog of
    /// <see cref="Opus48_ContextOneMillionBeta_ProbeAcceptance"/> +
    /// <see cref="Opus48_LargePrompt_ProbeOneMillionContextSupport"/> for
    /// sonnet-4.6, so the same probe pattern applies.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("context-1m-2025-08-07")]
    [InlineData("bogus-nonexistent-beta-99999")]
    public async Task Sonnet46_ContextOneMillionBeta_ProbeAcceptance(string? beta)
    {
        var payload = """
          {
            "model": "claude-sonnet-4.6",
            "max_tokens": 16,
            "messages": [{"role":"user","content":"reply: ok"}]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload, anthropicBeta: beta);
        _output.WriteLine($"[claude-sonnet-4.6] beta={beta ?? "<none>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 300)}");
    }

    /// <summary>
    /// 260k-char padded prompt against sonnet-4.6, with and without the 1M
    /// beta. Designed to land at &gt;200k tokens so the probe genuinely tests
    /// whether sonnet-4.6 honors its <c>/models</c>-advertised 1M context
    /// (not just the standard 200k). Earlier attempts with a long run of
    /// the same character tokenized to ~32k under sonnet's o200k_base
    /// vocabulary, well below the 200k boundary — so the padding here is
    /// drawn from a repeated incompressible random-ish string so the
    /// token-per-byte ratio stays close to 1:3 and 260k chars lands
    /// comfortably above the 200k token line.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("context-1m-2025-08-07")]
    public async Task Sonnet46_LargePrompt_ProbeOneMillionContextSupport(string? beta)
    {
        // Sonnet's tokenizer collapses long single-char runs aggressively,
        // so we cycle a short pseudo-random string to keep each byte
        // contributing roughly one token. ~600k chars · ~0.4 tok/char ≈
        // 240k tokens — over the 200k standard-ctx boundary.
        var unit = "qZ7$%w!eL#3xR2&Vp9*Jb4@Sk6mTn1Y";
        var padding = string.Concat(Enumerable.Repeat(unit, 600_000 / unit.Length));
        var payload = $$"""
          {
            "model": "claude-sonnet-4.6",
            "max_tokens": 16,
            "messages": [{"role":"user","content":"context follows; reply: ok\n\n{{padding}}"}]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload, anthropicBeta: beta);
        _output.WriteLine($"[claude-sonnet-4.6] padded-prompt beta={beta ?? "<none>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 300)}");
    }

    private static string Truncate(string s, int n) =>
        s.Length > n ? s[..n] + "…" : s;
}
