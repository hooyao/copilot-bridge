using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Deterministic Responses upstream for the real-Codex reasoning-accounting
/// behavior leg. Turn one creates historical encrypted reasoning at low reported
/// usage. Turn two returns a custom exec call at usage below the isolated compact
/// limit; adding Codex's fallback estimate for turn one's reasoning would cross
/// that limit. The upstream deliberately omits <c>X-Reasoning-Included</c> so the
/// bridge under test must supply the client-edge compatibility signal.
/// </summary>
internal sealed class ReasoningAccountingUpstreamServer : IAsyncDisposable
{
    private const string ExecCallId = "call_reasoning_accounting_exec";
    private const string ExecItemId = "ctc_reasoning_accounting_exec";
    private const string FirstReasoningId = "rs_reasoning_accounting_first";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly string _workDirectory;
    private readonly string _canary;
    private int _turnRequests;
    private int _toolOutputRequests;
    private int _compactionRequests;

    public string BaseUrl { get; }
    public int TurnRequests => Volatile.Read(ref _turnRequests);
    public int ToolOutputRequests => Volatile.Read(ref _toolOutputRequests);
    public int CompactionRequests => Volatile.Read(ref _compactionRequests);

    private ReasoningAccountingUpstreamServer(
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

    public static ReasoningAccountingUpstreamServer Start(
        string workDirectory,
        string canary)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return new ReasoningAccountingUpstreamServer(
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

            if (RequestKind(root) == "compaction")
            {
                Interlocked.Increment(ref _compactionRequests);
                await BeginSseAsync(context.Response);
                await WriteMessageAsync(
                    context.Response,
                    responseId: "resp_reasoning_accounting_compaction",
                    messageId: "msg_reasoning_accounting_compaction",
                    text: "Preserve the tool task and continue from its latest result.",
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
                    responseId: "resp_reasoning_accounting_final",
                    messageId: "msg_reasoning_accounting_final",
                    text: _canary,
                    inputTokens: 680,
                    outputTokens: 20);
                return;
            }

            var turnNumber = Interlocked.Increment(ref _turnRequests);
            await BeginSseAsync(context.Response);
            if (turnNumber == 1)
            {
                await WriteFirstTurnAsync(context.Response);
                return;
            }

            if (turnNumber == 2)
            {
                if (!HasCustomExecTool(root))
                    throw new InvalidDataException(
                        "Codex second-turn request did not advertise the custom exec tool.");
                await WriteSecondTurnExecAsync(context.Response);
                return;
            }

            // Mutation/recovery path: after a false compact Codex may resume with a
            // new ordinary turn whose compacted history no longer exposes the call
            // output. Return a bounded final so the real client can still complete;
            // the verifier rejects the run from CompactionRequests + trace evidence.
            await WriteMessageAsync(
                context.Response,
                responseId: "resp_reasoning_accounting_recovery",
                messageId: "msg_reasoning_accounting_recovery",
                text: _canary,
                inputTokens: 120,
                outputTokens: 20);
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // A client cancellation in the no-header mutation may close the stream.
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private static Task BeginSseAsync(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.SendChunked = true;
        // Intentionally NO X-Reasoning-Included here. The bridge is the edge that
        // owns the Codex compatibility assertion under test.
        return Task.CompletedTask;
    }

    private async Task WriteFirstTurnAsync(HttpListenerResponse response)
    {
        const string responseId = "resp_reasoning_accounting_first";
        const string messageId = "msg_reasoning_accounting_first";
        var reasoning = new
        {
            type = "reasoning",
            id = FirstReasoningId,
            encrypted_content = new string('A', 3000),
            summary = new[]
            {
                new { type = "summary_text", text = "Prepared the first-turn state." },
            },
        };
        var message = Message(messageId, "first-turn-ready");

        await WriteCreatedAsync(response, responseId, sequence: 1);
        await WriteEventAsync(response, "response.output_item.added", JsonSerializer.Serialize(new
        {
            type = "response.output_item.added",
            sequence_number = 2,
            output_index = 0,
            item = reasoning,
        }));
        await WriteEventAsync(response, "response.output_item.done", JsonSerializer.Serialize(new
        {
            type = "response.output_item.done",
            sequence_number = 3,
            output_index = 0,
            item = reasoning,
        }));
        await WriteMessageLifecycleAsync(response, messageId, "first-turn-ready", 4, outputIndex: 1);
        await WriteCompletedAsync(
            response, responseId, sequence: 10, output: new object[] { reasoning, message },
            inputTokens: 80, outputTokens: 20, reasoningTokens: 10);
    }

    private async Task WriteSecondTurnExecAsync(HttpListenerResponse response)
    {
        const string responseId = "resp_reasoning_accounting_exec";
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
            response, responseId, sequence: 6, output: new object[] { done },
            inputTokens: 620, outputTokens: 30, reasoningTokens: 10);
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
        await WriteMessageLifecycleAsync(response, messageId, text, 2, outputIndex: 0);
        await WriteCompletedAsync(
            response, responseId, sequence: 8, output: new object[] { message },
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
        int firstSequence,
        int outputIndex)
    {
        await WriteEventAsync(response, "response.output_item.added", JsonSerializer.Serialize(new
        {
            type = "response.output_item.added",
            sequence_number = firstSequence,
            output_index = outputIndex,
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
            output_index = outputIndex,
            content_index = 0,
            part = new { type = "output_text", text = "", annotations = Array.Empty<object>() },
        }));
        await WriteEventAsync(response, "response.output_text.delta", JsonSerializer.Serialize(new
        {
            type = "response.output_text.delta",
            sequence_number = firstSequence + 2,
            item_id = messageId,
            output_index = outputIndex,
            content_index = 0,
            delta = text,
        }));
        await WriteEventAsync(response, "response.output_text.done", JsonSerializer.Serialize(new
        {
            type = "response.output_text.done",
            sequence_number = firstSequence + 3,
            item_id = messageId,
            output_index = outputIndex,
            content_index = 0,
            text,
        }));
        await WriteEventAsync(response, "response.content_part.done", JsonSerializer.Serialize(new
        {
            type = "response.content_part.done",
            sequence_number = firstSequence + 4,
            item_id = messageId,
            output_index = outputIndex,
            content_index = 0,
            part = new { type = "output_text", text, annotations = Array.Empty<object>() },
        }));
        await WriteEventAsync(response, "response.output_item.done", JsonSerializer.Serialize(new
        {
            type = "response.output_item.done",
            sequence_number = firstSequence + 5,
            output_index = outputIndex,
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
            $"Set-Content -LiteralPath 'reasoning_accounting_probe.txt' -Value '{_canary}'");
        var read = JsonSerializer.Serialize(
            "Get-Content -Raw -LiteralPath 'reasoning_accounting_probe.txt'");
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
            if (ContainsCustomExec(tools)) return true;
        }
        return false;
    }

    private static bool ContainsCustomExec(JsonElement tools)
    {
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.TryGetProperty("type", out var toolType)
                && toolType.GetString() == "custom"
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
