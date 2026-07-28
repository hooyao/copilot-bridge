using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Deterministic <b>Anthropic</b> (<c>/v1/messages</c>) upstream for the keepalive
/// behavior case: it reproduces the failure this capability exists for.
///
/// <para>Request 1 opens a turn (<c>message_start</c> + a thinking
/// <c>content_block_start</c>) and then <b>goes silent past Claude Code's own
/// byte-level idle watchdog</b> before finishing normally with a tool call. That is
/// exactly what a real <c>claude-opus-5</c> turn at <c>effort=xhigh</c> was measured
/// doing against Copilot — <c>message_start</c>, a thinking block, then nothing for
/// 600 s — and it is the shape that makes the client kill a perfectly healthy
/// turn.</para>
///
/// <para>Without keepalive injection the client aborts mid-silence at its watchdog
/// bound; with injection the bridge fills the silence with <c>ping</c>s and the same
/// turn completes. Nothing about this upstream is unfair to the bridge: it always
/// answers, it simply thinks for a long time.</para>
/// </summary>
internal sealed class SilentThinkingUpstreamServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly string _probePath;
    private readonly string _canary;
    private readonly TimeSpan _silence;
    private int _requestCount;

    public string BaseUrl { get; }
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>How long the first turn stays silent after opening a thinking block.</summary>
    public TimeSpan Silence => _silence;

    private SilentThinkingUpstreamServer(string baseUrl, string probePath, string canary, TimeSpan silence)
    {
        BaseUrl = baseUrl;
        _probePath = probePath;
        _canary = canary;
        _silence = silence;
        _listener.Prefixes.Add(baseUrl + "/");
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public static SilentThinkingUpstreamServer Start(string probePath, string canary, TimeSpan silence)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return new SilentThinkingUpstreamServer($"http://127.0.0.1:{port}", probePath, canary, silence);
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
            if (context.Request.Url?.AbsolutePath != "/v1/messages")
            {
                context.Response.StatusCode = 404;
                return;
            }

            // Drain the real request so this is a genuine exchange, not a write-only fake.
            string requestBody;
            using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                requestBody = await reader.ReadToEndAsync(_stop.Token);

            using var requestDoc = JsonDocument.Parse(requestBody);
            var requestRoot = requestDoc.RootElement;
            var hasAgentTools = requestRoot.TryGetProperty("tools", out var tools)
                && tools.ValueKind == JsonValueKind.Array
                && tools.GetArrayLength() > 0;

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.SendChunked = true;

            // Claude Code concurrently asks for a session title (tools=[]). Answer it
            // promptly so it never consumes a phase of the case under test.
            if (!hasAgentTools)
            {
                await WriteTextTurnAsync(context.Response, "Keepalive behavior case");
                return;
            }

            var request = Interlocked.Increment(ref _requestCount);
            switch (request)
            {
                case 1:
                    // THE CASE: open a thinking block, go silent past the client's
                    // watchdog, then complete normally with a real tool call.
                    await WriteSilentThinkingThenToolCallAsync(context.Response);
                    break;
                case 2:
                    await WriteToolCallAsync(
                        context.Response, "toolu_read_after_silence", "Read",
                        JsonSerializer.Serialize(new { file_path = _probePath }));
                    break;
                default:
                    await WriteTextTurnAsync(context.Response, _canary);
                    break;
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // Expected if the client or bridge drops the connection mid-write.
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    /// <summary>
    /// message_start → thinking content_block_start → <b>silence</b> → the turn
    /// completes with a Bash tool call. The silence carries no bytes at all, which is
    /// what the measured Copilot behaviour looks like.
    /// </summary>
    private async Task WriteSilentThinkingThenToolCallAsync(HttpListenerResponse response)
    {
        await WriteEventAsync(response, "message_start", JsonSerializer.Serialize(new
        {
            type = "message_start",
            message = new
            {
                id = "msg_keepalive_case",
                type = "message",
                role = "assistant",
                model = "claude-opus-5",
                content = Array.Empty<object>(),
                stop_reason = (string?)null,
                usage = new { input_tokens = 12, output_tokens = 1 },
            },
        }));
        await WriteEventAsync(response, "content_block_start", JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 0,
            content_block = new { type = "thinking", thinking = "", signature = "" },
        }));

        // The silence under test: not one byte reaches the client from upstream.
        await Task.Delay(_silence, _stop.Token);

        await WriteEventAsync(response, "content_block_stop", JsonSerializer.Serialize(new
        {
            type = "content_block_stop",
            index = 0,
        }));
        await WriteToolCallBlocksAsync(
            response, index: 1, id: "toolu_bash_after_silence", name: "Bash",
            input: JsonSerializer.Serialize(new
            {
                command = $"echo {_canary} > cbridge_probe.txt",
                description = "Write the keepalive canary",
            }),
            stopReason: "tool_use");
    }

    private Task WriteToolCallAsync(HttpListenerResponse response, string id, string name, string input) =>
        WriteToolTurnAsync(response, id, name, input);

    private async Task WriteToolTurnAsync(
        HttpListenerResponse response, string id, string name, string input)
    {
        await WriteEventAsync(response, "message_start", JsonSerializer.Serialize(new
        {
            type = "message_start",
            message = new
            {
                id = "msg_" + id,
                type = "message",
                role = "assistant",
                model = "claude-opus-5",
                content = Array.Empty<object>(),
                stop_reason = (string?)null,
                usage = new { input_tokens = 8, output_tokens = 1 },
            },
        }));
        await WriteToolCallBlocksAsync(response, 0, id, name, input, "tool_use");
    }

    private async Task WriteToolCallBlocksAsync(
        HttpListenerResponse response, int index, string id, string name, string input, string stopReason)
    {
        await WriteEventAsync(response, "content_block_start", JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index,
            content_block = new { type = "tool_use", id, name, input = new { } },
        }));
        await WriteEventAsync(response, "content_block_delta", JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index,
            delta = new { type = "input_json_delta", partial_json = input },
        }));
        await WriteEventAsync(response, "content_block_stop", JsonSerializer.Serialize(new
        {
            type = "content_block_stop",
            index,
        }));
        await WriteEventAsync(response, "message_delta", JsonSerializer.Serialize(new
        {
            type = "message_delta",
            delta = new { stop_reason = stopReason, stop_sequence = (string?)null },
            usage = new { output_tokens = 24 },
        }));
        await WriteEventAsync(response, "message_stop", "{\"type\":\"message_stop\"}");
    }

    private async Task WriteTextTurnAsync(HttpListenerResponse response, string text)
    {
        await WriteEventAsync(response, "message_start", JsonSerializer.Serialize(new
        {
            type = "message_start",
            message = new
            {
                id = "msg_text_" + Guid.NewGuid().ToString("N")[..8],
                type = "message",
                role = "assistant",
                model = "claude-opus-5",
                content = Array.Empty<object>(),
                stop_reason = (string?)null,
                usage = new { input_tokens = 6, output_tokens = 1 },
            },
        }));
        await WriteEventAsync(response, "content_block_start", JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 0,
            content_block = new { type = "text", text = "" },
        }));
        await WriteEventAsync(response, "content_block_delta", JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index = 0,
            delta = new { type = "text_delta", text },
        }));
        await WriteEventAsync(response, "content_block_stop", JsonSerializer.Serialize(new
        {
            type = "content_block_stop",
            index = 0,
        }));
        await WriteEventAsync(response, "message_delta", JsonSerializer.Serialize(new
        {
            type = "message_delta",
            delta = new { stop_reason = "end_turn", stop_sequence = (string?)null },
            usage = new { output_tokens = 12 },
        }));
        await WriteEventAsync(response, "message_stop", "{\"type\":\"message_stop\"}");
    }

    private async Task WriteEventAsync(HttpListenerResponse response, string eventType, string data)
    {
        var bytes = Encoding.UTF8.GetBytes($"event: {eventType}\ndata: {data}\n\n");
        await response.OutputStream.WriteAsync(bytes, _stop.Token);
        await response.OutputStream.FlushAsync(_stop.Token);
    }
}
