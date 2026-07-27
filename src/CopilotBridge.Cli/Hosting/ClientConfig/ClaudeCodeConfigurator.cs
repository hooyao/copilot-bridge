using System.Globalization;
using System.Text.Encodings.Web;
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

    /// <summary>The legacy env key that disables Claude Code's
    /// streaming→non-streaming recovery. The bridge now removes it: cross-routed
    /// buffered Responses bodies are translated at the Claude edge.</summary>
    private const string FallbackKey = "CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK";

    /// <summary>
    /// Makes Claude Code treat the bridge base URL as first-party. Claude Code
    /// 2.1.216 decides the context window from a bundled model-capability table
    /// (<c>context.native_1m</c>, true for opus-4.6/4.7/4.8, sonnet-4.6, sonnet-5)
    /// gated on the request being first-party (<c>firstParty &amp;&amp; Wd()</c>);
    /// a custom base URL fails <c>Wd()</c> (host ≠ <c>api.anthropic.com</c>) and
    /// falls back to 200k — including after <c>--resume</c>. Asserting first-party
    /// lets the native-1M capability apply. Client-side signal only: inference
    /// traffic still targets the bridge base URL. See <c>docs/context-window.md</c>.
    /// </summary>
    private const string Assume1mKey = "_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL";

    /// <summary>
    /// Disables Claude Code's error-reporting (Datadog) telemetry. Written together
    /// with <see cref="Assume1mKey"/> because asserting first-party also flips that
    /// telemetry from off to on for a custom-base-URL user; keeping it off means
    /// enabling the 1M window changes exactly one thing.
    /// </summary>
    private const string DisableErrorReportingKey = "DISABLE_ERROR_REPORTING";

    /// <summary>The managed value both 1M-context env keys are force-written to.</summary>
    private const string ManagedFlagOn = "1";

    /// <summary>
    /// Claude Code's streaming-idle env key. Force-written from the bridge's own
    /// stream-idle budget so the client never aborts a healthy turn before the
    /// bridge's budget applies — the bridge, not the client, decides when a
    /// stalled turn ends. This is the ONLY knob that lifts both of Claude Code's
    /// idle watchdogs (measured against 2.1.220): the byte-level watchdog derives
    /// its budget from this value whenever its own key is unset, so setting the
    /// byte-level key alone leaves the event-level one pinned at its floor. See
    /// <c>docs/timeout-chain.md</c>.
    /// </summary>
    private const string StreamIdleTimeoutKey = ClaudeCodeTimeoutPolicy.StreamIdleKey;

    /// <summary>
    /// Claude Code's whole-request env key. Force-written from the bridge's
    /// first-byte budget. It also bounds each attempt of the non-streaming
    /// recovery request Claude Code issues after a streaming failure — the path
    /// that yields no bytes until the model has finished, so a deep-thinking turn
    /// needs it raised too.
    /// </summary>
    private const string RequestTimeoutKey = ClaudeCodeTimeoutPolicy.RequestTimeoutKey;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Relaxed escaping so PRESERVED user values keep their bytes: the default
        // (HTML-safe) encoder rewrites &, <, >, +, and all non-ASCII as \uXXXX, which
        // would silently mangle an unrelated key like a statusLine command containing
        // `&&` or a CJK/emoji value — breaking the surgical-preserve guarantee.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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
    /// <exception cref="ClientConfigException">The file is non-empty but is not
    /// parseable as a JSON object. Merging would silently discard the user's unrelated
    /// keys, so the merge refuses rather than overwrite — the caller aborts without
    /// touching the file.</exception>
    internal static (string Content, IReadOnlyList<string> Summary) BuildContent(
        string? original, BridgeConnection connection)
    {
        var root = ParseObjectOrThrow(original);
        var summary = MergeInto(root, connection);

        // Trailing newline: editors and git generally expect a final newline; keeps
        // re-runs byte-stable and diffs clean.
        var content = root.ToJsonString(WriteOptions) + "\n";
        return (content, summary);
    }

    public string? Apply(ConfigPlan plan) => ConfigFileWriter.Write(plan);

    public ConfigState Read(BridgeConnection connection, ConfigScope scope)
    {
        var path = ResolvePath(scope);
        var expected = connection.ClaudeCodeBaseUrl;

        if (!File.Exists(path))
        {
            return new ConfigState(ClientId, scope, path, Exists: false,
                ConfiguredForBridge: false, CurrentBaseUrl: null, ExpectedBaseUrl: expected,
                ExpectedFallback: null,
                CurrentFallback: null,
                ExpectedAssume1m: null, CurrentAssume1m: null,
                ExpectedDisableErrorReporting: null, CurrentDisableErrorReporting: null,
                ExpectedStreamIdleTimeout: null, CurrentStreamIdleTimeout: null,
                ExpectedRequestTimeout: null, CurrentRequestTimeout: null,
                Details: ["not configured (file does not exist)"]);
        }

        // Read must stay tolerant — `config status` should never crash on a malformed
        // file. Report it plainly instead of throwing.
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is null)
        {
            return new ConfigState(ClientId, scope, path, Exists: true,
                ConfiguredForBridge: false, CurrentBaseUrl: null, ExpectedBaseUrl: expected,
                ExpectedFallback: null,
                CurrentFallback: null,
                ExpectedAssume1m: null, CurrentAssume1m: null,
                ExpectedDisableErrorReporting: null, CurrentDisableErrorReporting: null,
                ExpectedStreamIdleTimeout: null, CurrentStreamIdleTimeout: null,
                ExpectedRequestTimeout: null, CurrentRequestTimeout: null,
                Details: ["file is not a JSON object (cannot read — run the config command to rewrite)"]);
        }

        var env = root["env"] as JsonObject;
        var current = AsStringOrNull(env?[BaseUrlKey]);
        var fallback = AsStringOrNull(env?[FallbackKey]);
        var assume1m = AsStringOrNull(env?[Assume1mKey]);
        var disableErrorReporting = AsStringOrNull(env?[DisableErrorReportingKey]);
        var streamIdleTimeout = AsStringOrNull(env?[StreamIdleTimeoutKey]);
        var requestTimeout = AsStringOrNull(env?[RequestTimeoutKey]);
        var expectedStreamIdle =
            connection.ClaudeCodeStreamIdleTimeoutMs.ToString(CultureInfo.InvariantCulture);
        var expectedRequestTimeout =
            connection.ClaudeCodeRequestTimeoutMs.ToString(CultureInfo.InvariantCulture);

        // "Configured for bridge" means the base URL points at THIS bridge's Claude Code
        // route (the `/cc` prefix), not merely that ANTHROPIC_BASE_URL is set — a config
        // aimed at some other Anthropic-compatible endpoint must read as "not pointed at
        // bridge", not as a drifted bridge config. The port/host may legitimately differ
        // (a bridge on another port is still a bridge, just drifted), so we key off the
        // route suffix rather than the full expected URL.
        var pointsAtBridge = current is not null && current.TrimEnd('/').EndsWith("/cc", StringComparison.Ordinal);

        var details = new List<string>();
        details.Add(current is null
            ? $"{BaseUrlKey}: (unset)"
            : $"{BaseUrlKey}: {current}");
        details.Add(fallback is null
            ? $"{FallbackKey}: (unset)"
            : $"{FallbackKey}: {fallback}");
        details.Add(assume1m is null
            ? $"{Assume1mKey}: (unset)"
            : $"{Assume1mKey}: {assume1m}");
        details.Add(disableErrorReporting is null
            ? $"{DisableErrorReportingKey}: (unset)"
            : $"{DisableErrorReportingKey}: {disableErrorReporting}");
        details.Add(streamIdleTimeout is null
            ? $"{StreamIdleTimeoutKey}: (unset)"
            : $"{StreamIdleTimeoutKey}: {streamIdleTimeout}");
        details.Add(requestTimeout is null
            ? $"{RequestTimeoutKey}: (unset)"
            : $"{RequestTimeoutKey}: {requestTimeout}");

        return new ConfigState(ClientId, scope, path, Exists: true,
            ConfiguredForBridge: pointsAtBridge, CurrentBaseUrl: pointsAtBridge ? current : null,
            ExpectedBaseUrl: expected,
            ExpectedFallback: null,
            CurrentFallback: pointsAtBridge ? fallback : null,
            // The bridge force-writes both 1M-context keys to "1"; a bridge-pointed
            // config missing either (or holding another value) is drift. Current values
            // are only meaningful when pointed at the bridge — a non-bridge config passes
            // null current so it never counts as drift.
            ExpectedAssume1m: ManagedFlagOn,
            CurrentAssume1m: pointsAtBridge ? assume1m : null,
            ExpectedDisableErrorReporting: ManagedFlagOn,
            CurrentDisableErrorReporting: pointsAtBridge ? disableErrorReporting : null,
            // Same rule for the timeout keys, except the expected value is DERIVED from
            // the current budgets — so raising a budget without re-running the config
            // command reads as drift, which is exactly when the client has stopped
            // outlasting the bridge.
            ExpectedStreamIdleTimeout: expectedStreamIdle,
            CurrentStreamIdleTimeout: pointsAtBridge ? streamIdleTimeout : null,
            ExpectedRequestTimeout: expectedRequestTimeout,
            CurrentRequestTimeout: pointsAtBridge ? requestTimeout : null,
            Details: details);
    }

    /// <summary>
    /// Read a JSON node as a string, or <c>null</c> if it is absent or not a string.
    /// Tolerant by design: a hand-edited file where an env value is a number/bool (e.g.
    /// <c>"CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK": 1</c> instead of the string
    /// <c>"1"</c>) must not crash <c>config status</c> —
    /// <see cref="JsonNode.GetValue{T}"/> would throw
    /// <see cref="System.InvalidOperationException"/> on a type mismatch.
    /// </summary>
    private static string? AsStringOrNull(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

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

        // 1M-context unlock — force-written as a consistent pair (see the key docs).
        // ASSUME_FIRST_PARTY makes Claude Code's native-1M capability gate fire for
        // the bridge base URL; DISABLE_ERROR_REPORTING neutralizes the telemetry
        // that first-party assertion would otherwise enable.
        env[Assume1mKey] = ManagedFlagOn;
        summary.Add($"set env.{Assume1mKey} = {ManagedFlagOn} (1M context on native-1M models, survives --resume)");
        env[DisableErrorReportingKey] = ManagedFlagOn;
        summary.Add($"set env.{DisableErrorReportingKey} = {ManagedFlagOn} (keep error-reporting telemetry off)");

        // Long-thinking timeouts — force-written from the bridge's own budgets so the
        // client outlasts them. Copilot sends no keepalive while a model thinks, so a
        // deep-thinking turn is legitimately silent for minutes; without these the
        // client aborts a healthy stream and the bridge's budget never applies.
        // Takes effect on Claude Code's NEXT start (it reads env at process start).
        var streamIdle = connection.ClaudeCodeStreamIdleTimeoutMs.ToString(CultureInfo.InvariantCulture);
        env[StreamIdleTimeoutKey] = streamIdle;
        summary.Add(
            $"set env.{StreamIdleTimeoutKey} = {streamIdle} "
            + "(outlasts the bridge stream-idle budget; effective on Claude Code's next start)");
        var requestTimeout = connection.ClaudeCodeRequestTimeoutMs.ToString(CultureInfo.InvariantCulture);
        env[RequestTimeoutKey] = requestTimeout;
        summary.Add(
            $"set env.{RequestTimeoutKey} = {requestTimeout} "
            + "(outlasts the bridge first-byte budget, incl. the non-streaming recovery request)");

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

        // Always remove the legacy disable switch. Claude Code's recovery from a
        // mid-stream SSE error is a stream:false fallback request; disabling it
        // turns the fault into a terminal API error instead of continuing the turn.
        if (env.ContainsKey(FallbackKey))
        {
            env.Remove(FallbackKey);
            summary.Add($"removed env.{FallbackKey} (bridge supports non-streaming recovery)");
        }

        return summary;
    }

    /// <summary>
    /// Parse existing JSON into a mutable object. An empty/whitespace file (or null,
    /// meaning the file does not exist) yields a fresh object. A <b>non-empty</b> file
    /// that is not a JSON object — invalid JSON (e.g. a JSONC <c>//</c> comment or
    /// trailing comma, which <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/>
    /// rejects) or a JSON value that is not an object — throws
    /// <see cref="ClientConfigException"/> rather than returning empty. Returning empty
    /// would silently discard the user's unrelated keys and violate the surgical-merge
    /// guarantee, so the write path aborts instead of overwriting.
    /// </summary>
    private static JsonObject ParseObjectOrThrow(string? content)
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
        catch (JsonException ex)
        {
            throw new ClientConfigException(
                "Existing settings file is not valid JSON (note: JSON does not allow " +
                "comments or trailing commas). Refusing to overwrite it so your other " +
                $"settings are not lost. Fix or remove the file, then re-run. Parser said: {ex.Message}");
        }

        return node as JsonObject
            ?? throw new ClientConfigException(
                "Existing settings file is valid JSON but not a JSON object. Refusing to " +
                "overwrite it so your other settings are not lost. Fix or remove the file, then re-run.");
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
