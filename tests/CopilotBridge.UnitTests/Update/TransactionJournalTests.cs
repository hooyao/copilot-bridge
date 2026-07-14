using CopilotBridge.Update.Wire;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Contract tests for <see cref="TransactionJournal"/> redaction ("never records
/// authentication secrets"). The journal accepts arbitrary diagnostic text, so its
/// defense-in-depth promise is only real if token-shaped values are actually
/// scrubbed before they hit disk — not merely truncated.
/// </summary>
public class TransactionJournalTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public TransactionJournalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "transaction.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_capability_token_in_detail_is_redacted_not_persisted()
    {
        // A 64-hex capability token — the exact shape UpdateCapability mints.
        var token = new string('a', 64);
        var journal = new TransactionJournal(_path);

        journal.Write("handoff.prepared", $"token={token}");

        var contents = File.ReadAllText(_path);
        Assert.DoesNotContain(token, contents);
        Assert.Contains("[redacted]", contents);
        // The non-secret phase name is still there for diagnostics.
        Assert.Contains("handoff.prepared", contents);
    }

    [Fact]
    public void A_bearer_like_blob_is_redacted()
    {
        var bearer = "ghp_" + new string('X', 36) + "AbCd0123";
        var journal = new TransactionJournal(_path);

        journal.Write("recover.old-bridge", $"leaked {bearer} here");

        var contents = File.ReadAllText(_path);
        Assert.DoesNotContain(bearer, contents);
        Assert.Contains("[redacted]", contents);
    }

    [Fact]
    public void Ordinary_phase_and_reason_text_is_left_intact()
    {
        // Short words (phase names, type names, human reasons) are well under the
        // token threshold and must NOT be redacted — over-redaction would gut the
        // journal's diagnostic value.
        var journal = new TransactionJournal(_path);

        journal.Write("cutover.failed", "config drifted before cutover (IOException)");

        var contents = File.ReadAllText(_path);
        Assert.Contains("config drifted before cutover", contents);
        Assert.DoesNotContain("[redacted]", contents);
    }
}
