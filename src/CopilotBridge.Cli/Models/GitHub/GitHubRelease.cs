namespace CopilotBridge.Cli.Models.GitHub;

/// <summary>
/// One asset attached to a GitHub release, as returned by the public Releases
/// REST API. Property names map to the snake_case wire shape via the shared
/// <see cref="JsonContext"/> naming policy (<c>browser_download_url</c>,
/// <c>digest</c>, …). <see cref="Digest"/> is GitHub-computed and formatted
/// <c>sha256:&lt;hex&gt;</c> (nullable — older assets may lack it). The updater
/// trusts this digest directly; no separately published checksum sidecar exists.
/// </summary>
internal sealed record GitHubReleaseAsset
{
    public required string Name { get; init; }

    /// <summary>Upload state; only <c>uploaded</c> assets are installable.</summary>
    public string? State { get; init; }

    public long Size { get; init; }

    public string? BrowserDownloadUrl { get; init; }

    /// <summary><c>sha256:&lt;64-hex&gt;</c>, or null when GitHub reports no digest.</summary>
    public string? Digest { get; init; }
}

/// <summary>
/// One GitHub release. <see cref="TagName"/> is the raw <c>v&lt;semver&gt;</c>
/// tag; <see cref="Body"/> is the release notes (may be null/empty).
/// </summary>
internal sealed record GitHubRelease
{
    public required string TagName { get; init; }

    public string? Name { get; init; }

    public string? Body { get; init; }

    public bool Draft { get; init; }

    public bool Prerelease { get; init; }

    public string? PublishedAt { get; init; }

    public string? HtmlUrl { get; init; }

    public List<GitHubReleaseAsset> Assets { get; init; } = [];
}
