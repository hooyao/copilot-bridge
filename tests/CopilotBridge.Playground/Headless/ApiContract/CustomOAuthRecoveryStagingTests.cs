using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.GitHub;
using Xunit;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Contract: a forced-refresh v4 behavior run may consume only an explicitly
/// disposable credential source. Ordinary success-test and installed credentials
/// remain read-only and cannot be selected accidentally for the rotating-token case.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class CustomOAuthRecoveryStagingTests
{
    [Fact]
    public void Disposable_recovery_staging_requires_the_exact_single_use_marker()
    {
        using var source = ClientBehaviorSupport.NewWorkDir("v4-recovery-source");
        using var scratch = ClientBehaviorSupport.NewWorkDir("v4-recovery-scratch");
        var sourceCredential = Path.Combine(source.Path, "github_credentials.dat");
        WriteCredential(sourceCredential);
        var sourceBytes = File.ReadAllBytes(sourceCredential);

        var missing = Assert.Throws<InvalidOperationException>(() =>
            ServeProcess.StageExplicitCredential(
                source.Path,
                scratch.Path,
                CredentialStagingMode.DisposableCustomOAuthVersionFourRecovery));
        Assert.Contains(
            ServeProcess.DisposableCustomOAuthRecoveryMarkerFileName,
            missing.Message,
            StringComparison.Ordinal);

        var marker = Path.Combine(
            source.Path, ServeProcess.DisposableCustomOAuthRecoveryMarkerFileName);
        File.WriteAllText(marker, "not an authorization marker");
        Assert.Throws<InvalidOperationException>(() =>
            ServeProcess.StageExplicitCredential(
                source.Path,
                scratch.Path,
                CredentialStagingMode.DisposableCustomOAuthVersionFourRecovery));

        File.WriteAllText(
            marker, ServeProcess.DisposableCustomOAuthRecoveryMarkerContents);
        ServeProcess.StageExplicitCredential(
            source.Path,
            scratch.Path,
            CredentialStagingMode.DisposableCustomOAuthVersionFourRecovery);

        Assert.Equal(sourceBytes, File.ReadAllBytes(sourceCredential));
        Assert.Equal(
            sourceBytes,
            File.ReadAllBytes(Path.Combine(scratch.Path, "github_credentials.dat")));
    }

    private static void WriteCredential(string path)
    {
        var record = new CredentialFileRecord
        {
            Version = CredentialFileRecord.CustomOAuthDirectVersion,
            AccessToken = "gho_disposable_contract",
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(8),
            RefreshToken = "ghr_disposable_contract",
            RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            OAuthClientId = "Ov23_DISPOSABLE_CONTRACT",
            CredentialId = "disposable-contract-id",
            Generation = 1,
        };
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            record, JsonContext.Default.CredentialFileRecord);
        try
        {
            var entropy = Encoding.UTF8.GetBytes("copilot-bridge.github_token.v1");
            File.WriteAllBytes(
                path,
                ProtectedData.Protect(
                    plaintext, entropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
