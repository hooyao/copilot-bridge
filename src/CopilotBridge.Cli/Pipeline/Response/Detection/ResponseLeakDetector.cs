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
/// <see cref="ResponseLeakGuardOptions.ScanThinking"/> is on, <c>thinking_delta</c>)
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
internal sealed class ResponseLeakDetector : AbstractOrderAwareDetector<ResponseLeakDetector>
{
    private readonly ResponseLeakGuardOptions _opts;
    private readonly BridgeContext<MessagesRequest> _ctx;
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
    public ResponseLeakDetector(
        DetectorOrder<ResponseLeakDetector> order,
        IOptionsSnapshot<ResponseLeakGuardOptions> opts,
        BridgeContext<MessagesRequest> ctx,
        ILogger<ResponseLeakDetector> log) : base(order)
    {
        _opts = opts.Value;
        _ctx = ctx;
        _log = log;
    }

    /// <summary>Translate the per-signature option flags into the set of enabled
    /// signature ids the automaton watches. Derived from the single source
    /// <see cref="LeakSignatures.All"/> filtered through
    /// <see cref="ResponseLeakSignaturesOptions.IsEnabled"/>, so a new signature is wired
    /// by adding it to <see cref="LeakSignatures"/> + one <c>IsEnabled</c> case — not
    /// by extending a parallel hand-maintained list here.</summary>
    private static IReadOnlySet<string> BuildEnabledSignatures(ResponseLeakSignaturesOptions s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in LeakSignatures.All)
        {
            if (s.IsEnabled(id)) set.Add(id);
        }
        return set;
    }

    public override string Name => "ResponseLeak";

    public override bool Enabled => _opts.Enabled;

    /// <summary>Buffer the whole response when the guard is configured to emit a
    /// real HTTP status instead of a mid-stream SSE error.</summary>
    public override bool RequiresBuffering => !_opts.PreserveStream;

    public override void Begin()
    {
        // Build the automaton from this request's declared tools + the enabled
        // signature gate (computed from the current options snapshot). Reset the
        // per-block streaming state so nothing carries over from a prior request.
        _automaton = BuildAutomaton();
        _blockIsScannable = false;
        _blockType = null;
    }

    /// <summary>Build a fresh automaton for this request from its declared tools and
    /// the enabled-signature gate. Shared by the streaming (<see cref="Begin"/>) and
    /// buffered (<see cref="InspectBuffered"/>) paths so the two never drift.</summary>
    private ResponseLeakAutomaton BuildAutomaton()
    {
        var tools = _ctx.Request.Body.Tools;
        var names = tools is null
            ? Array.Empty<string>()
            : tools.Select(t => t.Name);
        return new ResponseLeakAutomaton(names, BuildEnabledSignatures(_opts.Signatures));
    }

    /// <summary>Log the detection and build the abort action. Single source for both
    /// the streaming and buffered paths so the warning text and error stay identical;
    /// <paramref name="delivery"/> distinguishes <c>stream</c> vs <c>buffer</c>.</summary>
    private DetectionAction Trip(ResponseLeakAutomaton automaton, string? blockType, string delivery)
    {
        var subject = automaton.MatchedSubject ?? "?";
        var signature = automaton.MatchedSignature ?? "?";
        var signal = _opts.Signal;
        // Log at the detection point — the only place that knows the leaked
        // signature + subject + block. NOT the leaked text (that stays in the
        // opt-in trace files only). Names the exact switch to disable a false
        // positive; a restart is required after a config change (read at startup).
        _log.LogWarning(
            "response-leak detected: signature={Signature} subject={Subject} disable-key={DisableKey} block={Block} signal={Signal} delivery={Delivery} — forcing client retry (restart required after changing the switch)",
            signature,
            subject,
            ResponseLeakError.ConfigPath(signature),
            blockType ?? "?",
            ResponseLeakError.ErrorType(signal),
            delivery);
        // Own the "leak detected" flag here rather than in the stage: the stage
        // sees only a generic Abort action and can't tell a leak from a runaway,
        // so each detector marks its own context flag on trip. The endpoint reads
        // it after the stream drains (same scoped BridgeContext instance).
        _ctx.ResponseLeakDetected = true;
        return DetectionAction.Abort(
            ResponseLeakError.Json(signal, signature),
            ResponseLeakError.HttpStatus(signal));
    }

    public override DetectionAction InspectEvent(in SseItem<string> evt)
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
                                return Trip(automaton, _blockType, RequiresBuffering ? "buffer" : "stream");
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

    /// <summary>
    /// Buffered (non-streaming) counterpart of <see cref="InspectEvent"/>: scan a
    /// whole Anthropic Messages response body once. Feeds each <c>text</c> block
    /// (and each <c>thinking</c> block when <see cref="ResponseLeakGuardOptions.ScanThinking"/>
    /// is on) through a fresh per-block automaton, mirroring the streaming semantics
    /// (fences tracked for text, not for thinking). Aborts on the first leak with the
    /// same error + warning as the streaming path. Fails open (returns None) on a
    /// body that isn't parseable Anthropic JSON — a scan must never turn a real
    /// response into an error on a parse hiccup.
    /// </summary>
    public override DetectionAction InspectBuffered(byte[] body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return DetectionAction.None; // unparseable → fail open, deliver as-is
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                return DetectionAction.None;
            }

            // One automaton, reset per block — same instance discipline as streaming.
            var automaton = BuildAutomaton();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object
                    || !block.TryGetProperty("type", out var typeEl))
                {
                    continue;
                }
                var blockType = typeEl.GetString();
                var field = blockType switch
                {
                    "text" => "text",
                    "thinking" when _opts.ScanThinking => "thinking",
                    _ => null,
                };
                if (field is null || !block.TryGetProperty(field, out var textEl)
                    || textEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                // Thinking blocks have no fence concept → always-unfenced; text
                // blocks track fences so a teaching example inside ``` isn't a leak.
                automaton.Reset(trackFences: blockType != "thinking");
                foreach (var c in textEl.GetString()!)
                {
                    if (automaton.Feed(c))
                    {
                        return Trip(automaton, blockType, "buffer");
                    }
                }
            }
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
