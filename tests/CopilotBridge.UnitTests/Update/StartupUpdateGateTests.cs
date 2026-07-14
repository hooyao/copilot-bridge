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
}
