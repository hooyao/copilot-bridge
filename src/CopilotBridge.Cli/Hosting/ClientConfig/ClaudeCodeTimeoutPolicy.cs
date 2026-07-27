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
    /// Ceiling the bridge applies to <see cref="RequestTimeoutKey"/>. The client
    /// imposes no documented cap of its own; this bounds an unbounded-budget
    /// derivation to a finite, writable number.
    /// </summary>
    public const int RequestTimeoutMaxMs = 3_600_000;

    /// <summary>
    /// Effective client stream-idle bound (milliseconds) when
    /// <see cref="StreamIdleKey"/> is absent, given the bridge also writes
    /// <c>_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL=1</c> for the 1M context
    /// window. Asserting first-party selects the client's 180 s first-party
    /// budget in place of its 300 s default — so absence is a known-bad state
    /// the bridge itself creates, not an unknown one.
    /// </summary>
    public const int AbsentStreamIdleDefaultMs = 180_000;

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
    /// Value to write for <see cref="RequestTimeoutKey"/>, derived from the
    /// bridge's first-byte budget in seconds. Same disabled-budget rule as
    /// <see cref="StreamIdleMsFor"/>.
    /// </summary>
    public static int RequestTimeoutMsFor(int firstByteBudgetSeconds) =>
        DeriveMs(firstByteBudgetSeconds, RequestTimeoutMaxMs);

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
}
