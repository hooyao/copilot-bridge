namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// The pure result of a configurator's <c>Plan</c> step: the exact bytes that
/// <c>Apply</c> will write, plus enough metadata to print a useful <c>--dry-run</c>.
/// Computing this performs no filesystem writes, which is what makes <c>--dry-run</c>
/// free and idempotence testable (same inputs → same <see cref="NewContent"/>).
/// </summary>
/// <param name="ClientId">The configurator that produced this plan (e.g.
/// <c>"claude-code"</c>).</param>
/// <param name="Scope">The scope this plan targets.</param>
/// <param name="TargetPath">Absolute path of the file that will be created or
/// modified.</param>
/// <param name="NewContent">The full intended file content after the surgical
/// merge.</param>
/// <param name="OriginalContent">The current file content, or <c>null</c> if the
/// file does not yet exist. Used to detect a no-op (identical content → nothing to
/// write) and to decide whether a backup is needed.</param>
/// <param name="Summary">Human-readable lines describing what the plan changes,
/// shown under <c>--dry-run</c>.</param>
internal sealed record ConfigPlan(
    string ClientId,
    ConfigScope Scope,
    string TargetPath,
    string NewContent,
    string? OriginalContent,
    IReadOnlyList<string> Summary)
{
    /// <summary>True when the target file already exists with identical content —
    /// applying is a no-op and needs no write or backup.</summary>
    public bool IsNoOp => OriginalContent is not null &&
        string.Equals(OriginalContent, NewContent, System.StringComparison.Ordinal);

    /// <summary>True when the target file does not yet exist.</summary>
    public bool IsNew => OriginalContent is null;
}
