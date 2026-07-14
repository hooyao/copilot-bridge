using System.Security.Cryptography;
using System.Text.Json;

namespace CopilotBridge.Update.Wire;

/// <summary>
/// One role-scoped local control capability: a unique pipe name plus a 256-bit
/// token. Three independent capabilities exist per update — the CLI-created
/// parent/updater handoff, an updater-created target-Ready, and a distinct
/// updater-created rollback-Ready — and none is ever reused across roles. The
/// token proves the counterparty holds the same private material; the pipe name
/// scopes the channel to this attempt.
/// </summary>
internal sealed record UpdateCapability(string PipeName, string Token)
{
    /// <summary>Mint a fresh capability with a random pipe name and 256-bit token.</summary>
    public static UpdateCapability Create(string attemptId, string role)
    {
        // A short random suffix keeps the pipe name unique per attempt+role while
        // staying within the platform pipe-name length limits.
        var suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var pipe = $"copilot-bridge-update-{role}-{attemptId}-{suffix}";
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        return new UpdateCapability(pipe, token);
    }
}

/// <summary>
/// Length-bounded, source-generated JSON framing for one control/ready message
/// over a named pipe. Messages are a single UTF-8 JSON line terminated by
/// <c>\n</c>; anything larger than <see cref="MaxBytes"/> is rejected so a peer
/// cannot exhaust memory.
/// </summary>
internal static class UpdatePipeCodec
{
    public const int MaxBytes = 16 * 1024;

    public static string EncodeControl(UpdateControlMessage msg) =>
        JsonSerializer.Serialize(msg, UpdateWireJsonContext.Default.UpdateControlMessage);

    public static UpdateControlMessage? DecodeControl(string line)
    {
        if (line.Length == 0 || line.Length > MaxBytes)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(line, UpdateWireJsonContext.Default.UpdateControlMessage);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string EncodeReady(UpdateReadyMessage msg) =>
        JsonSerializer.Serialize(msg, UpdateWireJsonContext.Default.UpdateReadyMessage);

    public static UpdateReadyMessage? DecodeReady(string line)
    {
        if (line.Length == 0 || line.Length > MaxBytes)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(line, UpdateWireJsonContext.Default.UpdateReadyMessage);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validate a ready message against the expected role-scoped capability and
    /// launch facts. Everything must match: protocol, kind, attempt, role, token,
    /// the PID we launched, and the version we expect.
    /// </summary>
    public static bool IsValidReady(
        UpdateReadyMessage? msg,
        string attemptId,
        string role,
        string expectedToken,
        int expectedPid,
        string expectedVersion)
    {
        return msg is not null
            && msg.ProtocolVersion == UpdateWire.ProtocolVersion
            && msg.Kind == UpdateWire.MsgReady
            && string.Equals(msg.AttemptId, attemptId, StringComparison.Ordinal)
            && string.Equals(msg.Role, role, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(msg.Token),
                System.Text.Encoding.UTF8.GetBytes(expectedToken))
            && msg.Pid == expectedPid
            && string.Equals(msg.Version, expectedVersion, StringComparison.Ordinal);
    }
}
