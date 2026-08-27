using CopilotBridge.Cli.Models.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Selects the provider for the next interactive GitHub OAuth login. Existing
/// encrypted credentials remain bound to their recorded provider and version.
/// </summary>
internal sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    public bool UseCustomAppId { get; set; }

    public string CustomAppId { get; set; } = GitHubOAuthProvider.CopilotBridgeClientId;

    public GitHubOAuthLoginProvider ResolveLoginProvider()
    {
        ThrowIfInvalid(this);
        return UseCustomAppId
            ? GitHubOAuthLoginProvider.Custom(CustomAppId.Trim())
            : GitHubOAuthLoginProvider.OfficialCopilotPlugin;
    }

    public static AuthenticationOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new AuthenticationOptions();
        configuration.GetSection(SectionName).Bind(options);
        ThrowIfInvalid(options);
        return options;
    }

    internal static string? ValidationError(AuthenticationOptions options)
    {
        if (!options.UseCustomAppId) return null;
        if (string.IsNullOrWhiteSpace(options.CustomAppId))
            return "Authentication:CustomAppId must be non-empty when Authentication:UseCustomAppId is true.";
        if (string.Equals(
                options.CustomAppId.Trim(),
                GitHubOAuthProvider.CopilotPluginClientId,
                StringComparison.Ordinal))
        {
            return "Authentication:CustomAppId cannot be GitHub's official Copilot Plugin "
                + "client ID when Authentication:UseCustomAppId is true; set "
                + "Authentication:UseCustomAppId to false for that provider.";
        }
        return null;
    }

    private static void ThrowIfInvalid(AuthenticationOptions options)
    {
        var error = ValidationError(options);
        if (error is not null) throw new InvalidOperationException(error);
    }
}

internal sealed class AuthenticationOptionsValidator : IValidateOptions<AuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthenticationOptions options)
    {
        var error = AuthenticationOptions.ValidationError(options);
        return error is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(error);
    }
}
