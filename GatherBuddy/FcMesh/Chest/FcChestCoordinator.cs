using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.State;
using Protocol = GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.Chest;

public enum FcChestRouteStatus : byte
{
    Unavailable,
    Starting,
    Traveling,
    AtDestination,
    Failed,
}

public interface IFcChestRouteAdapter
{
    bool IsAvailable { get; }
    FcChestRouteStatus Status { get; }
    string LastError { get; }
    bool TryStart(FcEstateChestLocationRecord location, out string error);
    bool TryStartPublic(FcPublicChestDestination destination, out string error);
    bool TryOpenChest(FcEstateChestLocationRecord location, out string error);
    bool TryOpenPublic(FcPublicChestDestination destination, out string error);
    void Tick();
    void Stop();
}

public sealed class FcUnavailableChestRouteAdapter : IFcChestRouteAdapter
{
    public bool IsAvailable => false;
    public FcChestRouteStatus Status => FcChestRouteStatus.Unavailable;
    public string LastError => "No typed FC housing route adapter is available.";
    public bool TryStart(FcEstateChestLocationRecord location, out string error)
    {
        error = LastError;
        return false;
    }
    public bool TryOpenChest(FcEstateChestLocationRecord location, out string error)
    {
        error = LastError;
        return false;
    }
    public bool TryStartPublic(FcPublicChestDestination destination, out string error)
    {
        error = LastError;
        return false;
    }
    public bool TryOpenPublic(FcPublicChestDestination destination, out string error)
    {
        error = LastError;
        return false;
    }
    public void Tick() { }
    public void Stop() { }
}

public sealed class FcCallbackChestRouteAdapter : IFcChestRouteAdapter
{
    private readonly Func<FcEstateChestLocationRecord, bool> _start;
    private readonly Func<bool> _atDestination;
    private readonly Func<string?> _error;
    private readonly Action _stop;

    public FcCallbackChestRouteAdapter(
        Func<FcEstateChestLocationRecord, bool> start,
        Func<bool> atDestination,
        Action? stop = null,
        Func<string?>? error = null)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _atDestination = atDestination ?? throw new ArgumentNullException(nameof(atDestination));
        _stop = stop ?? (() => { });
        _error = error ?? (() => null);
    }

    public bool IsAvailable => true;
    public FcChestRouteStatus Status { get; private set; } = FcChestRouteStatus.Unavailable;
    public string LastError { get; private set; } = string.Empty;

    public bool TryStart(FcEstateChestLocationRecord location, out string error)
    {
        try
        {
            if (!_start(location))
            {
                error = _error() ?? "FC housing route was rejected.";
                LastError = error;
                Status = FcChestRouteStatus.Failed;
                return false;
            }
            error = string.Empty;
            LastError = string.Empty;
            Status = FcChestRouteStatus.Traveling;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            LastError = error;
            Status = FcChestRouteStatus.Failed;
            return false;
        }
    }

    public bool TryOpenChest(FcEstateChestLocationRecord location, out string error)
    {
        error = string.Empty;
        return true;
    }

    public bool TryStartPublic(FcPublicChestDestination destination, out string error)
    {
        error = "The callback chest route has no public destination binding.";
        return false;
    }

    public bool TryOpenPublic(FcPublicChestDestination destination, out string error)
    {
        error = "The callback chest route has no public destination binding.";
        return false;
    }

    public void Tick()
    {
        if (Status != FcChestRouteStatus.Traveling)
            return;
        try
        {
            if (_atDestination())
                Status = FcChestRouteStatus.AtDestination;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Status = FcChestRouteStatus.Failed;
        }
    }

    public void Stop()
    {
        try { _stop(); }
        catch (Exception exception) { LastError = exception.Message; }
        Status = FcChestRouteStatus.Unavailable;
    }
}

