using CopilotBridge.Update.Wire;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Contract tests for <see cref="OwnershipWindowRouter"/> — the policy for the
/// span after the parent bridge has authorized cutover (and so has already
/// returned WITHOUT building its listener). The governing invariant, stated in
/// words: once authorization is granted there is soon, or already, NO bridge
/// serving, so <b>every</b> outcome must resolve to a service-restoring action —
/// never a plain fail-open (process exit with no relaunch). Which restoring action
/// depends only on whether the transaction had begun MUTATING the install:
///  - nothing mutated  → relaunch the OLD bridge (its config is untouched);
///  - anything mutated  → rollback (restore binaries AND the exact original config).
/// The one Commit is the healthy path. These assert the table directly so a
/// regression to fail-open, or to recover-without-config after the config rename,
/// turns a test red.
/// </summary>
public class OwnershipWindowRouterTests
{
    // --- The load-bearing invariant: NOTHING maps to a plain fail-open. ---------
    // (RecoveryAction has no "FailOpen" member by construction; this enumerates
    // every outcome × mutating-state and asserts each yields a real restoring
    // action, so adding a fail-open path later would have to bypass this router.)
    [Fact]
    public void Every_outcome_maps_to_a_service_restoring_action()
    {
        foreach (var outcome in Enum.GetValues<OwnershipOutcome>())
        foreach (var mutating in new[] { false, true })
        {
            var action = OwnershipWindowRouter.Route(outcome, mutating);
            Assert.True(
                action is RecoveryAction.RecoverOldBridge
                       or RecoveryAction.Rollback
                       or RecoveryAction.Commit,
                $"{outcome}/{mutating} must restore service, got {action}");
        }
    }

    // --- Fix #1: parent-exit-unconfirmed after authorization is NOT a fail-open. -
    // Before this fix the engine called FailPreflightAsync here, which exits the
    // updater; the parent had already returned without a listener, so nothing was
    // serving. The contract: relaunch the old bridge (nothing was installed, its
    // config is intact).
    [Fact]
    public void Pre_cutover_failures_relaunch_the_old_bridge()
    {
        // No mutation has happened at these outcomes, regardless of the flag.
        Assert.Equal(RecoveryAction.RecoverOldBridge, OwnershipWindowRouter.Route(OwnershipOutcome.ParentExitUnconfirmed, transactionMutating: false));
        Assert.Equal(RecoveryAction.RecoverOldBridge, OwnershipWindowRouter.Route(OwnershipOutcome.DriftAfterHandoff, transactionMutating: false));
    }

    // --- Fix #2: a throw AFTER the config rename must rollback, not recover. -----
    // Cutover renames the live appsettings.json to .bak, then hashes it; if that
    // hash read throws, control lands at OwnershipOutcome.IoError with the mutating
    // flag ALREADY set (it is raised immediately before Cutover). The old code set
    // its flag only after Cutover RETURNED, so this exact case recovered the old
    // bridge WITHOUT restoring config — the old bridge would start with no
    // appsettings.json. The contract: mutating ⇒ rollback.
    [Fact]
    public void IoError_after_transaction_started_mutating_rolls_back()
    {
        Assert.Equal(RecoveryAction.Rollback, OwnershipWindowRouter.Route(OwnershipOutcome.IoError, transactionMutating: true));
    }

    [Fact]
    public void IoError_before_any_mutation_relaunches_old_bridge()
    {
        // Symmetric half: an I/O error before Cutover touched anything has no files
        // to restore, so relaunching the old bridge is correct (and required — the
        // parent is gone).
        Assert.Equal(RecoveryAction.RecoverOldBridge, OwnershipWindowRouter.Route(OwnershipOutcome.IoError, transactionMutating: false));
    }

    [Fact]
    public void Cancellation_routes_on_mutation_state()
    {
        Assert.Equal(RecoveryAction.RecoverOldBridge, OwnershipWindowRouter.Route(OwnershipOutcome.Cancelled, transactionMutating: false));
        Assert.Equal(RecoveryAction.Rollback, OwnershipWindowRouter.Route(OwnershipOutcome.Cancelled, transactionMutating: true));
    }

    [Fact]
    public void Post_mutation_failures_always_roll_back()
    {
        // These only ever occur with the transaction mutating; assert both flag
        // values map to rollback so the classification can't silently regress.
        foreach (var outcome in new[] { OwnershipOutcome.CutoverFailed, OwnershipOutcome.TargetNotReady })
        {
            Assert.Equal(RecoveryAction.Rollback, OwnershipWindowRouter.Route(outcome, transactionMutating: true));
            Assert.Equal(RecoveryAction.Rollback, OwnershipWindowRouter.Route(outcome, transactionMutating: false));
        }
    }

    [Fact]
    public void Valid_ready_commits()
    {
        Assert.Equal(RecoveryAction.Commit, OwnershipWindowRouter.Route(OwnershipOutcome.TargetReady, transactionMutating: true));
    }
}
