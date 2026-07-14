using System.Globalization;

namespace CopilotBridge.Update.Wire;

/// <summary>
/// An owner-private, append-only phase journal written next to the attempt's
/// working directory (never among managed install files). It records phase
/// transitions and concise failure reasons before and after each destructive
/// step so a transaction interrupted by an abrupt updater death is diagnosable
/// and manually recoverable. It NEVER records authentication secrets or full
/// configuration contents. This is a best-effort diagnostic aid, not a
/// crash-recovery coordinator.
/// </summary>
internal sealed partial class TransactionJournal
{
    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();

    public TransactionJournal(string path, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Append one phase line. Failures to write are swallowed (best-effort).</summary>
    public void Write(string phase, string? detail = null)
    {
        var stamp = _now().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var line = detail is null ? $"{stamp} {phase}" : $"{stamp} {phase}: {Redact(detail)}";
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            lock (_gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Journal is diagnostic only — never let a logging failure derail the
            // transaction.
        }
    }

    // Defense-in-depth: callers pass only phase/reason phrases, but Write accepts
    // arbitrary text, so scrub anything token-shaped before it is persisted — the
    // journal's contract is that it NEVER records a capability/bearer secret. We
    // redact long unbroken runs of token-alphabet characters (hex, base64/64url):
    // capability and handoff tokens are 64-hex, GitHub/bearer tokens are long
    // base64-ish blobs. Short words (phase names, type names) are well under the
    // threshold and untouched. Then bound the length.
    private static string Redact(string detail)
    {
        var scrubbed = TokenShaped().Replace(detail, "[redacted]");
        return scrubbed.Length > 512 ? scrubbed[..512] : scrubbed;
    }

    // 32+ consecutive token-alphabet chars (A–Z a–z 0–9 + / - _). 32 clears every
    // ordinary identifier/phrase we log while catching 64-hex capabilities and
    // base64 bearer tokens.
    [System.Text.RegularExpressions.GeneratedRegex(@"[A-Za-z0-9+/\-_]{32,}")]
    private static partial System.Text.RegularExpressions.Regex TokenShaped();
}
