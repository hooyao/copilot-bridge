using System.Globalization;

namespace CopilotBridge.Cli.Hosting;

internal static class ServeCommand
{
    private const int DefaultPort = 8765;

    public static async Task<int> RunAsync(string[] args)
    {
        var port = DefaultPort;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length:
                case "-p" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
                        || port is < 1 or > 65535)
                    {
                        await Console.Error.WriteLineAsync($"invalid port: {args[i]}");
                        return 1;
                    }
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
                default:
                    await Console.Error.WriteLineAsync($"unknown argument: {args[i]}");
                    PrintHelp();
                    return 1;
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        return await KestrelServer.RunAsync(port, cts.Token);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: copilot-bridge serve [--port <N>]");
        Console.WriteLine();
        Console.WriteLine("Starts the HTTP bridge on http://localhost:<N> (default 8765).");
        Console.WriteLine("Endpoints:");
        Console.WriteLine("  POST /v1/messages              Forward to Copilot, filter [DONE]");
        Console.WriteLine("  POST /v1/messages/count_tokens Placeholder, returns {input_tokens:1}");
        Console.WriteLine("  GET  /v1/models                List Claude models on this account");
        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to stop. Per-request logs are written to logs/<utc>-<seq>.json.");
    }
}
