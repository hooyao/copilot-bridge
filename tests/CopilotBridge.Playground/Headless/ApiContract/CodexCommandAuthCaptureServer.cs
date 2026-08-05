using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace CopilotBridge.Playground.Headless;

internal sealed class CodexCommandAuthCaptureServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly string _catalogJson;

    public string BaseUrl { get; }
    public ConcurrentQueue<(string Path, string? Authorization, string? UserAgent, string? RawUrl)> Requests { get; } = new();

    public CodexCommandAuthCaptureServer(string catalogJson)
    {
        _catalogJson = catalogJson;
        var port = GetFreePort();
        BaseUrl = $"http://localhost:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = LoopAsync();
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }

            try
            {
                var path = context.Request.Url?.AbsolutePath ?? "";
                Requests.Enqueue((
                    path,
                    context.Request.Headers["Authorization"],
                    context.Request.Headers["User-Agent"],
                    context.Request.RawUrl));
                if (path == "/models")
                {
                    await WriteAsync(context.Response, "application/json", _catalogJson);
                }
                else if (path == "/responses")
                {
                    var response =
                        "event: response.created\n" +
                        "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_auth\",\"status\":\"in_progress\"}}\n\n" +
                        "event: response.output_item.added\n" +
                        "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"msg_auth\",\"role\":\"assistant\",\"status\":\"in_progress\",\"content\":[]}}\n\n" +
                        "event: response.output_text.delta\n" +
                        "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_auth\",\"output_index\":0,\"content_index\":0,\"delta\":\"sentinel-ok\"}\n\n" +
                        "event: response.output_item.done\n" +
                        "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"msg_auth\",\"status\":\"completed\"}}\n\n" +
                        "event: response.completed\n" +
                        "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_auth\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}\n\n";
                    await WriteAsync(context.Response, "text/event-stream", response);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = 200;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
        try { await _loop; } catch { }
        _listener.Close();
        _stop.Dispose();
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
