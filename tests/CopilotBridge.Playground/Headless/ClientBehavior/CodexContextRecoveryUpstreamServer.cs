using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Deterministic Responses upstream for native Codex pre-turn context recovery.
/// It returns the confirmed Copilot context 400 on the first compaction request,
/// accepts the reduced retry, then drives a real custom-exec round trip.
/// </summary>
internal sealed class CodexContextRecoveryUpstreamServer : IAsyncDisposable
{
    private const string ExecCallId = "call_codex_context_recovery_exec";
    private const string ExecItemId = "ctc_codex_context_recovery_exec";
    private const string ContextError =
        "{\"error\":{\"message\":\"Your input exceeds the context window of this model. Please adjust your input and try again.\",\"code\":\"invalid_request_body\"}}";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly string _workDirectory;
    private readonly string _canary;
    private long _firstCompactionBytes;
    private int _turnRequests;
    private int _compactionRequests;
    private int _contextRejections;
    private int _reducedCompactionRequests;
    private int _toolOutputRequests;

    public string BaseUrl { get; }
    public int TurnRequests => Volatile.Read(ref _turnRequests);
    public int CompactionRequests => Volatile.Read(ref _compactionRequests);
    public int ContextRejections => Volatile.Read(ref _contextRejections);
    public int ReducedCompactionRequests => Volatile.Read(ref _reducedCompactionRequests);
    public int ToolOutputRequests => Volatile.Read(ref _toolOutputRequests);

