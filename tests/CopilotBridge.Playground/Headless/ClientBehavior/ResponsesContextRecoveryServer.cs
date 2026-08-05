using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Deterministic Responses upstream for Claude Code's reactive-compaction
/// acceptance case. It injects the production context 400 into a small main
/// request, serves the compact-summary turn, then drives Bash, Read, and final
/// text. Count and title traffic never consume a main-task phase.
/// </summary>
internal sealed class ResponsesContextRecoveryServer : IAsyncDisposable
{
    internal const string CompactPromptMarker =
        "Your task is to create a detailed summary of the conversation so far";

    private const string ContextError =
        "{\"error\":{\"message\":\"Your input exceeds the context window of this model. Please adjust your input and try again.\",\"code\":\"invalid_request_body\"}}";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly string _probePath;
    private readonly string _canary;
    private readonly object _evidenceLock = new();
    private readonly List<string> _requestKinds = [];
    private int _mainPhase;
    private int _contextRejections;
    private int _countRequests;
    private int _summaryRequests;
    private int _summarySeen;
    private int _armed;
    private int _prewarmRequests;

    public string BaseUrl { get; }
    public int MainPhase => Volatile.Read(ref _mainPhase);
    public int ContextRejections => Volatile.Read(ref _contextRejections);
    public int CountRequests => Volatile.Read(ref _countRequests);
    public int SummaryRequests => Volatile.Read(ref _summaryRequests);
    public int PrewarmRequests => Volatile.Read(ref _prewarmRequests);
    public IReadOnlyList<string> RequestKinds
    {
        get { lock (_evidenceLock) return _requestKinds.ToArray(); }
    }

    public void ArmContextRejection() => Volatile.Write(ref _armed, 1);

    private ResponsesContextRecoveryServer(
        string baseUrl, string probePath, string canary)
    {
        BaseUrl = baseUrl;
        _probePath = probePath;
        _canary = canary;
        _listener.Prefixes.Add(baseUrl + "/");
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public static ResponsesContextRecoveryServer Start(
        string probePath, string canary)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return new ResponsesContextRecoveryServer(
            $"http://127.0.0.1:{port}", probePath, canary);
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
            var path = context.Request.Url?.AbsolutePath ?? "";
            string body;
            using (var reader = new StreamReader(
                       context.Request.InputStream, context.Request.ContentEncoding))
                body = await reader.ReadToEndAsync(_stop.Token);

            if (path == "/v1/messages/count_tokens")
            {
                Interlocked.Increment(ref _countRequests);
                Record("count");
                await WriteJsonAsync(context.Response, "{\"input_tokens\":64}");
                return;
            }

            if (path != "/responses")
            {
                context.Response.StatusCode = 404;
                Record("unknown:" + path);
                return;
            }

            if (body.Contains(CompactPromptMarker, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _summaryRequests);
                Volatile.Write(ref _summarySeen, 1);
                Record("compact-summary");
                await WriteTextAsync(
                    context.Response,
                    "<analysis>The user requested a deterministic Bash and Read tool chain.</analysis>"
                    + "<summary>The active task is to write the exact canary to cbridge_probe.txt "
                    + "with Bash, read it with Read, and report the exact text.</summary>",
                    "resp_compact_summary");
                return;
            }

            using var doc = JsonDocument.Parse(body);
            var hasTools = doc.RootElement.TryGetProperty("tools", out var tools)
                && tools.ValueKind == JsonValueKind.Array
                && tools.GetArrayLength() > 0;
            if (!hasTools)
            {
                Record("auxiliary");
                await WriteTextAsync(
                    context.Response, "Context recovery", "resp_auxiliary");
                return;
            }

            if (Volatile.Read(ref _armed) == 0)
            {
                var warm = Interlocked.Increment(ref _prewarmRequests);
                Record("prewarm-" + warm);
                await WriteTextAsync(
                    context.Response, "Prewarm turn completed.", $"resp_prewarm_{warm}");
                return;
            }

            // Claude Code transparently retries some failed streaming turns once
            // in buffered mode before its query loop sees prompt-too-long. Both
            // wire attempts belong to the same first logical turn. Keep returning
            // the confirmed 400 until the client actually asks for its compact
            // summary; only post-summary task traffic may advance tool phases.
            if (Volatile.Read(ref _summarySeen) == 0)
            {
                var rejection = Interlocked.Increment(ref _contextRejections);
                Record("context-rejection-" + rejection);
                await WriteContextErrorAsync(context.Response);
                return;
            }

            var phase = Interlocked.Increment(ref _mainPhase);
            Record("main-" + phase);
            switch (phase)
            {
                case 1:
                    await WriteToolCallAsync(
                        context.Response,
                        "call_bash_after_compact",
                        "Bash",
                        JsonSerializer.Serialize(new
                        {
                            command = $"echo {_canary} > cbridge_probe.txt",
                            description = "Write the post-compact canary",
                        }));
                    break;
                case 2:
                    await WriteToolCallAsync(
                        context.Response,
                        "call_read_after_compact",
                        "Read",
                        JsonSerializer.Serialize(new { file_path = _probePath }));
                    break;
                default:
                    await WriteTextAsync(context.Response, _canary, "resp_final");
                    break;
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private void Record(string kind)
    {
        lock (_evidenceLock) _requestKinds.Add(kind);
    }

    private static async Task WriteContextErrorAsync(HttpListenerResponse response)
    {
        var bytes = Encoding.UTF8.GetBytes(ContextError);
        response.StatusCode = 400;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        await response.OutputStream.FlushAsync();
    }

    private static async Task WriteJsonAsync(
        HttpListenerResponse response, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = 200;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        await response.OutputStream.FlushAsync();
    }

    private static async Task WriteToolCallAsync(
        HttpListenerResponse response, string callId, string name, string arguments)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.SendChunked = true;
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

    private static async Task WriteTextAsync(
        HttpListenerResponse response, string value, string responseId)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.SendChunked = true;
        var text = JsonSerializer.Serialize(value);
        await WriteEventAsync(response, "response.created",
            $"{{\"type\":\"response.created\",\"response\":{{\"id\":\"{responseId}\",\"status\":\"in_progress\"}}}}");
        await WriteEventAsync(response, "response.output_item.added",
            $"{{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{{\"type\":\"message\",\"id\":\"msg_{responseId}\",\"role\":\"assistant\",\"status\":\"in_progress\",\"content\":[]}}}}");
        await WriteEventAsync(response, "response.output_text.delta",
            $"{{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_{responseId}\",\"output_index\":0,\"content_index\":0,\"delta\":{text}}}");
        await WriteEventAsync(response, "response.output_item.done",
            $"{{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{{\"type\":\"message\",\"id\":\"msg_{responseId}\",\"status\":\"completed\"}}}}");
        await WriteEventAsync(response, "response.completed", CompletedJson(responseId));
    }

    private static string CompletedJson(string responseId) =>
        $"{{\"type\":\"response.completed\",\"response\":{{\"id\":\"{responseId}\",\"status\":\"completed\",\"usage\":{{\"input_tokens\":64,\"output_tokens\":16,\"total_tokens\":80}}}}}}";

    private static async Task WriteEventAsync(
        HttpListenerResponse response, string eventType, string data)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"event: {eventType}\ndata: {data}\n\n");
        await response.OutputStream.WriteAsync(bytes);
        await response.OutputStream.FlushAsync();
    }
}
