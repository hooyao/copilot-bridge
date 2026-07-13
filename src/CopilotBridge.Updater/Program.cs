using System.Text.Json;
using CopilotBridge.Update.Wire;
using CopilotBridge.Updater;

// copilot-updater: the minimal, policy-free executor of one update plan.
//
// Usage: copilot-updater <plan.json>
//
// The bridge CLI makes every decision, writes an immutable plan, copies this
// executable to a private per-attempt directory, and launches that copy with the
// plan path. This process only executes the plan: download, verify, extract,
// merge config, back up, hand off, cut over, wait for the new bridge's Ready,
// commit — or roll back to the exact old files and config and restart the old
// bridge. It queries no releases, chooses no channel/version/asset, prompts no
// user, and holds no authentication secret.

if (args.Length < 1)
{
    Console.Error.WriteLine("copilot-updater: expected a plan file path argument.");
    return (int)UpdaterExit.PreflightFailed;
}

var planPath = args[0];
UpdatePlan? plan;
try
{
    var json = File.ReadAllText(planPath);
    plan = JsonSerializer.Deserialize(json, UpdateWireJsonContext.Default.UpdatePlan);
}
catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"copilot-updater: cannot read plan: {ex.GetType().Name}");
    return (int)UpdaterExit.PreflightFailed;
}

if (plan is null)
{
    Console.Error.WriteLine("copilot-updater: plan is empty or malformed.");
    return (int)UpdaterExit.PreflightFailed;
}

// Establish the trusted attempt root from the path WE were handed (the directory
// containing plan.json), NOT from any plan-supplied path. Validate the entire
// plan — including confining every temporary path to this trusted root — BEFORE
// opening the journal, because journal_path is plan-controlled: a tampered plan
// must not be able to make us append to an arbitrary file (e.g. the installed
// config) before it is rejected.
var trustedRoot = Path.GetDirectoryName(Path.GetFullPath(planPath));
if (string.IsNullOrEmpty(trustedRoot))
{
    Console.Error.WriteLine("copilot-updater: plan path has no directory.");
    return (int)UpdaterExit.PreflightFailed;
}

var validation = UpdatePlanValidator.Validate(plan, trustedRoot);
if (!validation.Ok)
{
    // Report to stderr only — no journal yet, since journal_path is not trusted
    // until the plan (which confines it to trustedRoot) has been validated.
    Console.Error.WriteLine($"copilot-updater: invalid plan: {validation.Reason}");
    return (int)UpdaterExit.PreflightFailed;
}

var journal = new TransactionJournal(plan.JournalPath);
journal.Write("updater.start");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    var engine = new UpdaterEngine(plan, journal, Console.Error, trustedRoot);
    var outcome = await engine.RunAsync(cts.Token);
    journal.Write("updater.exit", outcome.ToString());
    return (int)outcome;
}
catch (OperationCanceledException)
{
    // Cancellation that escapes the engine means it happened BEFORE cutover
    // (the engine handles post-authorization cancellation internally and never
    // rethrows it). Nothing was installed and the old bridge is still serving.
    journal.Write("updater.cancelled");
    return (int)UpdaterExit.PreflightFailed;
}
catch (Exception ex)
{
    // Last-resort guard: never crash with an unhandled exception mid-transaction.
    journal.Write("updater.fatal", ex.GetType().Name);
    Console.Error.WriteLine($"copilot-updater: unexpected error: {ex.GetType().Name}");
    return (int)UpdaterExit.Unrecovered;
}
