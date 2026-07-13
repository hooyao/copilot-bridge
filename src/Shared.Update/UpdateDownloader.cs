using System.Net;

namespace CopilotBridge.Update.Wire;

/// <summary>
/// Downloads exactly the planned Release Asset URL over HTTPS into the private
/// archive file, streaming to disk (never buffering the whole archive in
/// memory). Automatic redirect following is disabled; a short, finite redirect
/// chain is followed manually and every hop must be HTTPS. Size/digest
/// verification is the caller's responsibility (see <see cref="ArchiveExtractor"/>).
/// </summary>
internal sealed class UpdateDownloader
{
    private const int MaxRedirects = 5;
    private const long MaxArchiveBytes = 256L * 1024 * 1024; // defensive cap

    private readonly HttpClient _http;

    /// <summary>
    /// Construct over an <see cref="HttpClient"/> whose handler has automatic
    /// redirects disabled. <see cref="CreateDefaultHandler"/> builds one.
    /// </summary>
    public UpdateDownloader(HttpClient http) => _http = http;

    /// <summary>A handler with redirects disabled, for production use.</summary>
    public static HttpClientHandler CreateDefaultHandler() =>
        new() { AllowAutoRedirect = false };

    /// <summary>
    /// Download <paramref name="url"/> to <paramref name="destinationPath"/>.
    /// Returns success or a secret-free failure reason. Rejects a non-HTTPS URL
    /// or redirect, more than <see cref="MaxRedirects"/> hops, or a body over the
    /// defensive size cap.
    /// </summary>
    public async Task<UpdateStepResult> DownloadAsync(
        string url, string destinationPath, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;

        try
        {
            var current = url;
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!current.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return UpdateStepResult.Fail("download URL is not HTTPS");
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, current);
                using var resp = await _http
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);

                if (IsRedirect(resp.StatusCode))
                {
                    var location = resp.Headers.Location;
                    if (location is null)
                    {
                        return UpdateStepResult.Fail("redirect without location");
                    }
                    current = location.IsAbsoluteUri
                        ? location.ToString()
                        : new Uri(new Uri(current), location).ToString();
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    return UpdateStepResult.Fail($"download returned HTTP {(int)resp.StatusCode}");
                }

                var declared = resp.Content.Headers.ContentLength;
                if (declared is > MaxArchiveBytes)
                {
                    return UpdateStepResult.Fail("archive exceeds size cap");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await using var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var dst = new FileStream(
                    destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxArchiveBytes)
                    {
                        return UpdateStepResult.Fail("archive exceeds size cap");
                    }
                    await dst.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                await dst.FlushAsync(token).ConfigureAwait(false);
                return UpdateStepResult.Success();
            }

            return UpdateStepResult.Fail("too many redirects");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancellation — not a fail-open reason
        }
        catch (OperationCanceledException)
        {
            return UpdateStepResult.Fail("download timed out");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException or InvalidOperationException)
        {
            // Includes a malformed remote URL (UriFormatException while building the
            // request or resolving a redirect) — treat it as an ordinary download
            // failure so the updater fails open promptly instead of letting the
            // parent wait out the handoff timeout.
            return UpdateStepResult.Fail($"download failed: {ex.GetType().Name}");
        }
    }

    private static bool IsRedirect(HttpStatusCode code) => code is
        HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;
}