public sealed record FcAtomicTransferCommit(
    Guid OperationId,
    Guid SessionId,
    ulong SessionGeneration,
    Guid? PurposeListId,
    FcInventoryTransferKind Kind,
    FcInventoryTransferOutcome Outcome,
    FcItemQuantityMap ActualTransferred,
    ChestSnapshotRecord ChestAfter,
    FcItemQuantityMap PlayerAfter,
    FcItemQuantityMap? WorkerHeldBefore = null,
    WorkerSessionRecord? WorkerBefore = null);

public enum FcAtomicTransferCommitStatus : byte
{
    Accepted,
    Blocked,
}

public readonly record struct FcAtomicTransferCommitResult(
    FcAtomicTransferCommitStatus Status,
    string Message)
{
    public bool Accepted => Status == FcAtomicTransferCommitStatus.Accepted;
}

public sealed record FcChestCoordinatorDiagnostics(
    FcChestCoordinatorState State,
    FcChestLoadState LoadState,
    FcChestRouteStatus RouteStatus,
    bool HasPendingTransfer,
    Guid? PendingOperationId,
    string LastError);

public enum FcChestCoordinatorState : byte
{
    Idle,
    CheckingChest,
    Ready,
    Withdrawing,
    Depositing,
    Reconciling,
    CleanupPending,
    Blocked,
}

/// <summary>
/// Framework-tick state machine for chest knowledge and indivisible physical
/// transfers. It never assumes a transfer occurred until a complete reread
/// proves the paired chest/player delta.
/// </summary>
public sealed class FcChestCoordinator : IDisposable
{
    private sealed record PendingTransfer(
        Guid OperationId,
        Guid SessionId,
        ulong SessionGeneration,
        Guid? PurposeListId,
        FcInventoryTransferKind Kind,
        FcChestSnapshot Before,
        IReadOnlyList<ItemTransferRequest> Requested,
        FcItemQuantityMap WorkerHeldBefore,
        WorkerSessionRecord? WorkerBefore,
        DateTime Deadline,
        Task<FcTransferResult>? DispatchTask,
        bool DispatchAttempted);

    private readonly IFcChestAdapter _adapter;
    private readonly IFcChestRouteAdapter _route;
    private readonly FcPendingTransferJournal _journal;
    private readonly IFcPendingTransferJournalStore? _journalStore;
    private readonly Action<FcChestSnapshot> _onCompleteSnapshot;
    private readonly Func<FcAtomicTransferCommit, FcAtomicTransferCommitResult> _commitTransfer;
    private readonly Func<FcChestLocationProjection> _locationProvider;
    private readonly Func<FcChestSnapshot, ChestSnapshotRecord> _chestRecordFactory;
    private readonly Func<Guid> _operationIdProvider;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _reconciliationTimeout;
    private PendingTransfer? _pendingTransfer;
    private DateTime _nextPhysicalDispatchNotBefore;
    private FcPublicChestDestination? _publicDestination;
    private bool _checkRequested;
    private bool _stopRequested;
    private bool _disposed;
    private bool _journalBlocked;
    private string _lastError = string.Empty;
    private FcChestSnapshot? _lastSnapshot;

