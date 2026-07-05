using System.Text.Json;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Common;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Stages;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract tests for <see cref="PoisonedContextScanStage"/> — the request-side
/// observe-only stage. Contract:
/// <list type="bullet">
///   <item>records the TOTAL failure count on
///         <see cref="BridgeContext{TBody}.PoisonedToolResults"/>;</item>
///   <item>NEVER mutates the request body (observe-only) and NEVER blocks it;</item>
///   <item>logs exactly one WARNING (naming the looping tool) when a SINGLE tool's
///         failure count reaches <see cref="PoisonedContextOptions.WarnThreshold"/>,
///         and stays silent when no single tool crosses it — even if the grand total
///         does;</item>
///   <item>does nothing at all — no scan, no warning — when disabled.</item>
/// </list>
/// </summary>
public class PoisonedContextScanStageTests
{
    private static JsonElement StringContent(string s)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(s));
        return doc.RootElement.Clone();
    }

    private static ToolUseBlockParam Use(string id, string name)
    {
        using var doc = JsonDocument.Parse("{}");
        return new ToolUseBlockParam { Id = id, Name = name, Input = doc.RootElement.Clone() };
    }

    private static ToolResultBlockParam Fail(string useId) =>
        new() { ToolUseId = useId, Content = StringContent("API Error: 400 The requested model is not supported") };

    private static ToolResultBlockParam Ok(string useId) =>
        new() { ToolUseId = useId, Content = StringContent("ok") };

    /// <summary>A body where tool <paramref name="tool"/> fails <paramref name="failures"/>
    /// times, plus one clean result from another tool (to prove clean isn't counted).</summary>
    private static MessagesRequest BodyWith(int failures, string tool = "Agent")
    {
        var blocks = new List<ContentBlockParam>();
        for (var i = 0; i < failures; i++)
        {
            blocks.Add(Use($"{tool}{i}", tool));
            blocks.Add(Fail($"{tool}{i}"));
        }
        blocks.Add(Use("ok0", "Read"));
        blocks.Add(Ok("ok0"));
        return new MessagesRequest
        {
            Model = "gpt-5.5",
            Messages = [new MessageParam { Role = Role.User, Content = blocks }],
        };
    }

    private static (BridgeContext<MessagesRequest> Ctx, List<RecordedEvent> Events, PoisonedContextScanStage Stage)
        Build(MessagesRequest body, PoisonedContextOptions opts)
    {
        var ctx = new BridgeContext<MessagesRequest>
        {
            Request = new BridgeRequest<MessagesRequest>
            {
                Method = "POST",
                Path = "/cc/v1/messages",
                Body = body,
            },
            Response = new BridgeResponse(),
        };
        var provider = new RecordingLoggerProvider();
        var logger = provider.CreateLogger(typeof(PoisonedContextScanStage).FullName!);
        var stage = new PoisonedContextScanStage(
            TestOptions.Snapshot(opts),
            ctx,
            new TestLogger<PoisonedContextScanStage>(logger));
        return (ctx, provider.Events, stage);
    }

    // ── Total failure count recorded on the context ──────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(50)]
    public async Task RecordsTotalFailureCount_OnContext(int failures)
    {
        var (ctx, _, stage) = Build(BodyWith(failures), new PoisonedContextOptions());

        await stage.ApplyAsync();

        Assert.Equal(failures, ctx.PoisonedToolResults);
    }

    // ── WARNING keys off the WORST SINGLE TOOL, at/above threshold ───────────────

    [Fact]
    public async Task Warns_WhenOneToolReachesThreshold_NamingTheTool()
    {
        var (_, events, stage) = Build(
            BodyWith(5, tool: "Agent"),
            new PoisonedContextOptions { WarnThreshold = 5 });

        await stage.ApplyAsync();

        var warning = Assert.Single(events, e => e.Level == LogLevel.Warning);
        Assert.Contains("compact", warning.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Agent", warning.Message); // names the looping tool
    }

    [Fact]
    public async Task DoesNotWarn_WhenNoSingleToolCrossesThreshold_EvenIfTotalDoes()
    {
        // Four different tools each fail twice: total = 8 (over threshold 5) but the
        // WORST single tool is only 2. This is NOT a replay loop — must not warn.
        var blocks = new List<ContentBlockParam>();
        foreach (var tool in new[] { "Agent", "Grep", "Read", "WebSearch" })
        {
            for (var i = 0; i < 2; i++)
            {
                blocks.Add(Use($"{tool}{i}", tool));
                blocks.Add(Fail($"{tool}{i}"));
            }
        }
        var body = new MessagesRequest
        {
            Model = "gpt-5.5",
            Messages = [new MessageParam { Role = Role.User, Content = blocks }],
        };
        var (ctx, events, stage) = Build(body, new PoisonedContextOptions { WarnThreshold = 5 });

        await stage.ApplyAsync();

        Assert.Equal(8, ctx.PoisonedToolResults);                          // total still recorded
        Assert.DoesNotContain(events, e => e.Level == LogLevel.Warning);   // but no single-tool loop
    }

    [Fact]
    public async Task DoesNotWarn_BelowThreshold()
    {
        var (ctx, events, stage) = Build(
            BodyWith(4, tool: "Agent"),
            new PoisonedContextOptions { WarnThreshold = 5 });

        await stage.ApplyAsync();

        Assert.Equal(4, ctx.PoisonedToolResults);
        Assert.DoesNotContain(events, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task DoesNotWarn_WhenNoFailures()
    {
        var (_, events, stage) = Build(BodyWith(0), new PoisonedContextOptions { WarnThreshold = 5 });

        await stage.ApplyAsync();

        Assert.DoesNotContain(events, e => e.Level == LogLevel.Warning);
    }

    // ── Disabled → no work at all ────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_DoesNotScanOrWarn()
    {
        var (ctx, events, stage) = Build(
            BodyWith(50),
            new PoisonedContextOptions { Enabled = false });

        await stage.ApplyAsync();

        Assert.Equal(0, ctx.PoisonedToolResults);
        Assert.DoesNotContain(events, e => e.Level == LogLevel.Warning);
    }

    // ── Observe-only: the request body is never mutated ──────────────────────────

    [Fact]
    public async Task DoesNotMutateBody()
    {
        var body = BodyWith(50);
        var originalBlockCount = body.Messages[0].Content.Count;
        var (ctx, _, stage) = Build(body, new PoisonedContextOptions());

        await stage.ApplyAsync();

        Assert.Same(body, ctx.Request.Body);
        Assert.Equal(originalBlockCount, ctx.Request.Body.Messages[0].Content.Count);
    }

    /// <summary>Adapts a category-named <see cref="ILogger"/> to
    /// <see cref="ILogger{T}"/> so the stage's typed logger records into the
    /// provider's event list.</summary>
    private sealed class TestLogger<T> : ILogger<T>
    {
        private readonly ILogger _inner;
        public TestLogger(ILogger inner) => _inner = inner;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, System.Exception? exception, System.Func<TState, System.Exception?, string> formatter) =>
            _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
