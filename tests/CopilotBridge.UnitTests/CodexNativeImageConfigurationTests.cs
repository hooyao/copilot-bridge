using CopilotBridge.Cli.Hosting.ClientConfig;
using Xunit;

namespace CopilotBridge.UnitTests;

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
}
