using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.GitHub;

namespace CopilotBridge.Cli.Auth;

/// <summary>
/// Low-level encrypted persistence for the single versioned credential file.
/// Only CredentialService consumes this type at runtime.
/// </summary>
internal sealed class CredentialStore
{
    private const string FileName = "github_credentials.dat";
    private const string LegacyVersionedFileName = "github_credentials.v2.dat";
    private const string LegacyRawFileName = "github_token.dat";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);

    private readonly ITokenProtector _protector;
    private readonly Action<string>? _diagnostic;
    private readonly Action<string>? _beforeCommit;
    private readonly Action<string>? _beforeMigrationVerification;
    private readonly Action<string>? _beforeLegacyDelete;

    public CredentialStore(
        string directory,
        ITokenProtector protector,
        Action<string>? diagnostic = null,
        Action<string>? beforeCommit = null,
        Action<string>? beforeMigrationVerification = null,
        Action<string>? beforeLegacyDelete = null)
    {
        FilePath = Path.Combine(directory, FileName);
        LegacyVersionedPath = Path.Combine(directory, LegacyVersionedFileName);
        LegacyRawPath = Path.Combine(directory, LegacyRawFileName);
        _protector = protector;
        _diagnostic = diagnostic;
        _beforeCommit = beforeCommit;
        _beforeMigrationVerification = beforeMigrationVerification;
        _beforeLegacyDelete = beforeLegacyDelete;
    }

    public string FilePath { get; }
    public string LegacyVersionedPath { get; }
    public string LegacyRawPath { get; }
    public string LockPath => FilePath + ".lock";
    public string LegacyLockPath => LegacyVersionedPath + ".lock";

    public CredentialFileRecord? TryLoad()
    {
        if (!File.Exists(FilePath)) return null;
        byte[] plaintext;
        try
        {
            plaintext = _protector.Unprotect(File.ReadAllBytes(FilePath));
        }
        catch (Exception ex) when (
            ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "The encrypted github_credentials.dat file cannot be read; it was left unchanged.", ex);
        }

        CredentialFileRecord record;
        try
        {
            record = JsonSerializer.Deserialize(
                    plaintext, JsonContext.Default.CredentialFileRecord)
                ?? throw new JsonException("Credential payload is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "The decrypted github_credentials.dat payload is invalid; it was left unchanged.", ex);
        }

        Validate(record);
        return record;
    }

    public CredentialFileRecord? LoadOrMigrate()
    {
        var current = TryLoad();
        if (current is not null && !LegacyFilesExist()) return current;

        using var mutationLock = AcquireLock(LockTimeout);
        using var legacyLock = AcquirePathLock(LegacyLockPath, LockTimeout);
        current = TryLoad();
        if (current is not null)
        {
            DeleteLegacyFiles();
            _diagnostic?.Invoke(
                $"credential migration cleanup outcome=success version={current.Version}");
            return current;
        }

        var source = TryReadLegacyVersioned() ?? TryReadLegacyRaw();
        if (source is null) return null;

        var wroteNew = false;
        var verifiedCommit = false;
        try
        {
            WriteCore(source);
            wroteNew = true;
            _beforeMigrationVerification?.Invoke(FilePath);
            var verified = TryLoad()
                ?? throw new InvalidOperationException("Credential migration readback returned no record.");
            if (verified != source)
                throw new InvalidOperationException("Credential migration readback did not match the source record.");
            verifiedCommit = true;

            DeleteLegacyFiles();
            _diagnostic?.Invoke($"credential migration outcome=success version={verified.Version}");
            return verified;
        }
        catch
        {
            if (wroteNew && !verifiedCommit)
            {
                try { DeleteIfExists(FilePath); }
                catch { /* preserve the original migration failure */ }
            }
            throw;
        }
    }

    public void Save(CredentialFileRecord record)
    {
        Validate(record);
        using var mutationLock = AcquireLock(LockTimeout);
        using var legacyLock = AcquirePathLock(LegacyLockPath, LockTimeout);
        SaveWhileLocked(record);
        DeleteLegacyFiles();
    }

    internal void SaveWhileLocked(CredentialFileRecord record)
    {
        Validate(record);
        WriteCore(record);
        var verified = TryLoad()
            ?? throw new InvalidOperationException("Credential commit readback returned no record.");
        if (verified != record)
            throw new InvalidOperationException("Credential commit readback did not match the written record.");
    }

    public void DeleteAll()
    {
        using var mutationLock = AcquireLock(LockTimeout);
        using var legacyLock = AcquirePathLock(LegacyLockPath, LockTimeout);
        DeleteIfExists(FilePath);
        DeleteIfExists(LegacyVersionedPath);
        DeleteIfExists(LegacyRawPath);
        // Lock files intentionally remain at stable identities.
    }

    public async ValueTask<IAsyncDisposable> AcquireLockAsync(
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new CredentialLock(OpenLock(LockPath)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (stopwatch.Elapsed >= timeout)
                    throw new TimeoutException("Timed out waiting for the credential mutation lock.", ex);
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
            }
        }
    }

    internal static bool IsCredentialArtifactName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        return IsName(fileName, FileName)
            || IsName(fileName, LegacyVersionedFileName)
            || IsName(fileName, LegacyRawFileName)
            || IsName(fileName, FileName + ".lock")
            || IsName(fileName, LegacyVersionedFileName + ".lock")
            || IsAtomicTemporaryName(fileName, FileName)
            || IsAtomicTemporaryName(fileName, LegacyVersionedFileName)
            || IsAtomicTemporaryName(fileName, LegacyRawFileName);
    }

    private CredentialFileRecord? TryReadLegacyVersioned()
    {
        if (!File.Exists(LegacyVersionedPath)) return null;
        try
        {
            var plaintext = _protector.Unprotect(File.ReadAllBytes(LegacyVersionedPath));
            var old = JsonSerializer.Deserialize(
                plaintext, JsonContext.Default.GitHubCredentialRecord);
            if (old is null
                || old.FormatVersion != GitHubCredentialRecord.CurrentFormatVersion
                || string.IsNullOrWhiteSpace(old.AccessToken))
                return null;
            return FromLegacy(old);
        }
        catch (Exception ex) when (
            ex is CryptographicException
                or JsonException
                or IOException
                or UnauthorizedAccessException)
        {
            _diagnostic?.Invoke($"legacy v2 migration source unreadable type={ex.GetType().Name}");
            return null;
        }
    }

    private CredentialFileRecord? TryReadLegacyRaw()
    {
        if (!File.Exists(LegacyRawPath)) return null;
        try
        {
            var plaintext = _protector.Unprotect(File.ReadAllBytes(LegacyRawPath));
            var token = Encoding.UTF8.GetString(plaintext);
            if (string.IsNullOrWhiteSpace(token)) return null;
            return new CredentialFileRecord
            {
                Version = CredentialFileRecord.CopilotPluginVersion,
                AccessToken = token,
                CredentialId = Guid.NewGuid().ToString("N"),
                Generation = 0,
            };
        }
        catch (Exception ex) when (
            ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            _diagnostic?.Invoke($"legacy raw migration source unreadable type={ex.GetType().Name}");
            return null;
        }
    }

    private void WriteCore(CredentialFileRecord record)
    {
        Validate(record);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            record, JsonContext.Default.CredentialFileRecord);
        var encrypted = _protector.Protect(plaintext);
        WriteBlobAtomic(FilePath, encrypted, _beforeCommit);
    }

    private IDisposable AcquireLock(TimeSpan timeout)
        => AcquirePathLock(LockPath, timeout);

    private static IDisposable AcquirePathLock(string lockPath, TimeSpan timeout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try { return new CredentialLock(OpenLock(lockPath)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (stopwatch.Elapsed >= timeout)
                    throw new TimeoutException("Timed out waiting for the credential mutation lock.", ex);
                Thread.Sleep(TimeSpan.FromMilliseconds(25));
            }
        }
    }

    private static FileStream OpenLock(string lockPath) => new(
        lockPath,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None,
        bufferSize: 1,
        FileOptions.None);

    private static CredentialFileRecord FromLegacy(GitHubCredentialRecord old) => new()
    {
        Version = CredentialFileRecord.CopilotPluginVersion,
        AccessToken = old.AccessToken,
        AccessTokenExpiresAt = old.AccessTokenExpiresAt,
        RefreshToken = old.RefreshToken,
        RefreshTokenExpiresAt = old.RefreshTokenExpiresAt,
        TokenType = old.TokenType,
        Scope = old.Scope,
        CredentialId = string.IsNullOrWhiteSpace(old.CredentialId)
            ? Guid.NewGuid().ToString("N")
            : old.CredentialId,
        Generation = old.Generation,
    };

    private static void Validate(CredentialFileRecord record)
    {
        if (record.Version is not (
                CredentialFileRecord.CopilotPluginVersion
                or CredentialFileRecord.GitHubCliOAuthVersion))
            throw new UnsupportedCredentialVersionException(record.Version);
        if (string.IsNullOrWhiteSpace(record.AccessToken))
            throw new InvalidOperationException("Credential has no access token.");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private bool LegacyFilesExist() =>
        File.Exists(LegacyVersionedPath) || File.Exists(LegacyRawPath);

    private void DeleteLegacyFiles()
    {
        _beforeLegacyDelete?.Invoke(LegacyVersionedPath);
        DeleteIfExists(LegacyVersionedPath);
        _beforeLegacyDelete?.Invoke(LegacyRawPath);
        DeleteIfExists(LegacyRawPath);
    }

    private static bool IsName(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsAtomicTemporaryName(string fileName, string persistedName) =>
        fileName.StartsWith("." + persistedName + ".", StringComparison.OrdinalIgnoreCase)
        && fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

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
            catch { /* best effort inside the credential directory */ }
        }
    }

    private sealed class CredentialLock(FileStream stream) : IDisposable, IAsyncDisposable
    {
        public void Dispose() => stream.Dispose();
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
