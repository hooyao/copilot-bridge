using System.Text.Json;
using CopilotBridge.Update.Wire;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Contract tests for the frozen cross-executable wire contract ("Versioned
/// cross-executable wire contract" + "Immutable plan and policy-free executor").
/// Proves the bytes are stable and independent of any application serializer
/// policy, and that a plan never carries a secret.
/// </summary>
public class UpdateWireContractTests
{
    private static UpdatePlan SamplePlan() => new()
    {
        AttemptId = "abcd1234",
        ParentPid = 4321,
        ParentStartTicks = 638000000000000000,
        InstallDir = @"C:\Tools\copilot-bridge",
        BridgeExePath = @"C:\Tools\copilot-bridge\copilot-bridge.exe",
        UpdaterExePath = @"C:\Tools\copilot-bridge\copilot-updater.exe",
        ConfigPath = @"C:\Tools\copilot-bridge\appsettings.json",
        CurrentVersion = "0.4.13",
        TargetVersion = "0.4.14",
        AssetName = "copilot-bridge-0.4.14-win-x64.zip",
        AssetUrl = "https://github.com/hooyao/copilot-bridge/releases/download/v0.4.14/copilot-bridge-0.4.14-win-x64.zip",
        AssetSize = 6203101,
        AssetSha256 = new string('a', 64),
        ArchiveKind = UpdateWire.ArchiveZip,
        StagingDir = @"C:\Users\u\AppData\Local\copilot-bridge\updates\abcd1234\staging",
        BackupDir = @"C:\Users\u\AppData\Local\copilot-bridge\updates\abcd1234\backup",
        ArchivePath = @"C:\Users\u\AppData\Local\copilot-bridge\updates\abcd1234\a.zip",
        JournalPath = @"C:\Users\u\AppData\Local\copilot-bridge\updates\abcd1234\transaction.log",
        ManagedFiles = ["copilot-bridge.exe", "copilot-updater.exe", "appsettings.json"],
        OriginalArgs = ["serve", "--port", "18765"],
        WorkingDirectory = @"C:\work",
        DownloadTimeoutMs = 120000,
        ParentExitTimeoutMs = 30000,
        ReadyTimeoutMs = 120000,
        HandoffPipe = "copilot-bridge-update-handoff-abcd1234-deadbeef",
        HandoffToken = new string('b', 64),
    };

    [Fact]
    public void Plan_round_trips_through_the_shared_context()
    {
        var plan = SamplePlan();
        var json = JsonSerializer.Serialize(plan, UpdateWireJsonContext.Default.UpdatePlan);
        var back = JsonSerializer.Deserialize(json, UpdateWireJsonContext.Default.UpdatePlan);

        Assert.NotNull(back);
        Assert.Equal(plan.AttemptId, back!.AttemptId);
        Assert.Equal(plan.AssetSha256, back.AssetSha256);
        Assert.Equal(plan.OriginalArgs, back.OriginalArgs);
        Assert.Equal(plan.ManagedFiles, back.ManagedFiles);
        Assert.Equal(UpdateWire.ProtocolVersion, back.ProtocolVersion);
    }

    [Fact]
    public void Wire_property_names_are_frozen_snake_case_regardless_of_app_policy()
    {
        var json = JsonSerializer.Serialize(SamplePlan(), UpdateWireJsonContext.Default.UpdatePlan);

        // Explicit [JsonPropertyName] values — must appear verbatim, independent
        // of the bridge's global SnakeCaseLower context or any future change to it.
        Assert.Contains("\"protocol_version\"", json);
        Assert.Contains("\"attempt_id\"", json);
        Assert.Contains("\"asset_sha256\"", json);
        Assert.Contains("\"handoff_token\"", json);
        Assert.Contains("\"original_args\"", json);
        Assert.Contains("\"managed_files\"", json);
        // Never the C# PascalCase names.
        Assert.DoesNotContain("\"AttemptId\"", json);
        Assert.DoesNotContain("\"AssetSha256\"", json);
    }

    [Fact]
    public void Control_and_ready_messages_round_trip()
    {
        var control = new UpdateControlMessage
        {
            Kind = UpdateWire.MsgPrepared,
            AttemptId = "a1",
            Token = "t1",
            SenderPid = 7,
        };
        var cjson = UpdatePipeCodec.EncodeControl(control);
        Assert.Equal(control, UpdatePipeCodec.DecodeControl(cjson));

        var ready = new UpdateReadyMessage
        {
            AttemptId = "a1",
            Role = UpdateWire.RoleTarget,
            Token = "t2",
            Pid = 9,
            Version = "0.4.14",
        };
        var rjson = UpdatePipeCodec.EncodeReady(ready);
        Assert.Equal(ready, UpdatePipeCodec.DecodeReady(rjson));
    }

    [Fact]
    public void Ready_validation_is_bound_to_role_token_pid_and_version()
    {
        var msg = new UpdateReadyMessage
        {
            AttemptId = "a1",
            Role = UpdateWire.RoleTarget,
            Token = "correct-token",
            Pid = 100,
            Version = "0.4.14",
        };

        Assert.True(UpdatePipeCodec.IsValidReady(msg, "a1", UpdateWire.RoleTarget, "correct-token", 100, "0.4.14"));
        // Wrong token.
        Assert.False(UpdatePipeCodec.IsValidReady(msg, "a1", UpdateWire.RoleTarget, "other", 100, "0.4.14"));
        // Wrong role (target token must not satisfy a rollback wait).
        Assert.False(UpdatePipeCodec.IsValidReady(msg, "a1", UpdateWire.RoleRollback, "correct-token", 100, "0.4.14"));
        // Wrong PID.
        Assert.False(UpdatePipeCodec.IsValidReady(msg, "a1", UpdateWire.RoleTarget, "correct-token", 999, "0.4.14"));
        // Wrong version.
        Assert.False(UpdatePipeCodec.IsValidReady(msg, "a1", UpdateWire.RoleTarget, "correct-token", 100, "9.9.9"));
        // Wrong attempt.
        Assert.False(UpdatePipeCodec.IsValidReady(msg, "zz", UpdateWire.RoleTarget, "correct-token", 100, "0.4.14"));
    }

    [Fact]
    public void Plan_carries_no_authentication_secret_fields()
    {
        var json = JsonSerializer.Serialize(SamplePlan(), UpdateWireJsonContext.Default.UpdatePlan);
        // The plan's only tokens are update-control material; there is no field
        // for a GitHub OAuth token or a Copilot bearer token by construction.
        Assert.DoesNotContain("github_token", json, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("oauth", json, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bearer", json, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", json, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Three_capabilities_are_independent_per_role()
    {
        var handoff = UpdateCapability.Create("attempt1", "handoff");
        var target = UpdateCapability.Create("attempt1", UpdateWire.RoleTarget);
        var rollback = UpdateCapability.Create("attempt1", UpdateWire.RoleRollback);

        // Distinct pipe names and distinct tokens across all three roles.
        Assert.NotEqual(handoff.PipeName, target.PipeName);
        Assert.NotEqual(target.PipeName, rollback.PipeName);
        Assert.NotEqual(handoff.Token, target.Token);
        Assert.NotEqual(target.Token, rollback.Token);
        Assert.NotEqual(handoff.Token, rollback.Token);
        // Tokens are 256-bit (64 hex chars).
        Assert.Equal(64, target.Token.Length);
    }

    [Fact]
    public void Plan_without_protocol_version_is_rejected_not_defaulted_to_v1()
    {
        // Strict versioned-plan contract: an unversioned plan must NOT silently
        // deserialize to protocol v1. protocol_version is [JsonRequired], so its
        // absence is a deserialization failure, not a default.
        var full = JsonSerializer.Serialize(SamplePlan(), UpdateWireJsonContext.Default.UpdatePlan);
        Assert.Contains("\"protocol_version\"", full); // sanity: it IS written

        // Remove the protocol_version member and confirm the read fails.
        using var doc = JsonDocument.Parse(full);
        var withoutPv = new Dictionary<string, JsonElement>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Name != "protocol_version")
            {
                withoutPv[p.Name] = p.Value.Clone();
            }
        }
        var mangled = JsonSerializer.Serialize(withoutPv);

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(mangled, UpdateWireJsonContext.Default.UpdatePlan));
    }

    [Fact]
    public void Control_message_without_protocol_version_is_rejected()
    {
        var control = new UpdateControlMessage
        {
            Kind = UpdateWire.MsgCutoverAuthorized,
            AttemptId = "a1",
            Token = "t1",
            SenderPid = 7,
        };
        var json = JsonSerializer.Serialize(control, UpdateWireJsonContext.Default.UpdateControlMessage);
        var mangled = StripMember(json, "protocol_version");

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(mangled, UpdateWireJsonContext.Default.UpdateControlMessage));
    }

    [Fact]
    public void Ready_message_without_protocol_version_or_kind_is_rejected()
    {
        var ready = new UpdateReadyMessage
        {
            AttemptId = "a1",
            Role = UpdateWire.RoleTarget,
            Token = "t2",
            Pid = 9,
            Version = "0.4.14",
        };
        var json = JsonSerializer.Serialize(ready, UpdateWireJsonContext.Default.UpdateReadyMessage);

        // Both protocol_version and kind are required — omitting EITHER fails, so a
        // frame that omits the frozen protocol/kind can't pass as a valid Ready.
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(StripMember(json, "protocol_version"), UpdateWireJsonContext.Default.UpdateReadyMessage));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(StripMember(json, "kind"), UpdateWireJsonContext.Default.UpdateReadyMessage));
    }

    // Serialize a JSON object with one member removed, for "absent required member"
    // tests. Uses JsonDocument so we drop exactly one top-level property.
    private static string StripMember(string json, string member)
    {
        using var doc = JsonDocument.Parse(json);
        var kept = new Dictionary<string, JsonElement>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Name != member)
            {
                kept[p.Name] = p.Value.Clone();
            }
        }
        return JsonSerializer.Serialize(kept);
    }

    [Fact]
    public void Oversize_message_is_rejected_by_the_codec()
    {
        var huge = new string('x', UpdatePipeCodec.MaxBytes + 10);
        Assert.Null(UpdatePipeCodec.DecodeControl(huge));
        Assert.Null(UpdatePipeCodec.DecodeReady(huge));
    }
}
