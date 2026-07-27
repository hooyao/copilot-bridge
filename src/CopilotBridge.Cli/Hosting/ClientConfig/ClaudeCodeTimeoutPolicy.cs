namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// Derives the Claude Code timeout environment values from the bridge's own
/// upstream inactivity budgets, so the client's watchdogs always outlast the
/// bridge and the bridge stays the component that decides when a stalled turn
/// ends.
/// </summary>
/// <remarks>
/// <para>Pure and dependency-free — no configuration binding, no filesystem, no
/// DI. Both composition roots call it: the <c>config</c> command (to write the
/// values) and the server's startup report (to compare what the client actually
/// stores against what the current budgets would produce). The
/// <c>client-autoconfiguration</c> spec requires the config command's graph to
/// stay isolated from the server startup path, so the shared piece has to be a
/// plain static like this rather than a service.</para>
/// <para>The client-side facts encoded here were measured against Claude Code
/// 2.1.220 by driving the real client against an upstream that goes silent on
/// demand; see <c>docs/timeout-chain.md</c> for the method and the numbers. The
/// key one: <c>CLAUDE_STREAM_IDLE_TIMEOUT_MS</c> is the only knob that lifts
/// BOTH client idle watchdogs, because the client derives its byte-level budget
/// from the event-level value whenever the byte-level key is unset.</para>
/// </remarks>
internal static class ClaudeCodeTimeoutPolicy
{
    /// <summary>
    /// Claude Code's env key governing the streaming idle bound. Setting it
    /// raises both the event-level watchdog and — because the byte-level key is
    /// left unset — the byte-level one that would otherwise apply the 180 s
    /// first-party default.
    /// </summary>
    public const string StreamIdleKey = "CLAUDE_STREAM_IDLE_TIMEOUT_MS";

    /// <summary>
    /// Claude Code's env key governing the whole-request bound. It also bounds
    /// each attempt of the non-streaming recovery request the client issues
    /// after a streaming failure — the path that produces no bytes at all until
    /// the model has finished, so it must outlast the first-byte budget.
    /// </summary>
    public const string RequestTimeoutKey = "API_TIMEOUT_MS";

    /// <summary>
    /// Headroom added to each bridge budget so the client is comfortably the
    /// slower side. Without a margin an equal client value races the bridge and
    /// either could win, defeating the point of the bridge owning the decision.
    /// </summary>
    public const int MarginSeconds = 300;

    /// <summary>
    /// Ceiling Claude Code applies to <see cref="StreamIdleKey"/>. A larger
    /// value is silently reduced by the client, so the bridge clamps here —
    /// otherwise what it writes would not be what takes effect, and the startup
    /// report would compare against a value the client never honored.
    /// </summary>
    public const int StreamIdleMaxMs = 1_800_000;

    /// <summary>
    /// Ceiling the bridge applies to <see cref="RequestTimeoutKey"/>, and the value
    /// it always writes (see <see cref="RequestTimeoutMs"/> for why it is not
    /// derived). 60 minutes: comfortably above any single non-streaming recovery
    /// response, while still leaving a wedged client able to give up eventually.
    /// </summary>
    public const int RequestTimeoutMaxMs = 3_600_000;

    /// <summary>
    /// Effective client stream-idle bound (milliseconds) when
    /// <see cref="StreamIdleKey"/> is absent AND
    /// <c>_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL</c> is set — which the bridge
    /// writes for the 1M context window. Asserting first-party selects Claude
    /// Code's 180 s first-party budget in place of its 300 s default, so absence
    /// is a known-bad state the bridge itself creates, not an unknown one.
    /// </summary>
    public const int AbsentStreamIdleFirstPartyDefaultMs = 180_000;

    /// <summary>
    /// Effective client stream-idle bound (milliseconds) when
    /// <see cref="StreamIdleKey"/> is absent and the request is NOT first-party —
    /// e.g. a hand-managed config that sets only <c>ANTHROPIC_BASE_URL</c>. Claude
    /// Code falls back to its 300 s floor there, so reporting 180 s would name a
    /// bound that does not apply.
    /// </summary>
    public const int AbsentStreamIdleDefaultMs = 300_000;

