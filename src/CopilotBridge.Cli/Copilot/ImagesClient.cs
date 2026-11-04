using System.Net;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Copilot;

/// <summary>Authenticated, opaque transport for Copilot's Images endpoints.</summary>
internal sealed class ImagesClient(
    IHttpClientFactory httpClientFactory,
    IAuthService auth,
    CopilotHeaderFactory headers,
    IOptions<UpstreamTimeoutOptions> timeoutOptions,
    ILogger<ImagesClient> log) : IImagesClient
{
    public async ValueTask<HttpResponseMessage> PostImageAsync(
        ImageOperation operation, ReadOnlyMemory<byte> body, string? contentType,
        CancellationToken ct = default)
    {
        var path = operation switch
        {
            ImageOperation.Generate => "/v1/images/generations",
            ImageOperation.Edit => "/v1/images/edits",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        var lease = await auth.GetCopilotTokenAsync(ct: ct);
        var replayed = false;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{lease.ApiBaseUrl}{path}")
            {
                Content = new ReadOnlyMemoryContent(body),
            };
            if (contentType is not null)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            headers.ApplyTo(request, lease.Token, vision: operation == ImageOperation.Edit,
                integrationId: lease.IntegrationId);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var seconds = timeoutOptions.Value.FirstByteTimeoutSeconds;
            if (seconds > 0) deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
            HttpResponseMessage response;
            try
            {
                response = await httpClientFactory.CreateClient(UpstreamHttpClientNames.Images)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new UpstreamTimeoutException(UpstreamTimeoutPhase.FirstByte, TimeSpan.FromSeconds(seconds));
            }
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);

            var rejection = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => CopilotLeaseRejectionReason.Unauthorized,
                HttpStatusCode.Forbidden => CopilotLeaseRejectionReason.Forbidden,
                _ => (CopilotLeaseRejectionReason?)null,
            };
            if (replayed || rejection is null) return response;

            CopilotAuthLease replacement;
            try
            {
                replacement = await auth.GetCopilotTokenAsync(
                    new CopilotLeaseRejection(lease, rejection.Value), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Images authentication recovery failed; returning the original upstream rejection");
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
            response.Dispose();
            lease = replacement;
            replayed = true;
        }
    }
}
