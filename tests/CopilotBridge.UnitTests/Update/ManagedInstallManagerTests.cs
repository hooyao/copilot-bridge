using System.Text;
using CopilotBridge.Update.Wire;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Disposable-directory transaction tests for <see cref="ManagedInstallManager"/>
/// and <see cref="ConfigSnapshot"/> ("Template-based configuration migration",
/// "Complete pre-cutover safety", "Coordinated cutover", "Full rollback to exact
/// old installation"). No processes or IPC — just the filesystem mechanics.
/// </summary>
public class ManagedInstallManagerTests : IDisposable
{
    private readonly string _root;
    private readonly string _install;
    private readonly string _staging;
    private readonly string _backup;

    public ManagedInstallManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cb-install-" + Guid.NewGuid().ToString("N"));
        _install = Path.Combine(_root, "install");
        _staging = Path.Combine(_root, "staging");
        _backup = Path.Combine(_root, "backup");
        Directory.CreateDirectory(_install);
        Directory.CreateDirectory(_staging);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private const string BridgeName = "copilot-bridge.exe";
    private const string UpdaterName = "copilot-updater.exe";
    private const string ConfigName = "appsettings.json";

    private UpdatePlan Plan() => new()
    {
        AttemptId = "att1",
        ParentPid = 1,
        ParentStartTicks = 1,
        InstallDir = _install,
        BridgeExePath = Path.Combine(_install, BridgeName),
        UpdaterExePath = Path.Combine(_install, UpdaterName),
        ConfigPath = Path.Combine(_install, ConfigName),
        CurrentVersion = "0.4.13",
        TargetVersion = "0.4.14",
        AssetName = "a.zip",
        AssetUrl = "https://example/a.zip",
        AssetSize = 1,
        AssetSha256 = new string('a', 64),
        ArchiveKind = UpdateWire.ArchiveZip,
        StagingDir = _staging,
        BackupDir = _backup,
        ArchivePath = Path.Combine(_root, "a.zip"),
        JournalPath = Path.Combine(_root, "t.log"),
        ManagedFiles = [BridgeName, UpdaterName, ConfigName],
        OriginalArgs = [],
        WorkingDirectory = _install,
        DownloadTimeoutMs = 1000,
        ParentExitTimeoutMs = 1000,
        ReadyTimeoutMs = 1000,
        HandoffPipe = "p",
        HandoffToken = new string('b', 64),
    };

    private ManagedInstallManager Manager()
        => new(Plan(), new TransactionJournal(Path.Combine(_root, "t.log")));

    private void SeedInstalled(string bridge, string updater, string config)
    {
        File.WriteAllText(Path.Combine(_install, BridgeName), bridge);
        File.WriteAllText(Path.Combine(_install, UpdaterName), updater);
        File.WriteAllText(Path.Combine(_install, ConfigName), config);
    }

    private void SeedStaging(string bridge, string updater, string newConfig)
    {
        File.WriteAllText(Path.Combine(_staging, BridgeName), bridge);
        File.WriteAllText(Path.Combine(_staging, UpdaterName), updater);
        File.WriteAllText(Path.Combine(_staging, ConfigName), newConfig);
    }

    [Fact]
    public void Successful_transaction_installs_new_binaries_and_merged_config()
    {
        SeedInstalled("old-bridge", "old-updater", """{ "Server": { "Port": 19000 } }""");
        SeedStaging("new-bridge", "new-updater", """{ "Server": { "Port": 8765, "New": 1 } }""");

        var mgr = Manager();
        Assert.True(mgr.Prepare().Ok);

        var newDefault = File.ReadAllText(Path.Combine(_staging, ConfigName));
        var merged = mgr.BuildMergedConfig(newDefault);
        Assert.True(mgr.StageReplacements(merged).Ok);

        Assert.True(mgr.RevalidateNoDrift());
        Assert.True(mgr.Cutover().Ok);

        Assert.Equal("new-bridge", File.ReadAllText(Path.Combine(_install, BridgeName)));
        Assert.Equal("new-updater", File.ReadAllText(Path.Combine(_install, UpdaterName)));

        // Merged config: old port kept, new key added.
        var installedConfig = File.ReadAllText(Path.Combine(_install, ConfigName));
        Assert.Contains("19000", installedConfig);
        Assert.Contains("\"New\"", installedConfig);

        // The original config is preserved under a unique .bak.
        Assert.True(File.Exists(mgr.ConfigBackupPath));
        Assert.Contains(".bak.att1", mgr.ConfigBackupPath);
    }

