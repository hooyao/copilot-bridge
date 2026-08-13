namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// Versioned Claude Code timeout facts used for read-only startup interpretation.
/// Nothing here is a value the bridge writes into client configuration.
/// </summary>
internal static class ClaudeCodeTimeoutPolicy
{
    public const string VerifiedClientVersion = "2.1.221";
    public const string StreamIdleKey = "CLAUDE_STREAM_IDLE_TIMEOUT_MS";
    public const string ByteIdleKey = "CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS";
    public const string RequestTimeoutKey = "API_TIMEOUT_MS";
    public const string StreamWatchdogKey = "CLAUDE_ENABLE_STREAM_WATCHDOG";
    public const string ByteWatchdogKey = "CLAUDE_ENABLE_BYTE_WATCHDOG";
    public const string RetryKey = "CLAUDE_CODE_MAX_RETRIES";

    // Reconfirmed against the installed Claude Code 2.1.221 executable.
    public const int EventIdleFloorMs = 300_000;
    public const int AbsentByteIdleDefaultMs = 300_000;
    public const int AbsentByteIdleFirstPartyDefaultMs = 180_000;
    public const int ByteIdleMinMs = 10_000;
    public const int ByteIdleMaxMs = 1_800_000;
    public const int AbsentNormalRequestTimeoutMs = 600_000;
    public const int AbsentAfterStreamErrorTimeoutMs = 300_000;

    public static bool HasVerifiedFacts(string? installedVersion) =>
        string.Equals(installedVersion, VerifiedClientVersion, StringComparison.Ordinal);
}
