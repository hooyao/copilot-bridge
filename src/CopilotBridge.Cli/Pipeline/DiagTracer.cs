// DiagTracer — single-method file tracer with bounded-queue back pressure.
//
// Modeled after RamDrive's FsTracer
// (https://github.com/hooyao/RamDrive/blob/main/src/RamDrive.Core/Diagnostics/FsTracer.cs)
// with an added producer-consumer buffer so caller threads don't block on
// disk I/O.
//
// Marked [Conditional("BRIDGE_DIAG")] so the C# compiler removes every call
// site (and its argument expressions, including any string interpolation) at
// IL emission time when the consuming project does not define BRIDGE_DIAG.
// Production AOT publish (Release configuration) does not define it; Debug
// configuration does — see CopilotBridge.Cli.csproj. AOT-safe: the IL never
// contains the call, so ILC has nothing to keep or strip.
//
// To enable at runtime in a Debug build, set the BRIDGE_DIAG_FILE environment
// variable. Empty value → log to <exe>/logs/diag.log; explicit path → log
// there; variable unset → tracer stays disabled, even in Debug builds.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

namespace CopilotBridge.Cli.Pipeline;

internal static class DiagTracer
{
    private const string EnvFile = "BRIDGE_DIAG_FILE";

    /// <summary>Max lines buffered before <c>TryWrite</c> drops. ~1 MB at avg 256 B/line.</summary>
    private const int QueueCapacity = 4096;

    /// <summary>Lines per <c>StreamWriter.Flush</c> on the consumer side.</summary>
    private const int FlushBatchSize = 64;

    private static readonly Stopwatch _sw = Stopwatch.StartNew();
    private static readonly Channel<string>? _channel;
    private static readonly StreamWriter? _writer;
    private static readonly Task? _consumerTask;
    private static readonly bool _enabled;
    private static long _dropped;

    static DiagTracer()
    {
        var envPath = Environment.GetEnvironmentVariable(EnvFile);
        if (envPath is null) return;

        var path = envPath.Length > 0
            ? envPath
            : Path.Combine(AppContext.BaseDirectory, "logs", "diag.log");

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
            _writer.WriteLine($"# Bridge diag trace started {DateTime.UtcNow:O}  pid={Environment.ProcessId}");
            _writer.Flush();

            _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
            {
                // Non-blocking caller: Wait mode means TryWrite returns false on full
                // (rather than blocking the producer). We count the drop and move on.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
            _consumerTask = Task.Run(ConsumerLoopAsync);
            _enabled = true;

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
            Console.CancelKeyPress += (_, _) => Shutdown();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DiagTracer] init failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Append one trace line. Call sites in assemblies compiled without the
    /// <c>BRIDGE_DIAG</c> symbol are erased entirely by the C# compiler — the
    /// argument expression (including any string interpolation) is never
    /// evaluated. Zero runtime cost, zero AOT footprint.
    /// </summary>
    [Conditional("BRIDGE_DIAG")]
    public static void Log(string message)
    {
        if (!_enabled) return;
        Enqueue(message);
    }

    /// <summary>
    /// Append a JSON-serialized snapshot of an arbitrary object. Reflection-
    /// based serialization is fine because <see cref="ConditionalAttribute"/>
    /// erases call sites when <c>BRIDGE_DIAG</c> is undefined; the body is
    /// unreachable in AOT-published builds and gets trimmed. The suppression
    /// attributes silence trim/AOT analyzer warnings on the rare path where a
    /// caller doesn't propagate the conditional gate.
    /// </summary>
    [Conditional("BRIDGE_DIAG")]
    [RequiresDynamicCode("DiagTracer.Log(object) uses reflection-based JSON serialization; only reached in BRIDGE_DIAG-enabled (Debug) builds.")]
    [RequiresUnreferencedCode("DiagTracer.Log(object) uses reflection-based JSON serialization; only reached in BRIDGE_DIAG-enabled (Debug) builds.")]
    public static void Log(object? value)
    {
        if (!_enabled) return;
        string serialized;
        try
        {
            serialized = value is null ? "null" : JsonSerializer.Serialize(value);
        }
        catch (Exception ex)
        {
            serialized = $"<serialize failed: {ex.Message}>";
        }
        Enqueue(serialized);
    }

    public static bool Enabled => _enabled;

    /// <summary>Lines dropped because the bounded queue was full. Surfaced on shutdown.</summary>
    public static long DroppedCount => Interlocked.Read(ref _dropped);

    private static void Enqueue(string content)
    {
        var ts = _sw.Elapsed.TotalSeconds.ToString("0.000000", CultureInfo.InvariantCulture);
        var tid = Environment.CurrentManagedThreadId;
        var line = $"{ts}  T{tid,-4}  {content}";
        if (!_channel!.Writer.TryWrite(line))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private static async Task ConsumerLoopAsync()
    {
        if (_channel is null || _writer is null) return;
        var batch = 0;
        try
        {
            await foreach (var line in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                _writer.WriteLine(line);
                if (++batch >= FlushBatchSize)
                {
                    _writer.Flush();
                    batch = 0;
                }
            }
        }
        catch (Exception ex)
        {
            try { _writer.WriteLine($"# consumer crashed: {ex.Message}"); } catch { /* ignore */ }
        }
        finally
        {
            try { _writer.Flush(); } catch { /* ignore */ }
        }
    }

    private static void Shutdown()
    {
        if (!_enabled) return;
        try
        {
            _channel!.Writer.TryComplete();
            _consumerTask!.Wait(TimeSpan.FromSeconds(2));
            var dropped = Interlocked.Read(ref _dropped);
            if (dropped > 0)
            {
                _writer!.WriteLine($"# dropped {dropped} lines due to full queue (capacity={QueueCapacity})");
            }
            _writer!.Flush();
            _writer.Dispose();
        }
        catch { /* best-effort shutdown */ }
    }
}