    public FcChestCoordinator(
        IFcChestAdapter adapter,
        IFcChestRouteAdapter? route,
        FcPendingTransferJournal journal,
        Action<FcChestSnapshot> onCompleteSnapshot,
        Func<FcAtomicTransferCommit, FcAtomicTransferCommitResult> commitTransfer,
        Func<FcChestLocationProjection> locationProvider,
        Func<Guid>? operationIdProvider = null,
        TimeSpan? reconciliationTimeout = null,
        IFcPendingTransferJournalStore? journalStore = null,
        Func<FcChestSnapshot, ChestSnapshotRecord>? chestRecordFactory = null,
        Func<DateTime>? utcNow = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _route = route ?? new FcUnavailableChestRouteAdapter();
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _journalStore = journalStore;
        _onCompleteSnapshot = onCompleteSnapshot ?? throw new ArgumentNullException(nameof(onCompleteSnapshot));
        _commitTransfer = commitTransfer ?? throw new ArgumentNullException(nameof(commitTransfer));
        _locationProvider = locationProvider ?? throw new ArgumentNullException(nameof(locationProvider));
        _chestRecordFactory = chestRecordFactory ?? ToRecord;
        _operationIdProvider = operationIdProvider ?? Guid.NewGuid;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _reconciliationTimeout = reconciliationTimeout ?? TimeSpan.FromSeconds(15);
        if (_reconciliationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(reconciliationTimeout));
        if (_journalStore is not null)
        {
            FcPendingTransferJournalState? persisted = null;
            FcPendingTransferJournal? restoredJournal = null;
            try { persisted = _journalStore.Load(); }
            catch (Exception exception)
            {
                _journalBlocked = true;
                _lastError = $"Pending FC transfer journal could not be loaded: {exception.Message}";
            }
            if (!_journalBlocked && persisted is null)
            {
                _journalBlocked = true;
                _lastError = "Pending FC transfer journal is corrupt; physical reconciliation is required.";
            }
            else if (!_journalBlocked && !FcPendingTransferJournal.TryRestore(persisted!, out restoredJournal))
            {
                _journalBlocked = true;
                _lastError = "Pending FC transfer journal is corrupt; physical reconciliation is required.";
            }
            else if (!_journalBlocked && restoredJournal is not null)
            {
                // The journal is authoritative evidence that a dispatch may
                // have crossed the game boundary. Do not resume execution
                // until a complete physical reread proves the outcome.
                _journal = restoredJournal;
            }
        }
        State = _journalBlocked || _journal.HasPending
            ? FcChestCoordinatorState.CleanupPending
            : FcChestCoordinatorState.Idle;
    }

    public FcChestCoordinatorState State { get; private set; }
    public FcChestSnapshot? LastSnapshot => _lastSnapshot;
    public ChestSnapshotRecord? LastSnapshotRecord
        => _lastSnapshot is { } snapshot ? _chestRecordFactory(snapshot) : null;
    public bool HasPendingTransfer => _pendingTransfer is not null || _journal.HasPending;
    public bool AllowsWorkerPublication => !HasPendingTransfer;
    public string LastError => _lastError;

    public bool SelectPublicChestDestination(FcPublicChestDestination destination)
    {
        if (_disposed || destination is null || HasPendingTransfer)
            return false;
        _route.Stop();
        _publicDestination = destination;
        RequestCheck();
        return true;
    }

    public void StopSelectedRoute()
    {
        if (_disposed || HasPendingTransfer)
            return;
        _route.Stop();
        _publicDestination = null;
        _checkRequested = false;
        if (State == FcChestCoordinatorState.CheckingChest)
            State = FcChestCoordinatorState.Idle;
    }

    public FcChestCoordinatorDiagnostics Diagnostics
    {
        get
        {
            var operationId = _pendingTransfer?.OperationId;
            if (operationId is null && _journal.Pending.Count != 0)
                operationId = _journal.Pending.Keys.First();
            return new(State, _adapter.GetLoadState(), _route.Status, HasPendingTransfer,
                operationId, _lastError);
        }
    }

    public void EnsureChestKnown(bool projectedSnapshotFresh)
    {
        if (_disposed || HasPendingTransfer)
            return;
        if (projectedSnapshotFresh)
        {
            State = FcChestCoordinatorState.Ready;
            return;
        }
        RequestCheck();
    }

    public void RequestCheck()
    {
        if (_disposed || HasPendingTransfer || _journalBlocked)
            return;
        _checkRequested = true;
        State = FcChestCoordinatorState.CheckingChest;
    }

