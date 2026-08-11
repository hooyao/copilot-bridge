using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.GitHub;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract: the device-flow credential is a refreshable encrypted record, while
/// existing raw-token blobs remain readable. These assertions come from the
/// auth-token-lifecycle spec, not from TokenStore's current raw-string shape.
/// </summary>
public sealed class GitHubCredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"copilot-bridge-auth-contract-{Guid.NewGuid():N}");

    [Fact]
    public void Device_response_preserves_refresh_fields_through_source_generated_json()
    {
        const string json = """
            {
              "access_token":"ghu_access",
              "expires_in":3600,
              "refresh_token":"ghr_refresh",
              "refresh_token_expires_in":15552000,
              "token_type":"bearer",
              "scope":"read:user"
            }
            """;

        var response = JsonSerializer.Deserialize(json, JsonContext.Default.AccessTokenResponse);

        Assert.NotNull(response);
        Assert.Equal("ghu_access", response.AccessToken);
        Assert.Equal(3600, response.ExpiresIn);
        Assert.Equal("ghr_refresh", response.RefreshToken);
        Assert.Equal(15552000, response.RefreshTokenExpiresIn);
        Assert.Equal("bearer", response.TokenType);
        Assert.Equal("read:user", response.Scope);
    }

    [Fact]
    public void Refreshable_response_derives_both_deadlines_from_receipt_time()
    {
        var receivedAt = new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        var response = new AccessTokenResponse
        {
            AccessToken = "ghu_access",
            ExpiresIn = 3600,
            RefreshToken = "ghr_refresh",
            RefreshTokenExpiresIn = 7200,
            TokenType = "bearer",
            Scope = "read:user",
        };

        var record = GitHubCredentialRecord.FromOAuthResponse(response, receivedAt, generation: 7);

        Assert.Equal(GitHubCredentialRecord.CurrentFormatVersion, record.FormatVersion);
        Assert.Equal(receivedAt.AddHours(1), record.AccessTokenExpiresAt);
        Assert.Equal(receivedAt.AddHours(2), record.RefreshTokenExpiresAt);
        Assert.Equal(7, record.Generation);
    }

    [Fact]
    public void Non_expiring_response_does_not_invent_deadlines_or_refresh_state()
    {
        var response = new AccessTokenResponse
        {
            AccessToken = "ghu_non_expiring",
            TokenType = "bearer",
            Scope = "read:user",
        };

        var record = GitHubCredentialRecord.FromOAuthResponse(
            response,
            DateTimeOffset.UnixEpoch,
            generation: 1);

        Assert.Null(record.AccessTokenExpiresAt);
        Assert.Null(record.RefreshToken);
        Assert.Null(record.RefreshTokenExpiresAt);
        Assert.False(record.IsRefreshable);
    }

    [Fact]
    public void Fresh_logins_mint_distinct_persisted_identity_and_refresh_preserves_it()
    {
        // Contract: a generation is only monotonic within one credential instance.
        // Fresh logins both start at generation 1, so their persisted identities
        // must differ while rotation of one login preserves its identity.
        var receivedAt = new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        var response = new AccessTokenResponse { AccessToken = "ghu_login" };
        var first = GitHubCredentialRecord.FromOAuthResponse(response, receivedAt, 1);
        var second = GitHubCredentialRecord.FromOAuthResponse(response, receivedAt, 1);
        var rotated = GitHubCredentialRecord.FromOAuthResponse(
            new AccessTokenResponse
            {
                AccessToken = "ghu_rotated",
                RefreshToken = "ghr_rotated",
            },
            receivedAt.AddMinutes(10),
            2,
            first);

        var firstId = SerializedCredentialId(first);
        var secondId = SerializedCredentialId(second);
        var rotatedId = SerializedCredentialId(rotated);

        Assert.False(string.IsNullOrWhiteSpace(firstId));
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(firstId, rotatedId);
    }

    [Fact]
    public void Refresh_response_without_replacement_does_not_reuse_spent_refresh_token()
    {
        // Contract: GitHub refresh tokens rotate. Once the old token is used it
        // cannot be retained as fallback; an anomalous response with a new access
        // token but no replacement refresh token must downgrade safely.
        var receivedAt = new DateTimeOffset(2026, 8, 11, 1, 0, 0, TimeSpan.Zero);
        var previous = new GitHubCredentialRecord
        {
            AccessToken = "ghu_old",
            AccessTokenExpiresAt = receivedAt,
            RefreshToken = "ghr_spent",
            RefreshTokenExpiresAt = receivedAt.AddDays(30),
            Generation = 4,
        };
        var response = new AccessTokenResponse
        {
            AccessToken = "ghu_new",
            ExpiresIn = 28800,
            TokenType = "bearer",
        };

        var record = GitHubCredentialRecord.FromOAuthResponse(
            response,
            receivedAt,
            generation: 5,
            previous);

        Assert.Equal("ghu_new", record.AccessToken);
        Assert.Equal(receivedAt.AddHours(8), record.AccessTokenExpiresAt);
        Assert.Null(record.RefreshToken);
        Assert.Null(record.RefreshTokenExpiresAt);
        Assert.False(record.IsRefreshable);
        Assert.Equal(5, record.Generation);
    }

    [Fact]
    public void Versioned_record_round_trips_encrypted_and_keeps_raw_mirror_compatible()
    {
        var (store, protector) = CreateStore();
        var record = SampleRecord(generation: 3);

        store.SaveNew(record);
        var loaded = store.TryLoad();

        Assert.NotNull(loaded);
        Assert.Equal(GitHubCredentialFormat.Versioned, loaded.Format);
        Assert.Equal(record, loaded.Record);
        Assert.Equal(store.VersionedPrimaryPath, loaded.AuthoritativePath);

        var versionedBytes = File.ReadAllBytes(store.VersionedPrimaryPath);
        var mirrorBytes = File.ReadAllBytes(store.LegacyPrimaryPath);
        Assert.DoesNotContain("ghu_secret_access", Encoding.UTF8.GetString(versionedBytes));
        Assert.DoesNotContain("ghr_secret_refresh", Encoding.UTF8.GetString(versionedBytes));
        Assert.DoesNotContain("ghu_secret_access", Encoding.UTF8.GetString(mirrorBytes));
        Assert.Equal("ghu_secret_access", protector.ReadPlaintext(mirrorBytes));
    }

    [Fact]
    public void Legacy_raw_token_loads_without_rewrite()
    {
        var (store, protector) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.LegacyFallbackPath)!);
        File.WriteAllBytes(store.LegacyFallbackPath, protector.ProtectText("ghu_legacy"));

        var loaded = store.TryLoad();

        Assert.NotNull(loaded);
        Assert.Equal(GitHubCredentialFormat.Legacy, loaded.Format);
        Assert.Equal("ghu_legacy", loaded.Record.AccessToken);
        Assert.Null(loaded.Record.RefreshToken);
        Assert.Equal(store.LegacyFallbackPath, loaded.AuthoritativePath);
        Assert.False(File.Exists(store.VersionedPrimaryPath));
        Assert.False(File.Exists(store.VersionedFallbackPath));
    }

    [Fact]
    public void Valid_versioned_fallback_beats_stale_legacy_primary()
    {
        var (store, protector) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.LegacyPrimaryPath)!);
        File.WriteAllBytes(store.LegacyPrimaryPath, protector.ProtectText("ghu_stale"));
        store.SaveVersioned(SampleRecord(generation: 9), store.VersionedFallbackPath);

        var loaded = store.TryLoad();

        Assert.NotNull(loaded);
        Assert.Equal(GitHubCredentialFormat.Versioned, loaded.Format);
        Assert.Equal(9, loaded.Record.Generation);
        Assert.Equal(store.VersionedFallbackPath, loaded.AuthoritativePath);
    }

    [Fact]
    public void Corrupt_versioned_record_falls_back_to_valid_legacy_token()
    {
        var (store, protector) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.VersionedPrimaryPath)!);
        File.WriteAllBytes(store.VersionedPrimaryPath, [0x01, 0x02, 0x03]);
        File.WriteAllBytes(store.LegacyPrimaryPath, protector.ProtectText("ghu_legacy"));

        var loaded = store.TryLoad();

        Assert.NotNull(loaded);
        Assert.Equal(GitHubCredentialFormat.Legacy, loaded.Format);
        Assert.Equal("ghu_legacy", loaded.Record.AccessToken);
    }

    [Fact]
    public void Delete_removes_every_credential_representation()
    {
        var (store, _) = CreateStore();
        store.SaveNew(SampleRecord(generation: 1));
        store.SaveVersioned(SampleRecord(generation: 2), store.VersionedFallbackPath);

        store.Delete();

        Assert.False(File.Exists(store.VersionedPrimaryPath));
        Assert.False(File.Exists(store.VersionedFallbackPath));
        Assert.False(File.Exists(store.LegacyPrimaryPath));
        Assert.False(File.Exists(store.LegacyFallbackPath));
    }

    [Fact]
    public async Task Path_scoped_lock_serializes_two_store_instances()
    {
        var (firstStore, protector) = CreateStore();
        var secondStore = new GitHubCredentialStore(
            Path.GetDirectoryName(firstStore.VersionedPrimaryPath)!,
            Path.GetDirectoryName(firstStore.VersionedFallbackPath)!,
            protector);

        var firstLock = await firstStore.AcquireRotationLockAsync(
            firstStore.VersionedPrimaryPath,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        var secondTask = secondStore.AcquireRotationLockAsync(
            secondStore.VersionedPrimaryPath,
            TimeSpan.FromSeconds(2),
            CancellationToken.None).AsTask();

        await Task.Delay(100);
        Assert.False(secondTask.IsCompleted);

        await firstLock.DisposeAsync();
        await using var secondLock = await secondTask;
        Assert.NotNull(secondLock);
    }

    [Fact]
    public async Task Fresh_login_waits_for_old_refresh_and_commits_last()
    {
        var (refreshStore, protector) = CreateStore();
        var loginStore = new GitHubCredentialStore(
            Path.GetDirectoryName(refreshStore.VersionedPrimaryPath)!,
            Path.GetDirectoryName(refreshStore.VersionedFallbackPath)!,
            protector);
        refreshStore.SaveNew(SampleRecord(generation: 1) with
        {
            CredentialId = "old-login",
        });
        var freshLogin = SampleRecord(generation: 1) with
        {
            AccessToken = "ghu_fresh_login",
            CredentialId = "fresh-login",
        };

        var refreshLock = await refreshStore.AcquireRotationLockAsync(
            refreshStore.VersionedPrimaryPath,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        try
        {
            using var started = new ManualResetEventSlim();
            var loginTask = Task.Run(() =>
            {
                started.Set();
                return loginStore.SaveNew(freshLogin);
            });
            Assert.True(started.Wait(TimeSpan.FromSeconds(1)));
            await Task.Delay(100);
            Assert.False(loginTask.IsCompleted);

            refreshStore.SaveVersioned(
                SampleRecord(generation: 2) with { CredentialId = "old-login" },
                refreshStore.VersionedPrimaryPath);
            await refreshLock.DisposeAsync();

            Assert.True(await loginTask);
        }
        finally
        {
            await refreshLock.DisposeAsync();
        }

        var committed = refreshStore.TryLoad()!.Record;
        Assert.Equal("fresh-login", committed.CredentialId);
        Assert.Equal("ghu_fresh_login", committed.AccessToken);
        Assert.Equal(1, committed.Generation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_waits_for_each_rotation_path_and_cannot_be_undone(
        bool useFallback)
    {
        var (refreshStore, protector) = CreateStore();
        var logoutStore = new GitHubCredentialStore(
            Path.GetDirectoryName(refreshStore.VersionedPrimaryPath)!,
            Path.GetDirectoryName(refreshStore.VersionedFallbackPath)!,
            protector);
        var path = useFallback
            ? refreshStore.VersionedFallbackPath
            : refreshStore.VersionedPrimaryPath;
        refreshStore.SaveVersioned(
            SampleRecord(generation: 1) with { CredentialId = "old-login" },
            path);

        var refreshLock = await refreshStore.AcquireRotationLockAsync(
            path,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        try
        {
            using var started = new ManualResetEventSlim();
            var logoutTask = Task.Run(() =>
            {
                started.Set();
                logoutStore.Delete();
            });
            Assert.True(started.Wait(TimeSpan.FromSeconds(1)));
            await Task.Delay(100);
            Assert.False(logoutTask.IsCompleted);

            refreshStore.SaveVersioned(
                SampleRecord(generation: 2) with { CredentialId = "old-login" },
                path);
            await refreshLock.DisposeAsync();
            await logoutTask;
        }
        finally
        {
            await refreshLock.DisposeAsync();
        }

        Assert.Null(refreshStore.TryLoad());
        Assert.False(File.Exists(refreshStore.VersionedPrimaryPath));
        Assert.False(File.Exists(refreshStore.VersionedFallbackPath));
        Assert.False(File.Exists(refreshStore.LegacyPrimaryPath));
        Assert.False(File.Exists(refreshStore.LegacyFallbackPath));
    }

    [Fact]
    public void Failed_precommit_keeps_previous_generation_and_cleans_temp_file()
    {
        var (healthyStore, protector) = CreateStore();
        healthyStore.SaveNew(SampleRecord(generation: 1));
        var failingStore = new GitHubCredentialStore(
            Path.GetDirectoryName(healthyStore.VersionedPrimaryPath)!,
            Path.GetDirectoryName(healthyStore.VersionedFallbackPath)!,
            protector,
            beforeCommit: path =>
            {
                if (path.EndsWith("github_credentials.v2.dat", StringComparison.Ordinal))
                    throw new IOException("injected before replace");
            });

        Assert.Throws<IOException>(() =>
            failingStore.SaveVersioned(SampleRecord(generation: 2), failingStore.VersionedPrimaryPath));

        var loaded = healthyStore.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Record.Generation);
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(healthyStore.VersionedPrimaryPath)!, "*.tmp"));
    }

    [Fact]
    public void Mirror_failure_keeps_authoritative_v2_and_reports_degraded_compatibility()
    {
        var (healthyStore, protector) = CreateStore();
        var diagnostics = new List<string>();
        var store = new GitHubCredentialStore(
            Path.GetDirectoryName(healthyStore.VersionedPrimaryPath)!,
            Path.GetDirectoryName(healthyStore.VersionedFallbackPath)!,
            protector,
            diagnostics.Add,
            path =>
            {
                if (path.EndsWith("github_token.dat", StringComparison.Ordinal))
                    throw new IOException("injected mirror failure");
            });

        var mirrorSaved = store.SaveNew(SampleRecord(generation: 12));

        Assert.False(mirrorSaved);
        Assert.Equal(12, store.TryLoad()!.Record.Generation);
        Assert.False(File.Exists(store.LegacyPrimaryPath));
        Assert.Single(diagnostics);
        Assert.DoesNotContain("ghu_secret_access", diagnostics[0], StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (GitHubCredentialStore Store, TestProtector Protector) CreateStore()
    {
        var protector = new TestProtector();
        return (new GitHubCredentialStore(
            primaryDirectory: Path.Combine(_root, "primary"),
            fallbackDirectory: Path.Combine(_root, "fallback"),
            protector), protector);
    }

    private static GitHubCredentialRecord SampleRecord(long generation) => new()
    {
        FormatVersion = GitHubCredentialRecord.CurrentFormatVersion,
        AccessToken = "ghu_secret_access",
        AccessTokenExpiresAt = new DateTimeOffset(2026, 8, 11, 1, 0, 0, TimeSpan.Zero),
        RefreshToken = "ghr_secret_refresh",
        RefreshTokenExpiresAt = new DateTimeOffset(2027, 2, 11, 0, 0, 0, TimeSpan.Zero),
        TokenType = "bearer",
        Scope = "read:user",
        Generation = generation,
    };

    private static string? SerializedCredentialId(GitHubCredentialRecord record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            record,
            JsonContext.Default.GitHubCredentialRecord);
        using var json = JsonDocument.Parse(bytes);
        return json.RootElement.TryGetProperty("credential_id", out var property)
            ? property.GetString()
            : null;
    }

    private sealed class TestProtector : ITokenProtector
    {
        private static readonly byte[] Prefix = "encrypted:"u8.ToArray();

        public byte[] Protect(byte[] plaintext)
        {
            var output = new byte[Prefix.Length + plaintext.Length];
            Prefix.CopyTo(output, 0);
            for (var i = 0; i < plaintext.Length; i++)
                output[Prefix.Length + i] = (byte)(plaintext[plaintext.Length - 1 - i] ^ 0xA5);
            return output;
        }

        public byte[] Unprotect(byte[] blob)
        {
            if (!blob.AsSpan(0, Math.Min(blob.Length, Prefix.Length)).SequenceEqual(Prefix)
                || blob.Length < Prefix.Length)
                throw new System.Security.Cryptography.CryptographicException("invalid test envelope");

            var output = new byte[blob.Length - Prefix.Length];
            for (var i = 0; i < output.Length; i++)
                output[i] = (byte)(blob[blob.Length - 1 - i] ^ 0xA5);
            return output;
        }

        public byte[] ProtectText(string value) => Protect(Encoding.UTF8.GetBytes(value));
        public string ReadPlaintext(byte[] blob) => Encoding.UTF8.GetString(Unprotect(blob));
    }
}
