using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Serilog.Core;
using Serilog.Events;

namespace CopilotBridge.Cli.Hosting.Logging;

/// <summary>
/// Serilog sink that picks bridge IO events (EventIds 1001-1004) out of the
/// event stream and writes each one to a dedicated audit JSON file at
/// <c>&lt;BaseDirectory&gt;/logs/&lt;utc&gt;-&lt;seq&gt;-&lt;kind&gt;.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Producer/consumer.</b> <see cref="Emit"/> drops the payload into a
/// bounded <see cref="Channel{T}"/> (default capacity 256). A single worker
/// task drains the channel and writes each payload to disk in arrival order.
/// </para>
/// <para>
/// <b>Back-pressure.</b> The channel is in <see cref="BoundedChannelFullMode.Wait"/>
/// mode and <see cref="Emit"/> blocks the caller when full. This is the
/// project's deliberate choice: audit completeness wins over latency. Under
/// disk-bound load, request handling will throttle to match the worker's
/// write rate.
/// </para>
/// <para>
/// <b>Shutdown.</b> <see cref="IDisposable.Dispose"/> completes the channel
/// and waits for the worker to drain. Called transitively by
/// <c>Log.CloseAndFlush()</c> (registered in <c>Program.cs</c> on
/// <c>ProcessExit</c>), so pending audits land before the process exits.
/// </para>
/// <para>
/// <b>Filtering.</b> Non-bridge-IO events are passed through with no work —
/// they continue to the rolling-file / console sinks via the parent logger
/// configuration. We could use a Serilog filter to avoid even reaching
/// <see cref="Emit"/>, but a cheap type check here is just as fast.
/// </para>
/// </remarks>
internal sealed class BridgeIoSink : ILogEventSink, IDisposable
{
    private static readonly HashSet<string> RedactedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "x-api-key",
        "anthropic-auth-token",
    };

    private readonly string _dir;
    private readonly Channel<BridgeIoPayload> _channel;
    private readonly Task _worker;
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposed;

    public BridgeIoSink(string dir, int capacity = 256)
    {
        _dir = dir;
        System.IO.Directory.CreateDirectory(_dir);

        _channel = Channel.CreateBounded<BridgeIoPayload>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        _worker = Task.Run(WorkerLoopAsync);
    }

    public string Directory => _dir;

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue("Payload", out var prop)
            && prop is ScalarValue scalar
            && scalar.Value is BridgeIoPayload payload)
        {
            // BoundedChannelFullMode.Wait + synchronous WriteAsync.Wait()
            // = caller blocks until the worker has freed a slot. That IS
            // the back-pressure mechanism (see class doc above).
            _channel.Writer.WriteAsync(payload).AsTask().GetAwaiter().GetResult();
        }
        // Non-bridge-IO events: nothing to do; parent sinks handle them.
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            await foreach (var payload in _channel.Reader.ReadAllAsync(_shutdownCts.Token).ConfigureAwait(false))
            {
                try
                {
                    await WriteOneAsync(payload).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Worker must not die — print to stderr and move on so
                    // the audit pipeline keeps draining. A single bad
                    // payload (encoding glitch, full disk burst) should
                    // not stall every subsequent request.
                    try
                    {
                        await Console.Error.WriteLineAsync(
                            $"[BridgeIoSink] failed to write seq={payload.Seq} kind={payload.Kind}: {ex.GetType().Name}: {ex.Message}")
                            .ConfigureAwait(false);
                    }
                    catch { /* truly nowhere to go */ }
                }
                finally
                {
                    payload.Release();
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task WriteOneAsync(BridgeIoPayload p)
    {
        var stamp = p.TimestampUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"{stamp}-{p.Seq:D4}-{p.Kind}.json";
        var path = Path.Combine(_dir, fileName);

        var node = BuildJson(p);
        await File.WriteAllTextAsync(
            path,
            node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            })).ConfigureAwait(false);
    }

    private static JsonObject BuildJson(BridgeIoPayload p)
    {
        var root = new JsonObject
        {
            ["timestamp"] = p.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            ["seq"] = p.Seq,
            ["kind"] = p.Kind,
        };

        if (p.Method is not null) root["method"] = p.Method;
        if (p.Target is not null) root["target"] = p.Target;
        if (p.Status is not null) root["status"] = p.Status.Value;
        if (p.DurationMs is not null) root["duration_ms"] = p.DurationMs.Value;

        root["headers"] = HeadersNode(p.Headers);
        root["body"] = BodyNode(p.Body, p.BodyLength);

        if (p.Events is { Count: > 0 } evs)
        {
            var arr = new JsonArray();
            foreach (var e in evs)
            {
                var item = new JsonObject
                {
                    ["event"] = e.EventType,
                    ["data"] = ParseOrString(e.Data),
                };
                if (e.Filtered) item["filtered"] = true;
                arr.Add((JsonNode?)item);
            }
            root["events"] = arr;
        }

        if (p.Error is not null) root["error"] = p.Error;

        return root;
    }

    private static JsonObject HeadersNode(IReadOnlyDictionary<string, string> headers)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in headers)
        {
            obj[k] = RedactedHeaders.Contains(k) ? "<redacted>" : v;
        }
        return obj;
    }

    private static JsonNode? BodyNode(byte[] body, int length)
    {
        if (length == 0) return null;
        var text = Encoding.UTF8.GetString(body, 0, length);
        return ParseOrString(text);
    }

    /// <summary>
    /// Body is usually JSON; pretty-printing it as a nested object is much
    /// easier to read than a giant string literal. Falls back to string when
    /// parsing fails (non-JSON content, partial bodies).
    /// </summary>
    private static JsonNode? ParseOrString(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        try
        {
            return JsonNode.Parse(s);
        }
        catch (JsonException)
        {
            return JsonValue.Create(s);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Tell the writer side no more payloads are coming, then wait for
        // the worker to flush the queue. Don't cancel the worker's CT yet —
        // we want it to drain naturally; cancellation is the panic path.
        _channel.Writer.TryComplete();
        try
        {
            // Bounded wait so a stuck worker doesn't hang ProcessExit.
            if (!_worker.Wait(TimeSpan.FromSeconds(5)))
            {
                _shutdownCts.Cancel();
                _worker.Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch (AggregateException) { /* surfaced via worker stderr already */ }

        _shutdownCts.Dispose();
    }
}
