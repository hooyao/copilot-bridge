using System.Text.Json.Serialization;

namespace CopilotBridge.Update.Wire;

/// <summary>
/// The frozen, versioned wire protocol shared by the bridge CLI and the
/// <c>copilot-updater</c> executable. These types are compiled into BOTH
/// executables from this one source folder (no third runtime assembly). Every
/// JSON property name is pinned with <see cref="JsonPropertyNameAttribute"/> so
/// the bytes are independent of either application's global serializer naming
/// policy — an unrelated change to the bridge's <c>SnakeCaseLower</c> context can
/// never shift this wire shape. Wire-facing roles/kinds/phases are validated
/// stable string literals (see <see cref="UpdateWire"/>), never CLR enum names.
/// </summary>
/// <remarks>
/// Compatibility is cross-release: updater version N launches bridge N+1, so
/// this contract must stay readable/writable across that boundary. Bump
/// <see cref="ProtocolVersion"/> only with explicit negotiation or retained
/// previous-protocol support — never by silently renaming a property.
/// </remarks>
internal static class UpdateWire
{
    /// <summary>Current wire protocol version. Frozen at 1 for this change.</summary>
    public const int ProtocolVersion = 1;

    // Archive kinds (asset format).
    public const string ArchiveZip = "zip";
    public const string ArchiveTarGz = "tar.gz";

    // Readiness roles — a capability is minted per role and never reused.
    public const string RoleTarget = "target";     // the freshly installed candidate
    public const string RoleRollback = "rollback";  // the restored old bridge

    // Control-message kinds on the parent/updater handoff channel.
    public const string MsgPrepared = "prepared";
    public const string MsgCutoverAuthorized = "cutover_authorized";
    public const string MsgPreflightFailed = "preflight_failed";

    // Readiness-message kind.
    public const string MsgReady = "ready";
}

/// <summary>
/// The immutable, allowlisted plan the CLI hands the updater. Contains every
/// execution fact the updater needs and NO product policy (no release discovery,
/// channel choice, or asset selection) and NO secret (no GitHub OAuth token, no
/// Copilot bearer token, no full configuration contents). The only capability in
/// the plan is the parent/updater handoff token; target-Ready and rollback-Ready
/// capabilities are minted fresh by the updater and are never serialized here.
/// </summary>
internal sealed record UpdatePlan
{
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; init; } = UpdateWire.ProtocolVersion;

    [JsonPropertyName("attempt_id")]
    public required string AttemptId { get; init; }

    // --- Parent identity (verified before cutover; never kill by name) --------
    [JsonPropertyName("parent_pid")]
    public required int ParentPid { get; init; }

    [JsonPropertyName("parent_start_ticks")]
    public required long ParentStartTicks { get; init; }

    // --- Paths (all validated inside the canonical install root) --------------
    [JsonPropertyName("install_dir")]
    public required string InstallDir { get; init; }

    [JsonPropertyName("bridge_exe_path")]
    public required string BridgeExePath { get; init; }

    [JsonPropertyName("updater_exe_path")]
    public required string UpdaterExePath { get; init; }

    [JsonPropertyName("config_path")]
    public required string ConfigPath { get; init; }

    // --- Versions --------------------------------------------------------------
    [JsonPropertyName("current_version")]
    public required string CurrentVersion { get; init; }

    [JsonPropertyName("target_version")]
    public required string TargetVersion { get; init; }

    // --- Selected asset (already chosen by the CLI) ---------------------------
    [JsonPropertyName("asset_name")]
    public required string AssetName { get; init; }

    [JsonPropertyName("asset_url")]
    public required string AssetUrl { get; init; }

    [JsonPropertyName("asset_size")]
    public required long AssetSize { get; init; }

    [JsonPropertyName("asset_sha256")]
    public required string AssetSha256 { get; init; }

    [JsonPropertyName("archive_kind")]
    public required string ArchiveKind { get; init; }

    // --- Private per-attempt working directories ------------------------------
    [JsonPropertyName("staging_dir")]
    public required string StagingDir { get; init; }

    [JsonPropertyName("backup_dir")]
    public required string BackupDir { get; init; }

    [JsonPropertyName("archive_path")]
    public required string ArchivePath { get; init; }

    [JsonPropertyName("journal_path")]
    public required string JournalPath { get; init; }

    // --- Managed file allowlist (relative names inside install_dir) -----------
    [JsonPropertyName("managed_files")]
    public required List<string> ManagedFiles { get; init; }

    // --- Restart context -------------------------------------------------------
    [JsonPropertyName("original_args")]
    public required List<string> OriginalArgs { get; init; }

    [JsonPropertyName("working_directory")]
    public required string WorkingDirectory { get; init; }

    // --- Finite timeouts (milliseconds) ---------------------------------------
    [JsonPropertyName("download_timeout_ms")]
    public required int DownloadTimeoutMs { get; init; }

    [JsonPropertyName("parent_exit_timeout_ms")]
    public required int ParentExitTimeoutMs { get; init; }

    [JsonPropertyName("ready_timeout_ms")]
    public required int ReadyTimeoutMs { get; init; }

    // --- Parent/updater handoff capability ONLY -------------------------------
    [JsonPropertyName("handoff_pipe")]
    public required string HandoffPipe { get; init; }

    [JsonPropertyName("handoff_token")]
    public required string HandoffToken { get; init; }
}

/// <summary>
/// A control message on the parent/updater handoff channel. <see cref="Kind"/>
/// is one of <c>UpdateWire.Msg*</c>. Bound to the attempt, sender PID, and the
/// handoff token so neither side can be spoofed.
/// </summary>
internal sealed record UpdateControlMessage
{
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; init; } = UpdateWire.ProtocolVersion;

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("attempt_id")]
    public required string AttemptId { get; init; }

    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("sender_pid")]
    public required int SenderPid { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

/// <summary>
/// The one-launch readiness message a freshly launched bridge sends back after
/// it truly reaches serving state. Bound to a role-specific capability (target
/// or rollback), the attempt, the launched PID, and the expected product
/// version, so a stale or forged marker is rejected.
/// </summary>
internal sealed record UpdateReadyMessage
{
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; init; } = UpdateWire.ProtocolVersion;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = UpdateWire.MsgReady;

    [JsonPropertyName("attempt_id")]
    public required string AttemptId { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("pid")]
    public required int Pid { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}
