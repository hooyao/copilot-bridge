using Tomlyn.Syntax;

namespace CopilotBridge.Cli.Hosting.ClientConfig;

/// <summary>Source-confirmed timeout facts for the native Codex client.</summary>
internal static class CodexTimeoutPolicy
{
    /// <summary>Provider key consumed by <c>ModelProviderInfo</c>.</summary>
    public const string StreamIdleKey = "stream_idle_timeout_ms";

    /// <summary>
    /// Codex 0.144.1 and current <c>openai/codex</c> main both use 300,000 ms when
    /// <see cref="StreamIdleKey"/> is absent. The timer wraps each parsed SSE
    /// <c>stream.next()</c>, not the raw socket read.
    /// </summary>
    public const int DefaultStreamIdleTimeoutMs = 300_000;
}

/// <summary>What startup could learn about the active Codex provider's idle bound.</summary>
internal sealed record CodexTimeoutSnapshot(
    string ConfigPath,
    bool Readable,
    string? Reason,
    ClientTimeoutValue StreamIdle);

/// <summary>
/// Best-effort reader for Codex's global <c>config.toml</c>. It never writes and
/// never throws: the bridge can serve clients configured by another mechanism.
/// </summary>
internal static class CodexTimeoutReader
{
    private const string ManagedProvider = "copilot-bridge";
    private const string ModelProviderKey = "model_provider";
    private const string ProviderTable = "model_providers." + ManagedProvider;

    public static string DefaultConfigPath
    {
        get
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(codexHome))
            {
                codexHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            }
            return Path.Combine(codexHome, "config.toml");
        }
    }

    public static CodexTimeoutSnapshot Read(string? pathOverride = null)
    {
        var path = pathOverride ?? DefaultConfigPath;
        try
        {
            if (!File.Exists(path))
                return Unknown(path, "global Codex config does not exist");

            var doc = Tomlyn.Parsing.SyntaxParser.Parse(File.ReadAllText(path), path);
            if (doc.HasErrors)
                return Unknown(path, "global Codex config has TOML syntax errors");

            var provider = FindTopLevelString(doc, ModelProviderKey);
            if (!string.Equals(provider, ManagedProvider, StringComparison.Ordinal))
            {
                return Unknown(
                    path,
                    provider is null
                        ? "global Codex model_provider is unset"
                        : $"global Codex model_provider is {provider}, not {ManagedProvider}");
            }

            var table = FindTable(doc, ProviderTable);
            if (table is null)
                return Unknown(path, $"[{ProviderTable}] is missing");

            var explicitMs = FindTableInteger(table, CodexTimeoutPolicy.StreamIdleKey);
            if (explicitMs is null)
            {
                return new CodexTimeoutSnapshot(
                    path,
                    Readable: true,
                    Reason: null,
                    StreamIdle: new ClientTimeoutValue(
                        CodexTimeoutPolicy.StreamIdleKey,
                        CodexTimeoutPolicy.DefaultStreamIdleTimeoutMs,
                        IsExplicit: false));
            }

            if (explicitMs < 0)
                return Unknown(path, $"{CodexTimeoutPolicy.StreamIdleKey} is negative");

            return new CodexTimeoutSnapshot(
                path,
                Readable: true,
                Reason: null,
                StreamIdle: new ClientTimeoutValue(
                    CodexTimeoutPolicy.StreamIdleKey,
                    explicitMs.Value,
                    IsExplicit: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unknown(path, $"global Codex config could not be read ({ex.GetType().Name})");
        }
    }

    private static string? FindTopLevelString(DocumentSyntax doc, string key)
    {
        foreach (var item in doc.KeyValues)
        {
            if (item.Key?.ToString().Trim() == key && item.Value is StringValueSyntax value)
                return value.Value;
        }
        return null;
    }

    private static TableSyntax? FindTable(DocumentSyntax doc, string name)
    {
        for (var i = 0; i < doc.Tables.ChildrenCount; i++)
        {
            if (doc.Tables.GetChild(i) is TableSyntax table
                && table.Name?.ToString().Trim() == name)
                return table;
        }
        return null;
    }

    private static long? FindTableInteger(TableSyntax table, string key)
    {
        foreach (var item in table.Items)
        {
            if (item is KeyValueSyntax pair
                && pair.Key?.ToString().Trim() == key
                && pair.Value is IntegerValueSyntax value)
                return value.Value;
        }
        return null;
    }

    private static CodexTimeoutSnapshot Unknown(string path, string reason) =>
        new(
            path,
            Readable: false,
            Reason: reason,
            StreamIdle: new ClientTimeoutValue(
                CodexTimeoutPolicy.StreamIdleKey,
                EffectiveMs: null,
                IsExplicit: false));
}
