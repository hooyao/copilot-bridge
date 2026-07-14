using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Deterministic Responses upstream for the real Claude Code fault-recovery
/// behavior case. Request 1 writes partial commentary and then stays silent long
/// enough for the bridge's stream-idle budget to fire. Subsequent requests issue
/// Bash, then Read, then the final canary, proving the real client retried and
/// continued its agent loop after the failed attempt.
/// </summary>
internal sealed class ResponsesFaultRecoveryServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly string _probePath;
    private readonly string _canary;
    private int _requestCount;

    public string BaseUrl { get; }
    public int RequestCount => Volatile.Read(ref _requestCount);

    private ResponsesFaultRecoveryServer(string baseUrl, string probePath, string canary)
    {
        BaseUrl = baseUrl;
        _probePath = probePath;
        _canary = canary;
        _listener.Prefixes.Add(baseUrl + "/");
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public static ResponsesFaultRecoveryServer Start(string probePath, string canary)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return new ResponsesFaultRecoveryServer($"http://127.0.0.1:{port}", probePath, canary);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
        try { await _acceptLoop; } catch { }
        _listener.Close();
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().WaitAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (HttpListenerException) when (_stop.IsCancellationRequested) { return; }
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.Url?.AbsolutePath != "/responses")
            {
                context.Response.StatusCode = 404;
                return;
            }

            // Drain the real T2 request so the client can reuse the connection and
            // so this is a real request/response exchange, not a write-only fake.
            string requestBody;
            using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                requestBody = await reader.ReadToEndAsync(_stop.Token);

            using var requestDoc = JsonDocument.Parse(requestBody);
            var requestRoot = requestDoc.RootElement;
            var hasAgentTools = requestRoot.TryGetProperty("tools", out var tools)
                && tools.ValueKind == JsonValueKind.Array
                && tools.GetArrayLength() > 0;
            // Claude's non-streaming fallback omits `stream` rather than sending
            // false. Only an explicit true is the streaming protocol.
            var streaming = requestRoot.TryGetProperty("stream", out var streamValue)
                && streamValue.ValueKind == JsonValueKind.True;

            // Claude Code concurrently asks the same endpoint to generate a session
            // title (tools=[]). Give that auxiliary request a normal answer without
            // consuming a phase from the agent task under test.
            if (!hasAgentTools)
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                context.Response.SendChunked = true;
                await WriteTextAsync(context.Response, "{\"title\":\"Stream fault recovery\"}");
                return;
            }

            var request = Interlocked.Increment(ref _requestCount);
            context.Response.StatusCode = 200;

            switch (request)
            {
                case 1:
                    context.Response.ContentType = "text/event-stream";
                    context.Response.SendChunked = true;
                    await WritePartialThenStallAsync(context.Response);
                    break;
                case 2:
                    if (streaming)
                        throw new InvalidOperationException(
                            "Claude recovery request unexpectedly remained streaming.");
                    await WriteBufferedToolCallAsync(context.Response);
                    break;
                case 3:
                    context.Response.ContentType = "text/event-stream";
                    context.Response.SendChunked = true;
                    await WriteToolCallAsync(
                        context.Response,
                        "call_read_after_retry",
                        "Read",
                        JsonSerializer.Serialize(new { file_path = _probePath }));
                    break;
                default:
                    context.Response.ContentType = "text/event-stream";
                    context.Response.SendChunked = true;
                    await WriteTextAsync(context.Response, _canary);
                    break;
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // Expected when the bridge cancels the first stalled response body.
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task WriteBufferedToolCallAsync(HttpListenerResponse response)
    {
        var arguments = JsonSerializer.Serialize(new
        {
            command = $"echo {_canary} > cbridge_probe.txt",
            description = "Write the recovery canary",
        });
        var body = JsonSerializer.Serialize(new
        {
            id = "resp_bash_after_retry",
            @object = "response",
            status = "completed",
            model = "gpt-5.6-sol",
            output = new object[]
            {
                new
                {
                    type = "function_call",
                    id = "item_bash_after_retry",
                    call_id = "call_bash_after_retry",
                    name = "Bash",
                    arguments,
                    status = "completed",
                },
            },
            usage = new { input_tokens = 10, output_tokens = 5, total_tokens = 15 },
        });
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        await response.OutputStream.FlushAsync();
    }

    private async Task WritePartialThenStallAsync(HttpListenerResponse response)
    {
        await WriteEventAsync(response, "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp_stall\",\"status\":\"in_progress\"}}");
        await WriteEventAsync(response, "response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"msg_stall\",\"role\":\"assistant\",\"status\":\"in_progress\",\"content\":[]}}");
        await WriteEventAsync(response, "response.output_text.delta",
            "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_stall\",\"output_index\":0,\"content_index\":0,\"delta\":\"I will now perform the requested tool steps.\"}");
        await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token);
    }

    private static async Task WriteToolCallAsync(
        HttpListenerResponse response, string callId, string name, string arguments)
    {
        var encodedArgs = JsonSerializer.Serialize(arguments);
        await WriteEventAsync(response, "response.created",
            $"{{\"type\":\"response.created\",\"response\":{{\"id\":\"resp_{callId}\",\"status\":\"in_progress\"}}}}");
        await WriteEventAsync(response, "response.output_item.added",
            $"{{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{{\"type\":\"function_call\",\"id\":\"item_{callId}\",\"call_id\":\"{callId}\",\"name\":\"{name}\",\"arguments\":\"\",\"status\":\"in_progress\"}}}}");
        await WriteEventAsync(response, "response.function_call_arguments.delta",
            $"{{\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"item_{callId}\",\"output_index\":0,\"delta\":{encodedArgs}}}");
        await WriteEventAsync(response, "response.function_call_arguments.done",
            $"{{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"item_{callId}\",\"output_index\":0,\"arguments\":{encodedArgs}}}");
        await WriteEventAsync(response, "response.output_item.done",
            $"{{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{{\"type\":\"function_call\",\"id\":\"item_{callId}\",\"call_id\":\"{callId}\",\"name\":\"{name}\",\"arguments\":{encodedArgs},\"status\":\"completed\"}}}}");
        await WriteEventAsync(response, "response.completed", CompletedJson($"resp_{callId}"));
    }

    private static async Task WriteTextAsync(HttpListenerResponse response, string text)
    {
        var encodedText = JsonSerializer.Serialize(text);
        await WriteEventAsync(response, "response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp_final\",\"status\":\"in_progress\"}}");
        await WriteEventAsync(response, "response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"msg_final\",\"role\":\"assistant\",\"status\":\"in_progress\",\"content\":[]}}");
        await WriteEventAsync(response, "response.output_text.delta",
            $"{{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_final\",\"output_index\":0,\"content_index\":0,\"delta\":{encodedText}}}");
        await WriteEventAsync(response, "response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"msg_final\",\"status\":\"completed\"}}");
        await WriteEventAsync(response, "response.completed", CompletedJson("resp_final"));
    }

    private static string CompletedJson(string responseId) =>
        $"{{\"type\":\"response.completed\",\"response\":{{\"id\":\"{responseId}\",\"status\":\"completed\",\"usage\":{{\"input_tokens\":10,\"output_tokens\":5,\"total_tokens\":15}}}}}}";

    private static async Task WriteEventAsync(
        HttpListenerResponse response, string eventType, string data)
    {
        var bytes = Encoding.UTF8.GetBytes($"event: {eventType}\ndata: {data}\n\n");
        await response.OutputStream.WriteAsync(bytes);
        await response.OutputStream.FlushAsync();
    }
}
