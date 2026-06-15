using System.Net;
using System.Text.Json;

namespace CopilotBridge.Playground.Contract;

/// <summary>
/// Classifies a live Copilot response into the single boolean a contract fact
/// records: did the backend ACCEPT this request shape, or REJECT it? Shared by
/// both backend sweeps so "accepted" means the same thing on
/// <c>/v1/messages</c> and <c>/responses</c>.
/// </summary>
internal static class WireAcceptance
{
    /// <summary>
    /// True = accepted (2xx). False = rejected with a 4xx the backend chose
    /// (the request shape is unsupported). Throws on 5xx / transport — those are
    /// not contract facts (a flaky upstream must not silently look like a
    /// rejection and poison the snapshot).
    /// </summary>
    public static bool IsAccepted(HttpStatusCode status, string body, string context)
    {
        var code = (int)status;
        if (code is >= 200 and < 300) return true;
        if (code is >= 400 and < 500) return false;
        throw new InvalidOperationException(
            $"{context}: non-contract status {code} from Copilot (expected 2xx accept or 4xx reject). " +
            $"Body: {Trim(body, 300)}");
    }

    /// <summary>
    /// Some 500s are a known Copilot quirk we DO want to record as a contract
    /// fact (e.g. <c>mai-code-1-flash-internal</c> 500s on custom tools — research
    /// §2.4). This variant maps a 5xx to "rejected" instead of throwing, for the
    /// specific cells where a server error is the documented behavior.
    /// </summary>
    public static bool IsAcceptedTreating5xxAsReject(HttpStatusCode status)
    {
        var code = (int)status;
        return code is >= 200 and < 300;
    }

    /// <summary>Extract Copilot's error <c>message</c> if the body is a JSON error envelope, else a trimmed body.</summary>
    public static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m))
                    return m.GetString() ?? Trim(body, 200);
                if (err.ValueKind == JsonValueKind.String)
                    return err.GetString() ?? Trim(body, 200);
            }
        }
        catch (JsonException) { /* not JSON — fall through */ }
        return Trim(body, 200);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
