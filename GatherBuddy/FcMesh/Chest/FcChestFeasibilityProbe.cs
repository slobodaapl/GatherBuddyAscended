using System;
using Dalamud.Plugin.Services;

namespace GatherBuddy.FcMesh.Chest;

public enum FcChestProbeState : byte
{
    Idle,
    WaitingForChest,
    Prepared,
    Withdrawing,
    ReconcilingWithdraw,
    Depositing,
    ReconcilingDeposit,
    Passed,
    Failed,
    Cancelled,
}

/// <summary>
/// Developer-only, framework-thread-owned FC chest feasibility state machine.
/// The UI only requests state changes; all game pointer reads and native calls happen
/// from the subscribed framework callback.
/// </summary>
public sealed class FcChestFeasibilityProbe : IDisposable
{
    private static readonly TimeSpan ReconciliationTimeout = TimeSpan.FromSeconds(10);

    private readonly IFramework _framework;
    private readonly FcChestSnapshotReader _reader;
    private readonly FcChestTransferDispatcher _dispatcher;
    private bool _prepareRequested;
    private bool _executeRequested;
    private bool _cancelRequested;
    private bool _withdrawAttempted;
    private bool _depositAttempted;
    private bool _disposed;
    private DateTime _reconciliationDeadline;
    private DateTime _depositNotBefore;
    private FcChestSnapshot? _withdrawBefore;
    private FcChestTransferPlan? _depositPlan;

    public FcChestFeasibilityProbe(
        IFramework framework,
        FcChestSnapshotReader? reader = null,
        FcChestTransferDispatcher? dispatcher = null)
    {
        _framework = framework ?? throw new ArgumentNullException(nameof(framework));
        _reader = reader ?? new FcChestSnapshotReader();
        _dispatcher = dispatcher ?? new FcChestTransferDispatcher();
        StatusText = "Idle. Open the FC chest before preparing the probe.";
        _framework.Update += OnFrameworkUpdate;
    }

    public FcChestProbeState State { get; private set; } = FcChestProbeState.Idle;
    public string StatusText { get; private set; }
    public string? FailureReason { get; private set; }
    public bool IsArmed { get; private set; }
    public FcChestPreparation? Preparation { get; private set; }
    public FcChestTransferPlan? DepositPlan => _depositPlan;
    public FcChestSnapshot? LastSnapshot { get; private set; }
    public FcChestDeltaValidation? LastDelta { get; private set; }

    public bool RequestPrepare()
    {
        if (State == FcChestProbeState.Failed && (_withdrawAttempted || _depositAttempted))
            return false;

        if (_disposed || State is FcChestProbeState.Withdrawing
            or FcChestProbeState.ReconcilingWithdraw
            or FcChestProbeState.Depositing
            or FcChestProbeState.ReconcilingDeposit)
        {
            return false;
        }

        _prepareRequested = true;
        _executeRequested = false;
        _cancelRequested = false;
        _withdrawAttempted = false;
        _depositAttempted = false;
        _withdrawBefore = null;
        _depositNotBefore = default;
        _depositPlan = null;
        LastDelta = null;
        LastSnapshot = null;
        Preparation = null;
        FailureReason = null;
        IsArmed = false;
        State = FcChestProbeState.WaitingForChest;
        StatusText = "Prepare requested. Open the FC chest; all six chest containers and normal inventory pages must load.";
        return true;
    }

    public bool SetArmed(bool armed)
    {
        if (_disposed)
            return RejectSetArmed("SetArmed rejected: probe is disposed");

        if (State != FcChestProbeState.Prepared)
            return RejectSetArmed($"SetArmed rejected: probe state is {State}, expected Prepared");

        IsArmed = armed;
        StatusText = armed
            ? "Probe armed. Execute requires one more explicit click."
            : "Probe disarmed. No transfer can be dispatched.";
        return true;
    }

