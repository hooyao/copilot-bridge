using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Test-only ingress variants for real client execution against live Copilot:
/// mixed message schemas, or buffered HTTP requests with a transport-only SSE
/// envelope for Codex's streaming parser. Tool identities, arguments and dispatch
/// markers always come from the bridge's actual response.
/// </summary>
internal sealed class CollaborationIngressProxy : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(9) };
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly string _bridgeUrl;
    private readonly bool _mixedInventory;
    private readonly bool _bufferedResponses;
    private int _changedInventories;
    private int _bufferedResponseCount;

    internal string BaseUrl { get; }
    internal int ChangedInventories => Volatile.Read(ref _changedInventories);
    internal int BufferedResponseCount => Volatile.Read(ref _bufferedResponseCount);

    internal CollaborationIngressProxy(string bridgeUrl, bool mixedInventory, bool bufferedResponses)
    {
        _bridgeUrl = bridgeUrl;
        _mixedInventory = mixedInventory;
        _bufferedResponses = bufferedResponses;
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
            var bufferedRequest = _bufferedResponses && context.Request.HttpMethod == "POST"
                && context.Request.Url?.AbsolutePath == "/codex/responses";
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod),
                _bridgeUrl + context.Request.RawUrl);
            if (context.Request.HasEntityBody)
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = JsonNode.Parse(await reader.ReadToEndAsync(_stop.Token))!.AsObject();
                if (_mixedInventory)
                {
                    if (body["tools"] is JsonArray tools) MakeMixed(tools);
                    if (body["input"] is JsonArray input)
                        foreach (var item in input.OfType<JsonObject>())
                            if (item["type"]?.GetValue<string>() is "additional_tools" or "tool_search_output"
                                && item["tools"] is JsonArray historical) MakeMixed(historical);
                }
                if (bufferedRequest) body["stream"] = false;
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _stop.Token);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            context.Response.SendChunked = true;
            if (bufferedRequest && response.IsSuccessStatusCode)
            {
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(_stop.Token))!.AsObject();
                context.Response.ContentType = "text/event-stream";
                await WriteBufferedAsEventsAsync(context.Response.OutputStream, body);
                Interlocked.Increment(ref _bufferedResponseCount);
            }
            else await response.Content.CopyToAsync(context.Response.OutputStream, _stop.Token);
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception)
        {
            try { context.Response.StatusCode = 502; }
            catch (ObjectDisposedException) { }
        }
        finally { context.Response.Close(); }
    }

    private async Task WriteBufferedAsEventsAsync(Stream stream, JsonObject response)
    {
        var sequence = 0;
        async Task Emit(string type, JsonObject data)
        {
            data["type"] = type;
            data["sequence_number"] = sequence++;
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"event: {type}\ndata: {data.ToJsonString()}\n\n"), _stop.Token);
        }

        var created = response.DeepClone().AsObject();
        created["status"] = "in_progress";
        created["output"] = new JsonArray();
        await Emit("response.created", new JsonObject { ["response"] = created });
        if (response["output"] is JsonArray output)
            for (var index = 0; index < output.Count; index++)
            {
                var item = output[index]!.AsObject();
                var added = item.DeepClone().AsObject();
                var type = item["type"]!.GetValue<string>();
                var field = type == "function_call" ? "arguments" : type == "custom_tool_call" ? "input" : null;
                if (field is not null) added[field] = "";
                if (type == "message") added["content"] = new JsonArray();
                await Emit("response.output_item.added", new JsonObject { ["output_index"] = index, ["item"] = added });
                if (field is not null)
                {
                    var prefix = type == "function_call" ? "response.function_call_arguments" : "response.custom_tool_call_input";
                    await Emit(prefix + ".delta", new JsonObject
                    {
                        ["output_index"] = index, ["item_id"] = item["id"]?.DeepClone(), ["delta"] = item[field]!.DeepClone(),
                    });
                    await Emit(prefix + ".done", new JsonObject
                    {
                        ["output_index"] = index, ["item_id"] = item["id"]?.DeepClone(), [field] = item[field]!.DeepClone(),
                    });
                }
                if (type == "message" && item["content"] is JsonArray content)
                    for (var part = 0; part < content.Count; part++)
                    {
                        var actual = content[part]!.AsObject();
                        if (actual["type"]?.GetValue<string>() != "output_text") continue;
                        var empty = actual.DeepClone().AsObject();
                        empty["text"] = "";
                        await Emit("response.content_part.added", new JsonObject
                        {
                            ["output_index"] = index, ["content_index"] = part, ["item_id"] = item["id"]?.DeepClone(), ["part"] = empty,
                        });
                        await Emit("response.output_text.delta", new JsonObject
                        {
                            ["output_index"] = index, ["content_index"] = part, ["item_id"] = item["id"]?.DeepClone(), ["delta"] = actual["text"]!.DeepClone(),
                        });
                        await Emit("response.output_text.done", new JsonObject
                        {
                            ["output_index"] = index, ["content_index"] = part, ["item_id"] = item["id"]?.DeepClone(), ["text"] = actual["text"]!.DeepClone(),
                        });
                        await Emit("response.content_part.done", new JsonObject
                        {
                            ["output_index"] = index, ["content_index"] = part, ["item_id"] = item["id"]?.DeepClone(), ["part"] = actual.DeepClone(),
                        });
                    }
                await Emit("response.output_item.done", new JsonObject { ["output_index"] = index, ["item"] = item.DeepClone() });
            }
        await Emit("response.completed", new JsonObject { ["response"] = response.DeepClone() });
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
