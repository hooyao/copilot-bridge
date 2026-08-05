using System.Security.Cryptography;
using System.Text;
using CopilotBridge.Cli.Catalogs.Codex;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract: client_version selects one exact official Codex release tag. It is
/// never normalized to a neighboring release and never becomes a path segment.
/// </summary>
public sealed class CodexCatalogSourceContractTests
{
    [Theory]
    [InlineData("0.147.0")]
    [InlineData("0.147.0-alpha.1.2")]
    [InlineData("1.2.3-rc.1+desktop.7")]
    [InlineData("1.2.3-alpha-beta.1+desktop-x64")]
    public void CanonicalVersionPreservesTheCompleteExactIdentity(string text)
    {
        Assert.True(CodexClientVersion.TryParse(text, out var version));

        Assert.Equal(text, version.ToString());
        Assert.Equal(
            $"https://raw.githubusercontent.com/openai/codex/rust-v{text}/codex-rs/models-manager/models.json",
            CodexCatalogSource.BuildUri(version).AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" 0.147.0")]
    [InlineData("0.147.0 ")]
    [InlineData("v0.147.0")]
    [InlineData("0.147")]
    [InlineData("0.147.0.1")]
    [InlineData("00.147.0")]
    [InlineData("0.0147.0")]
    [InlineData("0.147.00")]
    [InlineData("0.147.0-alpha.01")]
    [InlineData("0.147.0-alpha_1")]
    [InlineData("0.147.0-alpha..1")]
    [InlineData("0.147.0+")]
    [InlineData("0.147.0+build_1")]
    [InlineData("0.147.0/../../main")]
    [InlineData("0.147.0%2fmain")]
    [InlineData("0.147.0?x=1")]
    [InlineData("0.147.0#fragment")]
    [InlineData("https://example.invalid")]
    [InlineData("0.147.0\nmain")]
    public void InvalidOrUnsafeVersionIsRejectedWithoutAnIdentity(string? text)
    {
        Assert.False(CodexClientVersion.TryParse(text, out var version));
        Assert.Equal(default, version);
    }

