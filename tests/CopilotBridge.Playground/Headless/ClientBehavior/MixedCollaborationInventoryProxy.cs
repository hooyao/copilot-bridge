using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Test-only ingress proxy: advertise already-plain send/follow-up schemas beside
/// encrypted spawn schemas, while preserving real client execution and live Copilot.
/// Responses are forwarded unchanged so the bridge must select plaintext dispatch.
/// </summary>
internal sealed class MixedCollaborationInventoryProxy : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(9) };
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly string _bridgeUrl;
    private int _changedInventories;

    internal string BaseUrl { get; }
    internal int ChangedInventories => Volatile.Read(ref _changedInventories);

    internal MixedCollaborationInventoryProxy(string bridgeUrl)
    {
        _bridgeUrl = bridgeUrl;
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)socket.LocalEndpoint).Port}";
        socket.Stop();
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().WaitAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (HttpListenerException) when (_stop.IsCancellationRequested) { return; }
            _ = ForwardAsync(context);
        }
    }

    private async Task ForwardAsync(HttpListenerContext context)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod),
                _bridgeUrl + context.Request.RawUrl);
            if (context.Request.HasEntityBody)
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = JsonNode.Parse(await reader.ReadToEndAsync(_stop.Token))!.AsObject();
                if (body["tools"] is JsonArray tools) MakeMixed(tools);
                if (body["input"] is JsonArray input)
                    foreach (var item in input.OfType<JsonObject>())
                        if (item["type"]?.GetValue<string>() is "additional_tools" or "tool_search_output"
                            && item["tools"] is JsonArray historical) MakeMixed(historical);
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _stop.Token);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            context.Response.SendChunked = true;
            await response.Content.CopyToAsync(context.Response.OutputStream, _stop.Token);
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception)
        {
            try { context.Response.StatusCode = 502; }
            catch (ObjectDisposedException) { }
        }
        finally { context.Response.Close(); }
    }

    private void MakeMixed(JsonArray tools)
    {
        foreach (var tool in tools.OfType<JsonObject>())
        {
            if (tool["type"]?.GetValue<string>() != "namespace"
                || tool["name"]?.GetValue<string>() != "collaboration"
                || tool["tools"] is not JsonArray children) continue;
            foreach (var child in children.OfType<JsonObject>())
            {
                if (child["name"]?.GetValue<string>() is not ("send_message" or "followup_task")
                    || child["parameters"]?["properties"]?["message"] is not JsonObject message) continue;
                message.Remove("encrypted");
                Interlocked.Increment(ref _changedInventories);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _loop;
        _listener.Close();
        _http.Dispose();
        _stop.Dispose();
    }
}
