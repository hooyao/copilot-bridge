using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Deterministic native Responses upstream for the Codex keepalive behavior case.
/// It opens the first response, emits no bytes past the client's configured parsed-
/// event idle timeout, then resumes with a real custom <c>exec</c> call. The exec
/// performs two nested shell operations (write then read); only a later request that
/// echoes the call output receives the final canary response.
/// </summary>
internal sealed class SilentResponsesUpstreamServer : IAsyncDisposable
{
    private const string ExecCallId = "call_keepalive_exec";
    private const string ExecItemId = "ctc_keepalive_exec";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly string _workDirectory;
    private readonly string _canary;
    private readonly TimeSpan _silence;
    private readonly bool _failFirstSampling;
    private readonly bool _failFirstHttpRequest;
    private readonly TimeSpan _firstFailureSilence;
    private int _samplingRequests;
    private int _toolOutputRequests;
    private int _timedOutSamplingRequests;
    private int _httpFailedRequests;

    public string BaseUrl { get; }
    public int SamplingRequests => Volatile.Read(ref _samplingRequests);
    public int ToolOutputRequests => Volatile.Read(ref _toolOutputRequests);
    public int TimedOutSamplingRequests => Volatile.Read(ref _timedOutSamplingRequests);
    public int HttpFailedRequests => Volatile.Read(ref _httpFailedRequests);
    public TimeSpan Silence => _silence;

    private SilentResponsesUpstreamServer(
        string baseUrl,
        string workDirectory,
        string canary,
        TimeSpan silence,
        bool failFirstSampling,
        bool failFirstHttpRequest,
        TimeSpan firstFailureSilence)
    {
        BaseUrl = baseUrl;
        _workDirectory = workDirectory;
        _canary = canary;
        _silence = silence;
        _failFirstSampling = failFirstSampling;
        _failFirstHttpRequest = failFirstHttpRequest;
        _firstFailureSilence = firstFailureSilence;
        _listener.Prefixes.Add(baseUrl + "/");
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public static SilentResponsesUpstreamServer Start(
        string workDirectory,
        string canary,
        TimeSpan silence,
        bool failFirstSampling = false,
        bool failFirstHttpRequest = false,
        TimeSpan? firstFailureSilence = null)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return new SilentResponsesUpstreamServer(
            $"http://127.0.0.1:{port}",
            workDirectory,
            canary,
            silence,
            failFirstSampling,
            failFirstHttpRequest,
            firstFailureSilence ?? TimeSpan.FromSeconds(10));
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

            string body;
            using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                body = await reader.ReadToEndAsync(_stop.Token);

            using var request = JsonDocument.Parse(body);
            var hasToolOutput = HasToolOutput(request.RootElement, ExecCallId);
            var samplingNumber = 0;
            if (hasToolOutput)
                Interlocked.Increment(ref _toolOutputRequests);
            else
                samplingNumber = Interlocked.Increment(ref _samplingRequests);

            if (hasToolOutput)
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                context.Response.SendChunked = true;
                await WriteFinalTextAsync(context.Response);
                return;
            }

            if (!HasCustomExecTool(request.RootElement))
                throw new InvalidDataException("Codex request did not advertise the custom exec tool.");