    public bool StartWithdrawal(
        Guid sessionId,
        ulong sessionGeneration,
        Guid? purposeListId,
        IReadOnlyList<ItemTransferRequest> items,
        FcItemQuantityMap? workerHeldBefore = null,
        WorkerSessionRecord? workerBefore = null)
        => StartTransfer(
            FcInventoryTransferKind.Withdraw,
            sessionId,
            sessionGeneration,
            purposeListId,
            items,
            workerHeldBefore,
            workerBefore);

    public bool StartDeposit(
        Guid sessionId,
        ulong sessionGeneration,
        Guid? purposeListId,
        IReadOnlyList<ItemTransferRequest> items,
        FcItemQuantityMap? workerHeldBefore = null,
        WorkerSessionRecord? workerBefore = null)
        => StartTransfer(
            FcInventoryTransferKind.Deposit,
            sessionId,
            sessionGeneration,
            purposeListId,
            items,
            workerHeldBefore,
            workerBefore);

    public void RequestStop()
    {
        _stopRequested = true;
        if (_pendingTransfer is { DispatchAttempted: false } pendingBeforeDispatch)
        {
            // Reservation exists, but the indivisible game action has not
            // crossed the dispatch boundary. Cancel only the reservation;
            // stopping never turns this into a travel/deposit operation.
            _journal.Complete(pendingBeforeDispatch.OperationId);
            _ = PersistJournal();
            _pendingTransfer = null;
            _route.Stop();
            _checkRequested = false;
            State = FcChestCoordinatorState.Idle;
            return;
        }
        if (_pendingTransfer is null)
        {
            _route.Stop();
            _checkRequested = false;
            State = FcChestCoordinatorState.Idle;
        }
    }

    /// <summary>
    /// Reconciles a journal loaded after a crash. A complete reread matching
    /// both persisted fingerprints proves that no observable transfer remains
    /// to publish. Any mismatch is deliberately blocked because the journal
    /// has no authority to invent compensation quantities.
    /// </summary>
    public bool RecoverPending()
    {
        if (_disposed || _journalBlocked)
            return false;
        if (!_journal.HasPending)
        {
            State = FcChestCoordinatorState.Ready;
            return true;
        }
        if (!_adapter.TryReadCompleteSnapshot(out var snapshot))
        {
            _lastError = "Pending FC transfer recovery requires a complete chest and inventory reread.";
            State = FcChestCoordinatorState.CleanupPending;
            return false;
        }
        _lastSnapshot = snapshot;
        var pendingOperations = _journal.Pending.Values.ToArray();
        foreach (var operation in pendingOperations)
        {
            if (operation.PhysicalBefore is { } physicalBefore
                && operation.Requested is { Length: 1 } requested)
            {
                FcChestSnapshot before;
                try { before = physicalBefore.ToSnapshot(); }
                catch (Exception exception)
                {
                    _lastError = $"Pending FC transfer preimage is invalid: {exception.Message}";
                    State = FcChestCoordinatorState.CleanupPending;
                    return false;
                }
                var delta = DeriveDelta(before, snapshot, requested[0].Item, operation.Kind);
                if (!delta.Accepted)
                {
                    _lastError = delta.Message;
                    State = FcChestCoordinatorState.CleanupPending;
                    return false;
                }
                if (delta.Quantity.Entries.Length != 0)
                {
                    var commit = new FcAtomicTransferCommit(
                        operation.OperationId,
                        operation.SessionId,
                        operation.SessionGeneration,
                        operation.PurposeListId,
                        operation.Kind,
                        FcInventoryTransferOutcome.Committed,
                        delta.Quantity,
                        _chestRecordFactory(snapshot),
                        ToPlayerQuantityMap(snapshot),
                        operation.WorkerHeldBefore,
                        operation.WorkerBefore);
                    var published = _commitTransfer(commit);
                    if (!published.Accepted)
                    {
                        _lastError = published.Message;
                        State = FcChestCoordinatorState.CleanupPending;
                        return false;
                    }
                }
                _journal.Complete(operation.OperationId);
                continue;
            }
            var chestFingerprint = Fingerprint(snapshot, chest: true);
            var workerFingerprint = Fingerprint(snapshot, chest: false);
            if (!string.Equals(operation.ChestFingerprint, chestFingerprint, StringComparison.Ordinal)
                || !string.Equals(operation.WorkerFingerprint, workerFingerprint, StringComparison.Ordinal))
            {
                _lastError = "Pending FC transfer recovery observed a physical mismatch; guessed compensation is blocked.";
                State = FcChestCoordinatorState.CleanupPending;
                return false;
            }

            // Legacy reservations predate the persisted physical preimage.
            // A complete fingerprint match is the only safe recovery proof
            // available for those records; consume that reservation before
            // persisting the cleared journal.
            _journal.Complete(operation.OperationId);
        }
        if (!PersistJournal())
        {
            foreach (var operation in pendingOperations)
            {
                if (!_journal.Pending.ContainsKey(operation.OperationId))
                    _journal.Begin(operation);
            }
            State = FcChestCoordinatorState.CleanupPending;
            return false;
        }
        State = FcChestCoordinatorState.Ready;
        return true;
    }

