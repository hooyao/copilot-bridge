using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

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
internal sealed class DetectorSetFactory
{
    private readonly ResponseModelRewriteOptions _rewrite;
    private readonly ToolLeakGuardOptions _leak;

    public DetectorSetFactory(
        IOptions<ResponseModelRewriteOptions> rewrite,
        IOptions<ToolLeakGuardOptions> leak)
    {
        _rewrite = rewrite.Value;
        _leak = leak.Value;
    }

    /// <summary>
    /// Build the detectors for this request. Returns an empty list when nothing is
    /// enabled/applicable so the stage can skip wrapping the stream entirely.
    /// </summary>
    public IReadOnlyList<IResponseDetector> Build(BridgeContext<MessagesRequest> ctx)
    {
        var list = new List<IResponseDetector>(3);

        // 1. DONE filter — always on (it is not user-configurable; the [DONE]
        //    terminator would crash the Anthropic SDK if forwarded).
        list.Add(new DoneFilterDetector());

        // 2. Model rewrite — its own Enabled + only active when original != resolved.
        var rewrite = new ModelRewriteDetector(
            _rewrite.Enabled,
            ctx.OriginalRequestedModel,
            ctx.Request.Body.Model);
        list.Add(rewrite);

        // 3. Tool-leak guard — only when enabled.
        if (_leak.Enabled)
        {
            list.Add(new ToolLeakDetector(_leak, ctx.Request.Body.Tools));
        }

        return list;
    }
}
