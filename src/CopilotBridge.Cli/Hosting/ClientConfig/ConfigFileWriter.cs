namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// Shared safe-write plumbing for configurators' <c>Apply</c>: back up the prior
/// file, then write the new content atomically (write a temp sibling, then move it
/// over the target) so a crash mid-write can never truncate the user's real config.
/// </summary>
internal static class ConfigFileWriter
{
    /// <summary>
    /// Apply a plan to disk. No-op plans (<see cref="ConfigPlan.IsNoOp"/>) write
    /// nothing. When the target already exists its prior content is copied to a
    /// sibling <c>&lt;name&gt;.bak</c> before the new content replaces it.
    /// </summary>
    /// <returns>The backup path written, or <c>null</c> if no backup was needed
    /// (new file or no-op).</returns>
    public static string? Write(ConfigPlan plan)
    {
        if (plan.IsNoOp)
        {
            return null;
        }

        var dir = Path.GetDirectoryName(plan.TargetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string? backupPath = null;
        if (!plan.IsNew && File.Exists(plan.TargetPath))
        {
            // Single .bak, overwritten each run (matches Codex's own
            // .codex-global-state.json.bak convention). Preserves the last-known-good
            // file so a bad merge is one copy away from recovery.
            backupPath = plan.TargetPath + ".bak";
            File.Copy(plan.TargetPath, backupPath, overwrite: true);
        }

        // Atomic-ish replace: write a temp file in the same directory, flush, then
        // move it over the target. A same-directory move is atomic on NTFS and on
        // POSIX filesystems, so readers never observe a partially written file.
        var tempPath = plan.TargetPath + ".tmp";
        File.WriteAllText(tempPath, plan.NewContent);
        File.Move(tempPath, plan.TargetPath, overwrite: true);

        return backupPath;
    }
}
