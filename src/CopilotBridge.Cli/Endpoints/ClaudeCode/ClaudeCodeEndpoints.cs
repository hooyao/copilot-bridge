using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace CopilotBridge.Cli.Endpoints.ClaudeCode;

/// <summary>
/// Mounts Claude Code's endpoints under the <c>/cc/v1/...</c> prefix. Claude
/// Code's <c>ANTHROPIC_BASE_URL</c> must be configured to
/// <c>http://localhost:&lt;port&gt;/cc</c>; its client appends <c>/v1/messages</c>
/// (etc.) and lands here.
/// </summary>
internal static class ClaudeCodeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/cc/v1/messages", ClaudeCodeMessagesEndpoint.HandleAsync);
        app.MapPost("/cc/v1/messages/count_tokens", ClaudeCodeCountTokensEndpoint.HandleAsync);
        app.MapGet("/cc/v1/models", ClaudeCodeModelsEndpoint.HandleAsync);
    }
}