    private bool RejectSetArmed(string guardReason)
    {
        var diagnostic = $"{guardReason}; state={State}, armed={IsArmed}, prepared={Preparation is not null}.";
        FailureReason = diagnostic;
        StatusText = diagnostic;
        return false;
    }

    public bool RequestExecute()
    {
        if (_disposed)
            return RejectExecuteRequest("Execute rejected: probe is disposed");

        if (State != FcChestProbeState.Prepared)
            return RejectExecuteRequest($"Execute rejected: probe state is {State}, expected Prepared");

        if (!IsArmed)
            return RejectExecuteRequest("Execute rejected: probe is not armed");

        if (Preparation is null)
            return RejectExecuteRequest("Execute rejected: no prepared item selection is available");

        _executeRequested = true;
        StatusText = "Execute requested; the framework will reread source and destination before one withdrawal call.";
        return true;
    }

    private bool RejectExecuteRequest(string guardReason)
    {
        var diagnostic = $"{guardReason}; state={State}, armed={IsArmed}, prepared={Preparation is not null}.";
        FailureReason = diagnostic;
        StatusText = diagnostic;
        return false;
    }

    public void Cancel()
    {
        if (_disposed || State is FcChestProbeState.Passed
            or FcChestProbeState.Failed
            or FcChestProbeState.Cancelled)
            return;

        _prepareRequested = false;
        _executeRequested = false;
        IsArmed = false;
        _depositNotBefore = default;

        if (!_withdrawAttempted && !_depositAttempted)
        {
            _cancelRequested = false;
            State = FcChestProbeState.Cancelled;
            StatusText = "Probe cancelled. No transfer was dispatched.";
            return;
        }

        _cancelRequested = true;
        StatusText = "Cancellation requested after a physical transfer attempt; no compensation will be assumed. Reread manually if needed.";
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (_disposed)
            return;

        try
        {
            switch (State)
            {
                case FcChestProbeState.WaitingForChest when _prepareRequested:
                    TryPrepareOnFramework();
                    break;
                case FcChestProbeState.Prepared when _executeRequested:
                    State = FcChestProbeState.Withdrawing;
                    TryDispatchWithdrawal();
                    break;
                case FcChestProbeState.Withdrawing:
                    TryDispatchWithdrawal();
                    break;
                case FcChestProbeState.ReconcilingWithdraw:
                    ReconcileWithdrawal();
                    break;
                case FcChestProbeState.Depositing:
                    TryDispatchDeposit();
                    break;
                case FcChestProbeState.ReconcilingDeposit:
                    ReconcileDeposit();
                    break;
            }
        }
        catch (Exception ex)
        {
            if (_withdrawAttempted || _depositAttempted)
                FailAfterPhysicalAttempt($"Probe update failed: {ex.Message}");
            else
                Fail($"Probe update failed: {ex.Message}");
        }
    }

    private void TryPrepareOnFramework()
    {
        var read = ReadSnapshot();
        if (!read.IsComplete)
        {
            StatusText = read.FailureReason;
            return;
        }

        LastSnapshot = read.Snapshot;
        if (!FcChestProbeRules.TryPrepare(read.Snapshot!, out var preparation, out var reason))
        {
            Fail(reason);
            return;
        }

        Preparation = preparation;
        _prepareRequested = false;
        State = FcChestProbeState.Prepared;
        StatusText = $"Prepared one {preparation.Withdrawal.Item} from {preparation.Withdrawal.Source} to empty player slot {preparation.Withdrawal.Destination}. Arm and execute explicitly.";
    }

