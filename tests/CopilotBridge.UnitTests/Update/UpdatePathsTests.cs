using CopilotBridge.Update.Wire;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Contract tests for path confinement ("Secure download and archive staging",
/// "Immutable plan and policy-free executor"). Uses a temp root as the install
/// root so both Windows and POSIX semantics are exercised on the host OS.
/// </summary>
public class UpdatePathsTests
{
    private static string Root() => Path.Combine(Path.GetTempPath(), "cb-paths-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Contained_relative_entry_resolves_inside_root()
    {
        var root = Root();
        var resolved = UpdatePaths.ResolveContained(root, "copilot-bridge.exe");
        Assert.NotNull(resolved);
        Assert.True(UpdatePaths.IsInside(root, resolved!));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData("..\\escape.txt")]
    public void Traversal_entries_are_rejected(string entry)
    {
        Assert.Null(UpdatePaths.ResolveContained(Root(), entry));
    }

    [Fact]
    public void Absolute_entries_are_rejected()
    {
        Assert.Null(UpdatePaths.ResolveContained(Root(), "/etc/passwd"));
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(UpdatePaths.ResolveContained(Root(), @"C:\Windows\System32\x"));
            // Drive-relative ("C:x") resolves against the drive's current dir, NOT
            // the staging root — must be rejected as rooted.
            Assert.Null(UpdatePaths.ResolveContained(Root(), "C:evil.exe"));
            // UNC path.
            Assert.Null(UpdatePaths.ResolveContained(Root(), @"\\server\share\x"));
        }
    }

    [Fact]
    public void IsInside_true_for_self_and_descendants_false_for_siblings()
    {
        var root = Path.Combine(Path.GetTempPath(), "cb-inside");
        Assert.True(UpdatePaths.IsInside(root, root));
        Assert.True(UpdatePaths.IsInside(root, Path.Combine(root, "a", "b.txt")));
        Assert.False(UpdatePaths.IsInside(root, Path.Combine(Path.GetTempPath(), "cb-inside-sibling", "x")));
    }
}
