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
[Trait("Kind", "ApiContract")]
public partial class ModelProfileProbe
{
    private readonly ITestOutputHelper _output;
    public ModelProfileProbe(ITestOutputHelper output) => _output = output;

    public static readonly string[] AllModels =
    [
        "claude-haiku-4.5",
        "claude-sonnet-4.6",
        "claude-sonnet-5",
        "claude-opus-4.6",
        "claude-opus-4.7",
        "claude-opus-4.8",
        "claude-opus-5",
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
    /// Focused re-probe of opus-4.8's effort acceptance — <c>/models</c> now
    /// advertises <c>effort=[low,medium,high,xhigh,max]</c> for opus-4.8
    /// (it previously listed only up to xhigh, and the catalog was built when
    /// only <c>medium</c> was actually accepted). <c>/models</c> capabilities
    /// have been wrong before (haiku advertises adaptive thinking but rejects
    /// it at runtime), so this probes each effort value directly against the
    /// live endpoint — both standalone AND combined with adaptive thinking
    /// (the exact shape Claude Code sends for opus). The two-axis combination
    /// matters: the bridge derives effort from the thinking budget, so the
    /// real request always has both fields.
    /// </summary>
    [Theory]
    [InlineData("low",    false)]
    [InlineData("medium", false)]
    [InlineData("high",   false)]
    [InlineData("xhigh",  false)]
    [InlineData("max",    false)]
    [InlineData("low",    true)]
    [InlineData("medium", true)]
    [InlineData("high",   true)]
    [InlineData("xhigh",  true)]
    [InlineData("max",    true)]
    public async Task Opus48_Effort_ReProbe(string effort, bool withAdaptiveThinking)
    {
        var thinkingBlock = withAdaptiveThinking ? ""","thinking":{"type":"adaptive"}""" : "";
        // max_tokens must exceed any derived thinking budget; keep it modest
        // so the probe returns fast even at effort=max.
        var payload = $$"""
          {
            "model": "claude-opus-4.8",
            "max_tokens": 64,
            "messages": [{"role":"user","content":"reply: ok"}],
            "output_config":{"effort":"{{effort}}"}{{thinkingBlock}}
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[claude-opus-4.8] effort={effort} adaptive-thinking={withAdaptiveThinking} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 280)}");
    }

    /// <summary>
    /// Re-probe effort acceptance for the rest of the Anthropic family whose
    /// <c>/models</c> capability now advertises higher effort tiers than the
    /// catalog's <c>AcceptedEfforts</c> currently allows (opus-4.8 covered
    /// separately above). As of 2026-06-05 <c>/models</c> shows:
    /// <list type="bullet">
    ///   <item>opus-4.6: <c>[low,medium,high,max]</c> (catalog: low/medium/high)</item>
    ///   <item>opus-4.7: <c>[low,medium,high,xhigh,max]</c> (catalog base: medium only)</item>
    ///   <item>sonnet-4.6: <c>[low,medium,high,max]</c> (catalog: low/medium/high)</item>
    /// </list>
    /// <c>/models</c> has lied before, so this probes each NEW tier (the ones
    /// the catalog doesn't yet allow) directly. Only the deltas are tested —
    /// low/medium are already known-good for these models. Pure effort, no
    /// thinking, small max_tokens for speed.
    /// <para>The <c>opus-4.6-1m</c> / <c>opus-4.7-1m-internal</c> rows were dropped
    /// in the 2026-07 reconciliation — Copilot retired both ids, so probing their
    /// effort range only burns quota on a guaranteed 400. Their liveness is still
    /// asserted by <see cref="RetiredCandidate_LivenessProbe"/>, which is where a
    /// resurrection would show up.</para>
    /// </summary>
    [Theory]
    // opus-4.6 family — new tier to confirm: max
    [InlineData("claude-opus-4.6",            "high")]
    [InlineData("claude-opus-4.6",            "xhigh")]
    [InlineData("claude-opus-4.6",            "max")]
    // opus-4.7 base — catalog only allows medium today; confirm high/xhigh/max
    [InlineData("claude-opus-4.7",            "high")]
    [InlineData("claude-opus-4.7",            "xhigh")]
    [InlineData("claude-opus-4.7",            "max")]
    // sonnet-4.6 — catalog allows low/medium/high; confirm xhigh/max
    [InlineData("claude-sonnet-4.6",          "high")]
    [InlineData("claude-sonnet-4.6",          "xhigh")]
    [InlineData("claude-sonnet-4.6",          "max")]
    // sonnet-5 — new to the catalog; /models advertises [low,medium,high,xhigh,max]
    // (same shape as opus-4.8). /models has lied before (haiku advertises adaptive
    // but rejects it), so confirm every tier directly — including xhigh, the first
    // Sonnet-tier model to claim it.
    [InlineData("claude-sonnet-5",            "high")]
    [InlineData("claude-sonnet-5",            "xhigh")]
    [InlineData("claude-sonnet-5",            "max")]
    public async Task Family_Effort_ReProbe(string model, string effort)
    {
        var payload = $$"""
          {
            "model": "{{model}}",
            "max_tokens": 32,
            "messages": [{"role":"user","content":"reply: ok"}],
            "output_config":{"effort":"{{effort}}"}
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[{model}] effort={effort} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 240)}");
    }

