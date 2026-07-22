using Tomlyn.Syntax;

namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>
/// Configures Codex to point at the bridge by editing <c>$CODEX_HOME/config.toml</c>
/// (default <c>~/.codex/config.toml</c>). Codex honors global scope only.
/// </summary>
/// <remarks>
/// <para>The merge is surgical and edits Tomlyn's <b>trivia-preserving syntax tree</b>
/// (<see cref="DocumentSyntax"/>) — NOT the model DOM (<c>TomlTable</c> /
/// <c>TomlModelSerializer</c>), which discards comments and formatting and would
/// rewrite the user's dense real config. Every table, comment, whitespace region, and
/// literal the bridge does not manage is preserved byte-for-byte.</para>
/// <para>The edit touches exactly two regions: (1) the top-level
/// <c>model_provider</c> key-value; (2) the single named table
/// <c>[model_providers.copilot-bridge]</c>. A pre-existing rival provider block (e.g.
/// <c>[model_providers.agent-maestro]</c>) is left intact so switching back is a
/// one-line pointer change.</para>
/// </remarks>
internal sealed class CodexConfigurator : IClientConfigurator
{
    private const string ProviderName = "copilot-bridge";
    private const string ModelProviderKey = "model_provider";
    private const string ProviderTableName = "model_providers.copilot-bridge";
    private const string WireApi = "responses";

    public string ClientId => "codex";

    public IReadOnlyList<ConfigScope> SupportedScopes { get; } = [ConfigScope.Global];

    public ConfigPlan Plan(BridgeConnection connection, ConfigScope scope)
    {
        var path = ResolvePath();
        var original = File.Exists(path) ? File.ReadAllText(path) : null;

        var (newContent, summary) = BuildContent(original, connection, path);

        return new ConfigPlan(ClientId, scope, path, newContent, original, summary);
    }

    /// <summary>
    /// The pure merge: parse the current content (or empty when the file does not
    /// exist) into a trivia-preserving syntax tree, apply the two managed edits, and
    /// render. No filesystem access — the seam contract tests exercise directly.
    /// <paramref name="sourcePath"/> is only used for parser diagnostics.
    /// </summary>
    /// <exception cref="ClientConfigException">The existing file is non-empty but has
    /// TOML parse errors. Editing an error-laden tree could drop or corrupt the user's
    /// unrelated content, so the merge refuses rather than risk it — the caller aborts
    /// without touching the file.</exception>
    internal static (string Content, IReadOnlyList<string> Summary) BuildContent(
        string? original, BridgeConnection connection, string sourcePath = "config.toml")
    {
        var doc = Tomlyn.Parsing.SyntaxParser.Parse(original ?? string.Empty, sourcePath);
        if (!string.IsNullOrWhiteSpace(original) && doc.HasErrors)
        {
            var first = doc.Diagnostics.Count > 0 ? doc.Diagnostics[0].ToString() : "unknown error";
            throw new ClientConfigException(
                "Existing config.toml has TOML syntax errors. Refusing to edit it so your " +
                $"other settings are not lost. Fix or remove the file, then re-run. Parser said: {first}");
        }

        // Normalize the trailing newline BEFORE any node surgery: appending a top-level
        // key or table onto a document whose last element has no trailing newline would
        // glue the new node onto that final line (e.g. `sandbox = "x"[model_providers…]`),
        // producing invalid TOML. Guaranteeing every existing element ends in a newline
        // makes the append unconditionally safe. Idempotent: our own writes already end
        // in a newline, so a re-run re-parses an already-normalized document.
        if (!string.IsNullOrEmpty(original) && !original.EndsWith('\n'))
        {
            doc = Tomlyn.Parsing.SyntaxParser.Parse(original + "\n", sourcePath);
        }

        var summary = MergeInto(doc, connection);
        return (doc.ToString(), summary);
    }

    public string? Apply(ConfigPlan plan) => ConfigFileWriter.Write(plan);

