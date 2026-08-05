using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Codex;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

public sealed class CodexCatalogDiskStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cb-codex-catalog-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PromotedRecordSurvivesAStoreRestartAndPreservesExactBytes()
    {
        var entry = Entry("0.147.0", Catalog("one"), DateTimeOffset.Parse("2026-08-01T00:00:00Z"));
        using (var writer = Store()) await writer.PromoteAsync(entry);

        using var reader = Store();
        var loaded = await reader.TryLoadAsync(entry.Version);

        Assert.NotNull(loaded);
        Assert.Equal(entry.SourceBytes, loaded.SourceBytes);
        Assert.Equal(entry.Metadata, loaded.Metadata);
        Assert.Equal("one", Assert.Single(loaded.Baseline.Models).GetProperty("slug").GetString());
    }

    [Theory]
    [InlineData("truncated-header")]
    [InlineData("truncated-body")]
    [InlineData("negative-metadata")]
    [InlineData("oversized-metadata")]
    [InlineData("oversized-source")]
    [InlineData("digest-mismatch")]
    public async Task CorruptOrMaliciousRecordIsNeverLoaded(string mutation)
    {
        var entry = Entry("0.147.0", Catalog("one"), DateTimeOffset.Parse("2026-08-01T00:00:00Z"));
        using var store = Store();
        await store.PromoteAsync(entry);
        var path = CodexCatalogCachePaths.GetRecordPath(_root, entry.Version);
        var bytes = await File.ReadAllBytesAsync(path);

        bytes = mutation switch
        {
            "truncated-header" => bytes[..7],
            "truncated-body" => bytes[..^1],
            "negative-metadata" => HeaderMutation(bytes, metadataLength: -1),
            "oversized-metadata" => HeaderMutation(bytes, metadataLength: 65_537),
            "oversized-source" => HeaderMutation(bytes, sourceLength: 16_777_217),
            "digest-mismatch" => BodyMutation(bytes),
            _ => throw new InvalidOperationException(),
        };
        await File.WriteAllBytesAsync(path, bytes);

        Assert.Null(await store.TryLoadAsync(entry.Version));
    }

    [Fact]
    public async Task CandidateIdentityAndSchemaAreRevalidatedBeforeAnyMutation()
    {
        var entry = Entry("0.147.0", Catalog("one"), DateTimeOffset.Parse("2026-08-01T00:00:00Z"));
        var mutationEntries = new[]
        {
            entry with { Metadata = entry.Metadata with { ClientVersion = "0.148.0" } },
            entry with { Metadata = entry.Metadata with { SourceUrl = "https://example.invalid/models.json" } },
            entry with { Metadata = entry.Metadata with { Sha256 = new string('0', 64) } },
            entry with { Metadata = entry.Metadata with { SourceETag = "not-an-etag" } },
            entry with { Metadata = entry.Metadata with { ValidatedAtUtc = entry.Metadata.FetchedAtUtc.AddMinutes(-1) } },
            entry with
            {
                Metadata = entry.Metadata with
                {
                    FetchedAtUtc = DateTimeOffset.UtcNow.AddDays(30),
                    ValidatedAtUtc = DateTimeOffset.UtcNow.AddDays(30),
                },
            },
            Entry("0.147.0", Encoding.UTF8.GetBytes("{\"models\":[]}"), DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Entry("0.147.0", InvalidDuplicateCatalog(), DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Entry("0.147.0", InvalidMissingInstructionsCatalog(), DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Entry("0.147.0", InvalidBehaviorTypeCatalog(), DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Entry("0.147.0", InvalidReviewTargetCatalog(), DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
        };
        var mutationEntriesList = mutationEntries.ToList();
        mutationEntriesList[3] = mutationEntriesList[3] with
        {
            Metadata = mutationEntriesList[3].Metadata with
            {
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(mutationEntriesList[3].SourceBytes)),
            },
        };

        using var store = Store();
        foreach (var candidate in mutationEntriesList)
            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.PromoteAsync(candidate));

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task InterruptedReplacementPreservesOldLastKnownGoodAndRemovesTemporaryFile()
    {
        var oldEntry = Entry("0.147.0", Catalog("old"), DateTimeOffset.Parse("2026-08-01T00:00:00Z"));
        using (var initial = Store()) await initial.PromoteAsync(oldEntry);
        var changed = Entry("0.147.0", Catalog("new"), DateTimeOffset.Parse("2026-08-02T00:00:00Z"));
        using var failing = Store(new CodexCatalogDiskStoreHooks
        {
            BeforePromotion = (_, _, _) => throw new IOException("simulated interrupted promotion"),
        });

        await Assert.ThrowsAsync<IOException>(async () => await failing.PromoteAsync(changed));

        var loaded = await failing.TryLoadAsync(oldEntry.Version);
        Assert.Equal("old", Assert.Single(loaded!.Baseline.Models).GetProperty("slug").GetString());
        Assert.Empty(Directory.EnumerateFiles(_root, ".catalog-*.tmp"));
    }

    [Fact]
    public async Task PartialCandidateWriteFailurePreservesOldRecordAndRemovesPartialTemporaryFile()
    {
        var oldEntry = Entry("0.147.0", Catalog("old"), DateTimeOffset.Parse("2026-08-01T00:00:00Z"));
        using (var initial = Store()) await initial.PromoteAsync(oldEntry);
        var changed = Entry("0.147.0", Catalog("new"), DateTimeOffset.Parse("2026-08-02T00:00:00Z"));
        using var failing = Store(new CodexCatalogDiskStoreHooks
        {
            AfterHeaderWritten = (_, _) => ValueTask.FromException(new IOException("simulated disk full")),
        });

        await Assert.ThrowsAsync<IOException>(async () => await failing.PromoteAsync(changed));

        var loaded = await failing.TryLoadAsync(oldEntry.Version);
        Assert.Equal("old", Assert.Single(loaded!.Baseline.Models).GetProperty("slug").GetString());
        Assert.Empty(Directory.EnumerateFiles(_root, ".catalog-*.tmp"));
    }

    [Fact]
    public async Task AllVersionPromotionsShareExactlyOneWriterCriticalSection()
    {
        var active = 0;
        var maximum = 0;
        var entries = Enumerable.Range(0, 8)
            .Select(index => Entry($"0.147.{index}", Catalog($"model-{index}"),
                DateTimeOffset.Parse("2026-08-01T00:00:00Z").AddMinutes(index)))
            .ToArray();
        using var store = Store(new CodexCatalogDiskStoreHooks
        {
            MutationEntered = async cancellationToken =>
            {
                var now = Interlocked.Increment(ref active);
                InterlockedExtensions.Max(ref maximum, now);
                await Task.Delay(30, cancellationToken);
            },
            MutationExited = () => Interlocked.Decrement(ref active),
        });

        await Task.WhenAll(entries.Select(entry => store.PromoteAsync(entry).AsTask()));

        Assert.Equal(1, maximum);
        Assert.Equal(0, active);
        Assert.Equal(entries.Length, Directory.EnumerateFiles(_root, "catalog-*.cache").Count());
    }

    [Fact]
    public async Task IndependentlyConstructedStoresStillShareTheProcessWriterLock()
    {
        var active = 0;
        var maximum = 0;
        CodexCatalogDiskStoreHooks Hooks() => new()
        {
            MutationEntered = async cancellationToken =>
            {
                var now = Interlocked.Increment(ref active);
                InterlockedExtensions.Max(ref maximum, now);
                await Task.Delay(40, cancellationToken);
            },
            MutationExited = () => Interlocked.Decrement(ref active),
        };
        using var one = Store(Hooks());
        using var two = Store(Hooks());

        await Task.WhenAll(
            one.PromoteAsync(Entry("0.147.1", Catalog("one"), Epoch())).AsTask(),
            two.PromoteAsync(Entry("0.147.2", Catalog("two"), Epoch())).AsTask());

        Assert.Equal(1, maximum);
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task WaitingWriterDoesNotOverwriteNewerDestinationState()
    {
        var newer = Entry("0.147.0", Catalog("newer"), DateTimeOffset.Parse("2026-08-02T00:00:00Z"));
        var older = Entry("0.147.0", Catalog("older"), DateTimeOffset.Parse("2026-08-01T00:00:00Z"));
        using var store = Store();
        await store.PromoteAsync(newer);

        var result = await store.PromoteAsync(older);

        Assert.Equal(newer.Metadata.Sha256, result.Metadata.Sha256);
        Assert.Equal("newer", Assert.Single((await store.TryLoadAsync(newer.Version))!.Baseline.Models)
            .GetProperty("slug").GetString());
    }

    [Fact]
    public async Task CleanupExcludesProtectedVersionsBeforeAgeAndCountPruning()
    {
        var now = DateTimeOffset.Parse("2026-08-20T00:00:00Z");
        var options = Options.Create(new CodexModelCatalogOptions
        {
            CacheDirectory = _root,
            MaxSourceBytes = 4 * 1024 * 1024,
            RetentionDays = 5,
            MaxRetainedVersions = 2,
        });
        using var store = new CodexCatalogDiskStore(
            options, NullLogger<CodexCatalogDiskStore>.Instance, timeProvider: new FixedTimeProvider(now));
        var entries = Enumerable.Range(0, 5)
            .Select(index => Entry($"0.148.{index}", Catalog($"model-{index}"), now.AddDays(-index)))
            .ToArray();
        foreach (var entry in entries)
        {
            await store.PromoteAsync(entry);
            File.SetLastWriteTimeUtc(
                CodexCatalogCachePaths.GetRecordPath(_root, entry.Version),
                entry.Metadata.FetchedAtUtc.UtcDateTime);
        }

        await store.CleanupAsync(version => version == entries[0].Version.ToString());

        Assert.True(File.Exists(CodexCatalogCachePaths.GetRecordPath(_root, entries[0].Version)));
        Assert.True(File.Exists(CodexCatalogCachePaths.GetRecordPath(_root, entries[1].Version)));
        Assert.True(File.Exists(CodexCatalogCachePaths.GetRecordPath(_root, entries[2].Version)));
        Assert.False(File.Exists(CodexCatalogCachePaths.GetRecordPath(_root, entries[3].Version)));
        Assert.False(File.Exists(CodexCatalogCachePaths.GetRecordPath(_root, entries[4].Version)));
    }

    [Fact]
    public async Task CleanupRechecksProtectionAfterEnteringWriterLock()
    {
        var now = DateTimeOffset.Parse("2026-08-20T00:00:00Z");
        var entry = Entry("0.149.0", Catalog("protected-late"), now.AddDays(-30));
        using (var seed = Store()) await seed.PromoteAsync(entry);
        var protectedNow = false;
        using var store = new CodexCatalogDiskStore(
            Options.Create(new CodexModelCatalogOptions
            {
                CacheDirectory = _root,
                MaxSourceBytes = 4 * 1024 * 1024,
                RetentionDays = 1,
                MaxRetainedVersions = 1,
            }),
            NullLogger<CodexCatalogDiskStore>.Instance,
            new CodexCatalogDiskStoreHooks
            {
                MutationEntered = _ =>
                {
                    protectedNow = true;
                    return ValueTask.CompletedTask;
                },
            },
            new FixedTimeProvider(now));

        await store.CleanupAsync(_ => protectedNow);

        Assert.True(File.Exists(CodexCatalogCachePaths.GetRecordPath(_root, entry.Version)));
    }

    [Fact]
    public async Task TemporaryCleanupFailureIsNonFatalAndUnrelatedFilesAreUntouched()
    {
        Directory.CreateDirectory(_root);
        var temporary = Path.Combine(_root, ".catalog-" + new string('a', 32) + ".tmp");
        var unrelated = Path.Combine(_root, "operator-notes.txt");
        await File.WriteAllTextAsync(temporary, "partial");
        await File.WriteAllTextAsync(unrelated, "keep");
        using var store = Store(new CodexCatalogDiskStoreHooks
        {
            BeforeDelete = (path, _) => path == temporary
                ? ValueTask.FromException(new IOException("permission denied"))
                : ValueTask.CompletedTask,
        });

        await store.CleanupAsync(_ => false);

        Assert.True(File.Exists(temporary));
        Assert.True(File.Exists(unrelated));
    }

    private CodexCatalogDiskStore Store(CodexCatalogDiskStoreHooks? hooks = null) => new(
        Options.Create(new CodexModelCatalogOptions
        {
            CacheDirectory = _root,
            MaxSourceBytes = 4 * 1024 * 1024,
            RetentionDays = 90,
            MaxRetainedVersions = 32,
        }),
        NullLogger<CodexCatalogDiskStore>.Instance,
        hooks);

    internal static CodexCatalogCacheEntry Entry(string versionText, byte[] bytes, DateTimeOffset timestamp)
    {
        Assert.True(CodexClientVersion.TryParse(versionText, out var version));
        var uri = CodexCatalogSource.BuildUri(version);
        var metadata = new CodexCatalogCacheMetadata
        {
            SchemaVersion = 1,
            ClientVersion = versionText,
            SourceUrl = uri.AbsoluteUri,
            SourceETag = "\"etag-" + versionText + "\"",
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            FetchedAtUtc = timestamp,
            ValidatedAtUtc = timestamp,
        };
        var baseline = CodexCatalogBaseline.Parse(bytes, metadata);
        return new CodexCatalogCacheEntry
        {
            Version = version,
            SourceUri = uri,
            SourceBytes = bytes,
            Metadata = metadata,
            Baseline = baseline,
        };
    }

    internal static byte[] Catalog(string slug) => Encoding.UTF8.GetBytes($$"""
        {"models":[{"slug":"{{slug}}","base_instructions":"instructions","context_window":272000,"max_context_window":272000,"auto_compact_token_limit":250000,"supported_in_api":true,"visibility":"list","auto_review_model_override":null}]}
        """);

    private static byte[] InvalidDuplicateCatalog() => Encoding.UTF8.GetBytes("""
        {"models":[
          {"slug":"same","base_instructions":"one","context_window":272000,"max_context_window":272000,"auto_compact_token_limit":250000,"supported_in_api":true,"visibility":"list"},
          {"slug":"same","base_instructions":"two","context_window":272000,"max_context_window":272000,"auto_compact_token_limit":250000,"supported_in_api":true,"visibility":"list"}
        ]}
        """);

    private static byte[] InvalidMissingInstructionsCatalog() => Encoding.UTF8.GetBytes("""
        {"models":[{"slug":"missing-instructions","context_window":272000,"max_context_window":272000,"auto_compact_token_limit":250000,"supported_in_api":true,"visibility":"list"}]}
        """);

    private static byte[] InvalidBehaviorTypeCatalog() => Encoding.UTF8.GetBytes("""
        {"models":[{"slug":"wrong-types","base_instructions":"instructions","context_window":"272000","max_context_window":272000,"auto_compact_token_limit":250000,"supported_in_api":"yes","visibility":1}]}
        """);

    private static byte[] InvalidReviewTargetCatalog() => Encoding.UTF8.GetBytes("""
        {"models":[{"slug":"reviewer","base_instructions":"instructions","context_window":272000,"max_context_window":272000,"auto_compact_token_limit":250000,"supported_in_api":true,"visibility":"list","auto_review_model_override":"missing"}]}
        """);

    private static byte[] HeaderMutation(byte[] bytes, int? metadataLength = null, int? sourceLength = null)
    {
        var result = bytes.ToArray();
        if (metadataLength.HasValue) BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8, 4), metadataLength.Value);
        if (sourceLength.HasValue) BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12, 4), sourceLength.Value);
        return result;
    }

    private static byte[] BodyMutation(byte[] bytes)
    {
        var result = bytes.ToArray();
        result[^1] ^= 1;
        return result;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }

    private static DateTimeOffset Epoch() => DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
