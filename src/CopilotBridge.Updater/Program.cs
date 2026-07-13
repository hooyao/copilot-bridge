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
    var engine = new UpdaterEngine(plan, journal, Console.Error);
    var outcome = await engine.RunAsync(cts.Token);
    journal.Write("updater.exit", outcome.ToString());
    return (int)outcome;
}
catch (OperationCanceledException)
{
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
