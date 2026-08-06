using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Deterministic Anthropic upstream used to establish whether the real Claude Code
/// client can carry opaque <c>redacted_thinking.data</c> across a tool-result turn.
/// The first main request returns redacted thinking followed by a Bash call. The
/// second request is captured by normal bridge tracing and receives a final canary.
/// Auxiliary title requests have no tools and do not consume a main phase.
/// </summary>
internal sealed class RedactedThinkingEchoUpstreamServer : IAsyncDisposable
{
    internal const string OpaqueData =
        "Y2JyaWRnZS1yZWRhY3RlZC10aGlua2luZy1yb3VuZHRyaXAtdjE6Ky89";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly string _canary;
    private int _requestCount;

    public string BaseUrl { get; }
    public int RequestCount => Volatile.Read(ref _requestCount);

    private RedactedThinkingEchoUpstreamServer(string baseUrl, string canary)
    {
        BaseUrl = baseUrl;
        _canary = canary;
        _listener.Prefixes.Add(baseUrl + "/");
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public static RedactedThinkingEchoUpstreamServer Start(string canary)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return new RedactedThinkingEchoUpstreamServer(
            $"http://127.0.0.1:{port}", canary);
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

            string requestBody;
            using (var reader = new StreamReader(
                       context.Request.InputStream, context.Request.ContentEncoding))
                requestBody = await reader.ReadToEndAsync(_stop.Token);

            using var doc = JsonDocument.Parse(requestBody);
            var hasTools = doc.RootElement.TryGetProperty("tools", out var tools)
                && tools.ValueKind == JsonValueKind.Array
                && tools.GetArrayLength() > 0;

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.SendChunked = true;

            if (!hasTools)
            {
                await WriteTextTurnAsync(context.Response, "Redacted thinking echo probe");
                return;
            }

            switch (Interlocked.Increment(ref _requestCount))
            {
                case 1:
                    await WriteRedactedThinkingAndToolAsync(context.Response);
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
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task WriteRedactedThinkingAndToolAsync(HttpListenerResponse response)
    {
        await WriteEventAsync(response, "message_start", JsonSerializer.Serialize(new
        {
            type = "message_start",
            message = new
            {
                id = "msg_redacted_echo",
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
            content_block = new { type = "redacted_thinking", data = OpaqueData },
        }));
        await WriteEventAsync(response, "content_block_stop",
            "{\"type\":\"content_block_stop\",\"index\":0}");

        const string arguments =
            "{\"command\":\"echo redacted-echo-tool > cbridge_probe.txt\","
            + "\"description\":\"Write the redacted-thinking probe\"}";
        await WriteEventAsync(response, "content_block_start", JsonSerializer.Serialize(new
        {
            type = "content_block_start",
            index = 1,
            content_block = new
            {
                type = "tool_use",
                id = "toolu_redacted_echo",
                name = "Bash",
                input = new { },
            },
        }));
        await WriteEventAsync(response, "content_block_delta", JsonSerializer.Serialize(new
        {
            type = "content_block_delta",
            index = 1,
            delta = new { type = "input_json_delta", partial_json = arguments },
        }));
        await WriteEventAsync(response, "content_block_stop",
            "{\"type\":\"content_block_stop\",\"index\":1}");
        await WriteEventAsync(response, "message_delta", JsonSerializer.Serialize(new
        {
            type = "message_delta",
            delta = new { stop_reason = "tool_use", stop_sequence = (string?)null },
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
                id = "msg_redacted_final",
                type = "message",
                role = "assistant",
                model = "claude-opus-5",
                content = Array.Empty<object>(),
                stop_reason = (string?)null,
                usage = new { input_tokens = 8, output_tokens = 1 },
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
        await WriteEventAsync(response, "content_block_stop",
            "{\"type\":\"content_block_stop\",\"index\":0}");
        await WriteEventAsync(response, "message_delta", JsonSerializer.Serialize(new
        {
            type = "message_delta",
            delta = new { stop_reason = "end_turn", stop_sequence = (string?)null },
            usage = new { output_tokens = 8 },
        }));
        await WriteEventAsync(response, "message_stop", "{\"type\":\"message_stop\"}");
    }

    private async Task WriteEventAsync(
        HttpListenerResponse response, string eventType, string data)
    {
        var bytes = Encoding.UTF8.GetBytes($"event: {eventType}\ndata: {data}\n\n");
        await response.OutputStream.WriteAsync(bytes, _stop.Token);
        await response.OutputStream.FlushAsync(_stop.Token);
    }
}
