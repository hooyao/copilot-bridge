namespace CopilotBridge.Cli.Hosting;

internal static class ServeCommand
{
    public const int DefaultPort = 8765;

    /// <summary>
    /// Start the bridge on <paramref name="port"/>. Wires up Ctrl+C cancellation
    /// and hands off to <see cref="KestrelServer.RunAsync(int, CancellationToken)"/>.
    /// </summary>
    public static async Task<int> RunAsync(int port)
    {
        if (port is < 1 or > 65535)
        {
            await Console.Error.WriteLineAsync($"invalid port: {port}");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        return await KestrelServer.RunAsync(port, cts.Token);
    }
}
