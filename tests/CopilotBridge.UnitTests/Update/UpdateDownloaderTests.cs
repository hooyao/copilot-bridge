using System.Net;
using System.Net.Http;
using CopilotBridge.Update.Wire;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Contract tests for <see cref="UpdateDownloader"/> ("Secure download and
/// archive staging"). The downloader controls the executable bytes entering the
/// transaction, so it must enforce HTTPS on every hop, bound redirects, honor a
/// finite timeout, and stream to disk. All HTTP is faked.
/// </summary>
public class UpdateDownloaderTests : IDisposable
{
    private readonly string _dir;

    public UpdateDownloaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static HttpResponseMessage Redirect(string location, HttpStatusCode code = HttpStatusCode.Found)
    {
        var resp = new HttpResponseMessage(code);
        resp.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return resp;
    }

    private static HttpResponseMessage Body(byte[] bytes)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    [Fact]
    public async Task Downloads_body_over_https_to_disk()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new FakeHttpMessageHandler((_, _) => Body(payload));
        var dl = new UpdateDownloader(new HttpClient(handler));
        var dest = Path.Combine(_dir, "a.zip");

        var result = await dl.DownloadAsync(
            "https://example/a.zip", dest, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task Redirect_to_http_is_rejected_no_downgrade()
    {
        // 200 → redirect to an insecure http:// URL. The downloader must refuse
        // rather than follow the downgrade.
        var handler = new FakeHttpMessageHandler((_, i) => i == 0
            ? Redirect("http://insecure/evil.zip")
            : Body(new byte[] { 9 }));
        var dl = new UpdateDownloader(new HttpClient(handler));

        var result = await dl.DownloadAsync(
            "https://example/a.zip", Path.Combine(_dir, "a.zip"), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("HTTPS", result.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Follows_a_bounded_https_redirect_chain()
    {
        var payload = new byte[] { 7, 7, 7 };
        var handler = new FakeHttpMessageHandler((_, i) => i switch
        {
            0 => Redirect("https://cdn.example/step2.zip"),
            1 => Redirect("https://cdn2.example/final.zip"),
            _ => Body(payload),
        });
        var dl = new UpdateDownloader(new HttpClient(handler));
        var dest = Path.Combine(_dir, "a.zip");

        var result = await dl.DownloadAsync(
            "https://example/a.zip", dest, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task Too_many_redirects_fail()
    {
        // Always redirect (to a fresh https URL) — must trip the redirect cap.
        var n = 0;
        var handler = new FakeHttpMessageHandler((_, _) => Redirect($"https://cdn.example/hop{++n}.zip"));
        var dl = new UpdateDownloader(new HttpClient(handler));

        var result = await dl.DownloadAsync(
            "https://example/a.zip", Path.Combine(_dir, "a.zip"), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("redirect", result.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Declared_length_over_cap_is_rejected()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1 }),
        };
        // Claim a huge content length (over the 256 MiB defensive cap).
        resp.Content.Headers.ContentLength = 300L * 1024 * 1024;
        var handler = new FakeHttpMessageHandler((_, _) => resp);
        var dl = new UpdateDownloader(new HttpClient(handler));

        var result = await dl.DownloadAsync(
            "https://example/a.zip", Path.Combine(_dir, "a.zip"), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("size cap", result.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_https_initial_url_is_rejected()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Body(new byte[] { 1 }));
        var dl = new UpdateDownloader(new HttpClient(handler));

        var result = await dl.DownloadAsync(
            "http://insecure/a.zip", Path.Combine(_dir, "a.zip"), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("HTTPS", result.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Application_shutdown_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new FakeHttpMessageHandler((_, _) => Body(new byte[] { 1 }));
        var dl = new UpdateDownloader(new HttpClient(handler));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dl.DownloadAsync(
            "https://example/a.zip", Path.Combine(_dir, "a.zip"), TimeSpan.FromSeconds(5), cts.Token));
    }
}