    public ConfigState Read(BridgeConnection connection, ConfigScope scope)
    {
        var path = ResolvePath();
        var expected = connection.CodexBaseUrl;

        if (!File.Exists(path))
        {
            return new ConfigState(ClientId, scope, path, Exists: false,
                ConfiguredForBridge: false, CurrentBaseUrl: null, ExpectedBaseUrl: expected,
                ExpectedFallback: null, CurrentFallback: null,
                ExpectedAssume1m: null, CurrentAssume1m: null,
                ExpectedDisableErrorReporting: null, CurrentDisableErrorReporting: null,
                Details: ["not configured (file does not exist)"]);
        }

        var doc = Tomlyn.Parsing.SyntaxParser.Parse(File.ReadAllText(path), path);

        // Report a malformed file plainly instead of walking a partial tree and
        // misreporting it as "not configured" — mirrors ClaudeCodeConfigurator.Read and
        // matches what BuildContent would refuse to edit.
        if (doc.HasErrors)
        {
            return new ConfigState(ClientId, scope, path, Exists: true,
                ConfiguredForBridge: false, CurrentBaseUrl: null, ExpectedBaseUrl: expected,
                ExpectedFallback: null, CurrentFallback: null,
                ExpectedAssume1m: null, CurrentAssume1m: null,
                ExpectedDisableErrorReporting: null, CurrentDisableErrorReporting: null,
                Details: ["file has TOML syntax errors (cannot read — fix or remove it, then re-run)"]);
        }

        var provider = FindTopLevelString(doc, ModelProviderKey);
        var baseUrl = FindProviderBaseUrl(doc);

        var configured = provider == ProviderName;
        var details = new List<string>
        {
            $"{ModelProviderKey} = {provider ?? "(unset)"}",
            $"[{ProviderTableName}].base_url = {baseUrl ?? "(unset)"}",
        };

        return new ConfigState(ClientId, scope, path, Exists: true,
            ConfiguredForBridge: configured, CurrentBaseUrl: configured ? baseUrl : null,
            ExpectedBaseUrl: expected, ExpectedFallback: null, CurrentFallback: null,
            ExpectedAssume1m: null, CurrentAssume1m: null,
            ExpectedDisableErrorReporting: null, CurrentDisableErrorReporting: null,
            Details: details);
    }

    /// <summary>
    /// Apply the two managed edits to the document, preserving everything else.
    /// Returns human-readable summary lines for <c>--dry-run</c>.
    /// </summary>
    private static IReadOnlyList<string> MergeInto(DocumentSyntax doc, BridgeConnection connection)
    {
        var summary = new List<string>();

        // Region 1: top-level model_provider pointer (before the first table).
        UpsertTopLevelString(doc, ModelProviderKey, ProviderName);
        summary.Add($"set {ModelProviderKey} = \"{ProviderName}\"");

        // Region 2: the named provider table, replaced by name if it already exists.
        UpsertProviderTable(doc, connection.CodexBaseUrl);
        summary.Add($"set [{ProviderTableName}] base_url = \"{connection.CodexBaseUrl}\", wire_api = \"{WireApi}\"");

        return summary;
    }

    /// <summary>
    /// Create or replace a top-level (pre-first-table) string key-value. TOML requires
    /// top-level keys to precede the first table; <see cref="DocumentSyntax"/> renders
    /// every entry in <see cref="DocumentSyntax.KeyValues"/> before any table.
    /// </summary>
    /// <remarks>
    /// When the key already exists its <b>value node is replaced in place</b> so the
    /// key keeps its original position and surrounding trivia (only the string value
    /// changes). New nodes are produced by parsing a well-formed TOML fragment and
    /// lifting the node, rather than hand-constructing — a hand-built node carries no
    /// trivia (no <c>=</c> spacing, no newline) and renders as malformed TOML.
    /// </remarks>
    private static void UpsertTopLevelString(DocumentSyntax doc, string key, string value)
    {
        var fragment = Tomlyn.Parsing.SyntaxParser.Parse($"{key} = \"{value}\"\n", "fragment");
        var fragmentKv = (KeyValueSyntax)fragment.KeyValues.GetChild(0)!;

        foreach (var kv in doc.KeyValues)
        {
            if (KeyName(kv.Key) == key)
            {
                // Replace only the value, preserving the key's position and trivia.
                var newValue = fragmentKv.Value!;
                fragmentKv.Value = null;
                kv.Value = newValue;
                return;
            }
        }

        // Not present: detach the whole key-value from the fragment and append.
        fragment.KeyValues.RemoveChildAt(0);
        doc.KeyValues.Add(fragmentKv);
    }

