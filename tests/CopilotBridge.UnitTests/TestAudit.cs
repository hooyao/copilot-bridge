using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Builds a <see cref="RequestAudit"/> for unit tests. The seam takes an
/// <c>IOptions&lt;TracingOptions&gt;</c> + an <c>ILogger&lt;MessagesRequest&gt;</c>;
/// tests only care about the on/off flag and (when asserting artifacts) the
/// logger, so this wraps the boilerplate. Pass a recording logger to observe the
/// emitted bridge-IO payloads; omit it for pure on/off behavior tests.
/// </summary>
internal static class TestAudit
{
    public static RequestAudit Create(bool enabled, ILogger<MessagesRequest>? io = null) =>
        new(
            Options.Create(new TracingOptions { Enabled = enabled }),
            io ?? NullLogger<MessagesRequest>.Instance);
}
