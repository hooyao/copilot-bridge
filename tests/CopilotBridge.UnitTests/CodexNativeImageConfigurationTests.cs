using CopilotBridge.Cli.Hosting.ClientConfig;
using Xunit;

namespace CopilotBridge.UnitTests;

[Collection(ClientConfigCollection.Name)]
public sealed class CodexNativeImageConfigurationTests
{
    [Fact]
    public void Native_image_configuration_preserves_the_existing_provider_and_user_settings()
    {
        const string original = """
            model_provider = "copilot-bridge"
            [features]
            enable_request_compression = true
            multi_agent = true
            [model_providers.copilot-bridge]
            base_url = "http://localhost:8765/codex"
            [model_providers.copilot-bridge.auth]
            command = "existing-credential-helper"
            """;
        var (content, summary) = CodexConfigurator.BuildNativeImagesContent(original, new BridgeConnection(8765));
        Assert.Contains("model_provider = \"copilot-bridge-native-images\"", content);
        Assert.Contains("[model_providers.copilot-bridge-native-images]", content);
        Assert.Contains("requires_openai_auth = true", content);
        Assert.Contains("supports_websockets = false", content);
        Assert.Contains("enable_request_compression = false", content);
        Assert.Contains("multi_agent = true", content);
        Assert.Contains("command = \"existing-credential-helper\"", content);
        Assert.Contains(summary, line => line.Contains("ChatGPT login", StringComparison.Ordinal));
        var (second, _) = CodexConfigurator.BuildNativeImagesContent(content, new BridgeConnection(8765));
        Assert.Equal(content, second);
    }

    [Theory]
    [InlineData("env_key = \"USER_KEY\"")]
    [InlineData("experimental_bearer_token = \"secret\"")]
    [InlineData("auth = { command = \"other\" }")]
    public void Native_configuration_refuses_conflicting_authentication(string setting)
    {
        var original = "[model_providers.copilot-bridge-native-images]\n" + setting + "\n";
        Assert.Throws<ClientConfigException>(() => CodexConfigurator.BuildNativeImagesContent(original, new BridgeConnection(8765)));
    }

    [Fact]
    public void Native_configuration_refuses_an_existing_auth_subtable()
    {
        const string original = "[model_providers.copilot-bridge-native-images.auth]\ncommand = \"other\"\n";
        Assert.Throws<ClientConfigException>(() => CodexConfigurator.BuildNativeImagesContent(original, new BridgeConnection(8765)));
    }

    // Contract: a valid inline or dotted features value must never be replaced
    // by an invalid duplicate table during the opt-in configuration edit.
    [Theory]
    [InlineData("features = { multi_agent = true }")]
    [InlineData("features.multi_agent = true")]
    [InlineData("\"features\".multi_agent = true")]
    public void Native_configuration_refuses_inline_or_dotted_features_without_writing(string setting)
    {
        var exception = Assert.Throws<ClientConfigException>(() =>
            CodexConfigurator.BuildNativeImagesContent(setting + "\n", new BridgeConnection(8765)));
        Assert.Contains("features", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("model_providers = { other = { name = \"other\" } }")]
    [InlineData("model_providers.copilot-bridge-native-images.name = \"other\"")]
    [InlineData("[model_providers]\ncopilot-bridge-native-images = { name = \"other\" }")]
    [InlineData("[model_providers]\ncopilot-bridge-native-images.name = \"other\"")]
    public void Native_configuration_refuses_conflicting_provider_representations(string setting)
    {
        Assert.Throws<ClientConfigException>(() =>
            CodexConfigurator.BuildNativeImagesContent(setting + "\n", new BridgeConnection(8765)));
    }

    [Fact]
    public void Native_configuration_keeps_unrelated_dotted_provider()
    {
        const string original = "model_providers.other.name = \"other\"\n";
        var (content, _) = CodexConfigurator.BuildNativeImagesContent(original, new BridgeConnection(8765));
        Assert.Contains(original, content);
        Assert.NotNull(Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(content));
    }

    // Contract: status reports conflicting provider auth without printing its value.
    [Theory]
    [InlineData("auth = { command = \"private-helper\" }")]
    [InlineData("auth.command = \"private-helper\"")]
    public void Native_status_reports_inline_or_dotted_authentication_as_drift(string setting)
    {
        var directory = Path.Combine(Path.GetTempPath(), "native-images-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", directory);
            var (content, _) = CodexConfigurator.BuildNativeImagesContent(null, new BridgeConnection(8765));
            content = content.Replace("requires_openai_auth = true", "requires_openai_auth = true\n" + setting, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(directory, "config.toml"), content);
            var state = new CodexConfigurator().Read(new BridgeConnection(8765), ConfigScope.Global);
            Assert.True(state.ConfiguredForBridge);
            Assert.True(state.Drifted);
            Assert.Contains(state.AdditionalDriftFacts!, fact => fact.Contains("conflicting provider authentication", StringComparison.Ordinal));
            Assert.DoesNotContain("private-helper", string.Join(" ", state.Details));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
            File.Delete(Path.Combine(directory, "config.toml"));
            Directory.Delete(directory);
        }
    }
}