    private CodexContextRecoveryUpstreamServer(
        string baseUrl,
        string workDirectory,
        string canary)
    {
        BaseUrl = baseUrl;
        _workDirectory = workDirectory;
        _canary = canary;
        _listener.Prefixes.Add(baseUrl + "/");
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public static CodexContextRecoveryUpstreamServer Start(
        string workDirectory,
        string canary)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return new CodexContextRecoveryUpstreamServer(
            $"http://127.0.0.1:{port}", workDirectory, canary);
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
            using (var reader = new StreamReader(
                context.Request.InputStream, context.Request.ContentEncoding))
                body = await reader.ReadToEndAsync(_stop.Token);
            using var request = JsonDocument.Parse(body);
            var root = request.RootElement;
            var kind = RequestKind(root);

            if (kind == "compaction")
            {
                var attempt = Interlocked.Increment(ref _compactionRequests);
                var bodyBytes = Encoding.UTF8.GetByteCount(body);
                if (attempt == 1)
                {
                    Interlocked.Exchange(ref _firstCompactionBytes, bodyBytes);
                    Interlocked.Increment(ref _contextRejections);
                    await WriteContextErrorAsync(context.Response);
                    return;
                }

                if (bodyBytes < Interlocked.Read(ref _firstCompactionBytes))
                    Interlocked.Increment(ref _reducedCompactionRequests);
                await BeginSseAsync(context.Response);
                await WriteMessageAsync(
                    context.Response,
                    "resp_codex_context_compaction",
                    "msg_codex_context_compaction",
                    "Preserve the user's pending tool task and continue from the reduced history.",
                    inputTokens: 100,
                    outputTokens: 20);
                return;
            }

            if (HasToolOutput(root, ExecCallId))
            {
                Interlocked.Increment(ref _toolOutputRequests);
                await BeginSseAsync(context.Response);
                await WriteMessageAsync(
                    context.Response,
                    "resp_codex_context_final",
                    "msg_codex_context_final",
                    _canary,
                    inputTokens: 120,
                    outputTokens: 20);
                return;
            }

            if (kind != "turn")
            {
                await BeginSseAsync(context.Response);
                await WriteMessageAsync(
                    context.Response,
                    "resp_codex_context_auxiliary",
                    "msg_codex_context_auxiliary",
                    "Context recovery auxiliary request completed.",
                    inputTokens: 20,
                    outputTokens: 10);
                return;
            }

            var turn = Interlocked.Increment(ref _turnRequests);
            await BeginSseAsync(context.Response);
            if (turn == 1)
            {
                // No mid-turn follow-up follows this text response. Its authoritative
                // total crosses the isolated 850-token compact threshold only when
                // the next user turn begins, producing a pre-turn compact request.
                await WriteMessageAsync(
                    context.Response,
                    "resp_codex_context_seed",
                    "msg_codex_context_seed",
                    "The first turn is ready.",
                    inputTokens: 840,
                    outputTokens: 20);
                return;
            }

            if (!HasCustomExecTool(root))
                throw new InvalidDataException(
                    "Codex post-compact request did not advertise the custom exec tool.");
            await WriteExecCallAsync(context.Response);
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // The real client may close a response while transitioning into recovery.
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task WriteExecCallAsync(HttpListenerResponse response)
    {
        const string responseId = "resp_codex_context_exec";
        var input = BuildExecInput();
        var added = new
        {
            type = "custom_tool_call",
            id = ExecItemId,
            call_id = ExecCallId,
            name = "exec",
            input = "",
            status = "in_progress",
        };
        var done = new
        {
            type = "custom_tool_call",
            id = ExecItemId,
            call_id = ExecCallId,
            name = "exec",
            input,
            status = "completed",
        };

        await WriteCreatedAsync(response, responseId, sequence: 1);
        await WriteEventAsync(response, "response.output_item.added", JsonSerializer.Serialize(new
        {
            type = "response.output_item.added",
            sequence_number = 2,
            output_index = 0,
            item = added,
        }));
        await WriteEventAsync(response, "response.custom_tool_call_input.delta", JsonSerializer.Serialize(new
        {
            type = "response.custom_tool_call_input.delta",
            sequence_number = 3,
            item_id = ExecItemId,
            output_index = 0,
            delta = input,
        }));
        await WriteEventAsync(response, "response.custom_tool_call_input.done", JsonSerializer.Serialize(new
        {
            type = "response.custom_tool_call_input.done",
            sequence_number = 4,
            item_id = ExecItemId,
            output_index = 0,
            input,
        }));
        await WriteEventAsync(response, "response.output_item.done", JsonSerializer.Serialize(new
        {
            type = "response.output_item.done",
            sequence_number = 5,
            output_index = 0,
            item = done,
        }));
        await WriteCompletedAsync(
            response, responseId, sequence: 6, output: [done],
            inputTokens: 120, outputTokens: 30, reasoningTokens: 10);
    }

    private async Task WriteMessageAsync(
        HttpListenerResponse response,
        string responseId,
        string messageId,
        string text,
        long inputTokens,
        long outputTokens)
    {
        var message = Message(messageId, text);
        await WriteCreatedAsync(response, responseId, sequence: 1);
        await WriteMessageLifecycleAsync(response, messageId, text, firstSequence: 2);
        await WriteCompletedAsync(
            response, responseId, sequence: 8, output: [message],
            inputTokens, outputTokens, reasoningTokens: 0);
    }

    private static object Message(string id, string text) => new
    {
        type = "message",
        id,
        role = "assistant",
        status = "completed",
        content = new[]
        {
            new { type = "output_text", text, annotations = Array.Empty<object>() },
        },
    };

    private async Task WriteMessageLifecycleAsync(
        HttpListenerResponse response,
        string messageId,
        string text,
        int firstSequence)
    {
        await WriteEventAsync(response, "response.output_item.added", JsonSerializer.Serialize(new
        {
            type = "response.output_item.added",
            sequence_number = firstSequence,
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
            sequence_number = firstSequence + 1,
            item_id = messageId,
            output_index = 0,
            content_index = 0,
            part = new { type = "output_text", text = "", annotations = Array.Empty<object>() },
        }));
        await WriteEventAsync(response, "response.output_text.delta", JsonSerializer.Serialize(new
        {
            type = "response.output_text.delta",
            sequence_number = firstSequence + 2,
            item_id = messageId,
            output_index = 0,
            content_index = 0,
            delta = text,
        }));
        await WriteEventAsync(response, "response.output_text.done", JsonSerializer.Serialize(new
        {
            type = "response.output_text.done",
            sequence_number = firstSequence + 3,
            item_id = messageId,
            output_index = 0,
            content_index = 0,
            text,
        }));
        await WriteEventAsync(response, "response.content_part.done", JsonSerializer.Serialize(new
        {
            type = "response.content_part.done",
            sequence_number = firstSequence + 4,
            item_id = messageId,
            output_index = 0,
            content_index = 0,
            part = new { type = "output_text", text, annotations = Array.Empty<object>() },
        }));
        await WriteEventAsync(response, "response.output_item.done", JsonSerializer.Serialize(new
        {
            type = "response.output_item.done",
            sequence_number = firstSequence + 5,
            output_index = 0,
            item = Message(messageId, text),
        }));
    }

    private async Task WriteCreatedAsync(
        HttpListenerResponse response,
        string responseId,
        int sequence)
    {
        await WriteEventAsync(response, "response.created", JsonSerializer.Serialize(new
        {
            type = "response.created",
            sequence_number = sequence,
            response = new
            {
                id = responseId,
                @object = "response",
                status = "in_progress",
                model = "gpt-5.6-sol",
                output = Array.Empty<object>(),
            },
        }));
    }

    private async Task WriteCompletedAsync(
        HttpListenerResponse response,
        string responseId,
        int sequence,
        object[] output,
        long inputTokens,
        long outputTokens,
        long reasoningTokens)
    {
        await WriteEventAsync(response, "response.completed", JsonSerializer.Serialize(new
        {
            type = "response.completed",
            sequence_number = sequence,
            response = new
            {
                id = responseId,
                @object = "response",
                status = "completed",
                model = "gpt-5.6-sol",
                output,
                usage = new
                {
                    input_tokens = inputTokens,
                    input_tokens_details = new { cached_tokens = 0 },
                    output_tokens = outputTokens,
                    output_tokens_details = new { reasoning_tokens = reasoningTokens },
                    total_tokens = inputTokens + outputTokens,
                },
            },
        }));
    }

    private string BuildExecInput()
    {
        var workdir = JsonSerializer.Serialize(_workDirectory);
        var write = JsonSerializer.Serialize(
            $"Set-Content -LiteralPath 'context_recovery_probe.txt' -Value '{_canary}'");
        var read = JsonSerializer.Serialize(
            "Get-Content -Raw -LiteralPath 'context_recovery_probe.txt'");
        return $"const writeResult = await tools.shell_command({{command:{write},workdir:{workdir}}});\n"
            + "text(writeResult);\n"
            + $"const readResult = await tools.shell_command({{command:{read},workdir:{workdir}}});\n"
            + "text(readResult);";
    }

    private static string RequestKind(JsonElement root)
    {
        if (!root.TryGetProperty("client_metadata", out var metadata)
            || metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("x-codex-turn-metadata", out var raw)
            || raw.ValueKind != JsonValueKind.String)
            return "turn";
        using var parsed = JsonDocument.Parse(raw.GetString()!);
        return parsed.RootElement.TryGetProperty("request_kind", out var kind)
            && kind.ValueKind == JsonValueKind.String
            ? kind.GetString()!
            : "turn";
    }

    private static bool HasToolOutput(JsonElement root, string callId)
    {
        if (!root.TryGetProperty("input", out var input)
            || input.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in input.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type)
                || type.GetString() is not ("custom_tool_call_output" or "function_call_output"))
                continue;
            if (item.TryGetProperty("call_id", out var id) && id.GetString() == callId)
                return true;
        }
        return false;
    }

    private static bool HasCustomExecTool(JsonElement root)
    {
        if (!root.TryGetProperty("input", out var input)
            || input.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in input.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var itemType)
                || itemType.GetString() != "additional_tools"
                || !item.TryGetProperty("tools", out var tools)
                || tools.ValueKind != JsonValueKind.Array)
                continue;
            if (ContainsCustomExec(tools)) return true;
        }
        return false;
    }

    private static bool ContainsCustomExec(JsonElement tools)
    {
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.TryGetProperty("type", out var type)
                && type.GetString() == "custom"
                && tool.TryGetProperty("name", out var name)
                && name.GetString() == "exec")
                return true;
            if (tool.TryGetProperty("tools", out var nested)
                && nested.ValueKind == JsonValueKind.Array
                && ContainsCustomExec(nested))
                return true;
        }
        return false;
    }

    private static Task BeginSseAsync(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.SendChunked = true;
        return Task.CompletedTask;
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

    private async Task WriteEventAsync(
        HttpListenerResponse response,
        string eventType,
        string data)
    {
        var bytes = Encoding.UTF8.GetBytes($"event: {eventType}\ndata: {data}\n\n");
        await response.OutputStream.WriteAsync(bytes, _stop.Token);
        await response.OutputStream.FlushAsync(_stop.Token);
    }
}
