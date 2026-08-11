using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.GitHub;

namespace CopilotBridge.Cli.Auth;

internal enum GitHubCredentialFormat
{
    Legacy,
    Versioned,
}

internal sealed record StoredGitHubCredential(
    GitHubCredentialRecord Record,
    GitHubCredentialFormat Format,
    string AuthoritativePath);

/// <summary>
/// Encrypted GitHub credential persistence. The v2 file is authoritative and
/// contains access + refresh state; github_token.dat remains an encrypted raw
/// access-token mirror so an older bridge binary can still authenticate.
/// </summary>
internal sealed class GitHubCredentialStore
{
    private const string LegacyFileName = "github_token.dat";
    private const string VersionedFileName = "github_credentials.v2.dat";

    private readonly ITokenProtector _protector;
    private readonly Action<string>? _diagnostic;
    private readonly Action<string>? _beforeCommit;

    public GitHubCredentialStore(
        string primaryDirectory,
        string fallbackDirectory,
        ITokenProtector protector,
        Action<string>? diagnostic = null,
        Action<string>? beforeCommit = null)
    {
        LegacyPrimaryPath = Path.Combine(primaryDirectory, LegacyFileName);
        LegacyFallbackPath = Path.Combine(fallbackDirectory, LegacyFileName);
        VersionedPrimaryPath = Path.Combine(primaryDirectory, VersionedFileName);
        VersionedFallbackPath = Path.Combine(fallbackDirectory, VersionedFileName);
        _protector = protector;
        _diagnostic = diagnostic;
        _beforeCommit = beforeCommit;
    }

    public string LegacyPrimaryPath { get; }
    public string LegacyFallbackPath { get; }
    public string VersionedPrimaryPath { get; }
    public string VersionedFallbackPath { get; }

    public StoredGitHubCredential? TryLoad()
    {
        // A versioned fallback carries refresh state and must beat a stale raw
        // primary mirror. Only fall back to raw tokens when no valid v2 exists.
        return TryLoadVersioned(VersionedPrimaryPath)
            ?? TryLoadVersioned(VersionedFallbackPath)
            ?? TryLoadLegacy(LegacyPrimaryPath)
            ?? TryLoadLegacy(LegacyFallbackPath);
    }

    public bool SaveNew(GitHubCredentialRecord record) =>
        SaveVersioned(record, VersionedPrimaryPath);

    public bool SaveVersioned(GitHubCredentialRecord record, string authoritativePath)
    {
        ValidateVersionedPath(authoritativePath);
        ValidateRecord(record);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            record, JsonContext.Default.GitHubCredentialRecord);
        var encrypted = _protector.Protect(plaintext);
        WriteBlobAtomic(authoritativePath, encrypted, _beforeCommit);

        // Compatibility is deliberately secondary to the authoritative commit.
        // A current binary remains healthy even if the downgrade mirror fails.
        var mirrorPath = Path.Combine(Path.GetDirectoryName(authoritativePath)!, LegacyFileName);
        try
        {
            var mirror = _protector.Protect(Encoding.UTF8.GetBytes(record.AccessToken));
            WriteBlobAtomic(mirrorPath, mirror, _beforeCommit);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostic?.Invoke($"legacy credential mirror update failed ({ex.GetType().Name})");
            return false;
        }
    }

    public void SaveLegacy(string accessToken, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("GitHub access token cannot be empty.", nameof(accessToken));
        var encrypted = _protector.Protect(Encoding.UTF8.GetBytes(accessToken));
        WriteBlobAtomic(path ?? LegacyPrimaryPath, encrypted, _beforeCommit);
    }

    public async ValueTask<IAsyncDisposable> AcquireRotationLockAsync(
        string authoritativePath,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ValidateVersionedPath(authoritativePath);
        var lockPath = authoritativePath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
                return new RotationLock(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (stopwatch.Elapsed >= timeout)
                    throw new TimeoutException(
                        "Timed out waiting for the GitHub credential rotation lock.", ex);
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
            }
        }
    }

    public void Delete()
    {
        foreach (var path in AllCredentialPaths())
        {
            if (File.Exists(path)) File.Delete(path);
        }
        foreach (var path in new[] { VersionedPrimaryPath + ".lock", VersionedFallbackPath + ".lock" })
            TryDelete(path);
    }

    private StoredGitHubCredential? TryLoadVersioned(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var plaintext = _protector.Unprotect(File.ReadAllBytes(path));
            var record = JsonSerializer.Deserialize(
                plaintext, JsonContext.Default.GitHubCredentialRecord);
            if (record is null
                || record.FormatVersion != GitHubCredentialRecord.CurrentFormatVersion
                || string.IsNullOrWhiteSpace(record.AccessToken))
                return null;
            return new StoredGitHubCredential(record, GitHubCredentialFormat.Versioned, path);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            _diagnostic?.Invoke($"versioned credential load failed ({ex.GetType().Name})");
            return null;
        }
    }

    private StoredGitHubCredential? TryLoadLegacy(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var plaintext = _protector.Unprotect(File.ReadAllBytes(path));
            var token = Encoding.UTF8.GetString(plaintext);
            if (string.IsNullOrWhiteSpace(token)) return null;
            return new StoredGitHubCredential(
                GitHubCredentialRecord.FromLegacyToken(token),
                GitHubCredentialFormat.Legacy,
                path);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            _diagnostic?.Invoke($"legacy credential load failed ({ex.GetType().Name})");
            return null;
        }
    }

    internal static void WriteBlobAtomic(
        string path,
        byte[] blob,
        Action<string>? beforeCommit = null)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Credential path has no parent directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (var stream = new FileStream(tempPath, options))
            {
                stream.Write(blob);
                stream.Flush(flushToDisk: true);
            }

            beforeCommit?.Invoke(path);
            File.Move(tempPath, path, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* best effort; never touch paths outside the credential directory */ }
        }
    }

    private IEnumerable<string> AllCredentialPaths()
    {
        yield return VersionedPrimaryPath;
        yield return VersionedFallbackPath;
        yield return LegacyPrimaryPath;
        yield return LegacyFallbackPath;
    }

    private void ValidateVersionedPath(string path)
    {
        if (!string.Equals(path, VersionedPrimaryPath, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(path, VersionedFallbackPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(path), "Credential path is not a configured v2 location.");
    }

    private static void ValidateRecord(GitHubCredentialRecord record)
    {
        if (record.FormatVersion != GitHubCredentialRecord.CurrentFormatVersion)
            throw new InvalidOperationException($"Unsupported GitHub credential format {record.FormatVersion}.");
        if (string.IsNullOrWhiteSpace(record.AccessToken))
            throw new InvalidOperationException("GitHub credential has no access token.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class RotationLock(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
