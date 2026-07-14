using System.Text.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.GitHub;
using CopilotBridge.Cli.Update;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Contract tests for <see cref="UpdateAssetSelector"/> from the "Exact RID
/// update asset selection" requirement, using real GitHub Release Asset response
/// shapes (deserialized through the shared source-gen context, proving the wire
/// mapping too).
/// </summary>
public class UpdateAssetSelectorTests
{
    // A realistic release with the five published assets for version 1.2.3.
    private static GitHubRelease ReleaseWithAssets(string version)
    {
        var json = $$"""
        {
          "tag_name": "v{{version}}",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "copilot-bridge-{{version}}-win-x64.zip", "state": "uploaded", "size": 6203101,
              "browser_download_url": "https://github.com/hooyao/copilot-bridge/releases/download/v{{version}}/copilot-bridge-{{version}}-win-x64.zip",
              "digest": "sha256:7adfd244b73107a345ff6368403049e415f4f56c756b281e7037637b6f3283a5" },
            { "name": "copilot-bridge-{{version}}-osx-arm64.tar.gz", "state": "uploaded", "size": 6061740,
              "browser_download_url": "https://github.com/hooyao/copilot-bridge/releases/download/v{{version}}/copilot-bridge-{{version}}-osx-arm64.tar.gz",
              "digest": "sha256:34c6e1f0c1a0a93f7df2f1e41fa2e33e79cfbae05cc07eb6e335c5954e6a2b6e" },
            { "name": "copilot-bridge-{{version}}-osx-arm64.pkg", "state": "uploaded", "size": 6065002,
              "browser_download_url": "https://github.com/hooyao/copilot-bridge/releases/download/v{{version}}/copilot-bridge-{{version}}-osx-arm64.pkg",
              "digest": "sha256:fc28867c72ece94a05a3a4a10581cd7cf54ff71a5e6b6e7a0a0b71d58eb9c41e" }
          ]
        }
        """;
        return JsonSerializer.Deserialize(json, JsonContext.Default.GitHubRelease)!;
    }

    [Fact]
    public void Windows_x64_selects_the_exact_zip()
    {
        var asset = UpdateAssetSelector.Resolve(ReleaseWithAssets("1.2.3"), "1.2.3", "win-x64");

        Assert.NotNull(asset);
        Assert.Equal("copilot-bridge-1.2.3-win-x64.zip", asset!.AssetName);
        Assert.Equal(ArchiveKind.Zip, asset.Kind);
        Assert.Equal(6203101, asset.Size);
        Assert.Equal("7adfd244b73107a345ff6368403049e415f4f56c756b281e7037637b6f3283a5", asset.Sha256Hex);
        Assert.StartsWith("https://", asset.DownloadUrl);
    }

    [Fact]
    public void MacOS_selects_targz_not_pkg()
    {
        var asset = UpdateAssetSelector.Resolve(ReleaseWithAssets("1.2.3"), "1.2.3", "osx-arm64");

        Assert.NotNull(asset);
        Assert.Equal("copilot-bridge-1.2.3-osx-arm64.tar.gz", asset!.AssetName);
        Assert.Equal(ArchiveKind.TarGz, asset.Kind);
        Assert.DoesNotContain(".pkg", asset.AssetName);
    }

    [Fact]
    public void Missing_asset_for_rid_returns_null()
    {
        // No linux-x64 asset in the release above.
        Assert.Null(UpdateAssetSelector.Resolve(ReleaseWithAssets("1.2.3"), "1.2.3", "linux-x64"));
    }

    [Fact]
    public void Non_uploaded_asset_is_rejected()
    {
        var release = new GitHubRelease
        {
            TagName = "v1.2.3",
            Assets =
            [
                new GitHubReleaseAsset
                {
                    Name = "copilot-bridge-1.2.3-win-x64.zip",
                    State = "uploading",
                    Size = 100,
                    BrowserDownloadUrl = "https://example/x.zip",
                    Digest = "sha256:" + new string('a', 64),
                },
            ],
        };
        Assert.Null(UpdateAssetSelector.Resolve(release, "1.2.3", "win-x64"));
    }

    [Fact]
    public void Missing_or_non_sha256_digest_is_rejected()
    {
        GitHubRelease WithDigest(string? digest) => new()
        {
            TagName = "v1.2.3",
            Assets =
            [
                new GitHubReleaseAsset
                {
                    Name = "copilot-bridge-1.2.3-win-x64.zip",
                    State = "uploaded",
                    Size = 100,
                    BrowserDownloadUrl = "https://example/x.zip",
                    Digest = digest,
                },
            ],
        };

        Assert.Null(UpdateAssetSelector.Resolve(WithDigest(null), "1.2.3", "win-x64"));
        Assert.Null(UpdateAssetSelector.Resolve(WithDigest("sha512:" + new string('a', 128)), "1.2.3", "win-x64"));
        Assert.Null(UpdateAssetSelector.Resolve(WithDigest("sha256:xyz"), "1.2.3", "win-x64"));
    }

    [Fact]
    public void Non_https_url_is_rejected()
    {
        var release = new GitHubRelease
        {
            TagName = "v1.2.3",
            Assets =
            [
                new GitHubReleaseAsset
                {
                    Name = "copilot-bridge-1.2.3-win-x64.zip",
                    State = "uploaded",
                    Size = 100,
                    BrowserDownloadUrl = "http://insecure/x.zip",
                    Digest = "sha256:" + new string('a', 64),
                },
            ],
        };
        Assert.Null(UpdateAssetSelector.Resolve(release, "1.2.3", "win-x64"));
    }

    [Fact]
    public void Unsupported_rid_returns_null()
    {
        Assert.Null(UpdateAssetSelector.Resolve(ReleaseWithAssets("1.2.3"), "1.2.3", "linux-arm64"));
    }

    [Fact]
    public void Supported_rids_are_the_four_published_targets()
    {
        Assert.Equal(
            new[] { "win-x64", "win-arm64", "linux-x64", "osx-arm64" },
            UpdateAssetSelector.SupportedRids);
    }

    [Fact]
    public void ArchiveKind_maps_to_the_frozen_wire_strings()
    {
        Assert.Equal("zip", ArchiveKind.Zip.ToWire());
        Assert.Equal("tar.gz", ArchiveKind.TarGz.ToWire());
    }
}
