using System.Net;
using System.Net.Http;
using System.Text;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Hosting.Logging;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// End-to-end audit coverage for the count_tokens endpoint with tracing ON. The
/// other count_tokens test runs tracing OFF (so every Record* no-ops); this one
/// pins the wire-fidelity contract the audit exists for, on the endpoint with the
/// riskiest shape — it materializes ONE array and hands it to the summary probe, the
/// upstream POST, and (via the zero-copy `RecordInbound(byte[])` overload) the async
/// audit sink. It also guards the upstream-req URL, which must reflect the account's
/// resolved base URL, not a hardcoded one.
/// </summary>
public class CountTokensAuditTests
{
    private const string RequestJson =
        """{"model":"claude-opus-4-8","messages":[{"role":"user","content":"count me"}]}""";

    private sealed class StubClient(
        byte[] response,
        HttpStatusCode status = HttpStatusCode.OK,
        string contentType = "application/json",
        Exception? failure = null) : ICopilotClient
    {
        public byte[]? LastPosted { get; private set; }

        public ValueTask<HttpResponseMessage> PostCountTokensAsync(
            ReadOnlyMemory<byte> body, CancellationToken ct = default)
        {
            if (failure is not null) throw failure;
            LastPosted = body.ToArray();
            var resp = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(response),
            };
            resp.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            return new(resp);
        }

