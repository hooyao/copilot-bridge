using CopilotBridge.Cli.Hosting.Options;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Maps a <see cref="ResponseLeakSignal"/> to the Anthropic error wire shape it emits
/// and the HTTP status it uses in buffered delivery. Shared by both delivery
/// modes so streaming injection and buffered rejection agree.
/// </summary>
internal static class ResponseLeakError
{
    /// <summary>The Anthropic <c>error.type</c> string for a signal.</summary>
    public static string ErrorType(ResponseLeakSignal signal) => signal switch
    {
        ResponseLeakSignal.ApiError => "api_error",
        _ => "overloaded_error",
    };

    /// <summary>The HTTP status used in buffered delivery for a signal.</summary>
    public static int HttpStatus(ResponseLeakSignal signal) => signal switch
    {
        ResponseLeakSignal.ApiError => 500,
        _ => 529,
    };

    /// <summary>The config section holding the per-signature disable switches.</summary>
    private const string SignaturesSection = "Pipeline:Detectors:ResponseLeakGuard:Signatures";

    /// <summary>
    /// The full config path whose switch disables <paramref name="signature"/>,
    /// e.g. <c>invoke</c> → <c>Pipeline:Detectors:ResponseLeakGuard:Signatures:Invoke</c>.
    /// </summary>
    public static string ConfigPath(string signature) =>
        SignaturesSection + ":" + ConfigKey(signature);

    /// <summary>
    /// Map a kebab-case signature id to its PascalCase config key
    /// (<c>cross-session-message</c> → <c>CrossSessionMessage</c>). Deterministic
    /// split-on-'-' + capitalize, so the key never drifts from a lookup table.
    /// </summary>
    public static string ConfigKey(string signature)
    {
        var sb = new System.Text.StringBuilder(signature.Length);
        var atSegmentStart = true;
        foreach (var ch in signature)
        {
            if (ch == '-')
            {
                atSegmentStart = true;
                continue;
            }
            sb.Append(atSegmentStart ? char.ToUpperInvariant(ch) : ch);
            atSegmentStart = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Human-readable diagnostic naming the tripped <paramref name="signature"/> and
    /// the exact switch to turn it off if it is a false positive (plus the required
    /// restart). Prefixed so a reader can attribute it. Deliberately contains no
    /// <c>"</c> or <c>\</c> so <see cref="Json"/> can embed it in hand-built JSON
    /// without escaping.
    /// </summary>
    public static string Message(string signature) =>
        "[copilot-bridge] Detected leaked Claude Code protocol markup ('"
        + signature
        + "') emitted as assistant text; forcing a clean retry. False positive (you were discussing this markup)? Disable just this signature: set "
        + ConfigPath(signature)
        + "=false in appsettings.json and restart copilot-bridge.";

    /// <summary>
    /// The Anthropic error JSON for the SSE <c>error</c> event / buffered body.
    /// Hand-built (not via a DTO) so it is AOT-safe and identical in both paths:
    /// <c>{"type":"error","error":{"type":"…","message":"…"}}</c>. The message
    /// names <paramref name="signature"/> and its disable switch.
    /// </summary>
    public static string Json(ResponseLeakSignal signal, string signature) =>
        "{\"type\":\"error\",\"error\":{\"type\":\""
        + ErrorType(signal)
        + "\",\"message\":\""
        + Message(signature)
        + "\"}}";
}