    /// <summary>
    /// Create or replace the <c>[model_providers.copilot-bridge]</c> table, matched by
    /// name. Other tables (including a rival provider block) are untouched. The new
    /// table is parsed from a well-formed TOML fragment so its header (dotted, unquoted
    /// key) and trivia are correct.
    /// </summary>
    /// <remarks>
    /// The leading blank line is only added when <b>appending a fresh</b> table (so it
    /// does not collide with the previous table's final line). When <b>replacing</b> an
    /// existing block the new table inherits the leading trivia already present in the
    /// document, so no extra newline is added — this is what makes a re-run byte-stable
    /// (idempotent): a leading newline on replace would accumulate a blank line each run.
    /// </remarks>
    private static void UpsertProviderTable(DocumentSyntax doc, string baseUrl)
    {
        var existingIndex = -1;
        for (var i = 0; i < doc.Tables.ChildrenCount; i++)
        {
            if (doc.Tables.GetChild(i) is TableSyntax existing && TableName(existing) == ProviderTableName)
            {
                existingIndex = i;
                break;
            }
        }

        var leading = existingIndex >= 0 ? string.Empty : "\n";
        var fragment = Tomlyn.Parsing.SyntaxParser.Parse(
            $"{leading}[{ProviderTableName}]\nname = \"{ProviderName}\"\nbase_url = \"{baseUrl}\"\nwire_api = \"{WireApi}\"\n",
            "fragment");
        var newTable = fragment.Tables.GetChild(0)!;
        fragment.Tables.RemoveChildAt(0);

        if (existingIndex >= 0)
        {
            doc.Tables.RemoveChildAt(existingIndex);
        }

        doc.Tables.Add(newTable);
    }

    private static string? FindTopLevelString(DocumentSyntax doc, string key)
    {
        foreach (var kv in doc.KeyValues)
        {
            if (KeyName(kv.Key) == key && kv.Value is StringValueSyntax sv)
            {
                return sv.Value;
            }
        }
        return null;
    }

    private static string? FindProviderBaseUrl(DocumentSyntax doc)
    {
        for (var i = 0; i < doc.Tables.ChildrenCount; i++)
        {
            if (doc.Tables.GetChild(i) is TableSyntax t && TableName(t) == ProviderTableName)
            {
                foreach (var item in t.Items)
                {
                    if (item is KeyValueSyntax kv && KeyName(kv.Key) == "base_url"
                        && kv.Value is StringValueSyntax sv)
                    {
                        return sv.Value;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>The dotted-key name of a key-value's key, e.g. <c>model_provider</c>.</summary>
    private static string KeyName(KeySyntax? key) => key?.ToString().Trim() ?? string.Empty;

    /// <summary>The dotted-key name of a table header, e.g.
    /// <c>model_providers.copilot-bridge</c>.</summary>
    private static string TableName(TableSyntax table) => table.Name?.ToString().Trim() ?? string.Empty;

    /// <summary>
    /// Resolve <c>$CODEX_HOME/config.toml</c>, falling back to <c>~/.codex/config.toml</c>.
    /// </summary>
    private static string ResolvePath()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }
        return Path.Combine(home, "config.toml");
    }
}
