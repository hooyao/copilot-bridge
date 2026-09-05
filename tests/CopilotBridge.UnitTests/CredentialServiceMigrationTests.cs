using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract: credential persistence has one versioned authority beside the executable.
/// Both historical files migrate transactionally and are deleted only after verified
/// readback; persistence and migration details are owned below CredentialService.
/// </summary>
public sealed class CredentialServiceMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"copilot-bridge-unified-credential-{Guid.NewGuid():N}");
    private readonly TestProtector _protector = new();

    [Fact]
    public void Versioned_file_round_trips_both_supported_versions()
    {
        var store = CreateStore("roundtrip");
        var legacy = LegacyRecord(generation: 7);
        store.Save(legacy);

        Assert.Equal(legacy, store.TryLoad());
        Assert.Equal("github_credentials.dat", Path.GetFileName(store.FilePath));
        Assert.False(File.Exists(store.LegacyVersionedPath));
        Assert.False(File.Exists(store.LegacyRawPath));

        var direct = new CredentialFileRecord
        {
            Version = CredentialFileRecord.GitHubCliOAuthVersion,
            AccessToken = "gho_direct_secret",
            TokenType = "bearer",
            Scope = "repo,read:org,gist",
            CredentialId = "direct-id",
            Generation = 1,
        };
        store.Save(direct);

        Assert.Equal(direct, store.TryLoad());
    }

    [Fact]
    public void Empty_lookup_does_not_create_historical_v2_lock()
    {
        var store = CreateStore("empty-lookup");

        var loaded = store.LoadOrMigrate();

        Assert.Null(loaded);
        Assert.False(File.Exists(store.LegacyLockPath));
    }

    [Fact]
    public void First_unified_save_does_not_create_historical_v2_lock()
    {
        var store = CreateStore("fresh-save");

        store.Save(LegacyRecord(generation: 1));

        Assert.NotNull(store.TryLoad());
        Assert.True(File.Exists(store.LockPath));
        Assert.False(File.Exists(store.LegacyLockPath));
    }

    [Fact]
    public void Complete_v2_record_wins_and_both_old_files_are_deleted_after_readback()
    {
        var directory = Path.Combine(_root, "v2-migration");
        var source = new GitHubCredentialRecord
        {
            AccessToken = "ghu_complete_access",
            AccessTokenExpiresAt = DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            RefreshToken = "ghr_complete_refresh",
            RefreshTokenExpiresAt = DateTimeOffset.Parse("2027-01-01T00:00:00Z"),
            TokenType = "bearer",
            Scope = "read:user",
            CredentialId = "legacy-complete-id",
            Generation = 9,
        };
        WriteLegacyVersioned(directory, source);
        WriteLegacyRaw(directory, source.AccessToken);
        var diagnostics = new List<string>();
        var store = new CredentialStore(directory, _protector, diagnostics.Add);

        var migrated = store.LoadOrMigrate();

        Assert.NotNull(migrated);
        Assert.Equal(CredentialFileRecord.CopilotPluginVersion, migrated.Version);
        Assert.Equal(source.AccessToken, migrated.AccessToken);
        Assert.Equal(source.AccessTokenExpiresAt, migrated.AccessTokenExpiresAt);
        Assert.Equal(source.RefreshToken, migrated.RefreshToken);
        Assert.Equal(source.RefreshTokenExpiresAt, migrated.RefreshTokenExpiresAt);
        Assert.Equal(source.TokenType, migrated.TokenType);
        Assert.Equal(source.Scope, migrated.Scope);
        Assert.Equal(source.CredentialId, migrated.CredentialId);
        Assert.Equal(source.Generation, migrated.Generation);
        Assert.True(File.Exists(store.FilePath));
        Assert.False(File.Exists(store.LegacyVersionedPath));
        Assert.False(File.Exists(store.LegacyRawPath));
        Assert.True(File.Exists(store.LegacyLockPath));
        Assert.Contains(
            diagnostics,
            message => message.Contains("source=legacy_v2", StringComparison.Ordinal));
    }

    [Fact]
    public void Raw_token_migrates_when_v2_is_unreadable_and_old_files_are_deleted()
    {
        var directory = Path.Combine(_root, "raw-migration");
        WriteLegacyRaw(directory, "ghu_raw_fallback");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "github_credentials.v2.dat"), [0x01, 0x02, 0x03]);
        var diagnostics = new List<string>();
        var store = new CredentialStore(directory, _protector, diagnostics.Add);

        var migrated = store.LoadOrMigrate();

        Assert.NotNull(migrated);
        Assert.Equal(CredentialFileRecord.CopilotPluginVersion, migrated.Version);
        Assert.Equal("ghu_raw_fallback", migrated.AccessToken);
        Assert.Null(migrated.RefreshToken);
        Assert.Null(migrated.AccessTokenExpiresAt);
        Assert.False(File.Exists(store.LegacyVersionedPath));
        Assert.False(File.Exists(store.LegacyRawPath));
        Assert.Contains(
            diagnostics,
            message => message.Contains("source=legacy_raw", StringComparison.Ordinal));
    }

    [Fact]
    public void Raw_migration_never_infers_semantics_from_the_token_prefix()
    {
        var directory = Path.Combine(_root, "raw-prefix-is-opaque");
        WriteLegacyRaw(directory, "gho_opaque_legacy_bytes");
        var store = new CredentialStore(directory, _protector);

        var migrated = store.LoadOrMigrate();

        Assert.NotNull(migrated);
        Assert.Equal(CredentialFileRecord.CopilotPluginVersion, migrated.Version);
        Assert.Equal("gho_opaque_legacy_bytes", migrated.AccessToken);
    }

    [Fact]
    public void Failed_post_commit_verification_keeps_both_old_files()
    {
        var directory = Path.Combine(_root, "failed-verification");
        var legacy = new GitHubCredentialRecord
        {
            AccessToken = "ghu_keep_old_access",
            RefreshToken = "ghr_keep_old_refresh",
            CredentialId = "keep-old-id",
            Generation = 3,
        };
        WriteLegacyVersioned(directory, legacy);
        WriteLegacyRaw(directory, legacy.AccessToken);
        var store = new CredentialStore(
            directory,
            _protector,
            beforeMigrationVerification: path => File.WriteAllBytes(path, [0x00]));

        Assert.ThrowsAny<Exception>(() => store.LoadOrMigrate());

        Assert.True(File.Exists(store.LegacyVersionedPath));
        Assert.True(File.Exists(store.LegacyRawPath));
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void Later_load_retries_cleanup_after_only_one_legacy_file_was_deleted()
    {
        var directory = Path.Combine(_root, "partial-cleanup");
        var legacy = new GitHubCredentialRecord
        {
            AccessToken = "ghu_partial_cleanup",
            CredentialId = "partial-cleanup-id",
            Generation = 4,
        };
        WriteLegacyVersioned(directory, legacy);
        WriteLegacyRaw(directory, legacy.AccessToken);
        var failRawDelete = true;
        var interrupted = new CredentialStore(
            directory,
            _protector,
            beforeLegacyDelete: path =>
            {
                if (failRawDelete && Path.GetFileName(path) == "github_token.dat")
                {
                    failRawDelete = false;
                    throw new IOException("Injected legacy cleanup interruption.");
                }
            });

        Assert.Throws<IOException>(() => interrupted.LoadOrMigrate());
        Assert.True(File.Exists(interrupted.FilePath));
        Assert.False(File.Exists(interrupted.LegacyVersionedPath));
        Assert.True(File.Exists(interrupted.LegacyRawPath));

        var recovered = new CredentialStore(directory, _protector).LoadOrMigrate();

        Assert.NotNull(recovered);
        Assert.Equal(legacy.AccessToken, recovered.AccessToken);
        Assert.False(File.Exists(interrupted.LegacyVersionedPath));
        Assert.False(File.Exists(interrupted.LegacyRawPath));
    }

    [Fact]
    public void Unknown_new_file_version_fails_closed_without_touching_legacy_files()
    {
        var directory = Path.Combine(_root, "unknown-version");
        WriteLegacyRaw(directory, "ghu_preserved_legacy");
        var store = new CredentialStore(directory, _protector);
        var unknown = LegacyRecord(generation: 1) with { Version = 999 };
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            unknown, JsonContext.Default.CredentialFileRecord);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(store.FilePath, _protector.Protect(plaintext));

        Assert.Throws<UnsupportedCredentialVersionException>(() => store.LoadOrMigrate());

        Assert.True(File.Exists(store.FilePath));
        Assert.True(File.Exists(store.LegacyRawPath));
    }

    [Fact]
    public async Task Concurrent_migration_converges_on_one_verified_record()
    {
        var directory = Path.Combine(_root, "concurrent-migration");
        WriteLegacyRaw(directory, "ghu_concurrent");
        var first = new CredentialStore(directory, _protector);
        var second = new CredentialStore(directory, _protector);

        var results = await Task.WhenAll(
            Task.Run(first.LoadOrMigrate),
            Task.Run(second.LoadOrMigrate));

        Assert.NotNull(results[0]);
        Assert.Equal(results[0], results[1]);
        Assert.Equal(results[0], first.TryLoad());
        Assert.False(File.Exists(first.LegacyVersionedPath));
        Assert.False(File.Exists(first.LegacyRawPath));
    }

    [Fact]
    public async Task Migration_waits_for_inflight_legacy_writer_before_deleting_old_files()
    {
        var directory = Path.Combine(_root, "legacy-lock-migration");
        WriteLegacyRaw(directory, "ghu_legacy_lock");
        var store = new CredentialStore(directory, _protector);
        Directory.CreateDirectory(directory);
        var legacyLock = new FileStream(
            store.LegacyLockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        try
        {
            var migration = Task.Run(store.LoadOrMigrate);
            await Task.Delay(100);
            Assert.False(migration.IsCompleted);

            legacyLock.Dispose();
            var migrated = await migration;

            Assert.NotNull(migrated);
            Assert.True(File.Exists(store.FilePath));
            Assert.False(File.Exists(store.LegacyRawPath));
        }
        finally
        {
            legacyLock.Dispose();
        }
    }

    [Fact]
    public async Task Refresh_does_not_reenter_migration_when_legacy_file_reappears_while_locked()
    {
        var directory = Path.Combine(_root, "refresh-with-recreated-legacy");
        var store = new CredentialStore(directory, _protector);
        var original = LegacyRecord(generation: 7);
        store.Save(original);
        var handler = new RefreshHandler();
        var factory = new SingleClientHttpClientFactory(new HttpClient(handler));
        using var service = new CredentialService(
            factory,
            store,
            // This contract covers one forced post-rejection rotation, not expiry-
            // driven refresh. Pin the clock before the fixture's access-token expiry
            // so the test remains deterministic after 2026-09-01.
            new ManualTimeProvider(DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            NullLogger<CredentialService>.Instance);
        var rejected = await service.GetUsableAsync(CancellationToken.None);
        IAsyncDisposable? heldLock = await store.AcquireLockAsync(
            TimeSpan.FromSeconds(1), CancellationToken.None);
        try
        {
            var recovery = service.RecoverAfterRejectionAsync(
                rejected, CancellationToken.None).AsTask();
            WriteLegacyRaw(directory, original.AccessToken);
            await heldLock.DisposeAsync();
            heldLock = null;

            var refreshed = await recovery;

            Assert.Equal(8, refreshed.Generation);
            Assert.Equal(1, handler.RequestCount);
            Assert.True(File.Exists(store.LegacyRawPath));
            Assert.NotNull(service.GetStatus());
            Assert.False(File.Exists(store.LegacyRawPath));
        }
        finally
        {
            if (heldLock is not null) await heldLock.DisposeAsync();
        }
    }

    [Fact]
    public void DeleteAll_removes_new_and_both_legacy_files_but_keeps_lock()
    {
        var directory = Path.Combine(_root, "delete-all");
        var store = new CredentialStore(directory, _protector);
        store.Save(LegacyRecord(generation: 1));
        WriteLegacyVersioned(directory, new GitHubCredentialRecord
        {
            AccessToken = "ghu_old",
            CredentialId = "old",
            Generation = 1,
        });
        WriteLegacyRaw(directory, "ghu_old");

        store.DeleteAll();

        Assert.False(File.Exists(store.FilePath));
        Assert.False(File.Exists(store.LegacyVersionedPath));
        Assert.False(File.Exists(store.LegacyRawPath));
        Assert.True(File.Exists(store.LockPath));
    }

    [Fact]
    public void SignOut_deletes_every_credential_file_without_loading_unreadable_bytes()
    {
        var directory = Path.Combine(_root, "unreadable-signout");
        Directory.CreateDirectory(directory);
        var store = new CredentialStore(directory, _protector);
        File.WriteAllBytes(store.FilePath, [0x01]);
        File.WriteAllBytes(store.LegacyVersionedPath, [0x02]);
        File.WriteAllBytes(store.LegacyRawPath, [0x03]);
        using var service = new CredentialService(
            new SingleClientHttpClientFactory(new HttpClient()),
            store,
            TimeProvider.System,
            NullLogger<CredentialService>.Instance);

        service.SignOut();

        Assert.False(File.Exists(store.FilePath));
        Assert.False(File.Exists(store.LegacyVersionedPath));
        Assert.False(File.Exists(store.LegacyRawPath));
    }

    [Fact]
    public void Logout_command_succeeds_without_parsing_unreadable_credential_bytes()
    {
        var directory = Path.Combine(_root, "unreadable-command-logout");
        Directory.CreateDirectory(directory);
        var store = new CredentialStore(directory, _protector);
        File.WriteAllBytes(store.FilePath, [0x01]);
        File.WriteAllBytes(store.LegacyVersionedPath, [0x02]);
        File.WriteAllBytes(store.LegacyRawPath, [0x03]);
        var factory = new SingleClientHttpClientFactory(new HttpClient());
        var credentials = new CredentialService(
            factory,
            store,
            TimeProvider.System,
            NullLogger<CredentialService>.Instance);
        using var auth = new AuthService(
            factory,
            credentials,
            TimeProvider.System,
            NullLoggerFactory.Instance,
            enableBackgroundRefresh: false,
            ownsCredentialService: true);
        using var output = new StringWriter();

        var exitCode = AuthCommand.Logout(auth, output);

        Assert.Equal(0, exitCode);
        Assert.Contains(store.FilePath, output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(store.FilePath));
        Assert.False(File.Exists(store.LegacyVersionedPath));
        Assert.False(File.Exists(store.LegacyRawPath));
    }

    [Theory]
    [InlineData("github_credentials.dat")]
    [InlineData("github_credentials.dat.lock")]
    [InlineData("github_credentials.v2.dat")]
    [InlineData("github_credentials.v2.dat.lock")]
    [InlineData("github_token.dat")]
    [InlineData(".github_credentials.dat.0123456789abcdef0123456789abcdef.tmp")]
    public void Artifact_classifier_covers_current_and_migration_files(string fileName) =>
        Assert.True(CredentialStore.IsCredentialArtifactName(fileName));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private CredentialStore CreateStore(string name) =>
        new(Path.Combine(_root, name), _protector);

    private void WriteLegacyVersioned(string directory, GitHubCredentialRecord record)
    {
        Directory.CreateDirectory(directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            record, JsonContext.Default.GitHubCredentialRecord);
        File.WriteAllBytes(
            Path.Combine(directory, "github_credentials.v2.dat"),
            _protector.Protect(plaintext));
    }

    private void WriteLegacyRaw(string directory, string token)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(
            Path.Combine(directory, "github_token.dat"),
            _protector.Protect(Encoding.UTF8.GetBytes(token)));
    }

    private static CredentialFileRecord LegacyRecord(long generation) => new()
    {
        Version = CredentialFileRecord.CopilotPluginVersion,
        AccessToken = "ghu_legacy_secret",
        AccessTokenExpiresAt = DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
        RefreshToken = "ghr_legacy_secret",
        RefreshTokenExpiresAt = DateTimeOffset.Parse("2027-01-01T00:00:00Z"),
        TokenType = "bearer",
        Scope = "read:user",
        CredentialId = "legacy-id",
        Generation = generation,
    };

    private sealed class TestProtector : ITokenProtector
    {
        public byte[] Protect(byte[] plaintext) =>
            [0xE1, .. plaintext.Select(value => (byte)(value ^ 0x51))];

        public byte[] Unprotect(byte[] blob)
        {
            if (blob.Length == 0 || blob[0] != 0xE1)
                throw new CryptographicException();
            return blob[1..].Select(value => (byte)(value ^ 0x51)).ToArray();
        }
    }

    private sealed class RefreshHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"access_token\":\"ghu_refreshed\",\"refresh_token\":\"ghr_rotated\","
                    + "\"expires_in\":28800,\"refresh_token_expires_in\":15552000,"
                    + "\"token_type\":\"bearer\",\"scope\":\"read:user\"}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