    [Fact]
    public void Rollback_restores_exact_original_config_including_old_only_keys()
    {
        var originalConfig = """{ "Server": { "Port": 19000 }, "RemovedLegacyOption": true }""";
        SeedInstalled("old-bridge", "old-updater", originalConfig);
        SeedStaging("new-bridge", "new-updater", """{ "Server": { "Port": 8765 } }""");

        var mgr = Manager();
        Assert.True(mgr.Prepare().Ok);
        var merged = mgr.BuildMergedConfig(File.ReadAllText(Path.Combine(_staging, ConfigName)));
        Assert.True(mgr.StageReplacements(merged).Ok);
        Assert.True(mgr.Cutover().Ok);

        // A successful migration would DROP RemovedLegacyOption...
        Assert.DoesNotContain("RemovedLegacyOption", File.ReadAllText(Path.Combine(_install, ConfigName)));

        // ...but a rollback must restore the EXACT original bytes, old-only key included.
        Assert.True(mgr.Rollback().Ok);
        Assert.Equal(originalConfig, File.ReadAllText(Path.Combine(_install, ConfigName)));
        Assert.Equal("old-bridge", File.ReadAllText(Path.Combine(_install, BridgeName)));
        Assert.Equal("old-updater", File.ReadAllText(Path.Combine(_install, UpdaterName)));
    }

    [Fact]
    public void Rollback_falls_back_to_private_copy_when_bak_is_missing()
    {
        var originalConfig = """{ "Server": { "Port": 19000 }, "RemovedLegacyOption": true }""";
        SeedInstalled("old-bridge", "old-updater", originalConfig);
        SeedStaging("new-bridge", "new-updater", """{ "Server": { "Port": 8765 } }""");

        var mgr = Manager();
        Assert.True(mgr.Prepare().Ok);
        var merged = mgr.BuildMergedConfig(File.ReadAllText(Path.Combine(_staging, ConfigName)));
        Assert.True(mgr.StageReplacements(merged).Ok);
        Assert.True(mgr.Cutover().Ok);

        // Simulate the transaction .bak being lost/corrupted before rollback.
        Assert.True(File.Exists(mgr.ConfigBackupPath));
        File.Delete(mgr.ConfigBackupPath);

        // Rollback must still restore the exact original from the verified private copy.
        Assert.True(mgr.Rollback().Ok);
        Assert.Equal(originalConfig, File.ReadAllText(Path.Combine(_install, ConfigName)));
    }

    [Fact]
    public void Rollback_falls_back_to_private_copy_when_bak_is_tampered()
    {
        var originalConfig = """{ "Server": { "Port": 19000 } }""";
        SeedInstalled("old-bridge", "old-updater", originalConfig);
        SeedStaging("new-bridge", "new-updater", """{ "Server": { "Port": 8765 } }""");

        var mgr = Manager();
        Assert.True(mgr.Prepare().Ok);
        var merged = mgr.BuildMergedConfig(File.ReadAllText(Path.Combine(_staging, ConfigName)));
        Assert.True(mgr.StageReplacements(merged).Ok);
        Assert.True(mgr.Cutover().Ok);

        // Tamper the .bak so its hash no longer matches the immutable snapshot.
        File.WriteAllText(mgr.ConfigBackupPath, "tampered!");

        Assert.True(mgr.Rollback().Ok);
        // The exact original (not the tampered bytes) is restored from the private copy.
        Assert.Equal(originalConfig, File.ReadAllText(Path.Combine(_install, ConfigName)));
    }

    [Fact]
    public void Rollback_fails_when_a_managed_binary_backup_is_missing()
    {
        SeedInstalled("old-bridge", "old-updater", """{ "Server": { "Port": 19000 } }""");
        SeedStaging("new-bridge", "new-updater", """{ "Server": { "Port": 8765 } }""");

        var mgr = Manager();
        Assert.True(mgr.Prepare().Ok);
        var merged = mgr.BuildMergedConfig(File.ReadAllText(Path.Combine(_staging, ConfigName)));
        Assert.True(mgr.StageReplacements(merged).Ok);
        Assert.True(mgr.Cutover().Ok);

        // Destroy a managed-binary backup — rollback can no longer restore it, which
        // is the branch that escalates to the Unrecovered outcome upstream.
        File.Delete(Path.Combine(_backup, BridgeName));

        Assert.False(mgr.Rollback().Ok);
    }

    [Fact]
    public void Rollback_fails_when_a_managed_binary_backup_is_corrupted()
    {
        SeedInstalled("old-bridge", "old-updater", """{ "Server": { "Port": 19000 } }""");
        SeedStaging("new-bridge", "new-updater", """{ "Server": { "Port": 8765 } }""");

        var mgr = Manager();
        Assert.True(mgr.Prepare().Ok);
        var merged = mgr.BuildMergedConfig(File.ReadAllText(Path.Combine(_staging, ConfigName)));
        Assert.True(mgr.StageReplacements(merged).Ok);
        Assert.True(mgr.Cutover().Ok);

        // Corrupt a backup after preparation — rollback must NOT copy+launch
        // unverified bytes while reporting success; it must fail the hash check.
        File.WriteAllText(Path.Combine(_backup, BridgeName), "corrupted");

        Assert.False(mgr.Rollback().Ok);
    }

