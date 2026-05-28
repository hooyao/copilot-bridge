using System.Text.Json;

namespace CopilotBridge.Playground;

/// <summary>
/// Loads <c>appsettings.local.json</c> sitting next to the test binary. Gitignored
/// — holds secrets like the Anthropic API key used by side-by-side comparison
/// tests. Returns null when the file is missing or a key is unset so tests can
/// gracefully skip rather than fail.
/// </summary>
internal static class LocalConfig
{
    private static readonly Lazy<Dictionary<string, string>> _values = new(Load);

    private static Dictionary<string, string> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    dict[prop.Name] = prop.Value.GetString() ?? "";
            }
            return dict;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static string? AnthropicApiKey =>
        _values.Value.TryGetValue("AnthropicApiKey", out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}