    private void TryDispatchWithdrawal()
    {
        if (FcChestProbeRules.ShouldSuppressFurtherTransfer(
                State,
                _cancelRequested,
                _withdrawAttempted))
        {
            if (_withdrawAttempted)
                FailAfterPhysicalAttempt("Withdrawal dispatch was requested more than once.");
            else
            {
                State = FcChestProbeState.Cancelled;
                StatusText = "Probe cancelled before withdrawal dispatch. No transfer was dispatched.";
            }
            return;
        }

        if (Preparation is not { } preparation)
        {
            Fail("Withdrawal has no prepared source and destination.");
            return;
        }

        var read = ReadSnapshot();
        if (!read.IsComplete)
        {
            Fail(read.FailureReason);
            return;
        }

        LastSnapshot = read.Snapshot;
        var validation = FcChestProbeRules.ValidatePreparedWithdrawal(preparation, read.Snapshot!);
        if (!validation.Accepted)
        {
            Fail(validation.Message);
            return;
        }

        _withdrawBefore = read.Snapshot;
        _withdrawAttempted = true;
        _executeRequested = false;
        if (!_dispatcher.TryMove(
                preparation.Withdrawal.Source,
                preparation.Withdrawal.Destination,
                out var failureReason))
        {
            FailAfterPhysicalAttempt(failureReason);
            return;
        }

        _reconciliationDeadline = DateTime.UtcNow + ReconciliationTimeout;
        State = FcChestProbeState.ReconcilingWithdraw;
        StatusText = "Withdrawal dispatched once. Waiting for an exact chest-minus-one/player-plus-one reread.";
    }

    private void ReconcileWithdrawal()
    {
        var read = ReadSnapshot();
        if (!read.IsComplete)
        {
            if (FcChestProbeRules.IsReconciliationTimedOut(DateTime.UtcNow, _reconciliationDeadline))
                FailAfterPhysicalAttempt($"Withdrawal reconciliation timed out: {read.FailureReason}");
            else
                StatusText = read.FailureReason;
            return;
        }

        LastSnapshot = read.Snapshot;
        if (_cancelRequested)
        {
            FailAfterPhysicalAttempt("Cancellation arrived after withdrawal dispatch. Deposit was suppressed; manually reconcile the exact held item.");
            return;
        }

        if (_withdrawBefore is not { } before || Preparation is not { } preparation)
        {
            FailAfterPhysicalAttempt("Withdrawal baseline was lost; manual reconciliation is required.");
            return;
        }

        var validation = FcChestProbeRules.ValidateObservedDelta(
            before,
            read.Snapshot!,
            preparation.Withdrawal,
            FcChestTransferDirection.Withdrawal);
        LastDelta = validation;
        if (!validation.Accepted)
        {
            if (validation.Failure == FcChestDeltaFailure.NoDelta
                && !FcChestProbeRules.IsReconciliationTimedOut(DateTime.UtcNow, _reconciliationDeadline))
            {
                StatusText = validation.Message;
                return;
            }

            FailAfterPhysicalAttempt(validation.Message);
            return;
        }

        if (!FcChestProbeRules.TryChooseDepositDestination(
                read.Snapshot!,
                preparation.Withdrawal.Source,
                out var depositDestination,
                out var reason))
        {
            FailAfterPhysicalAttempt(reason);
            return;
        }

        _depositPlan = new FcChestTransferPlan(
            preparation.Withdrawal.Destination,
            depositDestination,
            preparation.Withdrawal.Item,
            preparation.Withdrawal.Quantity);
        _depositNotBefore = DateTime.UtcNow + FcChestTransferTiming.PhysicalInterActionDelay;
        State = FcChestProbeState.Depositing;
        StatusText = "Exact withdrawal observed. Waiting for 500 ms inter-action delay before revalidating and depositing the held item.";
    }

