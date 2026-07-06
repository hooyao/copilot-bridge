using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// Configures Claude Code to point at the bridge by editing its settings JSON.
/// Supports both global (<c>~/.claude/settings.json</c>) and repo
/// (<c>./.claude/settings.local.json</c>, the personal gitignored file) scopes.
/// </summary>
/// <remarks>
/// The merge is surgical: only the bridge's own <c>env</c> keys are written; every
/// other key in the file (statusLine, enabledPlugins, effortLevel, …) is preserved
/// via a <see cref="JsonNode"/> DOM edit. <see cref="JsonNode"/> read/modify/write is
/// AOT-safe — it does not use reflection-based serialization.
/// </remarks>
internal sealed class ClaudeCodeConfigurator : IClientConfigurator
{
    /// <summary>The env key for the Anthropic API base URL Claude Code calls.</summary>
    private const string BaseUrlKey = "ANTHROPIC_BASE_URL";

    /// <summary>The (unused-by-bridge but required-by-Claude-Code) auth token env key.</summary>
    private const string AuthTokenKey = "ANTHROPIC_AUTH_TOKEN";

    /// <summary>Placeholder auth token — Claude Code requires the var to be set, but
    /// the bridge authenticates to Copilot with the stored GitHub token and ignores
    /// this value entirely.</summary>
    private const string AuthTokenPlaceholder = "copilot-bridge";

    /// <summary>The env key that disables Claude Code's silent streaming→non-streaming
    /// fallback so a mid-stream detector abort forces a whole-turn retry.</summary>
    private const string FallbackKey = "CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK";

    /// <summary>Truthy value for <see cref="FallbackKey"/>. Claude Code's check is
    /// <c>isEnvTruthy</c>, for which <c>"1"</c> is true.</summary>
    private const string FallbackOn = "1";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    public string ClientId => "claude-code";

    public IReadOnlyList<ConfigScope> SupportedScopes { get; } = [ConfigScope.Global, ConfigScope.Repo];

    public ConfigPlan Plan(BridgeConnection connection, ConfigScope scope)
    {
        var path = ResolvePath(scope);
        var original = File.Exists(path) ? File.ReadAllText(path) : null;

        var (newContent, summary) = BuildContent(original, connection);

        return new ConfigPlan(ClientId, scope, path, newContent, original, summary);
    }

    /// <summary>
    /// The pure merge: given the current file content (or null when the file does not
    /// exist), produce the new content and a human-readable summary. No filesystem
    /// access — this is the seam contract tests exercise directly.
    /// </summary>
    internal static (string Content, IReadOnlyList<string> Summary) BuildContent(
        string? original, BridgeConnection connection)
    {
        var root = ParseOrNewObject(original);
        var summary = MergeInto(root, connection);

        // Trailing newline: editors and git generally expect a final newline; keeps
        // re-runs byte-stable and diffs clean.
        var content = root.ToJsonString(WriteOptions) + "\n";
        return (content, summary);
    }

    public void Apply(ConfigPlan plan) => ConfigFileWriter.Write(plan);

    public ConfigState Read(BridgeConnection connection, ConfigScope scope)
    {
        var path = ResolvePath(scope);
        var expected = connection.ClaudeCodeBaseUrl;

        if (!File.Exists(path))
        {
            return new ConfigState(ClientId, scope, path, Exists: false,
                ConfiguredForBridge: false, CurrentBaseUrl: null, ExpectedBaseUrl: expected,
                Details: ["not configured (file does not exist)"]);
        }

        var root = ParseOrNewObject(File.ReadAllText(path));
        var env = root["env"] as JsonObject;
        var current = env?[BaseUrlKey]?.GetValue<string>();
        var fallback = env?[FallbackKey]?.GetValue<string>();

        var details = new List<string>();
        details.Add(current is null
            ? $"{BaseUrlKey}: (unset)"
            : $"{BaseUrlKey}: {current}");
        details.Add(fallback is null
            ? $"{FallbackKey}: (unset)"
            : $"{FallbackKey}: {fallback}");

        return new ConfigState(ClientId, scope, path, Exists: true,
            ConfiguredForBridge: current is not null, CurrentBaseUrl: current,
            ExpectedBaseUrl: expected, Details: details);
    }

    /// <summary>
    /// Apply the bridge's managed keys to the <c>env</c> object, preserving all other
    /// content. Returns human-readable summary lines for <c>--dry-run</c>.
    /// </summary>
    private static IReadOnlyList<string> MergeInto(JsonObject root, BridgeConnection connection)
    {
        var summary = new List<string>();

        if (root["env"] is not JsonObject env)
        {
            env = new JsonObject();
            root["env"] = env;
        }

        // base_url — always force-written (this is the point of the command).
        env[BaseUrlKey] = connection.ClaudeCodeBaseUrl;
        summary.Add($"set env.{BaseUrlKey} = {connection.ClaudeCodeBaseUrl}");

        // auth token — fill only if absent; preserve any existing value.
        if (env[AuthTokenKey] is null)
        {
            env[AuthTokenKey] = AuthTokenPlaceholder;
            summary.Add($"set env.{AuthTokenKey} = {AuthTokenPlaceholder} (was unset)");
        }
        else
        {
            summary.Add($"kept env.{AuthTokenKey} (already set)");
        }

        // fallback env — write "1" when a detector can abort mid-stream, else remove
        // the key so the written config self-heals to the current appsettings state.
        if (connection.NeedNonStreamingFallbackDisabled)
        {
            env[FallbackKey] = FallbackOn;
            summary.Add($"set env.{FallbackKey} = {FallbackOn}");
        }
        else if (env.ContainsKey(FallbackKey))
        {
            env.Remove(FallbackKey);
            summary.Add($"removed env.{FallbackKey} (no detector preserves the stream)");
        }

        return summary;
    }

    /// <summary>
    /// Parse existing JSON into a mutable object, or start a fresh object. A file that
    /// is not a JSON object (or is empty/whitespace) yields a new empty object so the
    /// merge still produces valid output.
    /// </summary>
    private static JsonObject ParseOrNewObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new JsonObject();
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return new JsonObject();
        }

        return node as JsonObject ?? new JsonObject();
    }

    /// <summary>Resolve the target settings file for a scope.</summary>
    private static string ResolvePath(ConfigScope scope) => scope switch
    {
        ConfigScope.Global => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "settings.json"),
        ConfigScope.Repo => Path.Combine(
            Environment.CurrentDirectory, ".claude", "settings.local.json"),
        _ => throw new System.ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported scope."),
    };
}
