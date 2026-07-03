namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Stable identifiers for the response-leak signatures the
/// <see cref="ResponseLeakAutomaton"/> can detect. Kebab-case, matching the leaked
/// tag/family so a log line or error message reads naturally.
/// </summary>
/// <remarks>
/// These ids are the contract shared by three places, kept in sync via this one
/// source: the automaton (which matcher to build, and what to report as
/// <see cref="ResponseLeakAutomaton.MatchedSignature"/>); the per-signature config
/// gate (<see cref="Hosting.Options.ResponseLeakSignaturesOptions"/>, whose PascalCase
/// property names map 1:1 to these ids); and the retry-error / warning-log text
/// (which turns an id into the exact config key to disable). Distinct from a
/// matched <i>subject</i>: for an <c>invoke</c> leak the subject is the captured
/// tool name, but the signature is always <c>invoke</c>.
/// </remarks>
internal static class LeakSignatures
{
    public const string Invoke = "invoke";
    public const string TaskNotification = "task-notification";
    public const string TeammateMessage = "teammate-message";
    public const string Channel = "channel";
    public const string CrossSessionMessage = "cross-session-message";
    public const string Tick = "tick";

    /// <summary>Every signature id, in the automaton's matcher order.</summary>
    public static readonly string[] All =
    {
        Invoke, TaskNotification, TeammateMessage, Channel, CrossSessionMessage, Tick,
    };
}
