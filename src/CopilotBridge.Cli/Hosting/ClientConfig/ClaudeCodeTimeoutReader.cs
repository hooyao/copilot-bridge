using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// What Claude Code's settings file says about a single timeout-governing env
/// key, as seen from the bridge's startup report.
/// </summary>
/// <param name="Key">The env key this describes.</param>
/// <param name="EffectiveMs">The bound that will actually apply: the stored
/// value when present and parseable, or the client's built-in default when the
/// key is absent. <c>null</c> only when the file itself could not be read or does
/// not concern this bridge.</param>
/// <param name="IsExplicit">True when the value came from the file; false when it
/// is the built-in default that applies in the key's absence.</param>
internal sealed record ClientTimeoutValue(string Key, int? EffectiveMs, bool IsExplicit)
{
    /// <summary>True when neither a stored value nor a known default applies.</summary>
    public bool IsUnknown => EffectiveMs is null;
}

/// <summary>
/// What the bridge could learn about the client's timeout configuration.
/// </summary>
/// <param name="SettingsPath">The file that was inspected.</param>
/// <param name="Readable">False when the file is missing, unreadable, malformed,
/// or not pointed at this bridge — the only states the report calls "unknown".</param>
/// <param name="Reason">Human-readable explanation when <paramref name="Readable"/>
/// is false; <c>null</c> otherwise.</param>
/// <param name="StreamIdle">The stream-idle bound.</param>
/// <param name="RequestTimeout">The whole-request bound.</param>
internal sealed record ClientTimeoutSnapshot(
    string SettingsPath,
    bool Readable,
    string? Reason,
    ClientTimeoutValue StreamIdle,
    ClientTimeoutValue RequestTimeout);

