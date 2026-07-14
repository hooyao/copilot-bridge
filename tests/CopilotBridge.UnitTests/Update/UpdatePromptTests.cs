using CopilotBridge.Cli.Update;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Contract tests for <see cref="UpdatePrompt"/> from the "Release presentation
/// and explicit consent" requirement. A fake console drives interactive and
/// non-interactive paths and captures output ordering.
/// </summary>
public class UpdatePromptTests
{
    private sealed class FakeConsole : IUpdateConsole
    {
        private readonly Queue<string?> _input;
        public List<string> Output { get; } = [];
        public bool IsInputUnavailable { get; init; }

        public FakeConsole(params string?[] input) => _input = new Queue<string?>(input);

        public void WriteLine(string text) => Output.Add(text);
        public string? ReadLine() => _input.Count > 0 ? _input.Dequeue() : null;

        public string Transcript => string.Join("\n", Output);
    }

    private static bool Announce(FakeConsole console, string? body = "Some notes") =>
        UpdatePrompt.Announce(
            console,
            installedVersion: "0.4.13",
            availableVersion: "0.4.14-beta.1",
            isPreRelease: true,
            releaseTitle: "copilot-bridge 0.4.14-beta.1",
            publishedAt: "2026-07-15T00:00:00Z",
            releaseBody: body,
            releaseUrl: "https://github.com/hooyao/copilot-bridge/releases/tag/v0.4.14-beta.1");

    [Fact]
    public void Notes_and_url_precede_the_prompt()
    {
        var console = new FakeConsole("n");
        Announce(console);

        var t = console.Transcript;
        var notesIdx = t.IndexOf("Release notes:", System.StringComparison.Ordinal);
        var urlIdx = t.IndexOf("Release page:", System.StringComparison.Ordinal);
        var promptIdx = t.IndexOf("Install this update now?", System.StringComparison.Ordinal);

        Assert.True(notesIdx >= 0 && urlIdx > notesIdx && promptIdx > urlIdx,
            "notes → url → prompt ordering violated");
    }

    [Fact]
    public void Empty_notes_render_explicit_message()
    {
        var console = new FakeConsole("n");
        Announce(console, body: "");
        Assert.Contains("No release notes were provided for this release.", console.Transcript);
    }

    [Theory]
    [InlineData("y")]
    [InlineData("Y")]
    [InlineData("yes")]
    [InlineData("  YES  ")]
    public void Explicit_yes_accepts(string answer)
    {
        Assert.True(Announce(new FakeConsole(answer)));
    }

    [Theory]
    [InlineData("")]      // bare Enter
    [InlineData("n")]
    [InlineData("no")]
    [InlineData("maybe")]
    [InlineData("yep")]   // only y/yes accept
    public void Non_yes_declines(string answer)
    {
        Assert.False(Announce(new FakeConsole(answer)));
    }

    [Fact]
    public void Null_input_declines()
    {
        Assert.False(Announce(new FakeConsole((string?)null)));
    }

    [Fact]
    public void Noninteractive_never_installs_and_never_reads()
    {
        // IsInputUnavailable=true simulates redirected/absent stdin; the prompt
        // must be skipped entirely, not just answered negatively.
        var console = new FakeConsole { IsInputUnavailable = true };
        var installed = UpdatePrompt.Announce(
            console, "0.4.13", "0.4.14", isPreRelease: false,
            releaseTitle: "t", publishedAt: "p", releaseBody: "b",
            releaseUrl: "https://example/tag");

        Assert.False(installed);
        Assert.DoesNotContain("Install this update now?", console.Transcript);
        Assert.Contains("Skipping update install", console.Transcript);
    }

    [Fact]
    public void Control_characters_in_notes_are_visibly_encoded_but_newlines_kept()
    {
        var console = new FakeConsole("n");
        // ESC (0x1B) must be neutralized; \n must survive.
        UpdatePrompt.Announce(
            console, "0.4.13", "0.4.14", isPreRelease: false,
            releaseTitle: "title", publishedAt: "p",
            releaseBody: "line1\n\u001b[31mred\u001b[0m\nline2",
            releaseUrl: "https://example/tag");

        var t = console.Transcript;
        Assert.DoesNotContain(t, ch => ch == '\u001b');
        Assert.Contains("\\u001B", t);
        Assert.Contains("line1", t);
        Assert.Contains("line2", t);
    }

    [Fact]
    public void Sanitize_preserves_tabs_and_newlines()
    {
        var s = UpdatePrompt.Sanitize("a\tb\nc\r\nd\u0007e");
        Assert.Equal("a\tb\nc\r\nd\\u0007e", s);
    }

    [Theory]
    [InlineData(true, "Prerelease")]
    [InlineData(false, "Stable")]
    public void Channel_label_follows_the_prerelease_flag(bool isPreRelease, string expectedLabel)
    {
        // The gate now derives isPreRelease from BOTH GitHub's flag and the parsed
        // SemVer (fix 8-3, matching selection): whatever it decides, the announced
        // channel label must reflect it - a mislabeled beta accepted as a prerelease
        // must NOT be shown to the user as "Stable".
        var console = new FakeConsole("n");
        UpdatePrompt.Announce(
            console, "0.4.13", "0.4.14", isPreRelease,
            releaseTitle: "t", publishedAt: "p", releaseBody: "b",
            releaseUrl: "https://example/tag");

        Assert.Contains($"Channel: {expectedLabel}", console.Transcript);
        var other = isPreRelease ? "Channel: Stable" : "Channel: Prerelease";
        Assert.DoesNotContain(other, console.Transcript);
    }
}
