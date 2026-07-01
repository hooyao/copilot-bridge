using System.Net.ServerSentEvents;
using System.Text.Json;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Detects a tool-call leak in a streaming response and, on detection, returns an
/// <see cref="DetectionActionKind.Abort"/> so the framework forces the client to
/// retry the turn. Feeds each <c>text_delta</c> (and, when
/// <see cref="ToolLeakGuardOptions.ScanThinking"/> is on, <c>thinking_delta</c>)
/// into a per-block <see cref="ToolLeakAutomaton"/>; resets the automaton at each
/// <c>content_block_start</c>. Per-request instance (holds the automaton state).
/// </summary>
internal sealed class ToolLeakDetector : IResponseDetector
{
    private readonly ToolLeakGuardOptions _opts;
    private readonly ToolLeakAutomaton _automaton;

    // Current block scope, tracked from event ordering (blocks are contiguous).
    private bool _blockIsScannable;

    public ToolLeakDetector(ToolLeakGuardOptions opts, IReadOnlyList<Tool>? tools)
    {
        _opts = opts;
        var names = tools is null
            ? Array.Empty<string>()
            : tools.Select(t => t.Name);
        _automaton = new ToolLeakAutomaton(names);
    }

    public string Name => "ToolLeak";

    /// <summary>Buffer the whole response when the guard is configured to emit a
    /// real HTTP status instead of a mid-stream SSE error.</summary>
    public bool RequiresBuffering => !_opts.PreserveStream;

    public DetectionAction InspectEvent(in SseItem<string> evt)
    {
        switch (evt.EventType)
        {
            case "content_block_start":
                var type = BlockType(evt.Data);
                _blockIsScannable = type == "text" || (type == "thinking" && _opts.ScanThinking);
                // Thinking blocks have no fence concept → always-unfenced; text
                // blocks track fences so a teaching example inside ``` isn't a leak.
                _automaton.Reset(trackFences: type != "thinking");
                break;

            case "content_block_delta":
                if (_blockIsScannable)
                {
                    var text = ExtractDeltaText(evt.Data);
                    if (text is not null)
                    {
                        foreach (var c in text)
                        {
                            if (_automaton.Feed(c))
                            {
                                var signal = _opts.Signal;
                                return DetectionAction.Abort(
                                    ToolLeakError.Json(signal),
                                    ToolLeakError.HttpStatus(signal));
                            }
                        }
                    }
                }
                break;

            case "content_block_stop":
                _blockIsScannable = false;
                break;
        }

        return DetectionAction.None;
    }

    /// <summary>The content block's type (<c>text</c>/<c>thinking</c>/…), or null
    /// if the start event can't be parsed.</summary>
    private static string? BlockType(string startData)
    {
        try
        {
            using var doc = JsonDocument.Parse(startData);
            if (!doc.RootElement.TryGetProperty("content_block", out var cb)
                || !cb.TryGetProperty("type", out var t))
            {
                return null;
            }
            return t.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Extract the streamed text off a <c>content_block_delta</c>:
    /// <c>text_delta.text</c> or <c>thinking_delta.thinking</c>.</summary>
    private static string? ExtractDeltaText(string deltaData)
    {
        try
        {
            using var doc = JsonDocument.Parse(deltaData);
            if (!doc.RootElement.TryGetProperty("delta", out var delta)
                || !delta.TryGetProperty("type", out var dt))
            {
                return null;
            }
            return dt.GetString() switch
            {
                "text_delta" => delta.TryGetProperty("text", out var x) ? x.GetString() : null,
                "thinking_delta" => delta.TryGetProperty("thinking", out var x) ? x.GetString() : null,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
