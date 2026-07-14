using CopilotBridge.Update.Wire;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Contract tests for <see cref="UpdateLaunchContext"/> ("Serve-only startup
/// update gate" one-launch suppression + role acceptance) and
/// <see cref="UpdatePlanValidator"/> ("Immutable plan and policy-free executor").
/// </summary>
public class UpdateLaunchAndPlanTests
{
    private static Func<string, string?> Env(Dictionary<string, string?> map)
        => key => map.TryGetValue(key, out var v) ? v : null;

    [Fact]
    public void Complete_target_context_parses()
    {
        var ctx = UpdateLaunchContext.FromEnvironment(Env(new()
        {
            [UpdateLaunchContext.EnvAttempt] = "a1",
            [UpdateLaunchContext.EnvRole] = UpdateWire.RoleTarget,
            [UpdateLaunchContext.EnvPipe] = "pipe1",
            [UpdateLaunchContext.EnvToken] = "tok1",
            [UpdateLaunchContext.EnvVersion] = "0.4.14",
        }));

        Assert.NotNull(ctx);
        Assert.Equal(UpdateWire.RoleTarget, ctx!.Role);
        Assert.Equal("pipe1", ctx.PipeName);
    }

    [Fact]
    public void Absent_context_is_null()
    {
        Assert.Null(UpdateLaunchContext.FromEnvironment(Env(new())));
    }

    [Fact]
    public void Incomplete_context_is_null_and_not_partially_honored()
    {
        var ctx = UpdateLaunchContext.FromEnvironment(Env(new()
        {
            [UpdateLaunchContext.EnvAttempt] = "a1",
            [UpdateLaunchContext.EnvRole] = UpdateWire.RoleTarget,
            // pipe/token/version missing
        }));
        Assert.Null(ctx);
    }

    [Fact]
    public void Unknown_role_is_rejected()
    {
        var ctx = UpdateLaunchContext.FromEnvironment(Env(new()
        {
            [UpdateLaunchContext.EnvAttempt] = "a1",
            [UpdateLaunchContext.EnvRole] = "administrator",
            [UpdateLaunchContext.EnvPipe] = "pipe1",
            [UpdateLaunchContext.EnvToken] = "tok1",
            [UpdateLaunchContext.EnvVersion] = "0.4.14",
        }));
        Assert.Null(ctx);
    }

    private static UpdatePlan ValidPlan(string installDir)
    {
        // A realistic layout: the install dir and the owner-private attempt root
        // are two separate, non-overlapping directories; every temporary path
        // lives under the attempt root.
        var attemptRoot = installDir + "-attempt";
        return new()
        {
            AttemptId = "a1",
            ParentPid = 10,
            ParentStartTicks = 123,
            InstallDir = installDir,
            BridgeExePath = Path.Combine(installDir, "copilot-bridge.exe"),
            UpdaterExePath = Path.Combine(installDir, "copilot-updater.exe"),
            ConfigPath = Path.Combine(installDir, "appsettings.json"),
            CurrentVersion = "0.4.13",
            TargetVersion = "0.4.14",
            AssetName = "a.zip",
            AssetUrl = "https://example/a.zip",
            AssetSize = 10,
            AssetSha256 = new string('a', 64),
            ArchiveKind = UpdateWire.ArchiveZip,
            StagingDir = Path.Combine(attemptRoot, "staging"),
            BackupDir = Path.Combine(attemptRoot, "backup"),
            ArchivePath = Path.Combine(attemptRoot, "a.zip"),
            JournalPath = Path.Combine(attemptRoot, "t.log"),
            ManagedFiles = ["copilot-bridge.exe", "copilot-updater.exe", "appsettings.json"],
            OriginalArgs = [],
            WorkingDirectory = installDir,
            DownloadTimeoutMs = 1000,
            ParentExitTimeoutMs = 1000,
            ReadyTimeoutMs = 1000,
            HandoffPipe = "pipe",
            HandoffToken = new string('b', 64),
        };
    }


    // The trusted attempt root the updater would establish from the plan-file
    // location — here, the parent of the plan's temporary paths.
    private static string TrustedRoot(string installDir) => installDir + "-attempt";

    [Fact]
    public void Valid_plan_passes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-plan-" + Guid.NewGuid().ToString("N"));
        Assert.True(UpdatePlanValidator.Validate(ValidPlan(dir), TrustedRoot(dir)).Ok);
    }

    [Fact]
    public void Non_https_asset_url_is_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-plan-" + Guid.NewGuid().ToString("N"));
        var plan = ValidPlan(dir) with { AssetUrl = "http://insecure/a.zip" };
        Assert.False(UpdatePlanValidator.Validate(plan, TrustedRoot(dir)).Ok);
    }

    [Fact]
    public void Managed_file_escaping_install_root_is_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-plan-" + Guid.NewGuid().ToString("N"));
        var plan = ValidPlan(dir) with { ManagedFiles = ["../evil.exe"] };
        Assert.False(UpdatePlanValidator.Validate(plan, TrustedRoot(dir)).Ok);
    }

    [Fact]
    public void Wrong_protocol_version_is_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-plan-" + Guid.NewGuid().ToString("N"));
        var plan = ValidPlan(dir) with { ProtocolVersion = 999 };
        Assert.False(UpdatePlanValidator.Validate(plan, TrustedRoot(dir)).Ok);
    }

    [Fact]
    public void Non_positive_asset_size_is_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-plan-" + Guid.NewGuid().ToString("N"));
        var plan = ValidPlan(dir) with { AssetSize = 0 };
        Assert.False(UpdatePlanValidator.Validate(plan, TrustedRoot(dir)).Ok);
    }

    [Fact]
    public void Managed_files_beyond_the_exact_allowlist_are_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-plan-" + Guid.NewGuid().ToString("N"));
        // An extra managed file the plan should never be allowed to add.
        var plan = ValidPlan(dir) with
        {
            ManagedFiles = ["copilot-bridge.exe", "copilot-updater.exe", "appsettings.json", "extra.dll"],
        };
        Assert.False(UpdatePlanValidator.Validate(plan, TrustedRoot(dir)).Ok);
    }

    [Fact]
    public void Managed_files_missing_a_required_target_are_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-plan-" + Guid.NewGuid().ToString("N"));
        var plan = ValidPlan(dir) with { ManagedFiles = ["copilot-bridge.exe", "appsettings.json"] };
        Assert.False(UpdatePlanValidator.Validate(plan, TrustedRoot(dir)).Ok);
    }

    [Fact]
    public void Managed_files_with_a_duplicate_masking_a_missing_target_are_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-plan-" + Guid.NewGuid().ToString("N"));
        // Count == 3 and every element IS in the allowlist, yet appsettings.json is
        // MISSING (copilot-bridge.exe is duplicated). A Count + All(contains) check
        // would accept this and let cutover run without ever staging the config; an
        // exact-set-equality check must reject it.
        var plan = ValidPlan(dir) with
        {
            ManagedFiles = ["copilot-bridge.exe", "copilot-bridge.exe", "copilot-updater.exe"],
        };
        Assert.False(UpdatePlanValidator.Validate(plan, TrustedRoot(dir)).Ok);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a.b")]          // a dot would make appsettings.json.bak.<attempt> ambiguous
    [InlineData("")]            // empty is caught by the required-fields check too
    [InlineData("with space")]
    public void Attempt_id_that_is_not_a_bounded_filename_safe_token_is_rejected(string attemptId)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-plan-" + Guid.NewGuid().ToString("N"));
        // A traversal/separator in AttemptId would escape the install dir when
        // concatenated into <managed>.new.<attempt> / appsettings.json.bak.<attempt>.
        var plan = ValidPlan(dir) with { AttemptId = attemptId };
        Assert.False(UpdatePlanValidator.Validate(plan, TrustedRoot(dir)).Ok);
    }

    [Theory]
    [InlineData("a1")]
    [InlineData("abcd1234")]
    [InlineData("e2eDEADBEEF")]
    [InlineData("0123456789abcdef")] // the CLI's 16-hex mint
    public void Attempt_id_that_is_a_bounded_alphanumeric_token_passes(string attemptId)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-plan-" + Guid.NewGuid().ToString("N"));
        Assert.True(UpdatePlanValidator.Validate(ValidPlan(dir) with { AttemptId = attemptId }, TrustedRoot(dir)).Ok);
    }

    [Fact]
    public void Nested_bridge_path_that_differs_from_the_replaced_child_is_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-plan-" + Guid.NewGuid().ToString("N"));
        // BridgeExePath points at <install>/sub/copilot-bridge.exe — the updater
        // would verify/kill/launch that, while file replacement targets
        // <install>/copilot-bridge.exe. Must be rejected (not a canonical child).
        var plan = ValidPlan(dir) with
        {
            BridgeExePath = Path.Combine(dir, "sub", "copilot-bridge.exe"),
        };
        Assert.False(UpdatePlanValidator.Validate(plan, TrustedRoot(dir)).Ok);
    }

    [Fact]
    public void Temporary_path_outside_the_attempt_root_is_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-plan-" + Guid.NewGuid().ToString("N"));
        // BackupDir escapes to an unrelated directory — later recursive cleanup
        // must never be aimable at an arbitrary path.
        var plan = ValidPlan(dir) with
        {
            BackupDir = Path.Combine(Path.GetTempPath(), "cb-elsewhere-" + Guid.NewGuid().ToString("N")),
        };
        Assert.False(UpdatePlanValidator.Validate(plan, TrustedRoot(dir)).Ok);
    }

    [Fact]
    public void Attempt_root_overlapping_the_install_dir_is_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-plan-" + Guid.NewGuid().ToString("N"));
        // A trusted root INSIDE the install dir, with temporaries under it — the
        // attempt area must never overlap the install directory.
        var overlappingRoot = Path.Combine(dir, "sub");
        var plan = ValidPlan(dir) with
        {
            StagingDir = Path.Combine(overlappingRoot, "staging"),
            BackupDir = Path.Combine(overlappingRoot, "backup"),
            ArchivePath = Path.Combine(overlappingRoot, "a.zip"),
            JournalPath = Path.Combine(overlappingRoot, "t.log"),
        };
        Assert.False(UpdatePlanValidator.Validate(plan, overlappingRoot).Ok);
    }

    [Fact]
    public void Non_positive_phase_timeout_is_rejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-plan-" + Guid.NewGuid().ToString("N"));
        Assert.False(UpdatePlanValidator.Validate(ValidPlan(dir) with { DownloadTimeoutMs = 0 }, TrustedRoot(dir)).Ok);
        Assert.False(UpdatePlanValidator.Validate(ValidPlan(dir) with { ParentExitTimeoutMs = -1 }, TrustedRoot(dir)).Ok);
        Assert.False(UpdatePlanValidator.Validate(ValidPlan(dir) with { ReadyTimeoutMs = 0 }, TrustedRoot(dir)).Ok);
    }

    [Theory]
    [InlineData("StagingDir")]
    [InlineData("BackupDir")]
    [InlineData("ArchivePath")]
    [InlineData("JournalPath")]
    [InlineData("HandoffPipe")]
    [InlineData("AssetName")]
    [InlineData("CurrentVersion")]
    [InlineData("TargetVersion")]
    [InlineData("WorkingDirectory")]
    public void Empty_required_path_field_is_rejected(string field)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-plan-" + Guid.NewGuid().ToString("N"));
        var plan = ValidPlan(dir);
        plan = field switch
        {
            "StagingDir" => plan with { StagingDir = "" },
            "BackupDir" => plan with { BackupDir = "" },
            "ArchivePath" => plan with { ArchivePath = "" },
            "JournalPath" => plan with { JournalPath = "" },
            "HandoffPipe" => plan with { HandoffPipe = "" },
            "AssetName" => plan with { AssetName = "" },
            "CurrentVersion" => plan with { CurrentVersion = "" },
            "TargetVersion" => plan with { TargetVersion = "" },
            "WorkingDirectory" => plan with { WorkingDirectory = "" },
            _ => plan,
        };
        Assert.False(UpdatePlanValidator.Validate(plan, TrustedRoot(dir)).Ok);
    }
}