    private void TryDispatchDeposit()
    {
        if (FcChestProbeRules.ShouldSuppressFurtherTransfer(
                State,
                _cancelRequested,
                _depositAttempted))
        {
            FailAfterPhysicalAttempt("Cancellation arrived after withdrawal. Deposit was suppressed; manually reconcile the exact held item.");
            return;
        }

        if (_depositPlan is not { } deposit)
        {
            FailAfterPhysicalAttempt("Deposit has no confirmed held item and destination; manual reconciliation is required.");
            return;
        }

        if (!FcChestTransferTiming.IsDispatchReady(DateTime.UtcNow, _depositNotBefore))
        {
            StatusText = "Exact withdrawal observed. Waiting for 500 ms inter-action delay before revalidating and depositing the held item.";
            return;
        }

        var read = ReadSnapshot();
        if (!read.IsComplete)
        {
            FailAfterPhysicalAttempt($"Chest became unavailable before deposit: {read.FailureReason}");
            return;
        }

        LastSnapshot = read.Snapshot;
        var validation = FcChestProbeRules.ValidatePreparedDeposit(deposit, read.Snapshot!);
        if (!validation.Accepted)
        {
            FailAfterPhysicalAttempt(validation.Message);
            return;
        }

        _withdrawBefore = read.Snapshot;
        _depositAttempted = true;
        if (!_dispatcher.TryMove(deposit.Source, deposit.Destination, out var failureReason))
        {
            FailAfterPhysicalAttempt(failureReason);
            return;
        }

        _reconciliationDeadline = DateTime.UtcNow + ReconciliationTimeout;
        State = FcChestProbeState.ReconcilingDeposit;
        StatusText = "Deposit dispatched once. Waiting for an exact player-minus-one/chest-plus-one reread.";
    }

    private void ReconcileDeposit()
    {
        var read = ReadSnapshot();
        if (!read.IsComplete)
        {
            if (FcChestProbeRules.IsReconciliationTimedOut(DateTime.UtcNow, _reconciliationDeadline))
                FailAfterPhysicalAttempt($"Deposit reconciliation timed out: {read.FailureReason}");
            else
                StatusText = read.FailureReason;
            return;
        }

        LastSnapshot = read.Snapshot;
        if (_cancelRequested)
        {
            FailAfterPhysicalAttempt("Cancellation arrived after deposit dispatch; manually verify the final FC chest and player state.");
            return;
        }

        if (_depositPlan is not { } deposit || _withdrawBefore is not { } before)
        {
            FailAfterPhysicalAttempt("Deposit baseline was lost; manual reconciliation is required.");
            return;
        }

        var validation = FcChestProbeRules.ValidateObservedDelta(
            before,
            read.Snapshot!,
            deposit,
            FcChestTransferDirection.Deposit);
        LastDelta = validation;
        if (!validation.Accepted)
        {
            if (validation.Failure == FcChestDeltaFailure.NoDelta
                && !FcChestProbeRules.IsReconciliationTimedOut(DateTime.UtcNow, _reconciliationDeadline))
            {
                StatusText = validation.Message;
                return;
            }

            FailAfterPhysicalAttempt(validation.Message);
            return;
        }

        State = FcChestProbeState.Passed;
        IsArmed = false;
        _depositNotBefore = default;
        StatusText = "FC chest probe passed: both physical transfers produced exact paired deltas.";
    }

    private FcChestReadResult ReadSnapshot()
    {
        try
        {
            var result = _reader.Read();
            if (result.Snapshot is not null)
                LastSnapshot = result.Snapshot;
            return result;
        }
        catch (Exception ex)
        {
            return FcChestReadResult.Incomplete($"FC chest snapshot read failed: {ex.Message}");
        }
    }

    private void Fail(string reason)
    {
        _prepareRequested = false;
        _executeRequested = false;
        IsArmed = false;
        _depositNotBefore = default;
        FailureReason = reason;
        State = FcChestProbeState.Failed;
        StatusText = $"Probe failed: {reason}";
    }

    private void FailAfterPhysicalAttempt(string reason)
    {
        Fail($"{reason} Manual reconciliation required; no further transfer will be attempted.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _framework.Update -= OnFrameworkUpdate;
        _prepareRequested = false;
        _executeRequested = false;
        IsArmed = false;
        _depositNotBefore = default;

        if ((_withdrawAttempted || _depositAttempted) && State != FcChestProbeState.Passed)
        {
            FailureReason = "Plugin unloaded after a physical transfer attempt; manual FC chest/player reconciliation is required.";
            State = FcChestProbeState.Failed;
            StatusText = FailureReason;
        }
    }
}
