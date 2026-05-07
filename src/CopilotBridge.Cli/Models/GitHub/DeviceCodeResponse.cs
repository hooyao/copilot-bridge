namespace CopilotBridge.Cli.Models.GitHub;

internal sealed record DeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresIn,
    int Interval);
