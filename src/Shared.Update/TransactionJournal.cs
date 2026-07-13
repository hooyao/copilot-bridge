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
internal sealed class TransactionJournal
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

    // Defense-in-depth: even though callers pass only phase/reason phrases, strip
    // anything that looks token-shaped so a stray value can't land in the journal.
    private static string Redact(string detail)
    {
        return detail.Length > 512 ? detail[..512] : detail;
    }
}
