using Microsoft.Extensions.Logging;

namespace CopilotBridge.UnitTests;

/// <summary>
/// In-memory MEL provider that records every log call as a flat record. Lets
/// tests assert that the new <c>RequestSummaryLogger</c> emits the expected
/// level, message template, and structured properties without spinning up a
/// real Serilog pipeline.
/// </summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    public List<RecordedEvent> Events { get; } = new();

    public ILogger CreateLogger(string categoryName) =>
        new RecordingLogger(categoryName, Events);

    public void Dispose() { }

    private sealed class RecordingLogger : ILogger
    {
        private readonly string _category;
        private readonly List<RecordedEvent> _events;

        public RecordingLogger(string category, List<RecordedEvent> events)
        {
            _category = category;
            _events = events;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var props = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                foreach (var kv in kvps)
                {
                    props[kv.Key] = kv.Value;
                }
            }

            _events.Add(new RecordedEvent(
                _category,
                logLevel,
                eventId,
                formatter(state, exception),
                exception,
                props));
        }
    }
}

/// <summary>One recorded log call. The <see cref="Properties"/> map captures
/// the structured placeholders from a templated <c>LogInformation(...)</c>
/// call so tests can assert on field-by-field values.</summary>
public sealed record RecordedEvent(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties);
