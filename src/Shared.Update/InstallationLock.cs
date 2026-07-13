using System.Security.Cryptography;
using System.Text;

namespace CopilotBridge.Update.Wire;

/// <summary>
/// An exclusive, process-owned lock scoped to one canonical installation
/// directory, so two update transactions can never run against the same
/// install. Implemented as an exclusively-opened lock file whose name is derived
/// from the install path; the handle is released (and the file becomes
/// reacquirable) when this object is disposed or the process dies — a stale path
/// never blocks forever.
/// </summary>
internal sealed class InstallationLock : IDisposable
{
    private FileStream? _handle;

    private InstallationLock(FileStream handle) => _handle = handle;

    /// <summary>
    /// Try to acquire the lock for <paramref name="installDir"/>, placing the
    /// lock file under <paramref name="lockRoot"/> (an owner-private directory —
    /// never the install dir). Returns null when another holder has it.
    /// </summary>
    public static InstallationLock? TryAcquire(string installDir, string lockRoot)
    {
        Directory.CreateDirectory(lockRoot);
        var key = DeriveKey(installDir);
        var lockPath = Path.Combine(lockRoot, $"install-{key}.lock");
        try
        {
            // FileShare.None => a second opener fails until we release.
            var handle = new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new InstallationLock(handle);
        }
        catch (IOException)
        {
            return null; // another transaction holds it
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Stable, filesystem-safe key from the canonical install path (case-folded on
    // Windows/macOS to match their path semantics).
    private static string DeriveKey(string installDir)
    {
        var full = Path.GetFullPath(installDir);
        if (!OperatingSystem.IsLinux())
        {
            full = full.ToLowerInvariant();
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return Convert.ToHexStringLower(hash)[..16];
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
