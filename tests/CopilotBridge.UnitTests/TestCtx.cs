using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Builds a minimal <see cref="BridgeContext{MessagesRequest}"/> for routing
/// tests — just enough body/header/beta state for <c>MatchExpression.Matches</c>
/// and <c>ModelRouteResolver.Apply</c> to operate on. No network, no pipeline.
/// </summary>
internal static class TestCtx
{
    public static BridgeContext<MessagesRequest> Build(
        string model,
        string? effort = null,
        IEnumerable<string>? betas = null,
        IDictionary<string, string>? headers = null)
    {
        var body = new MessagesRequest
        {
            Model = model,
            Messages = Array.Empty<MessageParam>(),
            OutputConfig = effort is null ? null : new OutputConfig { Effort = effort },
        };

        var req = new BridgeRequest<MessagesRequest>
        {
            Method = "POST",
            Path = "/cc/v1/messages",
            Body = body,
        };
        if (headers is not null)
        {
            foreach (var (k, v) in headers) req.Headers[k] = v;
        }

        return new BridgeContext<MessagesRequest>
        {
            Request = req,
            Response = new BridgeResponse(),
            Ct = default,
            InboundBetas = betas is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(betas, StringComparer.OrdinalIgnoreCase),
        };
    }
}