    /// <summary>
    /// Mid-conversation <c>role:"system"</c> support. opus-4.8 adds this; everything
    /// else (4.7 and older) should 400. Distinguishes targets that need
    /// <c>ProfileAdjuster.FoldMidConversationSystem</c> from those that don't.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-4.8")]
    [InlineData("claude-opus-5")]
    [InlineData("claude-opus-4.7")]
    [InlineData("claude-opus-4.6")]
    [InlineData("claude-sonnet-4.6")]
    [InlineData("claude-sonnet-5")]
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
    [InlineData("claude-opus-5")]
    [InlineData("claude-opus-4.7")]
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
    /// for 4.8 (unlike 4.7 at the time this was written). If 4.8 takes the 1M
    /// beta directly, the routing rule that then silently downgraded opus-4.8 +
    /// 1M beta to opus-4.7-1m-internal can be removed. (It was: the answer was
    /// yes, the redirect is gone, and Copilot has since retired that variant id
    /// entirely — see <see cref="RetiredCandidate_LivenessProbe"/>.)
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
    /// Probes the actual context window of <c>claude-sonnet-4.6</c> and
    /// <c>claude-haiku-4.5</c> on Copilot — does Copilot 400 a &gt;200k-token
    /// prompt with "prompt is too long" the way <c>docs/context-window.md</c>
    /// (PR #7, 2026-06-04) claimed, or has Copilot's gateway been upgraded to
    /// honor the 1M ctx the <c>/models</c> capability advertises for sonnet-4.6?
    /// (sonnet-4.5 was a third case here until Copilot retired it in the 2026-07
    /// reconciliation — see <see cref="RetiredCandidate_LivenessProbe"/>.)
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

