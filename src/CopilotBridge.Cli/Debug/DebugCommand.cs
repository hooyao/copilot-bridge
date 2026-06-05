using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;

namespace CopilotBridge.Cli.Debug;

/// <summary>
/// Diagnostic subcommands. These are throw-away tools used during development to
/// poke at Copilot's API surface; they share infrastructure with the future server
/// path (<see cref="CopilotClient"/>, <see cref="AuthService"/>) but live in their
/// own folder so they're easy to find and easy to delete.
/// </summary>
internal static class DebugCommand
{
    private const string MessagesEndpoint = "/v1/messages";

    public static async Task<int> ListModelsAsync(bool showAll)
    {
        if (TokenStore.TryLoad() is null)
        {
            Console.Error.WriteLine("Not logged in. Run `auth login` first.");
            return 1;
        }

        using var http = CreateHttpClient();
        using var auth = new AuthService(http);
        // GetModelsAsync doesn't use the retry path; supply defaults + a null
        // logger so this throwaway debug command needn't wire up DI.
        var copilot = new CopilotClient(
            http, auth, new CopilotHeaderFactory(),
            Microsoft.Extensions.Options.Options.Create(new Hosting.Options.UpstreamRetryOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CopilotClient>.Instance);

        try
        {
            // Force a token fetch so CopilotApiBaseUrl is populated before the /models call.
            _ = await auth.GetCopilotTokenAsync();

            var models = await copilot.GetModelsAsync();

            var matching = models.Data
                .Where(m => showAll || (m.SupportedEndpoints?.Contains(MessagesEndpoint) ?? false))
                .OrderBy(m => m.Id, StringComparer.Ordinal)
                .ToList();

            if (showAll)
                Console.WriteLine($"All models ({matching.Count}):");
            else
                Console.WriteLine($"Models advertising {MessagesEndpoint} ({matching.Count} of {models.Data.Count}):");
            Console.WriteLine();

            foreach (var m in matching)
            {
                var endpoints = m.SupportedEndpoints is null || m.SupportedEndpoints.Count == 0
                    ? "(none)"
                    : string.Join(",", m.SupportedEndpoints);
                Console.WriteLine($"  {m.Id,-30} {m.Vendor,-12} endpoints=[{endpoints}]");

                if (showAll && m.Capabilities is { } cap)
                {
                    var s = cap.Supports;
                    var effort = s?.ReasoningEffort is { Count: > 0 } e ? string.Join(",", e) : "(none)";
                    var budget = s is { MinThinkingBudget: not null, MaxThinkingBudget: not null }
                        ? $"{s.MinThinkingBudget}..{s.MaxThinkingBudget}"
                        : "(none)";
                    Console.WriteLine($"      family={cap.Family}  tokenizer={cap.Tokenizer}  type={cap.Type}");
                    Console.WriteLine($"      tools={s?.ToolCalls}  parallel={s?.ParallelToolCalls}  vision={s?.Vision}  streaming={s?.Streaming}");
                    Console.WriteLine($"      thinking={s?.AdaptiveThinking}  effort=[{effort}]  thinking_budget={budget}");
                    if (cap.Limits is { } l)
                        Console.WriteLine($"      ctx={l.MaxContextWindowTokens}  max_out={l.MaxOutputTokens}  max_prompt={l.MaxPromptTokens}");
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed: {ex.Message}");
            return 1;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("copilot-bridge/0.1");
        return http;
    }
}
