using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Catalogs.Codex;

internal enum CodexCatalogSourceStatus
{
    Modified,
    NotModified,
    NotFound,
    Throttled,
    ServerError,
    Timeout,
    TransportFailure,
    Failed,
}

internal sealed record CodexCatalogSourceResult
{
    public required CodexCatalogSourceStatus Status { get; init; }
    public byte[]? Bytes { get; init; }
    public string? ETag { get; init; }
    public string? Sha256 { get; init; }
    public string? Error { get; init; }
}

internal interface ICodexCatalogSourceClient
{
    ValueTask<CodexCatalogSourceResult> FetchAsync(
        CodexClientVersion version,
        string? etag,
        CancellationToken cancellationToken = default);
}

internal sealed class CodexCatalogSourceClient(
    IHttpClientFactory httpClientFactory,
    IOptions<CodexModelCatalogOptions> options) : ICodexCatalogSourceClient
{
    private readonly CodexModelCatalogOptions _options = options.Value;

    public async ValueTask<CodexCatalogSourceResult> FetchAsync(
        CodexClientVersion version,
        string? etag,
        CancellationToken cancellationToken = default)
    {
#if DEBUG
        if (string.Equals(
                Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_FAIL_CODEX_CATALOG_SOURCE"),
                "1",
                StringComparison.Ordinal))
            return new()
            {
                Status = CodexCatalogSourceStatus.TransportFailure,
                Error = "test-forced Codex catalog source failure",
            };
#endif
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.SourceTimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Get, CodexCatalogSource.BuildUri(version));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(etag) && EntityTagHeaderValue.TryParse(etag, out var parsed))
            request.Headers.IfNoneMatch.Add(parsed);

        try
        {
            using var response = await httpClientFactory
                .CreateClient(UpstreamHttpClientNames.CodexCatalogSource)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var responseEtag = response.Headers.ETag?.ToString();
            if (response.StatusCode == HttpStatusCode.NotModified)
                return new() { Status = CodexCatalogSourceStatus.NotModified, ETag = responseEtag ?? etag };
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new() { Status = CodexCatalogSourceStatus.NotFound, ETag = responseEtag };
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new() { Status = CodexCatalogSourceStatus.Throttled, ETag = responseEtag };
            if ((int)response.StatusCode >= 500)
                return new() { Status = CodexCatalogSourceStatus.ServerError, ETag = responseEtag };
            if (!response.IsSuccessStatusCode)
                return new() { Status = CodexCatalogSourceStatus.Failed, ETag = responseEtag, Error = $"HTTP {(int)response.StatusCode}" };

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is > 0 && declaredLength > _options.MaxSourceBytes)
                return new() { Status = CodexCatalogSourceStatus.Failed, ETag = responseEtag, Error = "source exceeds configured size limit" };

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var body = new MemoryStream(declaredLength is > 0 ? checked((int)declaredLength.Value) : 0);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(buffer, timeout.Token);
                if (read == 0) break;
                if (body.Length + read > _options.MaxSourceBytes)
                    return new() { Status = CodexCatalogSourceStatus.Failed, ETag = responseEtag, Error = "source exceeds configured size limit" };
                body.Write(buffer, 0, read);
            }
            var bytes = body.ToArray();
            return new()
            {
                Status = CodexCatalogSourceStatus.Modified,
                ETag = responseEtag,
                Bytes = bytes,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new() { Status = CodexCatalogSourceStatus.Timeout, Error = "source request timed out" };
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new() { Status = CodexCatalogSourceStatus.TransportFailure, Error = exception.Message };
        }
    }
}

internal static class CodexCatalogSourceHttpHandler
{
    public static SocketsHttpHandler Create() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };
}
