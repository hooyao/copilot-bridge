using CopilotBridge.Update.Wire;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Real named-pipe round-trip tests for the update control and readiness
/// channels ("Coordinated cutover", "Ready-authenticated commit"). These use
/// actual <see cref="System.IO.Pipes"/> endpoints across two tasks — no mock —
/// so they catch server/client role mistakes (e.g. two servers on one pipe) that
/// only surface with a live pipe. Role: updater is the SERVER, the parent bridge
/// and each launched bridge are CLIENTS.
/// </summary>
public class UpdatePipeTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Capability_uses_a_platform_appropriate_endpoint_name()
    {
        var cap = UpdateCapability.Create("an-attempt-with-a-long-id", "handoff");

        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(
                "copilot-bridge-update-handoff-an-attempt-with-a-long-id-",
                cap.PipeName,
                StringComparison.Ordinal);
            return;
        }

        Assert.StartsWith("/tmp/cbup-h-", cap.PipeName, StringComparison.Ordinal);
        Assert.True(Path.IsPathRooted(cap.PipeName));
        // Leave headroom for the native sockaddr_un limit and its terminator.
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(cap.PipeName), 1, 80);
    }

    [Fact]
    public async Task Overlong_platform_pipe_path_fails_open_for_all_transport_operations()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var overlongPath = "/tmp/" + new string('x', 200);
        var received = await UpdatePipeTransport.ServerReceiveLineAsync(
            overlongPath, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var reply = await UpdatePipeTransport.ServerSendLineAsync(
            overlongPath, "line", expectReply: true,
            TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var sent = await UpdatePipeTransport.ClientSendLineAsync(
            overlongPath, "line", TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var exchanged = await UpdatePipeTransport.ClientExchangeAsync(
            overlongPath, static _ => "reply",
            TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Null(received);
        Assert.Null(reply);
        Assert.False(sent);
        Assert.Null(exchanged);
    }

    [Fact]
    public async Task Empty_pipe_name_fails_open_for_all_transport_operations()
    {
        var received = await UpdatePipeTransport.ServerReceiveLineAsync(
            "", TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var reply = await UpdatePipeTransport.ServerSendLineAsync(
            "", "line", expectReply: true,
            TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var sent = await UpdatePipeTransport.ClientSendLineAsync(
            "", "line", TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var exchanged = await UpdatePipeTransport.ClientExchangeAsync(
            "", static _ => "reply",
            TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Null(received);
        Assert.Null(reply);
        Assert.False(sent);
        Assert.Null(exchanged);
    }

    [Fact]
    public async Task Handoff_prepared_then_authorized_round_trips()
    {
        var cap = UpdateCapability.Create("att1", "handoff");
        var prepared = new UpdateControlMessage
        {
            Kind = UpdateWire.MsgPrepared,
            AttemptId = "att1",
            Token = cap.Token,
            SenderPid = 111,
        };

        // Updater side (server): send Prepared, expect a reply.
        var serverTask = UpdatePipeTransport.ServerSendLineAsync(
            cap.PipeName, UpdatePipeCodec.EncodeControl(prepared),
            expectReply: true, Timeout, CancellationToken.None);

        // Parent side (client): read Prepared, reply CutoverAuthorized.
        string? seenByClient = null;
        var clientTask = UpdatePipeTransport.ClientExchangeAsync(
            cap.PipeName,
            makeReply: line =>
            {
                seenByClient = line;
                var authorize = new UpdateControlMessage
                {
                    Kind = UpdateWire.MsgCutoverAuthorized,
                    AttemptId = "att1",
                    Token = cap.Token,
                    SenderPid = 222,
                };
                return UpdatePipeCodec.EncodeControl(authorize);
            },
            Timeout, CancellationToken.None);

        await Task.WhenAll(serverTask, clientTask);

        // Server got the authorization reply.
        var reply = UpdatePipeCodec.DecodeControl(await serverTask ?? "");
        Assert.NotNull(reply);
        Assert.Equal(UpdateWire.MsgCutoverAuthorized, reply!.Kind);
        Assert.Equal(cap.Token, reply.Token);

        // Client saw the Prepared message.
        var seen = UpdatePipeCodec.DecodeControl(seenByClient ?? "");
        Assert.Equal(UpdateWire.MsgPrepared, seen!.Kind);
    }

    [Fact]
    public async Task Ready_client_to_server_round_trips_and_validates()
    {
        var cap = UpdateCapability.Create("att1", UpdateWire.RoleTarget);

        // Updater side (server): wait for the launched bridge's Ready line.
        var serverTask = UpdatePipeTransport.ServerReceiveLineAsync(
            cap.PipeName, Timeout, CancellationToken.None);

        // Launched-bridge side (client): send Ready.
        var ready = new UpdateReadyMessage
        {
            AttemptId = "att1",
            Role = UpdateWire.RoleTarget,
            Token = cap.Token,
            Pid = 777,
            Version = "0.4.14",
        };
        var sent = await UpdatePipeTransport.ClientSendLineAsync(
            cap.PipeName, UpdatePipeCodec.EncodeReady(ready), Timeout, CancellationToken.None);
        Assert.True(sent);

        var line = await serverTask;
        var msg = UpdatePipeCodec.DecodeReady(line ?? "");
        Assert.True(UpdatePipeCodec.IsValidReady(msg, "att1", UpdateWire.RoleTarget, cap.Token, 777, "0.4.14"));
    }

    [Fact]
    public async Task Preflight_failed_reaches_a_late_connecting_parent()
    {
        var cap = UpdateCapability.Create("att1", "handoff");
        var failed = new UpdateControlMessage
        {
            Kind = UpdateWire.MsgPreflightFailed,
            AttemptId = "att1",
            Token = cap.Token,
            SenderPid = 111,
            Detail = "download failed",
        };

        var serverTask = UpdatePipeTransport.ServerSendLineAsync(
            cap.PipeName, UpdatePipeCodec.EncodeControl(failed),
            expectReply: false, Timeout, CancellationToken.None);

        // Parent connects, reads the failure, replies nothing (returns null).
        string? seen = null;
        var clientTask = UpdatePipeTransport.ClientExchangeAsync(
            cap.PipeName,
            makeReply: line => { seen = line; return null; },
            Timeout, CancellationToken.None);

        await Task.WhenAll(serverTask, clientTask);

        var msg = UpdatePipeCodec.DecodeControl(seen ?? "");
        Assert.Equal(UpdateWire.MsgPreflightFailed, msg!.Kind);
        Assert.Equal("download failed", msg.Detail);
    }
}