    /// <summary>
    /// Effective client whole-request bound (milliseconds) when
    /// <see cref="RequestTimeoutKey"/> is absent. Claude Code's non-streaming
    /// fallback uses this same 300 s per attempt.
    /// </summary>
    public const int AbsentRequestTimeoutDefaultMs = 300_000;

    /// <summary>
    /// Value to write for <see cref="StreamIdleKey"/>, derived from the bridge's
    /// stream-idle budget in seconds. A budget of zero or less means the bridge
    /// imposes no bound on that phase; no finite client value can outlast "no
    /// bound", so the clamped maximum is written and the startup report says the
    /// phase is unbounded.
    /// </summary>
    public static int StreamIdleMsFor(int streamIdleBudgetSeconds) =>
        DeriveMs(streamIdleBudgetSeconds, StreamIdleMaxMs);

    /// <summary>
    /// Value to write for <see cref="RequestTimeoutKey"/> — always the maximum this
    /// policy will write, deliberately NOT derived from the budgets.
    /// </summary>
    /// <remarks>
    /// <para><b>No finite value here can satisfy the authority invariant.</b> The
    /// bridge's budgets bound <i>inactivity</i>, so a healthy turn that keeps
    /// emitting has no total duration at all, and even a stalled one can legally
    /// spend (first-byte budget + stream-idle budget + …) before any bridge timer
    /// fires. A wall-clock cap can always be crossed first. Deriving it — from the
    /// first-byte budget, or later from <c>Math.Max</c> of both — only moved the
    /// threshold and kept implying a guarantee that cannot exist: with first-byte
    /// 900 s and stream-idle 600 s, <c>Math.Max</c> wrote 1200 s while the bridge
    /// could legitimately take ~1500 s.</para>
    /// <para>It is still written, because it also bounds each attempt of Claude
    /// Code's non-streaming recovery request, whose client default (300 s) is far
    /// too short for a deep-thinking turn and was half of the original failure.
    /// That path <i>is</i> a bounded single response, so raising the ceiling
    /// genuinely helps. The bridge therefore writes the largest value it will use
    /// and the startup report surfaces it as a <b>residual wall-clock bound</b>
    /// rather than as something that outlasts the bridge.</para>
    /// </remarks>
    public static int RequestTimeoutMs() => RequestTimeoutMaxMs;

    /// <summary>
    /// budget + margin, in milliseconds, clamped to <paramref name="maxMs"/>.
    /// A non-positive budget (the disabled sentinel) yields <paramref name="maxMs"/>.
    /// </summary>
    private static int DeriveMs(int budgetSeconds, int maxMs)
    {
        if (budgetSeconds <= 0)
        {
            return maxMs;
        }

        // long arithmetic: (int.MaxValue + margin) * 1000 overflows an int, and a
        // hand-edited appsettings could legitimately hold a very large budget.
        var ms = ((long)budgetSeconds + MarginSeconds) * 1000L;
        return ms >= maxMs ? maxMs : (int)ms;
    }

    /// <summary>
    /// True when <paramref name="budgetSeconds"/> is so large that no writable
    /// client value can outlast it — the clamp would produce a bound SHORTER than
    /// the bridge's own, so the client aborts first and the bridge's authority
    /// silently inverts.
    /// </summary>
    /// <remarks>
    /// This is a real configuration mistake, not a rounding artifact: the client
    /// caps its idle bound at 30 minutes, so a stream-idle budget beyond that can
    /// never be honored. Re-running <c>config claude-code</c> would deterministically
    /// write the same insufficient value, so the operator has to lower the budget —
    /// which is why the startup report must SAY so rather than emit a fix-it hint
    /// that cannot work.
    /// </remarks>
    public static bool StreamIdleBudgetExceedsClientMaximum(int budgetSeconds) =>
        budgetSeconds > 0 && (long)budgetSeconds * 1000L > StreamIdleMaxMs;
}