    [Fact]
    public void ExactPrereleaseDoesNotCollapseToStableOrAdjacentVersion()
    {
        Assert.True(CodexClientVersion.TryParse("0.147.0-alpha.1.2", out var requested));

        var uri = CodexCatalogSource.BuildUri(requested).AbsoluteUri;

        Assert.Contains("rust-v0.147.0-alpha.1.2/", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("rust-v0.147.0/", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("rust-v0.146", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("/main/", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void WholeQueryAndCompleteCodexUserAgentResolveTheExactPrereleaseTag()
    {
        const string userAgent =
            "Codex Desktop/0.147.0-alpha.1.2 (Windows 10.0.26200; x86_64) vscode/1.0";

        var resolved = CodexCatalogRequestIdentity.TryResolve(
            "0.147.0", userAgent, out var version, out var error);

        Assert.True(resolved, error);
        Assert.Equal("0.147.0-alpha.1.2", version.ToString());
        Assert.Contains("rust-v0.147.0-alpha.1.2/", CodexCatalogSource.BuildUri(version).AbsoluteUri,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HeadlessExecUserAgentResolvesTheExactPrereleaseTag()
    {
        const string userAgent =
            "codex_exec/0.147.0-alpha.1.2 (Windows 10.0.26200; x86_64) unknown (codex_exec; 0.147.0-alpha.1.2)";

        var resolved = CodexCatalogRequestIdentity.TryResolve(
            "0.147.0", userAgent, out var version, out var error);

        Assert.True(resolved, error);
        Assert.Equal("0.147.0-alpha.1.2", version.ToString());
    }

    [Theory]
    [InlineData("0.147.0", "Codex Desktop/0.148.0-alpha.1 (Windows)")]
    [InlineData("0.147.0-alpha.1.2", "Codex Desktop/0.147.0-alpha.1.1 (Windows)")]
    [InlineData("0.147.0", "Codex Desktop/not-a-version (Windows)")]
    public void ContradictoryOrMalformedCodexUserAgentIsRejectedBeforeSourceResolution(
        string query, string userAgent)
    {
        Assert.False(CodexCatalogRequestIdentity.TryResolve(query, userAgent, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("0.147.0", null, "0.147.0")]
    [InlineData("0.147.0", "curl/8.10.0", "0.147.0")]
    [InlineData("0.147.0-alpha.1.2", null, "0.147.0-alpha.1.2")]
    public void StableOrExplicitExactQueryDoesNotRequireACodexUserAgent(
        string query, string? userAgent, string expected)
    {
        Assert.True(CodexCatalogRequestIdentity.TryResolve(query, userAgent, out var version, out var error), error);
        Assert.Equal(expected, version.ToString());
    }

    [Fact]
    public void CacheRecordNameIsAHashNotTheUntrustedVersionText()
    {
        Assert.True(CodexClientVersion.TryParse("0.147.0-alpha.1.2", out var version));
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-path-contract"));

        var path = CodexCatalogCachePaths.GetRecordPath(root, version);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(version.ToString())));

        Assert.Equal(Path.Combine(root, $"catalog-{expectedHash}.cache"), path);
        Assert.True(CodexCatalogCachePaths.IsUnderRoot(root, path));
        Assert.DoesNotContain(version.ToString(), Path.GetFileName(path), StringComparison.Ordinal);
    }

    [Fact]
    public void RelativeCacheOverrideIsRejected()
    {
        var options = ValidOptions() with { CacheDirectory = "relative-cache" };

        var result = new CodexModelCatalogOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("absolute", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AbsoluteCacheOverrideIsNormalizedAndContained()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-root", "nested"));
        var options = ValidOptions() with { CacheDirectory = root };
        Assert.True(CodexClientVersion.TryParse("0.147.0-alpha.1.2", out var version));

        var result = new CodexModelCatalogOptionsValidator().Validate(null, options);
        var resolvedRoot = CodexCatalogCachePaths.ResolveRoot(options);
        var record = CodexCatalogCachePaths.GetRecordPath(resolvedRoot, version);

        Assert.Equal(ValidateOptionsResult.Success, result);
        Assert.Equal(root, resolvedRoot);
        Assert.True(CodexCatalogCachePaths.IsUnderRoot(resolvedRoot, record));
    }

    [Theory]
    [InlineData(0, 10, 4_194_304, 90, 32)]
    [InlineData(169, 10, 4_194_304, 90, 32)]
    [InlineData(24, 0, 4_194_304, 90, 32)]
    [InlineData(24, 61, 4_194_304, 90, 32)]
    [InlineData(24, 10, 65_535, 90, 32)]
    [InlineData(24, 10, 16_777_217, 90, 32)]
    [InlineData(24, 10, 4_194_304, 0, 32)]
    [InlineData(24, 10, 4_194_304, 366, 32)]
    [InlineData(24, 10, 4_194_304, 90, 0)]
    [InlineData(24, 10, 4_194_304, 90, 257)]
    public void OutOfRangeCachePolicyIsRejected(
        int ttlHours, int timeoutSeconds, int maxSourceBytes, int retentionDays, int retainedVersions)
    {
        var options = ValidOptions() with
        {
            SourceTtlHours = ttlHours,
            SourceTimeoutSeconds = timeoutSeconds,
            MaxSourceBytes = maxSourceBytes,
            RetentionDays = retentionDays,
            MaxRetainedVersions = retainedVersions,
        };

        Assert.True(new CodexModelCatalogOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void StockConfigurationBindsTheDocumentedCatalogPolicyFromTheCorrectSection()
    {
        var path = FindRepoFile("src", "CopilotBridge.Cli", "appsettings.json");
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();
        var options = new CodexModelCatalogOptions();

        config.GetSection("Codex:ModelCatalog").Bind(options);

        Assert.True(options.Enabled);
        Assert.Equal(24, options.SourceTtlHours);
        Assert.Equal(10, options.SourceTimeoutSeconds);
        Assert.Equal(4 * 1024 * 1024, options.MaxSourceBytes);
        Assert.Equal(90, options.RetentionDays);
        Assert.Equal(32, options.MaxRetainedVersions);
        Assert.Null(options.CacheDirectory);
        Assert.Null(config["Pipeline:Detectors:ModelRewrite:SourceTtlHours"]);
    }

    private static string FindRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate repository file.");
    }

    private static CodexModelCatalogOptions ValidOptions() => new()
    {
        CacheDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "catalog-options-contract")),
        SourceTtlHours = 24,
        SourceTimeoutSeconds = 10,
        MaxSourceBytes = 4 * 1024 * 1024,
        RetentionDays = 90,
        MaxRetainedVersions = 32,
    };
}
