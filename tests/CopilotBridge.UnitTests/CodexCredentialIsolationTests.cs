using System.Text;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Hosting.ClientConfig;
using CopilotBridge.Cli.Hosting.Logging;
using Xunit;

namespace CopilotBridge.UnitTests;

public sealed class CodexCredentialIsolationTests
{
    [Fact]
    public void Real_token_cannot_enter_config_catalog_or_trace_artifacts()
    {
        const string realToken = "copilot-real-token-credential-isolation-canary";
        var invocation = new CodexProviderAuthInvocation("bridge.exe", ["auth", "provider-token"]);
        var connection = new BridgeConnection(8765);
        var (config, _) = CodexConfigurator.BuildContent(null, connection, invocation);

        var baseline = CodexCatalogTestFixtures.LoadCapturedBaseline();
        var catalog = string.Join("\n", baseline.Models.Select(model => model.GetRawText()));

        var traceDir = Path.Combine(Path.GetTempPath(), "cb-trace-auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var sink = new BridgeIoSink(traceDir))
            {
                var logger = new Serilog.LoggerConfiguration()
                    .MinimumLevel.Verbose()
                    .Destructure.AsScalar<BridgeIoPayload>()
                    .WriteTo.Sink(sink)
                    .CreateLogger();
                logger.ForContext("Payload", new BridgeIoPayload
                {
                    Seq = 1,
                    TraceId = "20260805-000000-0001",
                    TimestampUtc = DateTime.UtcNow,
                    Kind = "upstream-req",
                    Method = "POST",
                    Target = "https://api.githubcopilot.com/responses",
                    Headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer " + realToken,
                    },
                    Body = Encoding.UTF8.GetBytes("{}"),
                    BodyLength = 2,
                }).Information("bridge io");
                logger.Dispose();
            }

            var trace = File.ReadAllText(Directory.GetFiles(traceDir, "*.json").Single());
            Assert.Contains("<redacted>", trace, StringComparison.Ordinal);
            Assert.DoesNotContain(realToken, trace, StringComparison.Ordinal);
            Assert.DoesNotContain(realToken, config, StringComparison.Ordinal);
            Assert.DoesNotContain(realToken, catalog, StringComparison.Ordinal);
            Assert.DoesNotContain(AuthCommand.ProviderSentinel, catalog, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(traceDir, true); } catch { }
        }
    }
}
