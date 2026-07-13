using CopilotBridge.Update.Wire;

// stub-bridge: a test-only stand-in for the real bridge that an updater launches.
// It uses the SAME shared wire/pipe code the product uses, so a green real-process
// test exercises the actual protocol, not a re-implementation.
//
// Two hats, chosen from environment:
//
//  (A) PARENT hat — env COPILOT_BRIDGE_PARENT_PIPE/_TOKEN/_ATTEMPT are set.
//      Impersonates the initiating (old) bridge's startup gate: connect to the
//      updater's handoff pipe, read Prepared, reply CutoverAuthorized, then exit.
//      This is the process whose identity the updater verifies and whose exit it
//      waits for before cutover.
//
//  (B) LAUNCHED hat — a one-launch UpdateLaunchContext is present (the updater set
//      it). Report Ready over the readiness pipe. Behavior is tuned by inherited
//      markers so the SAME binary can be old and new yet behave per launch:
//        role=target AND STUB_FAIL_TARGET=1 -> exit 1 without Ready (forces rollback)
//        role=target AND STUB_BAD_VERSION=1 -> Ready with a wrong version (rejected)
//        otherwise (incl. role=rollback)    -> a valid Ready
//
// A launch with neither hat is an ordinary startup and does nothing.

var parentPipe = Environment.GetEnvironmentVariable("COPILOT_BRIDGE_PARENT_PIPE");
if (!string.IsNullOrEmpty(parentPipe))
{
    var token = Environment.GetEnvironmentVariable("COPILOT_BRIDGE_PARENT_TOKEN") ?? "";
    var attempt = Environment.GetEnvironmentVariable("COPILOT_BRIDGE_PARENT_ATTEMPT") ?? "";
    await UpdatePipeTransport.ClientExchangeAsync(
        parentPipe,
        makeReply: line =>
        {
            var msg = UpdatePipeCodec.DecodeControl(line);
            if (msg is null || msg.Kind != UpdateWire.MsgPrepared) return null;
            var authorize = new UpdateControlMessage
            {
                Kind = UpdateWire.MsgCutoverAuthorized,
                AttemptId = attempt,
                Token = token,
                SenderPid = Environment.ProcessId,
            };
            return UpdatePipeCodec.EncodeControl(authorize);
        },
        TimeSpan.FromSeconds(90),
        CancellationToken.None);
    return 0; // parent exits so the updater can replace it
}

var ctx = UpdateLaunchContext.FromEnvironment(Environment.GetEnvironmentVariable);
if (ctx is null)
{
    return 0;
}

var failTarget = Environment.GetEnvironmentVariable("STUB_FAIL_TARGET") == "1";
var badVersion = Environment.GetEnvironmentVariable("STUB_BAD_VERSION") == "1";

if (ctx.Role == UpdateWire.RoleTarget && failTarget)
{
    return 1; // exit before Ready -> rollback
}

var version = (ctx.Role == UpdateWire.RoleTarget && badVersion)
    ? "9.9.9-wrong"
    : ctx.ExpectedVersion;

var ready = new UpdateReadyMessage
{
    AttemptId = ctx.AttemptId,
    Role = ctx.Role,
    Token = ctx.Token,
    Pid = Environment.ProcessId,
    Version = version,
};

await UpdatePipeTransport.ClientSendLineAsync(
    ctx.PipeName,
    UpdatePipeCodec.EncodeReady(ready),
    TimeSpan.FromSeconds(15),
    CancellationToken.None);

// Stay alive briefly so the updater confirms the process is still running when it
// validates the Ready message, then exit cleanly.
await Task.Delay(TimeSpan.FromSeconds(6));
return 0;
