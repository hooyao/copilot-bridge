using System.Net.ServerSentEvents;
using System.Text.Json;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Detects a response leak in a streaming response and, on detection, returns an
/// <see cref="DetectionActionKind.Abort"/> so the framework forces the client to
/// retry the turn. Feeds each <c>text_delta</c> (and, when
/// <see cref="ToolLeakGuardOptions.ScanThinking"/> is on, <c>thinking_delta</c>)
/// into a single per-block <see cref="ResponseLeakAutomaton"/> that detects both
/// leaked <c>&lt;invoke&gt;</c> tool calls and leaked Claude Code control envelopes
/// (task notifications, teammate/channel/cross-session messages, ticks). The
/// automaton is reset at each <c>content_block_start</c>.
/// </summary>
/// <remarks>
/// Scoped DI service. Construction is pure DI (an <c>IOptionsSnapshot</c> + a
/// logger); the automaton — which depends on the request's declared tools and the
/// enabled-signature gate — is (re)built in <see cref="Begin"/>, once per request,
/// so streaming state never crosses requests.
/// </remarks>
internal sealed class ToolLeakDetector : IResponseDetector
{
    private readonly ToolLeakGuardOptions _opts;
    private readonly ILogger _log;

    private ResponseLeakAutomaton? _automaton;

    // Current block scope, tracked from event ordering (blocks are contiguous).
    private bool _blockIsScannable;
    private string? _blockType;

    // Config seam for future hot-reload. IOptionsSnapshot<T> is a scoped service:
    // its .Value re-binds per request scope, so the detector already reads the
    // options afresh on every request rather than a startup-frozen snapshot. The
    // one remaining reason a config edit still needs a restart is that the JSON
    // provider is registered with reloadOnChange:false (the project's edit+restart
    // convention). Flipping that single flag to true in BridgeConfigurationExtensions
    // would make a toggled switch take effect on the next request with zero change
    // to this detector or the stage. Today a restart is required — which both the
    // retry error and the warning log state.
    public ToolLeakDetector(IOptionsSnapshot<ToolLeakGuardOptions> opts, ILogger<ToolLeakDetector> log)
    {
        _opts = opts.Value;
        _log = log;
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

    public bool Enabled => _opts.Enabled;

    /// <summary>Buffer the whole response when the guard is configured to emit a
    /// real HTTP status instead of a mid-stream SSE error.</summary>
    public bool RequiresBuffering => !_opts.PreserveStream;

    public void Begin(BridgeContext<MessagesRequest> ctx)
    {
        var tools = ctx.Request.Body.Tools;
        var names = tools is null
            ? Array.Empty<string>()
            : tools.Select(t => t.Name);
        // Build the automaton from this request's declared tools + the enabled
        // signature gate (computed from the current options snapshot). Reset the
        // per-block streaming state so nothing carries over from a prior request.
        _automaton = new ResponseLeakAutomaton(names, BuildEnabledSignatures(_opts.Signatures));
        _blockIsScannable = false;
        _blockType = null;
    }

    public DetectionAction InspectEvent(in SseItem<string> evt)
    {
        var automaton = _automaton!;
        switch (evt.EventType)
        {
            case "content_block_start":
                _blockType = BlockType(evt.Data);
                _blockIsScannable = _blockType == "text" || (_blockType == "thinking" && _opts.ScanThinking);
                // Thinking blocks have no fence concept → always-unfenced; text
                // blocks track fences so a teaching example inside ``` isn't a leak.
                automaton.Reset(trackFences: _blockType != "thinking");
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
                            if (automaton.Feed(c))
                            {
                                var subject = automaton.MatchedSubject ?? "?";
                                var signature = automaton.MatchedSignature ?? "?";
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
