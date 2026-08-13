using Tomlyn.Syntax;

namespace CopilotBridge.Cli.Hosting.ClientConfig;

internal static class CodexTimeoutPolicy
{
    public const string StreamIdleKey = "stream_idle_timeout_ms";
    public const string RequestRetriesKey = "request_max_retries";
    public const string StreamRetriesKey = "stream_max_retries";
    // Official Configuration Reference:
    // https://learn.chatgpt.com/docs/config-file/config-reference
    public const int DefaultStreamIdleTimeoutMs = 300_000;
    public const int DefaultRequestRetries = 4;
    public const int DefaultStreamRetries = 5;
}

internal sealed record CodexTimeoutSnapshot(
    string ConfigPath,
    bool Readable,
    string? Reason,
    ClientDurationValue EventIdle,
    ClientCountValue RequestRetries,
    ClientCountValue StreamRetries);

/// <summary>Best-effort global Codex provider reader. Project/profile/CLI layers are out of scope.</summary>
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
                codexHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return Path.Combine(codexHome, "config.toml");
        }
    }

    public static CodexTimeoutSnapshot Read(
        string? pathOverride = null,
        string expectedBaseUrlSuffix = "/codex")
    {
        var path = pathOverride ?? DefaultConfigPath;
        try
        {
            if (!File.Exists(path)) return Unknown(path, "global Codex config does not exist");

            var doc = Tomlyn.Parsing.SyntaxParser.Parse(File.ReadAllText(path), path);
            if (doc.HasErrors) return Unknown(path, "global Codex config has TOML syntax errors");

            var provider = FindTopLevelString(doc, ModelProviderKey);
            if (!string.Equals(provider, ManagedProvider, StringComparison.Ordinal))
                return Unknown(
                    path,
                    provider is null
                        ? "global Codex model_provider is unset"
                        : $"global Codex model_provider is {provider}, not {ManagedProvider}");

            var table = FindTable(doc, ProviderTable);
            if (table is null) return Unknown(path, $"[{ProviderTable}] is missing");

            var providerName = FindTableString(table, "name");
            if (!string.Equals(providerName, ManagedProvider, StringComparison.Ordinal))
                return Unknown(
                    path,
                    providerName is null
                        ? $"[{ProviderTable}].name is missing"
                        : $"[{ProviderTable}].name is {providerName}, not {ManagedProvider}");

            var baseUrl = FindTableString(table, "base_url");
            if (baseUrl is null
                || !baseUrl.TrimEnd('/').EndsWith(expectedBaseUrlSuffix, StringComparison.Ordinal))
                return Unknown(path, $"[{ProviderTable}] is not pointed at this bridge");

            if (!string.Equals(FindTableString(table, "wire_api"), "responses", StringComparison.Ordinal))
                return Unknown(path, $"[{ProviderTable}].wire_api is not responses");

            return new CodexTimeoutSnapshot(
                path,
                Readable: true,
                Reason: null,
                EventIdle: ReadDuration(
                    table,
                    CodexTimeoutPolicy.StreamIdleKey,
                    CodexTimeoutPolicy.DefaultStreamIdleTimeoutMs),
                RequestRetries: ReadCount(
                    table,
                    CodexTimeoutPolicy.RequestRetriesKey,
                    CodexTimeoutPolicy.DefaultRequestRetries),
                StreamRetries: ReadCount(
                    table,
                    CodexTimeoutPolicy.StreamRetriesKey,
                    CodexTimeoutPolicy.DefaultStreamRetries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Unknown(path, $"global Codex config could not be read ({ex.GetType().Name})");
        }
    }

    private static ClientDurationValue ReadDuration(TableSyntax table, string key, long defaultMs)
    {
        var pair = FindTablePair(table, key);
        if (pair is null)
            return new(key, null, null, defaultMs, ClientValueSource.BuiltIn);
        if (pair.Value is not IntegerValueSyntax integer)
            return new(key, pair.Value?.ToString(), null, null, ClientValueSource.Invalid, "not an integer");
        var value = integer.Value;
        if (value < 0)
            return new(key, value.ToString(), null, null, ClientValueSource.Invalid, "negative value");
        return new(key, value.ToString(), value, value, ClientValueSource.Explicit);
    }

    private static ClientCountValue ReadCount(TableSyntax table, string key, long defaultValue)
    {
        var pair = FindTablePair(table, key);
        if (pair is null)
            return new(key, null, defaultValue, ClientValueSource.BuiltIn);
        if (pair.Value is not IntegerValueSyntax integer)
            return new(key, pair.Value?.ToString(), null, ClientValueSource.Invalid, "not an integer");
        var value = integer.Value;
        if (value < 0)
            return new(key, value.ToString(), null, ClientValueSource.Invalid, "negative value");
        return new(key, value.ToString(), value, ClientValueSource.Explicit);
    }

    private static string? FindTopLevelString(DocumentSyntax doc, string key)
    {
        foreach (var item in doc.KeyValues)
            if (item.Key?.ToString().Trim() == key && item.Value is StringValueSyntax value)
                return value.Value;
        return null;
    }

    private static TableSyntax? FindTable(DocumentSyntax doc, string name)
    {
        for (var i = 0; i < doc.Tables.ChildrenCount; i++)
            if (doc.Tables.GetChild(i) is TableSyntax table
                && table.Name?.ToString().Trim() == name)
                return table;
        return null;
    }

    private static string? FindTableString(TableSyntax table, string key)
    {
        foreach (var item in table.Items)
            if (item is KeyValueSyntax pair
                && pair.Key?.ToString().Trim() == key
                && pair.Value is StringValueSyntax value)
                return value.Value;
        return null;
    }

    private static KeyValueSyntax? FindTablePair(TableSyntax table, string key)
    {
        foreach (var item in table.Items)
            if (item is KeyValueSyntax pair && pair.Key?.ToString().Trim() == key)
                return pair;
        return null;
    }

    private static CodexTimeoutSnapshot Unknown(string path, string reason) =>
        new(
            path,
            Readable: false,
            reason,
            UnknownDuration(CodexTimeoutPolicy.StreamIdleKey),
            UnknownCount(CodexTimeoutPolicy.RequestRetriesKey),
            UnknownCount(CodexTimeoutPolicy.StreamRetriesKey));

    private static ClientDurationValue UnknownDuration(string key) =>
        new(key, null, null, null, ClientValueSource.Unknown);

    private static ClientCountValue UnknownCount(string key) =>
        new(key, null, null, ClientValueSource.Unknown);
}
