using System.Globalization;
using System.Text;
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
    private const string ProviderAuthTableName = ProviderTableName + ".auth";
    private const string WireApi = "responses";

    public string ClientId => "codex";

    public IReadOnlyList<ConfigScope> SupportedScopes { get; } = [ConfigScope.Global];

    public ConfigPlan Plan(BridgeConnection connection, ConfigScope scope)
    {
        var path = ResolvePath();
        var original = File.Exists(path) ? File.ReadAllText(path) : null;

        var (newContent, summary) = BuildContent(
            original, connection, CodexProviderAuthInvocation.ResolveCurrent(), path);

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
        string? original, BridgeConnection connection, string sourcePath = "config.toml") =>
        BuildContent(original, connection, CodexProviderAuthInvocation.ResolveCurrent(), sourcePath);

    internal static (string Content, IReadOnlyList<string> Summary) BuildContent(
        string? original,
        BridgeConnection connection,
        CodexProviderAuthInvocation authInvocation,
        string sourcePath = "config.toml")
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

        var summary = MergeInto(doc, connection, authInvocation);
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
                Details: ["file has TOML syntax errors (cannot read — fix or remove it, then re-run)"]);
        }

        var provider = FindTopLevelString(doc, ModelProviderKey);
        var providerTable = FindTable(doc, ProviderTableName);
        var authTable = FindTable(doc, ProviderAuthTableName);
        var baseUrl = FindTableString(providerTable, "base_url");
        var expectedInvocation = CodexProviderAuthInvocation.ResolveCurrent();

        var configured = provider == ProviderName;
        var drift = new List<string>();
        if (configured)
        {
            if (!string.Equals(FindTableString(providerTable, "name"), ProviderName, StringComparison.Ordinal))
                drift.Add("provider name differs from the managed value");
            if (!string.Equals(FindTableString(providerTable, "wire_api"), WireApi, StringComparison.Ordinal))
                drift.Add("provider wire API differs from the managed value");
            if (!string.Equals(FindTableString(authTable, "command"), expectedInvocation.Command, StringComparison.Ordinal))
                drift.Add("discovery-auth command is missing or stale");
            if (!SequenceEqual(FindTableStringArray(authTable, "args"), expectedInvocation.Args))
                drift.Add("discovery-auth arguments are missing or stale");
            if (FindTableInteger(authTable, "timeout_ms") != CodexProviderAuthInvocation.TimeoutMs)
                drift.Add("discovery-auth timeout is missing or stale");
            if (FindTableInteger(authTable, "refresh_interval_ms") != CodexProviderAuthInvocation.RefreshIntervalMs)
                drift.Add("discovery-auth refresh policy is missing or stale");
        }
        var details = new List<string>
        {
            $"{ModelProviderKey} = {provider ?? "(unset)"}",
            $"[{ProviderTableName}].base_url = {baseUrl ?? "(unset)"}",
            $"[{ProviderTableName}].stream_idle_timeout_ms = {FindTableValueDisplay(providerTable, "stream_idle_timeout_ms") ?? "(unset)"}",
            $"[{ProviderTableName}].request_max_retries = {FindTableValueDisplay(providerTable, "request_max_retries") ?? "(unset)"}",
            $"[{ProviderTableName}].stream_max_retries = {FindTableValueDisplay(providerTable, "stream_max_retries") ?? "(unset)"}",
            $"[{ProviderAuthTableName}] = {(authTable is null ? "missing" : "present")}",
        };

        return new ConfigState(ClientId, scope, path, Exists: true,
            ConfiguredForBridge: configured, CurrentBaseUrl: configured ? baseUrl : null,
            ExpectedBaseUrl: expected,
            Details: details,
            AdditionalDriftFacts: drift);
    }

    /// <summary>
    /// Apply the two managed edits to the document, preserving everything else.
    /// Returns human-readable summary lines for <c>--dry-run</c>.
    /// </summary>
    private static IReadOnlyList<string> MergeInto(
        DocumentSyntax doc,
        BridgeConnection connection,
        CodexProviderAuthInvocation authInvocation)
    {
        var summary = new List<string>();

        // Region 1: top-level model_provider pointer (before the first table).
        UpsertTopLevelString(doc, ModelProviderKey, ProviderName);
        summary.Add($"set {ModelProviderKey} = \"{ProviderName}\"");

        // Region 2: surgically upsert only bridge-owned connection/auth fields.
        UpsertProviderTable(doc, connection.CodexBaseUrl, authInvocation);
        summary.Add($"upsert [{ProviderTableName}].name = \"{ProviderName}\"");
        summary.Add($"upsert [{ProviderTableName}].base_url = \"{connection.CodexBaseUrl}\"");
        summary.Add($"upsert [{ProviderTableName}].wire_api = \"{WireApi}\"");
        summary.Add($"upsert [{ProviderAuthTableName}].command = {TomlBasicString(authInvocation.Command)}");
        var renderedArgs = string.Join(", ", authInvocation.Args.Select(TomlBasicString));
        summary.Add($"upsert [{ProviderAuthTableName}].args = [ {renderedArgs} ]");
        summary.Add($"upsert [{ProviderAuthTableName}].timeout_ms = {CodexProviderAuthInvocation.TimeoutMs}");
        summary.Add($"upsert [{ProviderAuthTableName}].refresh_interval_ms = {CodexProviderAuthInvocation.RefreshIntervalMs}");
        summary.Add("preserved client-owned provider timeout, retry, transport, query, and header fields");

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
    /// Create the bridge provider/auth tables when absent and otherwise replace only
    /// bridge-owned values in place. Every user-owned item in those tables survives.
    /// </summary>
    /// <remarks>
    /// Values and new key-value nodes are lifted from parsed TOML fragments rather than
    /// constructed by hand, preserving valid trivia under Native AOT.
    /// </remarks>
    private static void UpsertProviderTable(
        DocumentSyntax doc,
        string baseUrl,
        CodexProviderAuthInvocation authInvocation)
    {
        var providerTable = FindTable(doc, ProviderTableName)
            ?? AppendTable(doc, ProviderTableName);
        var authTable = FindTable(doc, ProviderAuthTableName)
            ?? AppendTable(doc, ProviderAuthTableName);
        var args = string.Join(", ", authInvocation.Args.Select(TomlBasicString));
        UpsertTableValue(providerTable, "name", TomlBasicString(ProviderName));
        UpsertTableValue(providerTable, "base_url", TomlBasicString(baseUrl));
        UpsertTableValue(providerTable, "wire_api", TomlBasicString(WireApi));
        UpsertTableValue(authTable, "command", TomlBasicString(authInvocation.Command));
        UpsertTableValue(authTable, "args", $"[ {args} ]");
        UpsertTableValue(authTable, "timeout_ms", CodexProviderAuthInvocation.TimeoutMs.ToString(CultureInfo.InvariantCulture));
        UpsertTableValue(authTable, "refresh_interval_ms", CodexProviderAuthInvocation.RefreshIntervalMs.ToString(CultureInfo.InvariantCulture));
    }

    private static TableSyntax AppendTable(DocumentSyntax doc, string tableName)
    {
        var fragment = Tomlyn.Parsing.SyntaxParser.Parse($"\n[{tableName}]\n", "fragment");
        if (fragment.HasErrors || fragment.Tables.GetChild(0) is not TableSyntax table)
            throw new InvalidOperationException($"Could not construct TOML table [{tableName}].");
        fragment.Tables.RemoveChildAt(0);
        doc.Tables.Add(table);
        return table;
    }

    private static void UpsertTableValue(TableSyntax table, string key, string valueSyntax)
    {
        var fragment = Tomlyn.Parsing.SyntaxParser.Parse($"[x]\n{key} = {valueSyntax}\n", "fragment");
        if (fragment.HasErrors || fragment.Tables.GetChild(0) is not TableSyntax fragmentTable
            || fragmentTable.Items.GetChild(0) is not KeyValueSyntax fragmentKv)
            throw new InvalidOperationException($"Could not construct TOML key {key}.");

        foreach (var item in table.Items)
        {
            if (item is not KeyValueSyntax existing || KeyName(existing.Key) != key) continue;
            var value = fragmentKv.Value!;
            fragmentKv.Value = null;
            existing.Value = value;
            return;
        }

        fragmentTable.Items.RemoveChildAt(0);
        table.Items.Add(fragmentKv);
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

    private static TableSyntax? FindTable(DocumentSyntax doc, string name)
    {
        for (var i = 0; i < doc.Tables.ChildrenCount; i++)
        {
            if (doc.Tables.GetChild(i) is TableSyntax table && TableName(table) == name)
            {
                return table;
            }
        }
        return null;
    }

    private static string? FindTableString(TableSyntax? table, string key)
    {
        if (table is null) return null;
        foreach (var item in table.Items)
        {
            if (item is KeyValueSyntax kv && KeyName(kv.Key) == key
                && kv.Value is StringValueSyntax value)
                return value.Value;
        }
        return null;
    }

    private static long? FindTableInteger(TableSyntax? table, string key)
    {
        if (table is null) return null;
        foreach (var item in table.Items)
        {
            if (item is KeyValueSyntax kv && KeyName(kv.Key) == key
                && kv.Value is IntegerValueSyntax value)
                return value.Value;
        }
        return null;
    }

    private static string? FindTableValueDisplay(TableSyntax? table, string key)
    {
        if (table is null) return null;
        foreach (var item in table.Items)
        {
            if (item is KeyValueSyntax kv && KeyName(kv.Key) == key)
                return kv.Value?.ToString().Trim();
        }
        return null;
    }

    private static IReadOnlyList<string>? FindTableStringArray(TableSyntax? table, string key)
    {
        if (table is null) return null;
        foreach (var item in table.Items)
        {
            if (item is not KeyValueSyntax kv || KeyName(kv.Key) != key
                || kv.Value is not ArraySyntax array)
                continue;

            var values = new List<string>();
            foreach (var arrayItem in array.Items)
            {
                if (arrayItem.Value is not StringValueSyntax value) return null;
                if (value.Value is not { } text) return null;
                values.Add(text);
            }
            return values;
        }
        return null;
    }

    private static bool SequenceEqual(IReadOnlyList<string>? actual, IReadOnlyList<string> expected) =>
        actual is not null && actual.SequenceEqual(expected, StringComparer.Ordinal);

    private static string TomlBasicString(string value)
    {
        var result = new StringBuilder(value.Length + 2).Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\b': result.Append("\\b"); break;
                case '\t': result.Append("\\t"); break;
                case '\n': result.Append("\\n"); break;
                case '\f': result.Append("\\f"); break;
                case '\r': result.Append("\\r"); break;
                default:
                    if (ch < 0x20 || ch == 0x7f)
                        result.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        result.Append(ch);
                    break;
            }
        }
        return result.Append('"').ToString();
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
