namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Catches uncaught exceptions at the top of <c>Program.cs</c>, prints them
/// to stderr, and — when stdin is a real terminal — waits for a keypress
/// before returning. Lets a user double-clicking the .exe actually see the
/// error before the console window vanishes. Skips the pause when stdin is
/// redirected (CI / pipes / parent process), since blocking there hangs the
/// caller forever.
/// </summary>
internal static class FatalErrorHandler
{
    /// <summary>
    /// Print <paramref name="ex"/> to stderr (full <c>ToString()</c> for unknown
    /// exceptions, just <see cref="Exception.Message"/> for recognized
    /// <see cref="BridgeStartupException"/> — its trace is noise to the user).
    /// Then pause for keypress if attached to a terminal.
    /// </summary>
    public static void PauseAndExit(Exception ex)
    {
        var rendered = ex is BridgeStartupException ? ex.Message : ex.ToString();

        try
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("=== FATAL ERROR ===");
            Console.Error.WriteLine(rendered);
            Console.Error.WriteLine();
        }
        catch
        {
            // Best-effort: even stderr can fail (closed handle).
        }

        // Skip the pause when stdin is redirected — blocking on ReadKey in a
        // headless context (CI, pipe, parent process) hangs forever.
        if (Console.IsInputRedirected)
        {
            return;
        }

        try
        {
            Console.Error.WriteLine("Press any key to close...");
            Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
            // No console attached (truly headless) — nothing more we can do.
        }
    }
}
