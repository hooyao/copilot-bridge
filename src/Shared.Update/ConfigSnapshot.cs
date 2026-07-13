using System.Security.Cryptography;
using System.Text;

namespace CopilotBridge.Update.Wire;

/// <summary>
/// One immutable byte snapshot of the installed <c>appsettings.json</c>, read
/// exactly once. The SAME bytes are used for parsing/merging AND for the private
/// rollback copy, so the merge input, the backup, and the file eventually
/// renamed to <c>.bak</c> can never diverge if an operator edits the file during
/// a long download/preflight. The hash lets the updater revalidate the installed
/// file before <c>Prepared</c> and the renamed <c>.bak</c> before installing.
/// </summary>
internal sealed class ConfigSnapshot
{
    private ConfigSnapshot(byte[] bytes, string sha256Hex)
    {
        _bytes = bytes;
        Sha256Hex = sha256Hex;
    }

    // Private so the snapshot is genuinely immutable — no caller can obtain the
    // array and mutate it out of sync with Sha256Hex. Consumers use Text /
    // Sha256Hex / MatchesFile / WriteVerifiedCopy.
    private readonly byte[] _bytes;

    public string Sha256Hex { get; }

    public string Text => Encoding.UTF8.GetString(_bytes);

    /// <summary>Read the installed config once into an immutable snapshot.</summary>
    public static ConfigSnapshot Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new ConfigSnapshot(bytes, Hash(bytes));
    }

    /// <summary>
    /// True when the file at <paramref name="path"/> still hashes to this
    /// snapshot's digest (i.e. it has not drifted since the snapshot was taken).
    /// </summary>
    public bool MatchesFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        return string.Equals(Hash(File.ReadAllBytes(path)), Sha256Hex, StringComparison.Ordinal);
    }

    /// <summary>
    /// Write the exact snapshot bytes to <paramref name="path"/> and confirm the
    /// written file reads back byte-identical (used for the verified private
    /// rollback copy — preparation cannot complete unless this holds).
    /// </summary>
    public bool WriteVerifiedCopy(string path)
    {
        File.WriteAllBytes(path, _bytes);
        return MatchesFile(path);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
