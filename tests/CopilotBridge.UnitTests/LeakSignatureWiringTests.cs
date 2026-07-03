using System.Linq;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Guards the single-source wiring for leak signatures: every id in
/// <see cref="LeakSignatures.All"/> must have a config flag (via
/// <see cref="ToolLeakSignaturesOptions.IsEnabled"/>) AND a matcher built by
/// <see cref="ResponseLeakAutomaton"/>. These catch the "add a signature, forget one
/// of the parallel lists" drift that previously failed silently (a new id with no
/// IsEnabled case or no matcher would be unwatched with no compile error).
/// </summary>
public class LeakSignatureWiringTests
{
    [Fact]
    public void EverySignatureId_HasAConfigFlag()
    {
        // Contract: IsEnabled resolves every LeakSignatures.All id — a new id without
        // a case throws here, rather than silently defaulting a signature off.
        var opts = new ToolLeakSignaturesOptions();
        foreach (var id in LeakSignatures.All)
        {
            var ex = Record.Exception(() => opts.IsEnabled(id));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void UnknownSignatureId_Throws()
    {
        // Contract: a typo / unknown id fails loudly, not silently.
        var opts = new ToolLeakSignaturesOptions();
        Assert.ThrowsAny<System.ArgumentException>(() => opts.IsEnabled("not-a-signature"));
    }

    [Fact]
    public void AllSignaturesEnabled_BuildsAMatcherForEveryId()
    {
        // Contract: with every signature enabled the automaton builds exactly the
        // LeakSignatures.All set — no id is left without a matcher (matcher-factory
        // drift) and no extra matcher exists that isn't a known id.
        var all = new System.Collections.Generic.HashSet<string>(LeakSignatures.All);
        var automaton = new ResponseLeakAutomaton(
            toolNames: new[] { "Read" },
            enabledSignatures: all);

        Assert.Equal(
            LeakSignatures.All.OrderBy(x => x),
            automaton.BuiltSignatures.OrderBy(x => x));
    }

    [Fact]
    public void DisablingOneFlag_OmitsExactlyThatMatcher()
    {
        // Contract: each flag is surgical — turning Tick off removes only the tick
        // matcher; every other signature's matcher is still built.
        var opts = new ToolLeakSignaturesOptions { Tick = false };
        var enabled = LeakSignatures.All.Where(opts.IsEnabled).ToHashSet();

        var automaton = new ResponseLeakAutomaton(new[] { "Read" }, enabled);

        Assert.DoesNotContain(LeakSignatures.Tick, automaton.BuiltSignatures);
        Assert.Contains(LeakSignatures.Invoke, automaton.BuiltSignatures);
        Assert.Contains(LeakSignatures.Channel, automaton.BuiltSignatures);
        Assert.Equal(LeakSignatures.All.Length - 1, automaton.BuiltSignatures.Count);
    }

    [Fact]
    public void DefaultOptions_EnableEverySignature()
    {
        // Contract: the default (an absent Signatures config block) enables all six —
        // backward-compatible with pre-toggle behavior.
        var opts = new ToolLeakSignaturesOptions();
        foreach (var id in LeakSignatures.All)
        {
            Assert.True(opts.IsEnabled(id), $"{id} should default enabled");
        }
    }
}
