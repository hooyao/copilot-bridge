using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Builds the ordered, per-request set of <see cref="IResponseDetector"/>s for a
/// response. A seam so tests can supply a custom detector set to exercise the
/// stage's action-precedence rendering without the concrete config-bound factory.
/// </summary>
internal interface IDetectorSetFactory
{
    IReadOnlyList<IResponseDetector> Build(BridgeContext<MessagesRequest> ctx);
}

/// <summary>
/// Builds the ordered, per-request set of <see cref="IResponseDetector"/>s for a
/// response. Registered as a singleton (holds bound options); produces fresh
/// detector instances on each call so cross-delta streaming state never leaks
/// across requests (design.md D8).
/// </summary>
/// <remarks>
/// Order reproduces the former response-stage order: DONE-filter first, then
/// model-rewrite (so the rewriter only sees events that will actually reach the
/// client), then the tool-leak guard. A detector that is disabled by config is
/// simply omitted from the set.
/// </remarks>
internal sealed class DetectorSetFactory : IDetectorSetFactory
{
    private readonly ResponseModelRewriteOptions _rewrite;
    private readonly ToolLeakGuardOptions _leak;
    private readonly ILogger<ToolLeakDetector> _toolLeakLog;

    public DetectorSetFactory(
        IOptions<ResponseModelRewriteOptions> rewrite,
        IOptions<ToolLeakGuardOptions> leak,
        ILogger<ToolLeakDetector> toolLeakLog)
    {
        _rewrite = rewrite.Value;
        _leak = leak.Value;
        _toolLeakLog = toolLeakLog;
    }

    /// <summary>
    /// Build the detectors for this request, in precedence order. Always includes
    /// the always-on DONE filter, so the set is never empty; the two configurable
    /// detectors are added only when enabled.
    /// </summary>
    public IReadOnlyList<IResponseDetector> Build(BridgeContext<MessagesRequest> ctx)
    {
        var list = new List<IResponseDetector>(3);

        // 1. DONE filter — always on. Not user-configurable: forwarding the
        //    [DONE] terminator crashes the Anthropic SDK, so it must always run.
        //    (This means the set is never empty, which is intentional.)
        list.Add(new DoneFilterDetector());

        // 2. Model rewrite — only when enabled. When on it still self-inerts if
        //    the router didn't rewrite the model (original == resolved).
        if (_rewrite.Enabled)
        {
            list.Add(new ModelRewriteDetector(
                _rewrite.Enabled,
                ctx.OriginalRequestedModel,
                ctx.Request.Body.Model));
        }

        // 3. Tool-leak guard — only when enabled.
        if (_leak.Enabled)
        {
            list.Add(new ToolLeakDetector(_leak, ctx.Request.Body.Tools, _toolLeakLog));
        }

        return list;
    }
}
