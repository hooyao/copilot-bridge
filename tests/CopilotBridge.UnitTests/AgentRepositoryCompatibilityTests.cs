using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract: a fresh clone exposes one canonical instruction source and the same
/// hand-maintained project workflows to Claude Code and Codex. Machine-local
/// Codex credentials stay outside version control.
/// </summary>
public sealed class AgentRepositoryCompatibilityTests
{
    private static readonly string[] MirroredSkills =
    [
        "copilot-model-sync",
        "real-client-verify",
        "ship-pr",
    ];

    private static readonly string[] RequiredCodexSkills =
    [
        .. MirroredSkills,
        "openspec-apply-change",
        "openspec-archive-change",
        "openspec-explore",
        "openspec-propose",
        "openspec-sync-specs",
        "source-command-opsx-apply",
        "source-command-opsx-archive",
        "source-command-opsx-explore",
        "source-command-opsx-propose",
        "source-command-opsx-sync",
    ];

    [Fact]
    public void Claude_imports_the_canonical_agents_instructions()
    {
        var root = LocateRepositoryRoot();

        Assert.Equal(
            "@AGENTS.md",
            File.ReadAllText(Path.Combine(root, "CLAUDE.md")).Trim());
        Assert.True(File.Exists(Path.Combine(root, "AGENTS.md")));
    }

    [Fact]
    public void Codex_skill_catalog_contains_every_required_project_workflow()
    {
        var root = LocateRepositoryRoot();
        var skillRoot = Path.Combine(root, ".agents", "skills");

        foreach (var skill in RequiredCodexSkills)
        {
            Assert.True(
                File.Exists(Path.Combine(skillRoot, skill, "SKILL.md")),
                $"Codex skill '{skill}' is missing its SKILL.md.");
        }

        Assert.True(File.Exists(Path.Combine(
            skillRoot, "real-client-verify", "scripts", "read-codex-log.cs")));
        Assert.True(File.Exists(Path.Combine(
            skillRoot, "ship-pr", "scripts", "pr-status.sh")));
        Assert.True(File.Exists(Path.Combine(
            skillRoot, "ship-pr", "scripts", "reply-resolve.sh")));
    }

    [Fact]
    public void Hand_maintained_skill_mirrors_differ_only_by_their_root_path()
    {
        var root = LocateRepositoryRoot();
        foreach (var skill in MirroredSkills)
        {
            var claudeRoot = Path.Combine(root, ".claude", "skills", skill);
            var codexRoot = Path.Combine(root, ".agents", "skills", skill);
            var claudeFiles = RelativeFiles(claudeRoot);
            var codexFiles = RelativeFiles(codexRoot);

            Assert.Equal(claudeFiles, codexFiles);
            foreach (var relativePath in claudeFiles)
            {
                var expected = File.ReadAllText(Path.Combine(claudeRoot, relativePath));
                var actual = File.ReadAllText(Path.Combine(codexRoot, relativePath))
                    .Replace(
                        ".agents/skills/",
                        ".claude/skills/",
                        StringComparison.Ordinal);
                Assert.True(
                    string.Equals(expected, actual, StringComparison.Ordinal),
                    $"Skill mirror drifted: {skill}/{relativePath}");
            }
        }
    }

    [Fact]
    public void Machine_local_codex_config_is_explicitly_ignored()
    {
        var root = LocateRepositoryRoot();
        var ignoreLines = File.ReadAllLines(Path.Combine(root, ".gitignore"))
            .Select(line => line.Trim())
            .ToArray();

        Assert.Contains(".codex/config.toml", ignoreLines, StringComparer.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, ".codex", "README.md")));
    }

    private static string[] RelativeFiles(string directory) =>
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CopilotBridge.slnx"))
                && File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate repository root from the unit-test output directory.");
    }
}