        public ValueTask<HttpResponseMessage> PostMessagesAsync(
            ReadOnlyMemory<byte> body, bool vision = false,
            IReadOnlyList<string>? anthropicBeta = null,
            IReadOnlyDictionary<string, string?>? copilotHeaderOverrides = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<HttpResponseMessage> PostResponsesAsync(
            ReadOnlyMemory<byte> body, bool vision = false, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask<CopilotModelsResponse> GetModelsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubAuth(string baseUrl) : IAuthService
    {
        public bool IsAuthenticated => true;
        public string TokenLocation => "(test)";
        public string? CopilotApiBaseUrl => baseUrl;
        public DateTimeOffset? CopilotTokenExpiry => DateTimeOffset.MaxValue;
        public ValueTask<string> EnsureGitHubTokenAsync(CancellationToken ct = default) => ValueTask.FromResult("gh");
        public ValueTask<CopilotAuthLease> GetCopilotTokenAsync(
            CopilotAuthLease? rejectedLease = null, CancellationToken ct = default) =>
            ValueTask.FromResult(new CopilotAuthLease
            {
                Token = "tok", ApiBaseUrl = CopilotApiBaseUrl!,
                RefreshAt = DateTimeOffset.MaxValue, ServerExpiresAt = DateTimeOffset.MaxValue, Generation = 1,
            });
        public void SignOut() { }
    }

    private sealed record Result(
        List<BridgeIoPayload> Audits, byte[]? PostedBody,
        int Status, string ClientBody, string? ClientContentType);

    private static async Task<Result> Run(
        string baseUrl,
        byte[] copilotResponse,
        HttpStatusCode status = HttpStatusCode.OK,
        string contentType = "application/json",
        RoutesConfig? routes = null,
        string requestJson = RequestJson,
        Exception? failure = null)
    {
        var recorder = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(recorder));
        var audit = TestAudit.Create(true, loggerFactory.CreateLogger<MessagesRequest>());
        var client = new StubClient(copilotResponse, status, contentType, failure);

        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/cc/v1/messages/count_tokens";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));
        var respStream = new MemoryStream();
        http.Response.Body = respStream;

        await ClaudeCodeCountTokensEndpoint.HandleAsync(
            http,
            client,
            new StubAuth(baseUrl),
            new RequestSummaryLogger(NullLogger<RequestSummaryLogger>.Instance),
            audit,
            CountTokensTestServices.Planner(routes),
            CountTokensTestServices.Context(),
            new CopilotBridge.Cli.Pipeline.Routing.CodexModelProfileCatalog(),
            CountTokensTestServices.Estimator(),
            CountTokensTestServices.CcOptions());

        var audits = recorder.Events
            .Select(e => e.Properties.TryGetValue("Payload", out var p) ? p as BridgeIoPayload : null)
            .Where(p => p is not null).Select(p => p!).ToList();
        return new Result(
            audits, client.LastPosted, http.Response.StatusCode,
            Encoding.UTF8.GetString(respStream.ToArray()), http.Response.ContentType);
    }

    private static string BodyText(BridgeIoPayload p) => Encoding.UTF8.GetString(p.Body, 0, p.BodyLength);

    /// <summary>
    /// Contract: with tracing on, count_tokens writes the four-boundary audit and
    /// each body is the exact wire bytes — inbound-req is the raw client request,
    /// upstream-req is the exact bytes POSTed (the shared array), upstream-resp is
    /// Copilot's response. The shared-array-across-probe/POST/sink path must survive:
    /// the audited upstream-req must equal what the stub client actually received.
    /// </summary>
    [Fact]
    public async Task TracingOn_AuditsInboundAndUpstreamBodies_ExactBytes()
    {
        var copilotResp = Encoding.UTF8.GetBytes("""{"input_tokens":7}""");
        var r = await Run("https://api.githubcopilot.com", copilotResp);

        var inReq = r.Audits.Single(a => a.Kind == "inbound-req");
        Assert.Equal(RequestJson, BodyText(inReq));

        var upReq = r.Audits.Single(a => a.Kind == "upstream-req");
        // The audited upstream-req is the SAME bytes the client POSTed (the shared
        // array), and equals the original request body (count_tokens forwards raw).
        Assert.NotNull(r.PostedBody);
        Assert.Equal(r.PostedBody, upReq.Body[..upReq.BodyLength]);
        Assert.Equal(RequestJson, BodyText(upReq));

        var upResp = r.Audits.Single(a => a.Kind == "upstream-resp");
        Assert.Equal(copilotResp, upResp.Body[..upResp.BodyLength]);
    }

    /// <summary>
    /// Contract: the audited upstream-req URL reflects the account's RESOLVED base URL,
    /// not a hardcoded api.githubcopilot.com. For an enterprise account the client
    /// POSTs to api.enterprise.githubcopilot.com, and the audit must say so — otherwise
    /// the trace misreports where the request actually went. Mutation: revert the URL
    /// to the hardcoded literal → this assertion goes RED for the enterprise base URL.
    /// </summary>
    [Fact]
    public async Task TracingOn_UpstreamReqUrl_UsesResolvedBaseUrl()
    {
        var r = await Run("https://api.enterprise.githubcopilot.com",
            Encoding.UTF8.GetBytes("""{"input_tokens":1}"""));

        var upReq = r.Audits.Single(a => a.Kind == "upstream-req");
        Assert.Equal("https://api.enterprise.githubcopilot.com/v1/messages/count_tokens", upReq.Target);
    }

    [Fact]
    public async Task NativeAnthropicFailure_PreservesStatusBodyAndContentType()
    {
        var raw = Encoding.UTF8.GetBytes("native-count-rejection");
        var r = await Run(
            "https://api.githubcopilot.com", raw,
            HttpStatusCode.UnprocessableEntity,
            "text/plain; charset=utf-8");

        Assert.Equal("native-count-rejection", r.ClientBody);
        var upstream = r.Audits.Single(a => a.Kind == "upstream-resp");
        var downstream = r.Audits.Single(a => a.Kind == "inbound-resp");
        Assert.Equal(422, upstream.Status);
        Assert.Equal(422, downstream.Status);
        Assert.Equal(raw, downstream.Body[..downstream.BodyLength]);
        Assert.Equal("text/plain; charset=utf-8", downstream.Headers["Content-Type"]);
    }

    [Fact]
    public async Task CrossRoutedCount_TraceSeparatesAllFourWireShapes()
    {
        var routes = new RoutesConfig
        {
            Locations =
            [
                new RouteLocation
                {
                    When = new MatchExpression { Model = "claude-opus-4.8" },
                    Use = new LocationUse { Model = "gpt-5.6-sol" },
                },
            ],
        };
        var raw = Encoding.UTF8.GetBytes("{\"input_tokens\":100}");
        var r = await Run(
            "https://api.githubcopilot.com", raw,
            routes: routes);

        var inbound = r.Audits.Single(a => a.Kind == "inbound-req");
        var upstream = r.Audits.Single(a => a.Kind == "upstream-req");
        var upstreamResponse = r.Audits.Single(a => a.Kind == "upstream-resp");
        var downstream = r.Audits.Single(a => a.Kind == "inbound-resp");

        Assert.Equal(RequestJson, BodyText(inbound));
        Assert.Contains("\"model\":\"gpt-5.6-sol\"", BodyText(upstream));
        Assert.Contains("\"input\":", BodyText(upstream));
        Assert.Equal(raw, upstreamResponse.Body[..upstreamResponse.BodyLength]);
        using var downstreamJson = System.Text.Json.JsonDocument.Parse(
            downstream.Body.AsMemory(0, downstream.BodyLength));
        Assert.Equal(121,
            downstreamJson.RootElement.GetProperty("input_tokens").GetInt32());
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"model\":\"claude-opus-4-8\",\"messages\":[],\"future_budget\":4096}")]
    public async Task LocalFailure_AuditMatchesExactClientResponse(string request)
    {
        var routes = new RoutesConfig
        {
            Locations =
            [
                new RouteLocation
                {
                    When = new MatchExpression { Model = "claude-opus-4.8" },
                    Use = new LocationUse { Model = "gpt-5.6-sol" },
                },
            ],
        };
        var r = await Run(
            "https://api.githubcopilot.com", Encoding.UTF8.GetBytes("unused"),
            routes: routes, requestJson: request);

        Assert.Equal(400, r.Status);
        Assert.Equal("application/json", r.ClientContentType);
        Assert.Null(r.PostedBody);
        var downstream = r.Audits.Single(a => a.Kind == "inbound-resp");
        Assert.Equal(r.Status, downstream.Status);
        Assert.Equal(r.ClientBody, BodyText(downstream));
        Assert.Equal(r.ClientContentType, downstream.Headers["Content-Type"]);
    }

    [Fact]
    public async Task UnknownModelFailure_AuditMatchesExactClientResponse()
    {
        var r = await Run(
            "https://api.githubcopilot.com", Encoding.UTF8.GetBytes("unused"),
            requestJson:
                "{\"model\":\"future-unknown-model\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}");

        Assert.Equal(400, r.Status);
        Assert.Equal("application/json", r.ClientContentType);
        Assert.Null(r.PostedBody);
        AssertClientAndAuditResponseEqual(r);
    }

    [Fact]
    public async Task UpstreamException_AuditMatchesExactClientResponse()
    {
        var r = await Run(
            "https://api.githubcopilot.com", Encoding.UTF8.GetBytes("unused"),
            failure: new HttpRequestException("forced count failure"));

        Assert.Equal(502, r.Status);
        Assert.Equal("application/json", r.ClientContentType);
        AssertClientAndAuditResponseEqual(r);
    }

    private static void AssertClientAndAuditResponseEqual(Result result)
    {
        var downstream = result.Audits.Single(a => a.Kind == "inbound-resp");
        Assert.Equal(result.Status, downstream.Status);
        Assert.Equal(result.ClientBody, BodyText(downstream));
        Assert.Equal(result.ClientContentType, downstream.Headers["Content-Type"]);
    }
}