    public void Resume()
    {
        if (_disposed || _journalBlocked || _journal.HasPending)
            return;
        _stopRequested = false;
        State = FcChestCoordinatorState.Idle;
    }

    public void Tick()
    {
        if (_disposed)
            return;
        try
        {
            if (_journalBlocked)
            {
                State = FcChestCoordinatorState.CleanupPending;
                return;
            }
            if (_pendingTransfer is not null)
            {
                TickTransfer();
                return;
            }
            if (_journal.HasPending)
            {
                State = FcChestCoordinatorState.CleanupPending;
                return;
            }
            if (_stopRequested)
            {
                _route.Stop();
                State = FcChestCoordinatorState.Idle;
                return;
            }
            if (!_checkRequested)
                return;
            TickCheck();
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            State = FcChestCoordinatorState.Blocked;
            if (_pendingTransfer is not null)
                State = FcChestCoordinatorState.CleanupPending;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _route.Stop();
        if (_pendingTransfer is not null)
        {
            _lastError = "Plugin disposed during an FC transfer; complete physical reconciliation is required.";
            State = FcChestCoordinatorState.CleanupPending;
        }
    }

    private void TickCheck()
    {
        if (_adapter.TryReadCompleteSnapshot(out var snapshot))
        {
            _lastSnapshot = snapshot;
            _checkRequested = false;
            _onCompleteSnapshot(snapshot);
            State = FcChestCoordinatorState.Ready;
            return;
        }

        if (!_route.IsAvailable)
        {
            _lastError = "FC chest snapshot is unknown and no housing route adapter is available.";
            State = FcChestCoordinatorState.Blocked;
            return;
        }

        var location = _locationProvider();
        if (location.HasUsableLocation && location.Location is { } record)
        {
            if (_route.Status is FcChestRouteStatus.Unavailable or FcChestRouteStatus.Failed)
            {
                if (!_route.TryStart(record, out var error))
                {
                    _lastError = error;
                    State = FcChestCoordinatorState.Blocked;
                    return;
                }
            }
            _route.Tick();
            if (_route.Status == FcChestRouteStatus.AtDestination
                && !_adapter.IsChestOpen
                && !_route.TryOpenChest(record, out var openError))
            {
                _lastError = openError;
                State = FcChestCoordinatorState.Blocked;
            }
            return;
        }
        if (_publicDestination is null)
        {
            _lastError = "FC chest location is unknown, forked, incompatible, or unpublished.";
            State = FcChestCoordinatorState.Blocked;
            return;
        }
        if (_route.Status is FcChestRouteStatus.Unavailable or FcChestRouteStatus.Failed)
        {
            if (!_route.TryStartPublic(_publicDestination, out var error))
            {
                _lastError = error;
                State = FcChestCoordinatorState.Blocked;
                return;
            }
        }
        _route.Tick();
        if (_route.Status == FcChestRouteStatus.AtDestination
            && !_adapter.IsChestOpen
            && !_route.TryOpenPublic(_publicDestination, out var publicOpenError))
        {
            _lastError = publicOpenError;
            State = FcChestCoordinatorState.Blocked;
        }
    }

    private bool StartTransfer(
        FcInventoryTransferKind kind,
        Guid sessionId,
        ulong sessionGeneration,
        Guid? purposeListId,
        IReadOnlyList<ItemTransferRequest> items,
        FcItemQuantityMap? workerHeldBefore,
        WorkerSessionRecord? workerBefore)
    {
        if (_disposed || _stopRequested || _pendingTransfer is not null || _journal.HasPending)
            return false;
        if (sessionId == Guid.Empty || sessionGeneration == 0
            || items is null || items.Count != 1 || !items[0].IsValid)
            return false;
        if (!_adapter.TryReadCompleteSnapshot(out var before))
        {
            RequestCheck();
            return false;
        }
        var operationId = _operationIdProvider();
        if (operationId == Guid.Empty)
            return false;
        var pre = new FcTransferPreOperation(
            operationId,
            sessionId,
            sessionGeneration,
            kind,
            purposeListId,
            Fingerprint(before, chest: true),
            Fingerprint(before, chest: false),
            new CrystalQuantityMap(before.GetCrystalTotals().Select(value => new CrystalQuantityEntry(value.Key, checked((int)value.Value)))),
            DateTimeOffset.UtcNow,
            FcTransferPhysicalSnapshot.Capture(before),
            items.ToArray(),
            workerHeldBefore ?? FcItemQuantityMap.Empty,
            workerBefore);
        try
        {
            _journal.Begin(pre);
            if (!PersistJournal())
            {
                _journal.Complete(operationId);
                State = FcChestCoordinatorState.CleanupPending;
                return false;
            }
        }
        catch (Exception exception)
        {
            _lastError = $"FC transfer reservation persistence failed: {exception.Message}";
            State = FcChestCoordinatorState.CleanupPending;
            return false;
        }
        _lastSnapshot = before;
        _pendingTransfer = new PendingTransfer(
            operationId,
            sessionId,
            sessionGeneration,
            purposeListId,
            kind,
            before,
            items.ToArray(),
            workerHeldBefore ?? FcItemQuantityMap.Empty,
            workerBefore,
            DateTime.UtcNow + _reconciliationTimeout,
            null,
            false);
        State = kind == FcInventoryTransferKind.Withdraw
            ? FcChestCoordinatorState.Withdrawing
            : FcChestCoordinatorState.Depositing;
        return true;
    }

    private void TickTransfer()
    {
        if (_pendingTransfer is not { } pending)
            return;
        if (!pending.DispatchAttempted)
        {
            var now = _utcNow();
            if (!FcChestTransferTiming.IsDispatchReady(now, _nextPhysicalDispatchNotBefore))
                return;

            _nextPhysicalDispatchNotBefore = now + FcChestTransferTiming.PhysicalInterActionDelay;
            var cancellation = CancellationToken.None;
            var dispatchTask = pending.Kind == FcInventoryTransferKind.Withdraw
                ? _adapter.WithdrawAsync(pending.Requested, cancellation)
                : _adapter.DepositAsync(pending.Requested, cancellation);
            _pendingTransfer = pending with { DispatchTask = dispatchTask, DispatchAttempted = true };
            return;
        }
        if (pending.DispatchTask is not { IsCompleted: true } task)
        {
            if (DateTime.UtcNow >= pending.Deadline)
                FailPending("FC transfer dispatch did not complete before the reconciliation deadline.");
            return;
        }
        FcTransferResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            // A managed dispatch exception does not prove that the native/UI
            // boundary was untouched. Treat it like a failed acknowledgement
            // and require the same complete physical reread as every other
            // post-dispatch outcome.
            result = new FcTransferResult(
                FcTransferResultStatus.Failed,
                $"FC transfer dispatch raised an exception: {exception.Message}",
                pending.Requested);
        }
        if (!_adapter.TryReadCompleteSnapshot(out var after))
        {
            if (DateTime.UtcNow >= pending.Deadline)
                FailPending(result.Message.Length == 0
                    ? "FC transfer completed without a complete post-transfer reconciliation."
                    : result.Message);
            else
                State = FcChestCoordinatorState.Reconciling;
            return;
        }
        _lastSnapshot = after;
        var delta = DeriveDelta(pending.Before, after, pending.Requested[0].Item, pending.Kind);
        if (!delta.Accepted)
        {
            if (DateTime.UtcNow >= pending.Deadline)
                FailPending(delta.Message);
            else
                State = FcChestCoordinatorState.Reconciling;
            return;
        }
        if (!_journal.TryGet(pending.OperationId, out var reserved))
        {
            FailPending("FC transfer reservation disappeared before completion.");
            return;
        }
        var commit = new FcAtomicTransferCommit(
            pending.OperationId,
            pending.SessionId,
            pending.SessionGeneration,
            pending.PurposeListId,
            pending.Kind,
            delta.Quantity.Entries.Length == 0
                ? FcInventoryTransferOutcome.ReconciledFailure
                : FcInventoryTransferOutcome.Committed,
            delta.Quantity,
            _chestRecordFactory(after),
            ToPlayerQuantityMap(after),
            reserved.WorkerHeldBefore,
            reserved.WorkerBefore);
        var committed = _commitTransfer(commit);
        if (!committed.Accepted)
        {
            FailPending(committed.Message);
            return;
        }
        _journal.Complete(pending.OperationId);
        if (!PersistJournal())
        {
            // Keep durable pre-operation evidence after a post-commit local
            // persistence failure. The operation ID makes replay idempotent;
            // clearing it would lose the crash-recovery boundary.
            _journal.Begin(reserved);
            FailPending("FC transfer committed but pending journal cleanup could not be persisted.");
            return;
        }
        _pendingTransfer = null;
        State = FcChestCoordinatorState.Ready;
    }

