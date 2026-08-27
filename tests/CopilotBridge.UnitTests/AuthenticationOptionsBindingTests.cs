using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract: the shipped authentication provider choice is conspicuous and safe.
/// The official Copilot Plugin remains the default; custom OAuth is an explicit
/// opt-in whose configured issuer is carried into the login transaction.
/// </summary>
public sealed class AuthenticationOptionsBindingTests
{
    private const string RequestedDefaultCustomAppId = "Ov23liSD97ZYGfIEHAZE";

    [Fact]
    public void Stock_appsettings_keeps_official_auth_default_and_prefills_custom_app_id()
    {
        var path = FindRepoFile("src", "CopilotBridge.Cli", "appsettings.json");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build();

        var options = AuthenticationOptions.FromConfiguration(configuration);

        Assert.False(options.UseCustomAppId);
        Assert.Equal(RequestedDefaultCustomAppId, options.CustomAppId);
        var provider = options.ResolveLoginProvider();
        Assert.Equal(GitHubOAuthProvider.CopilotPluginClientId, provider.ClientId);
        Assert.Equal(CredentialFileRecord.CopilotPluginExplicitProviderVersion,
            provider.CredentialVersion);
        Assert.False(provider.IsDirect);

        var explanation = configuration["Authentication:_comment"];
        Assert.Contains("auth login", explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("direct", explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Enabled_custom_provider_uses_configured_id_and_version_four_direct_semantics()
    {
        var configuration = Config(
            ("Authentication:UseCustomAppId", "true"),
            ("Authentication:CustomAppId", "Ov23_CUSTOM_CONTRACT"));

        var options = AuthenticationOptions.FromConfiguration(configuration);
        var provider = options.ResolveLoginProvider();

        Assert.True(options.UseCustomAppId);
        Assert.Equal("Ov23_CUSTOM_CONTRACT", provider.ClientId);
        Assert.Equal(GitHubOAuthProvider.CopilotPluginScope, provider.Scope);
        Assert.Equal(CredentialFileRecord.CustomOAuthDirectVersion,
            provider.CredentialVersion);
        Assert.True(provider.IsDirect);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Enabled_custom_provider_requires_a_nonblank_app_id(string? appId)
    {
        var configuration = Config(
            ("Authentication:UseCustomAppId", "true"),
            ("Authentication:CustomAppId", appId));

        var error = Assert.Throws<InvalidOperationException>(() =>
            AuthenticationOptions.FromConfiguration(configuration));

        Assert.Contains("Authentication:CustomAppId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_custom_provider_rejects_the_official_Copilot_Plugin_app_id()
    {
        var configuration = Config(
            ("Authentication:UseCustomAppId", "true"),
            ("Authentication:CustomAppId", GitHubOAuthProvider.CopilotPluginClientId));

        var error = Assert.Throws<InvalidOperationException>(() =>
            AuthenticationOptions.FromConfiguration(configuration));

        Assert.Contains("Authentication:CustomAppId", error.Message, StringComparison.Ordinal);
        Assert.Contains("Authentication:UseCustomAppId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_custom_provider_ignores_a_blank_custom_id()
    {
        var configuration = Config(
            ("Authentication:UseCustomAppId", "false"),
            ("Authentication:CustomAppId", ""));

        var provider = AuthenticationOptions.FromConfiguration(configuration)
            .ResolveLoginProvider();

        Assert.Equal(GitHubOAuthProvider.CopilotPluginClientId, provider.ClientId);
        Assert.Equal(CredentialFileRecord.CopilotPluginExplicitProviderVersion,
            provider.CredentialVersion);
    }

    [Fact]
    public void Standalone_auth_command_and_server_bind_the_same_custom_provider_selection()
    {
        var configuration = Config(
            ("Authentication:UseCustomAppId", "true"),
            ("Authentication:CustomAppId", "Ov23_SHARED_SELECTION"));

        var commandProvider = AuthCommand.LoadLoginProvider(configuration);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBridgeServer(configuration);
        using var serviceProvider = services.BuildServiceProvider();
        var serverProvider = serviceProvider
            .GetRequiredService<IOptions<AuthenticationOptions>>()
            .Value.ResolveLoginProvider();

        Assert.Equal(commandProvider, serverProvider);
        Assert.Equal("Ov23_SHARED_SELECTION", serverProvider.ClientId);
        Assert.True(serverProvider.IsDirect);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair =>
                new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

    private static string FindRepoFile(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException($"Could not find {Path.Combine(parts)} from test output.");
    }
}