            if (_failFirstHttpRequest && samplingNumber == 1)
            {
                Interlocked.Increment(ref _httpFailedRequests);
                await WriteRetryableHttpFailureAsync(context.Response);
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.SendChunked = true;

            if (_failFirstSampling
                && Interlocked.CompareExchange(ref _timedOutSamplingRequests, 1, 0) == 0)
            {
                await WriteFirstSamplingStallAsync(context.Response);
                return;
            }

            await WriteSilentThenExecAsync(context.Response);
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // Expected in the mutation case: without bridge pings Codex cancels the
            // silent request and closes the connection before the server resumes.
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private static async Task WriteRetryableHttpFailureAsync(HttpListenerResponse response)
    {
        var bytes = Encoding.UTF8.GetBytes(
            "{\"error\":{\"message\":\"temporary upstream failure\","
            + "\"type\":\"server_error\",\"code\":\"server_error\"}}");
        response.StatusCode = 500;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        await response.OutputStream.FlushAsync();
    }

    private async Task WriteFirstSamplingStallAsync(HttpListenerResponse response)
    {
        await WriteEventAsync(response, "response.created", JsonSerializer.Serialize(new
        {
            type = "response.created",
            sequence_number = 1,
            response = new
            {
                id = "resp_retryable_stream_timeout",
                @object = "response",
                status = "in_progress",
                model = "gpt-5.6-sol",
                output = Array.Empty<object>(),
            },
        }));
        await Task.Delay(_firstFailureSilence, _stop.Token);
    }

    private async Task WriteSilentThenExecAsync(HttpListenerResponse response)
    {
        const string responseId = "resp_keepalive_exec";
        await WriteEventAsync(response, "response.created", JsonSerializer.Serialize(new
        {
            type = "response.created",
            sequence_number = 1,
            response = new
            {
                id = responseId,
                @object = "response",
                status = "in_progress",
                model = "gpt-5.6-sol",
                output = Array.Empty<object>(),
            },
        }));

        // No bytes at all during this interval. It exceeds the isolated Codex
        // provider's watchdog but remains below the bridge's upstream-idle budget.
        await Task.Delay(_silence, _stop.Token);

        var execInput = BuildExecInput();
        var addedItem = new
        {
            type = "custom_tool_call",
            id = ExecItemId,
            call_id = ExecCallId,
            name = "exec",
            input = "",
            status = "in_progress",
        };
        var doneItem = new
        {
            type = "custom_tool_call",
            id = ExecItemId,
            call_id = ExecCallId,
            name = "exec",
            input = execInput,
            status = "completed",
        };

        await WriteEventAsync(response, "response.output_item.added", JsonSerializer.Serialize(new
        {
            type = "response.output_item.added",
            sequence_number = 2,
            output_index = 0,
            item = addedItem,
        }));
        await WriteEventAsync(response, "response.custom_tool_call_input.delta", JsonSerializer.Serialize(new
        {
            type = "response.custom_tool_call_input.delta",
            sequence_number = 3,
            item_id = ExecItemId,
            output_index = 0,
            delta = execInput,
        }));
        await WriteEventAsync(response, "response.custom_tool_call_input.done", JsonSerializer.Serialize(new
        {
            type = "response.custom_tool_call_input.done",
            sequence_number = 4,
            item_id = ExecItemId,
            output_index = 0,
            input = execInput,
        }));
        await WriteEventAsync(response, "response.output_item.done", JsonSerializer.Serialize(new
        {
            type = "response.output_item.done",
            sequence_number = 5,
            output_index = 0,
            item = doneItem,
        }));
        await WriteEventAsync(response, "response.completed", JsonSerializer.Serialize(new
        {
            type = "response.completed",
            sequence_number = 6,
            response = new
            {
                id = responseId,
                @object = "response",
                status = "completed",
                model = "gpt-5.6-sol",
                output = new[] { doneItem },
                usage = new
                {
                    input_tokens = 100,
                    output_tokens = 20,
                    total_tokens = 120,
                    input_tokens_details = new { cached_tokens = 0 },
                    output_tokens_details = new { reasoning_tokens = 0 },
                },
            },
        }));
    }

    private async Task WriteFinalTextAsync(HttpListenerResponse response)
    {
        const string responseId = "resp_keepalive_final";
        const string messageId = "msg_keepalive_final";
        var message = new
        {
            type = "message",
            id = messageId,
            role = "assistant",
            status = "completed",
            content = new[] { new { type = "output_text", text = _canary, annotations = Array.Empty<object>() } },
        };

        await WriteEventAsync(response, "response.created", JsonSerializer.Serialize(new
        {
            type = "response.created",
            sequence_number = 1,
            response = new
            {
                id = responseId,
                @object = "response",
                status = "in_progress",
                model = "gpt-5.6-sol",
                output = Array.Empty<object>(),
            },
        }));
        await WriteEventAsync(response, "response.output_item.added", JsonSerializer.Serialize(new
        {
            type = "response.output_item.added",
            sequence_number = 2,
            output_index = 0,
            item = new
            {
                type = "message",
                id = messageId,
                role = "assistant",
                status = "in_progress",
                content = Array.Empty<object>(),
            },
        }));
        await WriteEventAsync(response, "response.content_part.added", JsonSerializer.Serialize(new
        {
            type = "response.content_part.added",
            sequence_number = 3,
            item_id = messageId,
            output_index = 0,
            content_index = 0,
            part = new { type = "output_text", text = "", annotations = Array.Empty<object>() },
        }));
        await WriteEventAsync(response, "response.output_text.delta", JsonSerializer.Serialize(new
        {
            type = "response.output_text.delta",
            sequence_number = 4,
            item_id = messageId,
            output_index = 0,
            content_index = 0,
            delta = _canary,
        }));
        await WriteEventAsync(response, "response.output_text.done", JsonSerializer.Serialize(new
        {
            type = "response.output_text.done",
            sequence_number = 5,
            item_id = messageId,
            output_index = 0,
            content_index = 0,
            text = _canary,
        }));
        await WriteEventAsync(response, "response.content_part.done", JsonSerializer.Serialize(new
        {
            type = "response.content_part.done",
            sequence_number = 6,
            item_id = messageId,
            output_index = 0,
            content_index = 0,
            part = new { type = "output_text", text = _canary, annotations = Array.Empty<object>() },
        }));
        await WriteEventAsync(response, "response.output_item.done", JsonSerializer.Serialize(new
        {
            type = "response.output_item.done",
            sequence_number = 7,
            output_index = 0,
            item = message,
        }));
        await WriteEventAsync(response, "response.completed", JsonSerializer.Serialize(new
        {
            type = "response.completed",
            sequence_number = 8,
            response = new
            {
                id = responseId,
                @object = "response",
                status = "completed",
                model = "gpt-5.6-sol",
                output = new[] { message },
                usage = new
                {
                    input_tokens = 120,
                    output_tokens = 8,
                    total_tokens = 128,
                    input_tokens_details = new { cached_tokens = 0 },
                    output_tokens_details = new { reasoning_tokens = 0 },
                },
            },
        }));
    }

    private string BuildExecInput()
    {
        var workdir = JsonSerializer.Serialize(_workDirectory);
        var write = JsonSerializer.Serialize(
            $"Set-Content -LiteralPath 'codex_keepalive_probe.txt' -Value '{_canary}'");
        var read = JsonSerializer.Serialize(
            "Get-Content -Raw -LiteralPath 'codex_keepalive_probe.txt'");
        return $"const writeResult = await tools.shell_command({{command:{write},workdir:{workdir}}});\n"
            + "text(writeResult);\n"
            + $"const readResult = await tools.shell_command({{command:{read},workdir:{workdir}}});\n"
            + "text(readResult);";
    }

    private static bool HasToolOutput(JsonElement root, string callId)
    {
        if (!root.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in input.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !type.GetString()!.EndsWith("_output", StringComparison.Ordinal))
                continue;
            if (item.TryGetProperty("call_id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() == callId)
                return true;
        }
        return false;
    }

    private static bool HasCustomExecTool(JsonElement root)
    {
        if (!root.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in input.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var itemType)
                || itemType.GetString() != "additional_tools"
                || !item.TryGetProperty("tools", out var tools)
                || tools.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.TryGetProperty("type", out var toolType)
                    && toolType.GetString() == "custom"
                    && tool.TryGetProperty("name", out var name)
                    && name.GetString() == "exec")
                    return true;
            }
        }
        return false;
    }

    private async Task WriteEventAsync(HttpListenerResponse response, string eventType, string data)
    {
        var bytes = Encoding.UTF8.GetBytes($"event: {eventType}\ndata: {data}\n\n");
        await response.OutputStream.WriteAsync(bytes, _stop.Token);
        await response.OutputStream.FlushAsync(_stop.Token);
    }
}