    private void FailPending(string reason)
    {
        _lastError = reason;
        State = FcChestCoordinatorState.CleanupPending;
        _pendingTransfer = null;
        // Journal remains pending: a later recovery must reread physical state.
    }

    private bool PersistJournal()
    {
        if (_journalStore is null)
            return true;
        try
        {
            _journalStore.Save(_journal.ExportState());
            return true;
        }
        catch (Exception exception)
        {
            _lastError = $"Pending FC transfer journal persistence failed: {exception.Message}";
            _journalBlocked = true;
            return false;
        }
    }

    private static FcDeltaResult DeriveDelta(
        FcChestSnapshot before,
        FcChestSnapshot after,
        FcChestItemKey key,
        FcInventoryTransferKind direction)
    {
        var beforeChest = new FcItemQuantityMap(before.GetChestTotals().Select(item =>
            new ItemQuantityEntry(item.Key.ItemId, item.Key.IsHq ? Protocol.FcItemQuality.Hq : Protocol.FcItemQuality.Nq, checked((int)item.Value))));
        var afterChest = new FcItemQuantityMap(after.GetChestTotals().Select(item =>
            new ItemQuantityEntry(item.Key.ItemId, item.Key.IsHq ? Protocol.FcItemQuality.Hq : Protocol.FcItemQuality.Nq, checked((int)item.Value))));
        var beforePlayer = new FcItemQuantityMap(before.GetPlayerTotals().Select(item =>
            new ItemQuantityEntry(item.Key.ItemId, item.Key.IsHq ? Protocol.FcItemQuality.Hq : Protocol.FcItemQuality.Nq, checked((int)item.Value))));
        var afterPlayer = new FcItemQuantityMap(after.GetPlayerTotals().Select(item =>
            new ItemQuantityEntry(item.Key.ItemId, item.Key.IsHq ? Protocol.FcItemQuality.Hq : Protocol.FcItemQuality.Nq, checked((int)item.Value))));
        var itemKey = new Protocol.FcQuantityKey(key.ItemId, key.IsHq ? Protocol.FcItemQuality.Hq : Protocol.FcItemQuality.Nq);
        var chestDelta = direction == FcInventoryTransferKind.Withdraw
            ? beforeChest.Get(itemKey) - afterChest.Get(itemKey)
            : afterChest.Get(itemKey) - beforeChest.Get(itemKey);
        var playerDelta = direction == FcInventoryTransferKind.Withdraw
            ? afterPlayer.Get(itemKey) - beforePlayer.Get(itemKey)
            : beforePlayer.Get(itemKey) - afterPlayer.Get(itemKey);
        var allChanges = FcChestProbeRules.GetChanges(before, after);
        if (allChanges.Count == 0)
            return FcDeltaResult.AcceptedFailure;
        var changes = allChanges
            .Where(change => change.Before?.Item == key || change.After?.Item == key)
            .ToArray();
        if (chestDelta <= 0 || chestDelta != playerDelta)
            return FcDeltaResult.Invalid("Complete reread did not show one equal chest/player quantity delta.");
        if (changes.Length != 2 || allChanges.Count != 2)
            return FcDeltaResult.Invalid("Transfer reread included unrelated or missing slot mutations.");
        return new(true, new FcItemQuantityMap([new ItemQuantityEntry(key.ItemId, itemKey.Quality, chestDelta)]), string.Empty);
    }

