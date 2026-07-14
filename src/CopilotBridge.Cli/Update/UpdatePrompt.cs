using System.Text;

namespace CopilotBridge.Cli.Update;

/// <summary>
/// Console seam for the update prompt so rendering and consent are testable
/// without a real terminal. Production uses <see cref="SystemUpdateConsole"/>.
/// </summary>
internal interface IUpdateConsole
{
    /// <summary>True when stdin cannot be used for an interactive prompt.</summary>
    bool IsInputUnavailable { get; }

    void WriteLine(string text);

    /// <summary>Read one line of input, or null at end-of-stream / on error.</summary>
    string? ReadLine();
}

/// <summary>Real console: writes to stdout, reads stdin, reports redirection.</summary>
internal sealed class SystemUpdateConsole : IUpdateConsole
{
    public bool IsInputUnavailable
    {
        get
        {
            try
            {
                return Console.IsInputRedirected;
            }
            catch
            {
                // No console attached at all — treat as unavailable.
                return true;
            }
        }
    }

    public void WriteLine(string text) => Console.WriteLine(text);

    public string? ReadLine()
    {
        try
        {
            return Console.ReadLine();
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Renders the release announcement and asks for consent. Release title/body are
/// untrusted remote data: normal line breaks and tabs are preserved, but other
/// control/escape characters are visibly encoded so a crafted release cannot
/// drive the terminal. Only a trimmed case-insensitive <c>y</c>/<c>yes</c>
/// accepts; a non-interactive stdin never installs and never blocks.
/// </summary>
internal static class UpdatePrompt
{
    private const string Separator = "--------------------------------------------------";

    /// <summary>
    /// Print the announcement, then (when interactive) ask to install. Returns
    /// true only on explicit interactive consent.
    /// </summary>
    public static bool Announce(
        IUpdateConsole console,
        string installedVersion,
        string availableVersion,
        bool isPreRelease,
        string? releaseTitle,
        string? publishedAt,
        string? releaseBody,
        string releaseUrl)
    {
        console.WriteLine(string.Empty);
        console.WriteLine("A new copilot-bridge version is available.");
        console.WriteLine(string.Empty);
        console.WriteLine($"Installed version: {installedVersion}");
        console.WriteLine($"Available version: {availableVersion}");
        console.WriteLine($"Channel: {(isPreRelease ? "Prerelease" : "Stable")}");
        if (!string.IsNullOrWhiteSpace(releaseTitle))
        {
            console.WriteLine($"Title: {Sanitize(releaseTitle)}");
        }
        if (!string.IsNullOrWhiteSpace(publishedAt))
        {
            console.WriteLine($"Published: {Sanitize(publishedAt)}");
        }
        console.WriteLine(string.Empty);
        console.WriteLine("Release notes:");
        console.WriteLine(Separator);
        console.WriteLine(string.IsNullOrWhiteSpace(releaseBody)
            ? "No release notes were provided for this release."
            : Sanitize(releaseBody));
        console.WriteLine(Separator);
        console.WriteLine(string.Empty);
        console.WriteLine("Release page:");
        console.WriteLine(Sanitize(releaseUrl));
        console.WriteLine(string.Empty);

        if (console.IsInputUnavailable)
        {
            console.WriteLine(
                "Skipping update install: standard input is not interactive. " +
                "Starting the current version. Update manually or run interactively to install.");
            return false;
        }

        console.WriteLine("Install this update now? [y/N]");
        var answer = console.ReadLine();
        return IsAffirmative(answer);
    }

    private static bool IsAffirmative(string? answer)
    {
        if (answer is null)
        {
            return false;
        }
        var trimmed = answer.Trim();
        return string.Equals(trimmed, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Keep <c>\n</c>, <c>\r</c>, and <c>\t</c> (real release notes are
    /// multi-line) but replace every other C0/C1 control character with a
    /// visible <c>\uXXXX</c> token so escape sequences cannot reach the terminal.
    /// </summary>
    internal static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\n' or '\r' or '\t')
            {
                sb.Append(c);
            }
            else if (char.IsControl(c))
            {
                sb.Append("\\u").Append(((int)c).ToString("X4"));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