/// <summary>
/// Reads the two timeout-governing env values out of Claude Code's settings file
/// for the bridge's startup report. Never throws — a client the bridge did not
/// configure, or a file it cannot parse, must not fail startup.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="ClaudeCodeConfigurator"/>'s
/// <c>Read</c>: that one serves <c>config status</c> and lives in the config
/// command's composition root, which the <c>client-autoconfiguration</c> spec
/// requires to stay isolated from the server startup path. This reader is a
/// plain static the hosted service can call directly.
///
/// <para>"Unknown" is reserved for a file that could not be read or does not
/// concern this bridge. A readable, bridge-pointed file that simply lacks a key
/// is NOT unknown — the key's absence selects a known client default, which the
/// report treats as a real (and undercutting) bound.</para>
/// </remarks>
internal static class ClaudeCodeTimeoutReader
{
    /// <summary>Global Claude Code settings path (<c>~/.claude/settings.json</c>).</summary>
    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "settings.json");

    /// <summary>
    /// Inspect the Claude Code settings the bridge can actually see: the global
    /// <c>~/.claude/settings.json</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Repo-scoped settings are deliberately NOT resolved here.</b>
    /// <c>config claude-code --scope repo</c> writes
    /// <c>./.claude/settings.local.json</c>, which Claude Code layers over the
    /// global file — but "." means the <i>Claude session's</i> project directory,
    /// which the bridge cannot know. A long-running bridge typically starts from its
    /// install directory and serves sessions in many repositories at once, so
    /// resolving that path against the bridge's own working directory would be
    /// meaningless at best and actively misleading at worst (claiming a repo-local
    /// file is authoritative when it belongs to an unrelated directory).</para>
    /// <para>The report therefore states the global values and flags that a
    /// repo-local override, if any, is outside what it can see. Doing this properly
    /// needs per-request client context, not startup state.</para>
    /// </remarks>
    /// <param name="settingsPath">Explicit file to read (tests). Defaults to the
    /// global settings file.</param>
    /// <param name="expectedBaseUrlSuffix">The route suffix that marks the file as
    /// pointed at a bridge — matching <see cref="ClaudeCodeConfigurator"/>'s own
    /// "points at bridge" test, so a config aimed at some other
    /// Anthropic-compatible endpoint reads as unrelated rather than as a
    /// misconfigured bridge client.</param>
    public static ClientTimeoutSnapshot Read(
        string? settingsPath = null, string expectedBaseUrlSuffix = "/cc") =>
        ReadFile(settingsPath ?? DefaultSettingsPath, expectedBaseUrlSuffix);

    private static ClientTimeoutSnapshot ReadFile(string path, string expectedBaseUrlSuffix)
    {
        string text;
        try
        {
            if (!File.Exists(path))
            {
                return Unknown(path, "settings file does not exist");
            }
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Unknown(path, $"settings file could not be read ({ex.GetType().Name})");
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is null)
        {
            return Unknown(path, "settings file is not a JSON object");
        }

        var env = root["env"] as JsonObject;

        // Same rule as the configurator's Read: key off the route suffix, not the
        // full URL, so a bridge on another port still counts as this bridge's client.
        var baseUrl = AsStringOrNull(env?["ANTHROPIC_BASE_URL"]);
        if (baseUrl is null || !baseUrl.TrimEnd('/').EndsWith(expectedBaseUrlSuffix, StringComparison.Ordinal))
        {
            return Unknown(path, "settings file is not pointed at this bridge");
        }

        // Which default applies in a key's ABSENCE depends on whether the request is
        // first-party. The bridge normally writes _CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL
        // (for the 1M window), which selects Claude Code's shorter 180 s first-party
        // idle bound. A hand-managed config that sets only ANTHROPIC_BASE_URL does NOT,
        // and falls back to the 300 s floor — assuming 180 s there would report a
        // bound that is not real and warn about a problem that does not exist.
        var assumeFirstParty = IsEnabled(
            AsStringOrNull(env?["_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL"]));
        var absentStreamIdleMs = assumeFirstParty
            ? ClaudeCodeTimeoutPolicy.AbsentStreamIdleFirstPartyDefaultMs
            : ClaudeCodeTimeoutPolicy.AbsentStreamIdleDefaultMs;

        return new ClientTimeoutSnapshot(
            path,
            Readable: true,
            Reason: null,
            StreamIdle: ValueOf(
                env, ClaudeCodeTimeoutPolicy.StreamIdleKey, absentStreamIdleMs,
                clientMaxMs: ClaudeCodeTimeoutPolicy.StreamIdleMaxMs),
            RequestTimeout: ValueOf(
                env, ClaudeCodeTimeoutPolicy.RequestTimeoutKey,
                ClaudeCodeTimeoutPolicy.AbsentRequestTimeoutDefaultMs));
    }

    /// <summary>
    /// Claude Code treats an env value as on when it is a non-empty, non-"0",
    /// non-"false" string (its own truthiness check), so mirror that rather than
    /// testing for exactly "1".
    /// </summary>
    private static bool IsEnabled(string? raw) =>
        !string.IsNullOrWhiteSpace(raw)
        && !string.Equals(raw.Trim(), "0", StringComparison.Ordinal)
        && !string.Equals(raw.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve one key: an explicit parseable value, else the built-in default
    /// that applies in its absence. A present-but-unparseable value (empty,
    /// non-numeric, non-positive, or the wrong JSON type) is treated as absent —
    /// Claude Code's own parse yields no usable number either, so its default is
    /// what actually applies.
    /// </summary>
    private static ClientTimeoutValue ValueOf(
        JsonObject? env, string key, int absentDefaultMs, int? clientMaxMs = null)
    {
        var raw = AsStringOrNull(env?[key]);
        // long, not int: a hand-edited value can exceed int.MaxValue, and parsing it
        // as absent would report the built-in default while the client honors the
        // (clamped) stored value.
        if (raw is not null
            && long.TryParse(raw.Trim(), out var parsed)
            && parsed > 0)
        {
            // Report what the client will ACTUALLY apply. Claude Code silently caps
            // this key, so echoing a larger stored number would overstate the bound —
            // e.g. a hand-managed 9999999 is really 1800000.
            var effective = clientMaxMs is { } max && parsed > max ? max : parsed;
            return new ClientTimeoutValue(key, (int)effective, IsExplicit: true);
        }

        return new ClientTimeoutValue(key, absentDefaultMs, IsExplicit: false);
    }

    private static ClientTimeoutSnapshot Unknown(string path, string reason) =>
        new(path,
            Readable: false,
            Reason: reason,
            StreamIdle: new ClientTimeoutValue(
                ClaudeCodeTimeoutPolicy.StreamIdleKey, EffectiveMs: null, IsExplicit: false),
            RequestTimeout: new ClientTimeoutValue(
                ClaudeCodeTimeoutPolicy.RequestTimeoutKey, EffectiveMs: null, IsExplicit: false));

    /// <summary>
    /// Read a node as a string, tolerating a hand-edited file where the value is
    /// a JSON number instead of the string Claude Code expects.
    /// </summary>
    private static string? AsStringOrNull(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<string>(out var s))
        {
            return s;
        }
        return value.TryGetValue<long>(out var n) ? n.ToString() : null;
    }
}
