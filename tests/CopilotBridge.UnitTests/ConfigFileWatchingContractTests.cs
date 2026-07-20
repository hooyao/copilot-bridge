using CopilotBridge.Cli.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Security/behavior contract: the serve host must not register any configuration
/// source that <em>watches the filesystem for changes</em>.
/// <para>
/// Rationale (from the incident, not from the code): a
/// <see cref="FileConfigurationSource"/> with
/// <see cref="FileConfigurationSource.ReloadOnChange"/> = <c>true</c> makes
/// <c>ConfigurationManager</c> eagerly start a <b>recursive</b>
/// <c>FileSystemWatcher</c> rooted at the content root (the process working
/// directory), alive for the whole process regardless of traffic. On macOS that
/// watch descends into every File Provider domain under the working directory
/// (iCloud Drive, Google Drive, …) and the OS raises a TCC access prompt for each
/// cloud provider — the bridge appeared to "try to access iCloud/Google" while
/// completely idle with no client. The bridge intentionally never hot-reloads
/// config (every options type is read once and documents "restart to change"),
/// so the correct state is: zero watching file sources.
/// </para>
/// <para>
/// The fix is exercised through its real construction seam
/// (<see cref="BridgeConfigurationExtensions.CreateServeHostBuilder"/>, the ONE
/// method <c>ServeCommand</c> builds the host with) so a regression to a bare
/// <c>CreateSlimBuilder()</c> turns these tests red. Assertions observe the real
/// builder at two levels: the source flag and the live watcher object. No process
/// environment is mutated: the switch is code-scoped host configuration.
/// </para>
/// <para>
/// The "watching-on" positive controls deliberately create the recursive watcher,
/// so they MUST NOT root it at the test process's working directory — a macOS
/// checkout under iCloud/Google Drive would otherwise let the unit suite itself
/// reproduce the very prompts this change removes. They root the builder at an
/// isolated EMPTY temp directory and dispose the live watcher deterministically
/// (see <see cref="ForcedWatchOnHost"/>), never leaving it to GC.
/// </para>
/// </summary>
public class ConfigFileWatchingContractTests
{
    // The framework host switch, forced ON, to reproduce the pre-fix hazard
    // deterministically regardless of the ambient environment. Command-line host
    // config takes precedence over the DOTNET_ env var, so this pins watching on
    // even on a box that exported the disable var. (Proven by
    // ArgsSwitch_TakesPrecedenceOverEnvironment below.)
    private static readonly string[] ForceWatchingOnArgs = ["--hostBuilder:reloadConfigOnChange=true"];