    [Fact]
    public void Cutover_refuses_when_config_drifts_after_prepare()
    {
        SeedInstalled("old-bridge", "old-updater", """{ "Server": { "Port": 19000 } }""");
        SeedStaging("new-bridge", "new-updater", """{ "Server": { "Port": 8765 } }""");

        var mgr = Manager();
        Assert.True(mgr.Prepare().Ok);
        var merged = mgr.BuildMergedConfig(File.ReadAllText(Path.Combine(_staging, ConfigName)));
        Assert.True(mgr.StageReplacements(merged).Ok);

        // Operator edits the config after Prepare, before cutover.
        File.WriteAllText(Path.Combine(_install, ConfigName), """{ "Server": { "Port": 22222 } }""");

        Assert.False(mgr.RevalidateNoDrift());
        var cutover = mgr.Cutover();
        Assert.False(cutover.Ok);

        // Nothing installed; the operator's latest edit is intact.
        Assert.Equal("old-bridge", File.ReadAllText(Path.Combine(_install, BridgeName)));
        Assert.Contains("22222", File.ReadAllText(Path.Combine(_install, ConfigName)));
    }

    [Fact]
    public void Prepare_fails_when_a_staged_binary_is_missing()
    {
        SeedInstalled("old-bridge", "old-updater", "{}");
        // Only stage the config, not the binaries.
        File.WriteAllText(Path.Combine(_staging, ConfigName), "{}");

        Assert.False(Manager().Prepare().Ok);
    }

    [Fact]
    public void Prepare_probes_install_writability_without_touching_managed_targets()
    {
        SeedInstalled("old-bridge", "old-updater", "{}");
        SeedStaging("new-bridge", "new-updater", "{}");

        // Record the managed binaries' bytes; the writability probe must not alter
        // them (it writes and deletes a throwaway probe file, nothing managed).
        var bridgeBefore = File.ReadAllText(Path.Combine(_install, BridgeName));

        Assert.True(Manager().Prepare().Ok);

        Assert.Equal(bridgeBefore, File.ReadAllText(Path.Combine(_install, BridgeName)));
        // No probe file left behind.
        Assert.Empty(Directory.GetFiles(_install, ".copilot-update-probe.*"));
    }

    [Fact]
    public void ConfigSnapshot_detects_drift_and_writes_verified_copy()
    {
        var path = Path.Combine(_install, ConfigName);
        File.WriteAllText(path, "original");
        var snap = ConfigSnapshot.Read(path);

        Assert.True(snap.MatchesFile(path));

        File.WriteAllText(path, "changed");
        Assert.False(snap.MatchesFile(path));

        var copyPath = Path.Combine(_root, "copy.json");
        Assert.True(snap.WriteVerifiedCopy(copyPath));
        Assert.Equal("original", File.ReadAllText(copyPath));
    }

    [Fact]
    public void ConfigSnapshot_Text_strips_utf8_bom_but_rollback_stays_byte_exact()
    {
        // A BOM-prefixed appsettings.json is accepted by the config provider, so
        // ConfigSnapshot.Text must NOT leave a U+FEFF that would break
        // JsonDocument.Parse during migration — yet the private copy must restore
        // the exact original bytes (BOM included).
        var path = Path.Combine(_install, ConfigName);
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var jsonBytes = System.Text.Encoding.UTF8.GetBytes("""{ "Server": { "Port": 19000 } }""");
        var withBom = bom.Concat(jsonBytes).ToArray();
        File.WriteAllBytes(path, withBom);

        var snap = ConfigSnapshot.Read(path);

        // Text has no leading BOM char and parses as JSON.
        Assert.False(snap.Text.StartsWith('﻿'));
        using var doc = System.Text.Json.JsonDocument.Parse(snap.Text);
        Assert.Equal(19000, doc.RootElement.GetProperty("Server").GetProperty("Port").GetInt32());

        // The verified private copy is byte-for-byte the original, BOM included.
        var copyPath = Path.Combine(_root, "copy.json");
        Assert.True(snap.WriteVerifiedCopy(copyPath));
        Assert.Equal(withBom, File.ReadAllBytes(copyPath));
    }