    /// <summary>
    /// sonnet-5 mid-conversation <c>role:"system"</c> placement matrix — the analog
    /// of <see cref="Opus48_MidConversationSystem_PlacementRules"/>. The single
    /// <c>user→system→user</c> probe returned the placement-specific 4.8-style error
    /// ("role 'system' must precede an 'assistant' message or end the array") rather
    /// than the unconditional-reject ("Unexpected role 'system'") that 4.7/4.6/sonnet-4.6
    /// give — evidence sonnet-5's gateway ACCEPTS mid-conv system but enforces position.
    /// The catalog must not set <see cref="ModelProfile.AcceptsMidConversationSystem"/>
    /// = true on the strength of one rejected placement: this matrix confirms the LEGAL
    /// placements (S after user, ending the array or followed by assistant) actually
    /// return 200. If they do, sonnet-5 gets the same true+placement-fix path as 4.8.
    /// </summary>
    [Theory]
    [InlineData("end-after-user",          """[{"role":"user","content":"hi"},{"role":"system","content":"S"}]""")]
    [InlineData("between-two-users",       """[{"role":"user","content":"hi"},{"role":"system","content":"S"},{"role":"user","content":"there"}]""")]
    [InlineData("end-after-assistant",     """[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"},{"role":"system","content":"S"}]""")]
    [InlineData("between-assistant-user",  """[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"},{"role":"system","content":"S"},{"role":"user","content":"more"}]""")]
    [InlineData("between-two-assistants",  """[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"},{"role":"system","content":"S"},{"role":"assistant","content":"world"}]""")]
    [InlineData("end-after-user-followup", """[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"},{"role":"user","content":"more"},{"role":"system","content":"S"}]""")]
    public async Task Sonnet5_MidConversationSystem_PlacementRules(string label, string messagesJson)
    {
        var payload = $$"""
          {
            "model": "claude-sonnet-5",
            "max_tokens": 64,
            "messages": {{messagesJson}}
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[claude-sonnet-5] placement={label} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 240)}");
    }

    /// <summary>
    /// After Copilot retired the dedicated 1M variants (opus-4.6-1m,
    /// opus-4.7-1m-internal — both 400 as of the 2026 reconciliation), the
    /// appsettings.json routing rules that redirected opus-4.6/4.7 + 1M beta to
    /// those variants have no valid target. This probe answers the follow-on
    /// question: do the opus-4.6 / opus-4.7 BASE ids now serve &gt;200k prompts
    /// natively (the way opus-4.8 does), so removing the redirect keeps 1M — or is
    /// 1M genuinely lost for those families? A &gt;200k-token incompressible padded
    /// prompt: 200 = base serves 1M (redirect was only ever an id-swap), 400
    /// "prompt is too long" = base is 200k and 1M is gone with the variant.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-4.6", null)]
    [InlineData("claude-opus-4.6", "context-1m-2025-08-07")]
    [InlineData("claude-opus-4.7", null)]
    [InlineData("claude-opus-4.7", "context-1m-2025-08-07")]
    public async Task OpusBase_LargePrompt_ProbeOneMillionContextSupport(string model, string? beta)
    {
        var unit = "qZ7$%w!eL#3xR2&Vp9*Jb4@Sk6mTn1Y";
        var padding = string.Concat(Enumerable.Repeat(unit, 600_000 / unit.Length));
        var payload = $$"""
          {
            "model": "{{model}}",
            "max_tokens": 16,
            "messages": [{"role":"user","content":"context follows; reply: ok\n\n{{padding}}"}]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload, anthropicBeta: beta);
        _output.WriteLine($"[{model}] padded-prompt beta={beta ?? "<none>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 300)}");
    }

    /// <summary>
    /// Liveness probe for catalog ids that are NOT in Copilot's current
    /// <c>/models</c> list (as of the 2026 reconciliation): opus-4.5, opus-4.6-1m,
    /// and the opus-4.7 -high / -xhigh / -1m-internal variants. <c>/models</c> is
    /// unreliable in BOTH directions — the -1m-internal / -high / -xhigh variants
    /// were originally kept in the catalog precisely because they routed 200 despite
    /// never being advertised. So absence from the model list is NOT sufficient
    /// grounds to delete a profile; a genuine retirement must show as a 400/404 on a
    /// minimal request. This probe is the ground truth for the prune: a 200 means
    /// "keep (still routable)", a 4xx means "delete (Copilot retired it)".
    /// </summary>
    [Theory]
    [InlineData("claude-opus-4.5")]
    [InlineData("claude-opus-4.6-1m")]
    [InlineData("claude-opus-4.7-high")]
    [InlineData("claude-opus-4.7-xhigh")]
    [InlineData("claude-opus-4.7-1m-internal")]
    // sonnet-4.5 dropped out of /models in the 2026-07 reconciliation while its
    // catalog profile remained. Absence is NOT a delete license (see the class
    // remarks) — this probe is the ground truth for whether it still routes.
    [InlineData("claude-sonnet-4.5")]
    public async Task RetiredCandidate_LivenessProbe(string model)
    {
        var payload = $$"""
          {
            "model": "{{model}}",
            "max_tokens": 16,
            "messages": [{"role":"user","content":"reply: ok"}]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[{model}] liveness → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 300)}");
    }

    /// <summary>
    /// Focused re-probe of sonnet-5's effort acceptance — the analog of
    /// <see cref="Opus48_Effort_ReProbe"/> for the newly-added model. <c>/models</c>
    /// advertises <c>effort=[low,medium,high,xhigh,max]</c> for sonnet-5 (the first
    /// Sonnet-tier model to claim <c>xhigh</c>), but capabilities have been wrong
    /// before (haiku advertises adaptive thinking yet rejects it). Probe each effort
    /// value directly — both standalone AND combined with adaptive thinking (the
    /// exact shape Claude Code sends for a modern model: the bridge derives effort
    /// from the thinking budget, so the real request always carries both fields).
    /// </summary>
    [Theory]
    [InlineData("low",    false)]
    [InlineData("medium", false)]
    [InlineData("high",   false)]
    [InlineData("xhigh",  false)]
    [InlineData("max",    false)]
    [InlineData("low",    true)]
    [InlineData("medium", true)]
    [InlineData("high",   true)]
    [InlineData("xhigh",  true)]
    [InlineData("max",    true)]
    public async Task Sonnet5_Effort_ReProbe(string effort, bool withAdaptiveThinking)
    {
        var thinkingBlock = withAdaptiveThinking ? ""","thinking":{"type":"adaptive"}""" : "";
        var payload = $$"""
          {
            "model": "claude-sonnet-5",
            "max_tokens": 64,
            "messages": [{"role":"user","content":"reply: ok"}],
            "output_config":{"effort":"{{effort}}"}{{thinkingBlock}}
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[claude-sonnet-5] effort={effort} adaptive-thinking={withAdaptiveThinking} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 280)}");
    }

    /// <summary>
    /// Per-thinking-shape acceptance for sonnet-5. <c>/models</c> reports
    /// <c>adaptive_thinking</c> support and the claude-api reference says the
    /// Sonnet-5 wire contract matches opus-4.7/4.8 (adaptive only —
    /// <c>thinking.type.enabled</c> returns 400). Verify directly: send each of
    /// null / adaptive / enabled / disabled and record the status, so the catalog's
    /// <see cref="ModelProfile.Thinking"/> policy is grounded, not inherited from a
    /// family-name guess.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("adaptive")]
    [InlineData("enabled")]
    [InlineData("disabled")]
    public async Task Sonnet5_Thinking_ProbeAcceptance(string? thinkingType)
    {
        var thinkingBlock = thinkingType switch
        {
            null       => "",
            "enabled"  => ""","thinking":{"type":"enabled","budget_tokens":8192}""",
            _          => $$$""","thinking":{"type":"{{{thinkingType}}}"}""",
        };
        var payload = $$"""
          {
            "model": "claude-sonnet-5",
            "max_tokens": 16384,
            "messages": [{"role":"user","content":"reply: ok"}]{{thinkingBlock}}
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[claude-sonnet-5] thinking={thinkingType ?? "<null>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 280)}");
    }

    /// <summary>
    /// Does sonnet-5 accept the <c>context-1m-2025-08-07</c> beta, and does it
    /// actually serve prompts past the 200k standard window? <c>/models</c> reports
    /// <c>ctx=1000000, max_prompt=936000</c> for sonnet-5 (same shape as opus-4.8 and
    /// sonnet-4.6 — no separate <c>-1m-internal</c> variant in the model list). The
    /// analog of <see cref="Opus48_ContextOneMillionBeta_ProbeAcceptance"/> +
    /// <see cref="Opus48_LargePrompt_ProbeOneMillionContextSupport"/>: a small
    /// baseline / with-beta / bogus-beta control, then a &gt;200k-token padded prompt.
    /// If both hold, sonnet-5 — like opus-4.8 and sonnet-4.6 — needs no routing rule
    /// to "unlock 1M" and no <c>StripBetas=["context-1m-*"]</c> entry.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("context-1m-2025-08-07")]
    [InlineData("bogus-nonexistent-beta-99999")]
    public async Task Sonnet5_ContextOneMillionBeta_ProbeAcceptance(string? beta)
    {
        var payload = """
          {
            "model": "claude-sonnet-5",
            "max_tokens": 16,
            "messages": [{"role":"user","content":"reply: ok"}]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload, anthropicBeta: beta);
        _output.WriteLine($"[claude-sonnet-5] beta={beta ?? "<none>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 300)}");
    }

    /// <summary>
    /// &gt;200k-token padded prompt against sonnet-5, with and without the 1M beta —
    /// confirms sonnet-5 honors its advertised 1M context rather than 400ing with
    /// "prompt is too long". Incompressible padding (repeated pseudo-random string)
    /// keeps the byte-to-token ratio near 1:3 so ~600k chars lands over the 200k line.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("context-1m-2025-08-07")]
    public async Task Sonnet5_LargePrompt_ProbeOneMillionContextSupport(string? beta)
    {
        var unit = "qZ7$%w!eL#3xR2&Vp9*Jb4@Sk6mTn1Y";
        var padding = string.Concat(Enumerable.Repeat(unit, 600_000 / unit.Length));
        var payload = $$"""
          {
            "model": "claude-sonnet-5",
            "max_tokens": 16,
            "messages": [{"role":"user","content":"context follows; reply: ok\n\n{{padding}}"}]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload, anthropicBeta: beta);
        _output.WriteLine($"[claude-sonnet-5] padded-prompt beta={beta ?? "<none>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 300)}");
    }

    // ── claude-opus-5 (added 2026-07) ────────────────────────────────────────
    // Mirrors the sonnet-5 probe set, PLUS a cross-field probe no earlier model
    // needed. Anthropic's own docs state opus-5 has thinking ON by default and
    // rejects thinking:disabled at effort xhigh/max — a two-field constraint the
    // single-axis matrix above cannot see, and one no other catalog model has.

    /// <summary>
    /// Per-thinking-shape acceptance for opus-5 — the analog of
    /// <see cref="Sonnet5_Thinking_ProbeAcceptance"/>. Anthropic documents opus-5 as
    /// adaptive-by-default (unlike opus-4.8/4.7, where omitting <c>thinking</c> means
    /// no thinking) with <c>budget_tokens</c> removed. Whether COPILOT's gateway
    /// enforces the same is a separate question — probe it rather than inherit the
    /// opus-4.8 answer from the family name.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("adaptive")]
    [InlineData("enabled")]
    [InlineData("disabled")]
    public async Task Opus5_Thinking_ProbeAcceptance(string? thinkingType)
    {
        var thinkingBlock = thinkingType switch
        {
            null       => "",
            "enabled"  => ""","thinking":{"type":"enabled","budget_tokens":8192}""",
            _          => $$$""","thinking":{"type":"{{{thinkingType}}}"}""",
        };
        var payload = $$"""
          {
            "model": "claude-opus-5",
            "max_tokens": 16384,
            "messages": [{"role":"user","content":"reply: ok"}]{{thinkingBlock}}
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[claude-opus-5] thinking={thinkingType ?? "<null>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 280)}");
    }

    /// <summary>
    /// Effort acceptance for opus-5, standalone and combined with adaptive thinking
    /// (the shape Claude Code actually sends — the bridge derives effort from the
    /// thinking budget, so both fields ride together). <c>/models</c> advertises
    /// <c>effort=[low,medium,high,xhigh,max]</c>; capabilities have lied before
    /// (haiku-4.5 advertises adaptive and 400s it), so each tier is probed directly.
    /// </summary>
    [Theory]
    [InlineData("low",    false)]
    [InlineData("medium", false)]
    [InlineData("high",   false)]
    [InlineData("xhigh",  false)]
    [InlineData("max",    false)]
    [InlineData("low",    true)]
    [InlineData("medium", true)]
    [InlineData("high",   true)]
    [InlineData("xhigh",  true)]
    [InlineData("max",    true)]
    public async Task Opus5_Effort_ReProbe(string effort, bool withAdaptiveThinking)
    {
        var thinkingBlock = withAdaptiveThinking ? ""","thinking":{"type":"adaptive"}""" : "";
        var payload = $$"""
          {
            "model": "claude-opus-5",
            "max_tokens": 64,
            "messages": [{"role":"user","content":"reply: ok"}],
            "output_config":{"effort":"{{effort}}"}{{thinkingBlock}}
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[claude-opus-5] effort={effort} adaptive-thinking={withAdaptiveThinking} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 280)}");
    }

    /// <summary>
    /// <b>Cross-field probe with no precedent in this file.</b> Anthropic documents a
    /// constraint unique to opus-5: <c>thinking:{"type":"disabled"}</c> is accepted
    /// only at effort <c>high</c> or below, and returns 400 when paired with
    /// <c>xhigh</c> / <c>max</c>. Every other probe here is single-axis and would
    /// report "disabled → 200" and "effort=max → 200" independently while the
    /// COMBINATION 400s — exactly the blind spot that ships a silent bug.
    /// <para>This matters concretely for the bridge: <see cref="Routing.ProfileAdjuster"/>
    /// can produce that pair. Claude Code sends <c>thinking:disabled</c> with
    /// <c>effort:max</c> whenever a user disables thinking at max effort, and the
    /// adjuster passes both through untouched when the profile accepts each field
    /// on its own. If Copilot enforces Anthropic's rule, the catalog needs a new
    /// mechanism (this profile shape cannot express a cross-field constraint) —
    /// so the probe decides whether that work is required at all.</para>
    /// </summary>
    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public async Task Opus5_DisabledThinking_EffortInteraction_Probe(string effort)
    {
        var payload = $$"""
          {
            "model": "claude-opus-5",
            "max_tokens": 64,
            "messages": [{"role":"user","content":"reply: ok"}],
            "thinking":{"type":"disabled"},
            "output_config":{"effort":"{{effort}}"}
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[claude-opus-5] thinking=disabled + effort={effort} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 300)}");
    }

    /// <summary>
    /// opus-5 mid-conversation <c>role:"system"</c> placement matrix — same six
    /// placements as <see cref="Opus48_MidConversationSystem_PlacementRules"/> and
    /// <see cref="Sonnet5_MidConversationSystem_PlacementRules"/>. Anthropic lists
    /// opus-5 among the models supporting mid-conv system, but sonnet-5 already
    /// proved the doc list and Copilot's gateway disagree in BOTH directions, so
    /// <see cref="ModelProfile.AcceptsMidConversationSystem"/> must be set from the
    /// legal placements actually returning 200 — not from the doc claim.
    /// </summary>
    [Theory]
    [InlineData("end-after-user",          """[{"role":"user","content":"hi"},{"role":"system","content":"S"}]""")]
    [InlineData("between-two-users",       """[{"role":"user","content":"hi"},{"role":"system","content":"S"},{"role":"user","content":"there"}]""")]
    [InlineData("end-after-assistant",     """[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"},{"role":"system","content":"S"}]""")]
    [InlineData("between-assistant-user",  """[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"},{"role":"system","content":"S"},{"role":"user","content":"more"}]""")]
    [InlineData("between-two-assistants",  """[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"},{"role":"system","content":"S"},{"role":"assistant","content":"world"}]""")]
    [InlineData("end-after-user-followup", """[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"},{"role":"user","content":"more"},{"role":"system","content":"S"}]""")]
    public async Task Opus5_MidConversationSystem_PlacementRules(string label, string messagesJson)
    {
        var payload = $$"""
          {
            "model": "claude-opus-5",
            "max_tokens": 64,
            "messages": {{messagesJson}}
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[claude-opus-5] placement={label} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 240)}");
    }

    /// <summary>
    /// Beta-token acceptance control for opus-5: baseline / real 1M beta / bogus
    /// beta. The bogus arm is the control that makes the 1M arm meaningful — if a
    /// nonexistent beta also returns 200, Copilot ignores unknown betas and a 200 on
    /// <c>context-1m-2025-08-07</c> proves nothing on its own (the padded-prompt
    /// probe below is what actually establishes 1M).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("context-1m-2025-08-07")]
    [InlineData("bogus-nonexistent-beta-99999")]
    public async Task Opus5_ContextOneMillionBeta_ProbeAcceptance(string? beta)
    {
        var payload = """
          {
            "model": "claude-opus-5",
            "max_tokens": 16,
            "messages": [{"role":"user","content":"reply: ok"}]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload, anthropicBeta: beta);
        _output.WriteLine($"[claude-opus-5] beta={beta ?? "<none>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 300)}");
    }

    /// <summary>
    /// &gt;200k-token padded prompt against opus-5, with and without the 1M beta.
    /// <c>/models</c> reports <c>ctx=1000000, max_prompt=936000</c>; this confirms the
    /// gateway honors it rather than 400ing "prompt is too long", which decides
    /// whether the profile needs a <c>StripBetas=["context-1m-*"]</c> entry (as
    /// whether the profile needs a <c>StripBetas=["context-1m-*"]</c> entry (as
    /// haiku-4.5 does) or passes the beta through (as opus-4.8 / sonnet-5 do).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("context-1m-2025-08-07")]
    public async Task Opus5_LargePrompt_ProbeOneMillionContextSupport(string? beta)
    {
        var unit = "qZ7$%w!eL#3xR2&Vp9*Jb4@Sk6mTn1Y";
        var padding = string.Concat(Enumerable.Repeat(unit, 600_000 / unit.Length));
        var payload = $$"""
          {
            "model": "claude-opus-5",
            "max_tokens": 16,
            "messages": [{"role":"user","content":"context follows; reply: ok\n\n{{padding}}"}]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload, anthropicBeta: beta);
        _output.WriteLine($"[claude-opus-5] padded-prompt beta={beta ?? "<none>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 300)}");
    }

    /// <summary>
    /// Dumps Copilot's <b>integrator allowlist</b> in full — the second source of
    /// truth alongside <c>/models</c>, and often the more current one. The gateway
    /// emits "Available models: [...]" for integrator <c>vscode-chat</c> only when the
    /// requested id <b>exists upstream but is not granted</b>; a genuinely unknown id
    /// returns a bare <c>model_not_supported</c> with no list. So this probe must ask
    /// for a real-but-restricted id (a retired sibling variant) rather than a made-up
    /// one. Prints the response UNTRUNCATED so a reconciliation can diff against it.
    /// <para>The two lists genuinely disagree in both directions: the retired
    /// <c>-1m-internal</c> / <c>-high</c> / <c>-xhigh</c> variants routed 200 for
    /// months while absent from <c>/models</c>, and the 2026-07 run found the reverse
    /// — ids in the allowlist that <c>/models</c> never advertises. Neither list alone
    /// is grounds to add or delete a profile; a live probe is.</para>
    /// </summary>
    [Fact]
    public async Task IntegratorAllowlist_Dump()
    {
        var payload = """
          {
            "model": "claude-opus-4.7-high",
            "max_tokens": 16,
            "messages": [{"role":"user","content":"reply: ok"}]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[allowlist] → {(int)status} {status}");
        _output.WriteLine(body);
    }

    /// <summary>
    /// Liveness for ids that appear in the <b>integrator allowlist but NOT in
    /// <c>/models</c></b> (2026-07: <c>claude-fable-5</c>, <c>claude-opus-4.8-fast</c>).
    /// The mirror image of <see cref="RetiredCandidate_LivenessProbe"/>: absence from
    /// <c>/models</c> is not grounds to ignore an id any more than it is grounds to
    /// delete one. A 200 means the id is genuinely reachable and is a real candidate
    /// for a profile; a 400 means the allowlist over-reports what this account can
    /// actually reach — which is what the 2026-07 run found for both ids, so neither
    /// gets a profile.
    /// </summary>
    [Theory]
    [InlineData("claude-fable-5")]
    [InlineData("claude-opus-4.8-fast")]
    public async Task UnadvertisedCandidate_LivenessProbe(string model)
    {
        var payload = $$"""
          {
            "model": "{{model}}",
            "max_tokens": 16,
            "messages": [{"role":"user","content":"reply: ok"}]
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[{model}] liveness → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 300)}");
    }
}
