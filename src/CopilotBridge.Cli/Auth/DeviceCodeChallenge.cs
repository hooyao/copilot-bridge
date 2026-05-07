namespace CopilotBridge.Cli.Auth;

public sealed record DeviceCodeChallenge(string UserCode, string VerificationUri, TimeSpan ExpiresIn);