    [Fact]
    public void CleanupAfterCommit_removes_attempt_root_even_with_readonly_children()
    {
        // The gate hardens plan.json read-only after flush. Cleanup after a
        // committed update must still delete the WHOLE attempt root — otherwise the
        // capability-bearing plan and the private updater copy linger on disk after
        // every successful update. A read-only child must not defeat the delete.
        SeedInstalled("old-bridge", "old-updater", """{ "Server": { "Port": 19000 } }""");
        SeedStaging("new-bridge", "new-updater", """{ "Server": { "Port": 8765 } }""");

        var attemptRoot = Path.Combine(_root, "attempt");
        var nested = Path.Combine(attemptRoot, "staging");
        Directory.CreateDirectory(nested);
        var planPath = Path.Combine(attemptRoot, "plan.json");
        File.WriteAllText(planPath, "{}");
        var nestedReadonly = Path.Combine(nested, "updater-copy.exe");
        File.WriteAllText(nestedReadonly, "copy");
        // Harden exactly as the gate does: read-only at the top level AND nested.
        File.SetAttributes(planPath, File.GetAttributes(planPath) | FileAttributes.ReadOnly);
        File.SetAttributes(nestedReadonly, File.GetAttributes(nestedReadonly) | FileAttributes.ReadOnly);

        var mgr = Manager();
        Assert.True(mgr.Prepare().Ok);
        var merged = mgr.BuildMergedConfig(File.ReadAllText(Path.Combine(_staging, ConfigName)));
        Assert.True(mgr.StageReplacements(merged).Ok);
        Assert.True(mgr.Cutover().Ok);

        mgr.CleanupAfterCommit(attemptRoot);

        // The entire attempt root is gone — no read-only plan/updater copy left.
        Assert.False(Directory.Exists(attemptRoot));
        // And the transaction .bak was swept too.
        Assert.False(File.Exists(mgr.ConfigBackupPath));
    }

    [Fact]
    public void StageReplacements_failure_leaves_no_debris_temp_in_the_install_dir()
    {
        // Contract: a *.new.<attempt> temp is recorded BEFORE it is written, so if
        // the write of THAT file fails midway (disk-full etc.), the partial temp
        // already on disk in the live install dir is still tracked and removable by
        // the failure-path cleanup — never left as debris beside the managed files.
        SeedInstalled("old-bridge", "old-updater", """{ "Server": { "Port": 19000 } }""");
        SeedStaging("new-bridge", "new-updater", """{ "Server": { "Port": 8765 } }""");

        var mgr = Manager();
        Assert.True(mgr.Prepare().Ok);
        var merged = mgr.BuildMergedConfig(File.ReadAllText(Path.Combine(_staging, ConfigName)));

        // Make the FIRST managed file's staging fail as its own write begins: occupy
        // the bridge's temp path with a directory so the CreateNew FileStream throws
        // WHILE staging that file. ManagedFiles order is [bridge, updater, config].
        var bridgeTemp = Path.Combine(_install, BridgeName + ".new.att1");
        Directory.CreateDirectory(bridgeTemp);

        var stage = mgr.StageReplacements(merged);
        Assert.False(stage.Ok);

        // The temp of the file that FAILED mid-write must be tracked (recorded
        // before the write). Under the old code it was recorded only after a
        // successful write, so this file would be absent and leak.
        Assert.Contains(bridgeTemp, mgr.StagedTempPaths);

        // And the standard failure cleanup can therefore act on it.
        mgr.RemoveStagedReplacementTemps();
        Assert.Empty(mgr.StagedTempPaths);
    }

    [Fact]
    public void CleanupAfterPreflightFailure_removes_backups_staging_and_archive()
    {
        // A preflight failure never changed the install, so the managed-binary
        // backups are dead weight. Cleanup must remove BackupDir (full exe copies),
        // the staging tree, and the downloaded archive — otherwise repeated rejected
        // updates accumulate tens of MB per attempt. It must NOT touch the install.
        SeedInstalled("old-bridge", "old-updater", """{ "Server": { "Port": 19000 } }""");
        SeedStaging("new-bridge", "new-updater", """{ "Server": { "Port": 8765 } }""");
        File.WriteAllText(Path.Combine(_root, "a.zip"), "archive-bytes");

        var mgr = Manager();
        Assert.True(mgr.Prepare().Ok); // creates BackupDir with backups of both binaries

        // Preconditions: the backups + staging + archive all exist.
        Assert.True(File.Exists(Path.Combine(_backup, BridgeName)));
        Assert.True(Directory.Exists(_staging));
        Assert.True(File.Exists(Path.Combine(_root, "a.zip")));

        mgr.CleanupAfterPreflightFailure();

        // All transient material is gone...
        Assert.False(Directory.Exists(_backup));
        Assert.False(Directory.Exists(_staging));
        Assert.False(File.Exists(Path.Combine(_root, "a.zip")));
        // ...but the live installation is untouched.
        Assert.Equal("old-bridge", File.ReadAllText(Path.Combine(_install, BridgeName)));
        Assert.Equal("""{ "Server": { "Port": 19000 } }""", File.ReadAllText(Path.Combine(_install, ConfigName)));
    }
}
