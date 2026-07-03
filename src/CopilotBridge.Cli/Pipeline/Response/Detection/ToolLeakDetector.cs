using System.Net.ServerSentEvents;
using System.Text.Json;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Detects a response leak in a streaming response and, on detection, returns an
/// <see cref="DetectionActionKind.Abort"/> so the framework forces the client to
/// retry the turn. Feeds each <c>text_delta</c> (and, when
/// <see cref="ToolLeakGuardOptions.ScanThinking"/> is on, <c>thinking_delta</c>)
/// into a single per-block <see cref="ResponseLeakAutomaton"/> that detects both
/// leaked <c>&lt;invoke&gt;</c> tool calls and leaked Claude Code control envelopes
/// (task notifications, teammate/channel/cross-session messages, ticks). The
/// automaton is reset at each <c>content_block_start</c>. Per-request instance
/// (holds the automaton state).
/// </summary>
internal sealed class ToolLeakDetector : IResponseDetector
{
    private readonly ToolLeakGuardOptions _opts;
    private readonly ResponseLeakAutomaton _automaton;
    private readonly ILogger _log;

    // Current block scope, tracked from event ordering (blocks are contiguous).
    private bool _blockIsScannable;
    private string? _blockType;

    public ToolLeakDetector(ToolLeakGuardOptions opts, IReadOnlyList<Tool>? tools, ILogger log)
    {
        _opts = opts;
        _log = log;
        var names = tools is null
            ? Array.Empty<string>()
            : tools.Select(t => t.Name);
        // Compute the enabled-signature gate here, per detector construction (i.e.
        // per request), straight from the options handed in — never a static or
        // startup cache. This is the single seam for future config hot-reload:
        // because detectors are rebuilt per request, pointing DetectorSetFactory at
        // a live options source (IOptionsMonitor.CurrentValue) instead of the frozen
        // IOptions snapshot would make a flipped switch take effect on the next
        // request with no change here. Today config is read at startup, so a restart
        // is required — which both the retry error and the warning log state.
        _automaton = new ResponseLeakAutomaton(names, BuildEnabledSignatures(_opts.Signatures));
    }

    /// <summary>Translate the per-signature option flags into the set of enabled
    /// signature ids the automaton watches (an unset flag omits its matcher).</summary>
    private static IReadOnlySet<string> BuildEnabledSignatures(ToolLeakSignaturesOptions s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (s.Invoke) set.Add(LeakSignatures.Invoke);
        if (s.TaskNotification) set.Add(LeakSignatures.TaskNotification);
        if (s.TeammateMessage) set.Add(LeakSignatures.TeammateMessage);
        if (s.Channel) set.Add(LeakSignatures.Channel);
        if (s.CrossSessionMessage) set.Add(LeakSignatures.CrossSessionMessage);
        if (s.Tick) set.Add(LeakSignatures.Tick);
        return set;
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
                _blockType = BlockType(evt.Data);
                _blockIsScannable = _blockType == "text" || (_blockType == "thinking" && _opts.ScanThinking);
                // Thinking blocks have no fence concept → always-unfenced; text
                // blocks track fences so a teaching example inside ``` isn't a leak.
                _automaton.Reset(trackFences: _blockType != "thinking");
                break;

            case "content_block_delta":
                if (_blockIsScannable)
                {
                    var text = ExtractDeltaText(evt.Data);
                    if (text is not null)
                    {
                        foreach (var c in text)
                        {
                            // Feed the automaton every character (it keeps its own
                            // O(1) state); a leaked <invoke> or a leaked control
                            // envelope both abort the turn.
                            if (_automaton.Feed(c))
                            {
                                var subject = _automaton.MatchedSubject ?? "?";
                                var signature = _automaton.MatchedSignature ?? "?";
                                var signal = _opts.Signal;
                                // Log at the detection point — the only place that
                                // knows the leaked signature + subject + block. NOT
                                // the leaked text (that stays in the opt-in trace
                                // files only). Names the exact switch to disable a
                                // false positive; a restart is required after a
                                // config change (config is read at startup).
                                _log.LogWarning(
                                    "response-leak detected: signature={Signature} subject={Subject} disable-key={DisableKey} block={Block} signal={Signal} delivery={Delivery} — forcing client retry (restart required after changing the switch)",
                                    signature,
                                    subject,
                                    ToolLeakError.ConfigPath(signature),
                                    _blockType ?? "?",
                                    ToolLeakError.ErrorType(signal),
                                    RequiresBuffering ? "buffer" : "stream");
                                return DetectionAction.Abort(
                                    ToolLeakError.Json(signal, signature),
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
