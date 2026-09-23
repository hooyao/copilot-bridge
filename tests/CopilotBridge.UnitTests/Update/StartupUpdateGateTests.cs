using System.Diagnostics;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Update;
using CopilotBridge.Update.Wire;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Contract tests for <see cref="StartupUpdateGate"/>'s no-network short-circuit
/// paths ("Serve-only startup update gate"): disabled config and a one-launch
/// recovery/replacement context must both return ContinueCurrentVersion WITHOUT
/// making any GitHub request or starting any process. These are activation-safety
/// guarantees, so they're asserted directly.
/// </summary>
[Collection("gate-env")] // these mutate process env vars; keep them serialized
public class StartupUpdateGateTests
{
    private sealed class NoConsole : IUpdateConsole
    {
        public bool IsInputUnavailable => true;
        public void WriteLine(string text) { }
        public string? ReadLine() => null;
    }

    private static StartupUpdateGate Gate(bool enabled)
    {
        var opts = new AutoUpdateOptions { EnableAutoUpdate = enabled, AllowBetaUpdates = false };
        return new StartupUpdateGate(Options.Create(opts), originalArgs: [], new NoConsole());
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CopilotBridge.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static string StubDir()
    {
        var config = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
            ? "Release" : "Debug";
        return Path.Combine(RepoRoot(), "tests", "StubBridge", "bin", config, "net10.0");
    }

    private static void StopExact(Process process, long startTicks, string expectedPath)
    {
        if (process.HasExited)
        {
            return;
        }
        var identity = ProcessIdentity.Check(process.Id, startTicks, expectedPath);
        if (identity != IdentityCheck.Matched)
        {
            throw new InvalidOperationException(
                $"Refusing to stop fixture process {process.Id}: identity is {identity}.");
        }
        process.Kill();
        process.WaitForExit(5000);
    }

    [Fact]
    public async Task Disabled_config_continues_without_any_network()
    {
        // EnableAutoUpdate=false must return immediately. If it tried to reach
        // GitHub this would hang/throw; instead it returns instantly.
        var decision = await Gate(enabled: false).RunAsync(CancellationToken.None);
        Assert.Equal(UpdateGateDecision.ContinueCurrentVersion, decision);
    }

    [Fact]
    public async Task One_launch_recovery_context_suppresses_the_check()
    {
        // Simulate having been launched by the updater (a replacement/rollback
        // launch): the one-launch context must suppress discovery entirely, even
        // with auto-update enabled.
        var saved = new Dictionary<string, string?>();
        void Set(string k, string? v)
        {
            saved[k] = Environment.GetEnvironmentVariable(k);
            Environment.SetEnvironmentVariable(k, v);
        }
        try
        {
            Set(UpdateLaunchContext.EnvAttempt, "att1");
            Set(UpdateLaunchContext.EnvRole, UpdateWire.RoleTarget);
            Set(UpdateLaunchContext.EnvPipe, "pipe1");
            Set(UpdateLaunchContext.EnvToken, "tok1");
            Set(UpdateLaunchContext.EnvVersion, "0.4.14");

            var decision = await Gate(enabled: true).RunAsync(CancellationToken.None);
            Assert.Equal(UpdateGateDecision.ContinueCurrentVersion, decision);
        }
        finally
        {
            foreach (var (k, v) in saved)
            {
                Environment.SetEnvironmentVariable(k, v);
            }
        }
    }

    [Fact]
    public async Task Same_install_sibling_stops_handoff_before_attempt_creation()
    {
        var root = Path.Combine(Path.GetTempPath(), "cb-gate-sibling-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "install");
        var updateRoot = Path.Combine(root, "updates");
        Directory.CreateDirectory(install);

        var stubHost = Path.Combine(
            StubDir(), OperatingSystem.IsWindows() ? "stub-bridge.exe" : "stub-bridge");
        foreach (var source in Directory.GetFiles(StubDir()))
        {
            File.Copy(source, Path.Combine(install, Path.GetFileName(source)), overwrite: true);
        }
        var bridgePath = Path.Combine(
            install, OperatingSystem.IsWindows() ? "copilot-bridge.exe" : "copilot-bridge");
        File.Copy(stubHost, bridgePath, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(bridgePath, File.GetUnixFileMode(stubHost));
        }
        var updaterName = OperatingSystem.IsWindows() ? "copilot-updater.exe" : "copilot-updater";
        var updaterPath = Path.Combine(install, updaterName);
        File.Copy(stubHost, updaterPath, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(updaterPath, File.GetUnixFileMode(stubHost));
        }

        Process StartSibling()
        {
            var start = new ProcessStartInfo
            {
                FileName = bridgePath,
                WorkingDirectory = install,
                UseShellExecute = false,
            };
            start.Environment["STUB_HOLD_OPEN"] = "1";
            return Process.Start(start)!;
        }

        using var initiator = StartSibling();
        using var sibling = StartSibling();
        var initiatorTicks = ProcessIdentity.StartTicks(initiator);
        var siblingTicks = ProcessIdentity.StartTicks(sibling);
        Assert.NotEqual(0, initiatorTicks);
        Assert.NotEqual(0, siblingTicks);
        var updaterStartMarker = Path.Combine(root, "updater-started.marker");
        var savedMarker = Environment.GetEnvironmentVariable("STUB_ORDINARY_START_MARKER");

        try
        {
            // Set only after the two sibling fixtures are already running. If the
            // gate accidentally launches the updater copy, that child inherits
            // this marker and records its PID, making process creation observable.
            Environment.SetEnvironmentVariable("STUB_ORDINARY_START_MARKER", updaterStartMarker);
            Assert.True(SemanticVersion.TryParse("0.5.19", out var installed));
            Assert.True(SemanticVersion.TryParse("0.5.20", out var target));
            var selected = new SelectedRelease(
                new ReleaseCandidate("v0.5.20", IsDraft: false, IsPreRelease: false), target);
            var asset = new ResolvedAsset(
                "update.zip", "https://example.invalid/update.zip", 1,
                new string('a', 64), ArchiveKind.Zip);
            var environment = new StartupUpdateEnvironment(
                install, bridgePath, initiator.Id, initiatorTicks, updateRoot);
            var gate = new StartupUpdateGate(
                Options.Create(new AutoUpdateOptions { EnableAutoUpdate = true }),
                originalArgs: [], console: new NoConsole(), environment: environment);

            var decision = await gate.HandoffAsync(
                installed, selected, asset, "0.5.20", CancellationToken.None);

            Assert.Equal(UpdateGateDecision.ContinueCurrentVersion, decision);
            Assert.False(Directory.Exists(updateRoot));
            Assert.False(File.Exists(updaterStartMarker));
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(install),
                path => Path.GetFileName(path).Contains(".new.", StringComparison.Ordinal)
                    || Path.GetFileName(path).Contains(".bak.", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("STUB_ORDINARY_START_MARKER", savedMarker);
            StopExact(sibling, siblingTicks, bridgePath);
            StopExact(initiator, initiatorTicks, bridgePath);
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