    private static bool AnyFileSourceWatches(WebApplicationBuilder builder)
    {
        foreach (var source in ((IConfigurationBuilder)builder.Configuration).Sources)
        {
            if (source is FileConfigurationSource { ReloadOnChange: true })
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A slim builder with config file-watching forced ON, rooted at an isolated
    /// EMPTY temporary content root — so the recursive watcher this creates can
    /// never descend into the developer's iCloud/Google-Drive domains — and
    /// disposable so the live watcher is torn down deterministically at end of test
    /// rather than surviving to nondeterministic GC. Used only by the positive
    /// controls that need the pre-fix hazard reproduced in a hermetic sandbox.
    /// </summary>
    private sealed class ForcedWatchOnHost : IDisposable
    {
        public string ContentRoot { get; }
        public WebApplicationBuilder Builder { get; }

        public ForcedWatchOnHost()
        {
            ContentRoot = Path.Combine(Path.GetTempPath(), "cb-watchtest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ContentRoot);
            Builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = ForceWatchingOnArgs,
                // Root the eager recursive watcher at the empty sandbox, NOT the
                // test process CWD (which on a macOS checkout may be under iCloud).
                ContentRootPath = ContentRoot,
            });
        }

        public void Dispose()
        {
            // The live PhysicalFilesWatcher → FileSystemWatcher is owned by the
            // source's PhysicalFileProvider. Dispose those file providers so the OS
            // watch is released deterministically before the temp root is removed
            // (rather than surviving to nondeterministic GC).
            foreach (var source in ((IConfigurationBuilder)Builder.Configuration).Sources)
            {
                if (source is FileConfigurationSource { FileProvider: IDisposable fileProvider })
                {
                    fileProvider.Dispose();
                }
            }
            try { Directory.Delete(ContentRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The fix, exercised through its real construction seam
    /// (<see cref="BridgeConfigurationExtensions.CreateServeHostBuilder"/>): the
    /// serve host registers no file source that watches for changes — so no
    /// recursive working-directory watcher, and no idle macOS cloud-storage prompt.
    /// </summary>
    [Fact]
    public void ServeHostBuilder_LeavesNoWatchingFileSource()
    {
        var builder = BridgeConfigurationExtensions.CreateServeHostBuilder();

        Assert.False(
            AnyFileSourceWatches(builder),
            "No FileConfigurationSource may watch for changes: a reloadOnChange source "
            + "starts a recursive working-directory FileSystemWatcher that trips macOS "
            + "iCloud/Google-Drive TCC prompts while the bridge is idle.");
    }

    /// <summary>
    /// Mutation guard — proves the assertion above actually bites. With watching
    /// forced on, <c>CreateSlimBuilder</c> DOES register watching file sources; if
    /// this ever comes back false the contract test above would be vacuously green
    /// and could no longer catch a regression.
    /// </summary>
    [Fact]
    public void WatchingOn_DoesRegisterWatchingFileSource_soTheContractIsMeaningful()
    {
        using var host = new ForcedWatchOnHost();

        Assert.True(
            AnyFileSourceWatches(host.Builder),
            "Expected watching-on to register at least one watching file source — if it "
            + "no longer does, the disable switch (and its contract test) guards nothing.");
    }

    // ── Runtime observation ────────────────────────────────────────────────
    // The assertions above read the source-level flag. The flag is only the
    // switch; the artifact that actually trips the macOS TCC prompt is the live
    // watcher OBJECT. A FileConfigurationProvider whose source has
    // ReloadOnChange=true calls source.FileProvider.Watch(...) in its constructor,
    // which lazily materializes a cached PhysicalFilesWatcher (an enabled,
    // subdirectory-recursive FileSystemWatcher rooted at the content root) on the
    // PhysicalFileProvider._fileWatcher field. These tests build the REAL slim host
    // and observe whether that watcher object materialized on the live providers —
    // the runtime proof that the fix removes the thing, not just the flag. (See
    // dotnet/runtime FileConfigurationProvider ctor + PhysicalFileProvider.)

    // True when at least one built configuration provider is watching a physical
    // file — i.e. its source's PhysicalFileProvider has materialized its lazy
    // _fileWatcher (the live PhysicalFilesWatcher). Reflection is the only way to
    // observe this private runtime state; the field name is pinned by the grounded
    // analysis above and guarded by the positive-control test below (if the
    // internals ever change, it goes red and tells us to re-ground).
    private static bool AnyProviderMaterializedAFileWatcher(IConfigurationRoot root)
    {
        foreach (var provider in root.Providers)
        {
            if (provider is not FileConfigurationProvider fileProvider)
            {
                continue;
            }
            if (fileProvider.Source.FileProvider is not PhysicalFileProvider physical)
            {
                continue;
            }
            var field = typeof(PhysicalFileProvider).GetField(
                "_fileWatcher",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            // If the field name ever changes the positive-control test below fails
            // loudly rather than this silently returning false.
            if (field is not null && field.GetValue(physical) is not null)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Runtime proof of the fix: the serve host (built through the real seam) has no
    /// configuration provider that materialized a live file watcher — so the process
    /// holds no recursive working-directory FileSystemWatcher and macOS raises no
    /// idle cloud-storage prompt.
    /// </summary>
    [Fact]
    public void ServeHostBuilder_MaterializesNoLiveFileWatcher()
    {
        var builder = BridgeConfigurationExtensions.CreateServeHostBuilder();

        Assert.False(
            AnyProviderMaterializedAFileWatcher(builder.Configuration),
            "A live PhysicalFilesWatcher materialized on the built host — the recursive "
            + "working-directory watch behind the macOS cloud-storage prompt is still active.");
    }

    /// <summary>
    /// Positive control for the runtime observation: with watching on, building the
    /// host DOES materialize a live file watcher. This proves
    /// <see cref="AnyProviderMaterializedAFileWatcher"/> actually observes the
    /// artifact (correct field, right timing) — otherwise the fix test above would
    /// be a false negative that could never fail. The watcher is created over an
    /// isolated temp root and disposed deterministically, never over the real CWD.
    /// </summary>
    [Fact]
    public void WatchingOn_MaterializesALiveFileWatcher()
    {
        using var host = new ForcedWatchOnHost();

        Assert.True(
            AnyProviderMaterializedAFileWatcher(host.Builder.Configuration),
            "Expected watching-on to materialize a live PhysicalFilesWatcher; if it does "
            + "not, the runtime observation guards nothing (or the reflected field name "
            + "drifted — re-ground against PhysicalFileProvider).");
    }

    /// <summary>
    /// Proves the switch is genuinely code-scoped: passing it through the serve seam
    /// overrides an opposite value in the <c>DOTNET_</c> host-config environment
    /// variable — command-line host config wins. This is why the fix does not need
    /// (and deliberately avoids) mutating the process environment.
    /// </summary>
    [Fact]
    public void ServeHostBuilder_TakesPrecedenceOverEnvironment()
    {
        const string envVar = "DOTNET_hostBuilder__reloadConfigOnChange";
        var original = Environment.GetEnvironmentVariable(envVar);
        Environment.SetEnvironmentVariable(envVar, "true"); // env says WATCH
        try
        {
            var builder = BridgeConfigurationExtensions.CreateServeHostBuilder();

            Assert.False(
                AnyFileSourceWatches(builder),
                "Command-line host config must override the DOTNET_ env var — the seam's "
                + "args switch is expected to win so the fix never has to touch the environment.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }
}
