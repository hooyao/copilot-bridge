using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CopilotBridge.Update.Wire;
using Xunit;

namespace CopilotBridge.Playground.Update;

/// <summary>
/// Real-process end-to-end tests for the transactional self-update. They build a
/// real ZIP archive containing the <c>stub-bridge</c> helper (which speaks the
/// REAL readiness pipe protocol via the shared wire code) and drive the ACTUAL
/// <c>copilot-updater</c> executable through a full transaction against a
/// disposable, non-8765 installation. The verdict is the updater's own exit code
/// plus the on-disk result — not a mock.
///
/// Tagged Integration/ApiContract: it asserts the cross-process wire+file
/// contract in-test (no live Copilot or headless LLM client needed).
/// </summary>
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class RealProcessUpdateTests : IDisposable
{
    private readonly string _root;
    private readonly string _install;

    public RealProcessUpdateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cb-e2e-" + Guid.NewGuid().ToString("N"));
        _install = Path.Combine(_root, "install");
        Directory.CreateDirectory(_install);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private const string BridgeName = "copilot-bridge-stub.exe";   // *.exe launcher name
    private const string UpdaterName = "copilot-updater-stub.exe";
    private const string ConfigName = "appsettings.json";

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CopilotBridge.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static string BinDir(string project)
    {
        // Mirror the current test's configuration (Debug/Release).
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
            ? "Release" : "Debug";
        return Path.Combine(RepoRoot(), project, "bin", config, "net10.0");
    }

    private static string RealUpdaterExe()
    {
        var name = OperatingSystem.IsWindows() ? "copilot-updater.exe" : "copilot-updater";
        var p = Path.Combine(BinDir(Path.Combine("src", "CopilotBridge.Updater")), name);
        if (!File.Exists(p))
        {
            throw new InvalidOperationException($"copilot-updater not built at {p}");
        }
        return p;
    }

    private static string StubDir()
    {
        var dir = BinDir(Path.Combine("tests", "StubBridge"));
        if (!File.Exists(Path.Combine(dir, "stub-bridge.dll")))
        {
            throw new InvalidOperationException($"stub-bridge not built at {dir}");
        }
        return dir;
    }

    private static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(s));
    }

    // -- The stub is built with an apphost, so the managed "bridge"/"updater" ARE
    //    real native executables (the apphost locates stub-bridge.dll, still
    //    present alongside). --

    private (string archive, long size, string sha) BuildArchive(string newConfigJson)
    {
        var staging = Path.Combine(_root, "arc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        var stubHost = Path.Combine(StubDir(), OperatingSystem.IsWindows() ? "stub-bridge.exe" : "stub-bridge");
        if (!File.Exists(stubHost))
        {
            throw new InvalidOperationException($"stub apphost not found at {stubHost}; build produces an apphost by default");
        }

        // Copy the full stub output, then the managed bridge/updater ARE the apphost
        // renamed (the apphost locates stub-bridge.dll by its original name, which is
        // still present alongside).
        foreach (var f in Directory.GetFiles(StubDir()))
        {
            File.Copy(f, Path.Combine(staging, Path.GetFileName(f)), overwrite: true);
        }
        File.Copy(stubHost, Path.Combine(staging, BridgeName), overwrite: true);
        File.Copy(stubHost, Path.Combine(staging, UpdaterName), overwrite: true);
        File.WriteAllText(Path.Combine(staging, ConfigName), newConfigJson);

        var archive = Path.Combine(_root, "update.zip");
        if (File.Exists(archive)) File.Delete(archive);
        ZipFile.CreateFromDirectory(staging, archive);
        return (archive, new FileInfo(archive).Length, Sha256(archive));
    }

    private void SeedInstall(string oldConfigJson)
    {
        // The "old" install already has a bridge/updater (the same apphost) so the
        // transaction has binaries to back up. It also needs the stub's support
        // files so the OLD bridge apphost can run during rollback.
        foreach (var f in Directory.GetFiles(StubDir()))
        {
            File.Copy(f, Path.Combine(_install, Path.GetFileName(f)), overwrite: true);
        }
        var stubHost = Path.Combine(StubDir(), OperatingSystem.IsWindows() ? "stub-bridge.exe" : "stub-bridge");
        File.Copy(stubHost, Path.Combine(_install, BridgeName), overwrite: true);
        File.Copy(stubHost, Path.Combine(_install, UpdaterName), overwrite: true);
        File.WriteAllText(Path.Combine(_install, ConfigName), oldConfigJson);
    }

    private UpdatePlan BuildPlan(string attemptDir, string archive, long size, string sha, int parentPid, long parentStartTicks, string handoffPipe)
    {
        return new UpdatePlan
        {
            AttemptId = "e2e" + Guid.NewGuid().ToString("N")[..6],
            ParentPid = parentPid,
            ParentStartTicks = parentStartTicks,
            InstallDir = _install,
            BridgeExePath = Path.Combine(_install, BridgeName),
            UpdaterExePath = Path.Combine(_install, UpdaterName),
            ConfigPath = Path.Combine(_install, ConfigName),
            CurrentVersion = "0.4.13",
            TargetVersion = "0.4.14",
            AssetName = Path.GetFileName(archive),
            AssetUrl = "https://example.invalid/never-downloaded.zip",
            AssetSize = size,
            AssetSha256 = sha,
            ArchiveKind = UpdateWire.ArchiveZip,
            StagingDir = Path.Combine(attemptDir, "staging"),
            BackupDir = Path.Combine(attemptDir, "backup"),
            ArchivePath = Path.Combine(attemptDir, Path.GetFileName(archive)),
            JournalPath = Path.Combine(attemptDir, "transaction.log"),
            ManagedFiles = [BridgeName, UpdaterName, ConfigName],
            OriginalArgs = [],
            WorkingDirectory = _install,
            DownloadTimeoutMs = 20_000,
            ParentExitTimeoutMs = 20_000,
            ReadyTimeoutMs = 30_000,
            HandoffPipe = handoffPipe,
            HandoffToken = new string('b', 64),
        };
    }

    /// <summary>
    /// Drive the FULL real choreography: launch a real "parent" bridge process
    /// (the installed stub in parent-hat) whose identity the updater verifies and
    /// whose exit it waits for; then launch the real updater against a plan that
    /// names that parent. The updater downloads nothing (idempotent-resume off the
    /// pre-staged archive), hands off, cuts over, launches the replacement, and
    /// commits or rolls back — all via real processes and real named pipes.
    /// </summary>
    private async Task<int> RunFullTransactionAsync(
        string attemptDir, string archive, long size, string sha, bool corruptArchive = false)
    {
        Directory.CreateDirectory(attemptDir);

        var handoffPipe = UpdateCapability.Create("e2e", "handoff").PipeName;
        var handoffToken = new string('b', 64);
        var attemptId = "e2e" + Guid.NewGuid().ToString("N")[..6];

        // 1. Launch the real parent bridge (parent-hat). It connects to the
        //    handoff pipe, authorizes cutover, then exits.
        var parentPsi = new ProcessStartInfo
        {
            FileName = Path.Combine(_install, BridgeName),
            WorkingDirectory = _install,
            UseShellExecute = false,
        };
        parentPsi.Environment["COPILOT_BRIDGE_PARENT_PIPE"] = handoffPipe;
        parentPsi.Environment["COPILOT_BRIDGE_PARENT_TOKEN"] = handoffToken;
        parentPsi.Environment["COPILOT_BRIDGE_PARENT_ATTEMPT"] = attemptId;
        var parent = Process.Start(parentPsi)!;
        var parentStartTicks = ProcessIdentity.StartTicks(parent);

        // 2. Build the plan naming that real parent.
        var plan = BuildPlan(attemptDir, archive, size, sha, parent.Id, parentStartTicks, handoffPipe)
            with { AttemptId = attemptId, HandoffToken = handoffToken };

        var planPath = Path.Combine(attemptDir, "plan.json");
        File.Copy(Path.Combine(_root, "update.zip"), plan.ArchivePath, overwrite: true);
        if (corruptArchive)
        {
            // Flip the pre-staged bytes so the plan's digest no longer matches.
            // The updater must abort at the digest check before any cutover.
            await File.WriteAllBytesAsync(plan.ArchivePath, new byte[] { 0x50, 0x4B, 0x00, 0x00, 0xFF });
        }
        await File.WriteAllTextAsync(planPath,
            JsonSerializer.Serialize(plan, UpdateWireJsonContext.Default.UpdatePlan));

        // 3. Launch the real updater.
        var psi = new ProcessStartInfo
        {
            FileName = RealUpdaterExe(),
            WorkingDirectory = attemptDir,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(planPath);
        var updater = Process.Start(psi)!;

        await updater.WaitForExitAsync();
        try { if (!parent.HasExited) parent.Kill(); } catch { /* best effort */ }

        // On an unexpected outcome, surface the updater's own journal so the
        // real-process failure is diagnosable rather than opaque.
        if (updater.ExitCode != 0 && Environment.GetEnvironmentVariable("CB_E2E_DUMP_JOURNAL") == "1"
            && File.Exists(plan.JournalPath))
        {
            Console.WriteLine("=== updater transaction.log ===");
            Console.WriteLine(await File.ReadAllTextAsync(plan.JournalPath));
        }
        return updater.ExitCode;
    }

    [Fact]
    public async Task Successful_update_commits_and_migrates_config()
    {
        SeedInstall("""{ "Server": { "Port": 19000 }, "RemovedLegacyOption": true }""");
        var (_, size, sha) = BuildArchive("""{ "Server": { "Port": 8765, "NewKey": 1 } }""");

        var attemptDir = Path.Combine(_root, "attempt-ok");
        var exit = await RunFullTransactionAsync(attemptDir, "update.zip", size, sha);

        Assert.Equal((int)UpdaterExitCodes.Committed, exit);

        // Merged config: old port kept, old-only key dropped, new key present.
        var config = await File.ReadAllTextAsync(Path.Combine(_install, ConfigName));
        Assert.Contains("19000", config);
        Assert.Contains("NewKey", config);
        Assert.DoesNotContain("RemovedLegacyOption", config);
    }

    [Fact]
    public async Task Target_that_never_reports_ready_rolls_back_to_exact_old_config()
    {
        var originalConfig = """{ "Server": { "Port": 19000 }, "RemovedLegacyOption": true }""";
        SeedInstall(originalConfig);
        var (_, size, sha) = BuildArchive("""{ "Server": { "Port": 8765 } }""");

        var attemptDir = Path.Combine(_root, "attempt-rollback");

        // Force the freshly-installed TARGET to exit before Ready; the restored
        // OLD bridge (role=rollback) still reports Ready normally.
        Environment.SetEnvironmentVariable("STUB_FAIL_TARGET", "1");
        try
        {
            var exit = await RunFullTransactionAsync(attemptDir, "update.zip", size, sha);
            Assert.Equal((int)UpdaterExitCodes.RolledBack, exit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STUB_FAIL_TARGET", null);
        }

        // The original config is restored byte-for-byte, old-only key included.
        var config = await File.ReadAllTextAsync(Path.Combine(_install, ConfigName));
        Assert.Equal(originalConfig, config);
    }

    [Fact]
    public async Task Target_that_reports_wrong_version_is_rejected_and_rolls_back()
    {
        var originalConfig = """{ "Server": { "Port": 19000 } }""";
        SeedInstall(originalConfig);
        var (_, size, sha) = BuildArchive("""{ "Server": { "Port": 8765 } }""");

        var attemptDir = Path.Combine(_root, "attempt-badversion");

        // The freshly-installed TARGET reports Ready but with the WRONG version;
        // the updater must reject that Ready and roll back to the old version.
        Environment.SetEnvironmentVariable("STUB_BAD_VERSION", "1");
        try
        {
            var exit = await RunFullTransactionAsync(attemptDir, "update.zip", size, sha);
            Assert.Equal((int)UpdaterExitCodes.RolledBack, exit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STUB_BAD_VERSION", null);
        }

        Assert.Equal(originalConfig, await File.ReadAllTextAsync(Path.Combine(_install, ConfigName)));
    }

    [Fact]
    public async Task Corrupt_archive_aborts_before_cutover_and_leaves_install_untouched()
    {
        var originalConfig = """{ "Server": { "Port": 19000 }, "RemovedLegacyOption": true }""";
        SeedInstall(originalConfig);
        var (_, size, sha) = BuildArchive("""{ "Server": { "Port": 8765 } }""");

        // Record the exact pre-update install bytes.
        var bridgeBefore = await File.ReadAllBytesAsync(Path.Combine(_install, BridgeName));

        var attemptDir = Path.Combine(_root, "attempt-corrupt");

        // Plan advertises the real size+digest, but the pre-staged archive is
        // corrupt — the updater's digest check must fail BEFORE the old bridge is
        // stopped, so nothing is installed and the old config/binaries are intact.
        var exit = await RunFullTransactionAsync(
            attemptDir, "update.zip", size, sha, corruptArchive: true);

        Assert.Equal((int)UpdaterExitCodes.PreflightFailed, exit);
        Assert.Equal(originalConfig, await File.ReadAllTextAsync(Path.Combine(_install, ConfigName)));
        Assert.Equal(bridgeBefore, await File.ReadAllBytesAsync(Path.Combine(_install, BridgeName)));
    }
}

/// <summary>Mirror of the updater's exit codes (its enum is internal to that exe).</summary>
internal static class UpdaterExitCodes
{
    public const int PreflightFailed = 10;
    public const int Committed = 0;
    public const int RolledBack = 20;
    public const int Unrecovered = 30;
}