    private static string Fingerprint(FcChestSnapshot snapshot, bool chest)
    {
        var items = (chest ? snapshot.ChestItems : snapshot.PlayerItems).Values
            .OrderBy(item => item.Address.Container)
            .ThenBy(item => item.Address.Slot)
            .Select(item => $"{item.Address.Container}:{item.Address.Slot}:{item.Item.ItemId}:{item.Item.IsHq}:{item.Quantity}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", items)))).ToLowerInvariant();
    }

    private static FcItemQuantityMap ToPlayerQuantityMap(FcChestSnapshot snapshot)
        => new(snapshot.GetPlayerTotals().Select(item =>
            new ItemQuantityEntry(
                item.Key.ItemId,
                item.Key.IsHq ? Protocol.FcItemQuality.Hq : Protocol.FcItemQuality.Nq,
                checked((int)item.Value))));

    private static ChestSnapshotRecord ToRecord(FcChestSnapshot snapshot)
        => new(
            new FcRecordHeader(
                FcProtocolVersion.Current,
                FcProtocolVersion.CurrentSchema,
                FcRecordTypes.ChestSnapshot,
                Guid.Parse("3bda7fd9-01f6-4d9f-a3b8-3b9f8c3aa7b2"),
                "local-reconciliation",
                1),
            true,
            FcRecordValidator.CompleteChestPageMask,
            snapshot.GetChestTotals().Select(item => new ItemQuantityEntry(
                item.Key.ItemId,
                item.Key.IsHq ? Protocol.FcItemQuality.Hq : Protocol.FcItemQuality.Nq,
                checked((int)item.Value))).ToArray(),
            new CrystalQuantityMap(snapshot.GetCrystalTotals().Select(item => new CrystalQuantityEntry(item.Key, checked((int)item.Value)))));

    private readonly record struct FcDeltaResult(bool Accepted, FcItemQuantityMap Quantity, string Message)
    {
        public static FcDeltaResult AcceptedFailure { get; }
            = new(true, FcItemQuantityMap.Empty, string.Empty);

        public static FcDeltaResult Invalid(string message)
            => new(false, FcItemQuantityMap.Empty, message);
    }
}
