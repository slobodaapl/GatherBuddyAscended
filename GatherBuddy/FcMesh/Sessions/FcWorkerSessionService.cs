using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Native;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Sessions;

/// <summary>
/// Author-owned WorkerSession register service. Framework methods only capture
/// immutable game facts and enqueue an absolute command. Persistence and native
/// Put happen on the contained worker; no framework tick waits for either.
/// </summary>
public sealed class FcWorkerSessionService : IDisposable
{
    public const string WorkerRegisterSuffix = "/worker";
    public const int MaxLogicalEntries = FcRecordValidator.MaxArrayEntries;

    private readonly object _gate = new();
    private readonly IFcWorkerSessionStateStore _stateStore;
    private readonly IFcPublicationTransport _transport;
    private readonly Func<string?> _authorScopeProvider;
    private readonly Func<string?> _authorIdProvider;
    private readonly Func<FcCompatibilityContext> _compatibilityProvider;
    private readonly Func<IReadOnlyList<FcPublicListView>> _publicListProvider;
    private readonly Func<CharacterIdentity?> _characterProvider;
    private readonly Func<string> _worldFingerprintProvider;
    private readonly Func<Guid> _sessionIdProvider;
    private readonly Func<IEnumerable<FcQuantityKey>, FcWorkerPhysicalInventorySnapshotResult>? _physicalSnapshotProvider;
    private readonly IFcClock _clock;
    private readonly Channel<FcWorkerWork> _commands = Channel.CreateUnbounded<FcWorkerWork>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private FcWorkerSessionState _state;
    private FcWorkerSessionLoadStatus _loadStatus;
    private WorkerSessionRecord? _desired;
    private WorkerSessionRecord? _accepted;
    private FcHlcTimestamp? _acceptedHlc;
    private FcContributionLedger? _ledger;
    private HashSet<FcQuantityKey> _dependencyClosure = new();
    private bool _recoveryRequired;
    private bool _queuedPending;
    private bool _disposed;
    private bool _forked;
    private bool _rebindInFlight;
    private bool _reschedulePending;
    private int _pendingCommands;
    private string _lastError = string.Empty;

    public FcWorkerSessionService(
        IFcWorkerSessionStateStore stateStore,
        IFcPublicationTransport transport,
        Func<string?> authorScopeProvider,
        Func<string?> authorIdProvider,
        Func<FcCompatibilityContext> compatibilityProvider,
        Func<IReadOnlyList<FcPublicListView>> publicListProvider,
        Func<CharacterIdentity?> characterProvider,
        Func<string>? worldFingerprintProvider = null,
        IFcClock? clock = null,
        Func<Guid>? sessionIdProvider = null,
        Func<IEnumerable<FcQuantityKey>, FcWorkerPhysicalInventorySnapshotResult>? physicalSnapshotProvider = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _authorScopeProvider = authorScopeProvider ?? throw new ArgumentNullException(nameof(authorScopeProvider));
        _authorIdProvider = authorIdProvider ?? throw new ArgumentNullException(nameof(authorIdProvider));
        _compatibilityProvider = compatibilityProvider ?? throw new ArgumentNullException(nameof(compatibilityProvider));
        _publicListProvider = publicListProvider ?? throw new ArgumentNullException(nameof(publicListProvider));
        _characterProvider = characterProvider ?? throw new ArgumentNullException(nameof(characterProvider));
        _worldFingerprintProvider = worldFingerprintProvider ?? (() => "");
        _clock = clock ?? FcSystemClock.Instance;
        _sessionIdProvider = sessionIdProvider ?? Guid.NewGuid;
        _physicalSnapshotProvider = physicalSnapshotProvider;

        var scope = _authorScopeProvider();
        var author = _authorIdProvider();
        _state = FcWorkerSessionState.Create(scope ?? string.Empty, author ?? string.Empty);
        _loadStatus = FcWorkerSessionLoadStatus.Corrupt;
        if (!string.IsNullOrWhiteSpace(scope))
        {
            var loaded = _stateStore.Load(scope);
            _state = loaded.State;
            _loadStatus = loaded.Status;
            _lastError = loaded.Error;
            if (loaded.Status == FcWorkerSessionLoadStatus.Missing
                && !string.IsNullOrWhiteSpace(author))
            {
                _state = FcWorkerSessionState.Create(scope, author);
                try
                {
                    _stateStore.Save(scope, _state);
                    _loadStatus = FcWorkerSessionLoadStatus.Clean;
                }
                catch (Exception exception)
                {
                    _loadStatus = FcWorkerSessionLoadStatus.Corrupt;
                    _lastError = $"Worker session state initialization failed: {exception.Message}";
                }
            }
            else if (loaded.Status == FcWorkerSessionLoadStatus.Clean
                && !string.IsNullOrWhiteSpace(author)
                && !string.Equals(_state.AuthorId, author, StringComparison.Ordinal))
            {
                _loadStatus = FcWorkerSessionLoadStatus.Corrupt;
                _lastError = "Current native author does not match persisted worker state.";
            }
        }

        if (_loadStatus == FcWorkerSessionLoadStatus.Clean)
        {
            _desired = _state.PendingWorker ?? _state.LastAcceptedWorker;
            _accepted = _state.LastAcceptedWorker;
            _recoveryRequired = !_state.CleanShutdown
                && !_state.ExplicitUnsubscribed
                && _state.LastAcceptedWorker is { State: FcWorkerState.Active or FcWorkerState.Waiting }
                && _state.PendingWorker is null;
            _dependencyClosure = ReadPersistedClosure(_state);
            if (_state.LedgerRecovery is { } recovery)
            {
                try
                {
                    _ledger = FcContributionLedger.Recover(
                        recovery,
                        recovery.StartingInventory);
                }
                catch (Exception exception)
                {
                    _recoveryRequired = true;
                    _lastError = $"Contribution recovery is invalid: {exception.Message}";
                }
            }
            _forked = IsWorkerForkedLocked();
        }
        _worker = Task.Run(ProcessCommandsAsync);
    }

    public FcWorkerSessionState State
    {
        get
        {
            lock (_gate)
                return FcInMemoryWorkerSessionStateStore.Clone(_state);
        }
    }

    public FcWorkerSessionDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                var state = _desired?.State ?? _state.State;
                return new(
                    _loadStatus.ToString(),
                    _state.AuthorScope,
                    _state.AuthorId,
                    CanWriteLocked(),
                    _recoveryRequired,
                    _forked,
                    state,
                    _state.SessionId,
                    _state.SessionGeneration,
                    _state.LastReservedRevision,
                    _state.LastCommunicationUnixMilliseconds,
                    _state.NextRefreshUnixMilliseconds,
                    _pendingCommands,
                    string.IsNullOrWhiteSpace(_lastError) ? _state.LastError : _lastError);
            }
        }
    }

    public bool RecoveryReady
    {
        get
        {
            lock (_gate)
            {
                _forked = IsWorkerForkedLocked();
                return _recoveryRequired
                    && !_disposed
                    && !_forked
                    && (_loadStatus is FcWorkerSessionLoadStatus.Clean or FcWorkerSessionLoadStatus.Missing)
                    && _transport.IsReady
                    && !string.IsNullOrWhiteSpace(_state.AuthorScope)
                    && !string.IsNullOrWhiteSpace(_state.AuthorId)
                    && string.Equals(_authorScopeProvider(), _state.AuthorScope, StringComparison.Ordinal)
                    && string.Equals(_authorIdProvider(), _state.AuthorId, StringComparison.Ordinal)
                    && _compatibilityProvider().IsValid;
            }
        }
    }

    public FcWorkerSessionStatus Status
    {
        get
        {
            lock (_gate)
                return new(
                    _desired?.State is FcWorkerState.Active or FcWorkerState.Waiting,
                    _recoveryRequired,
                    Clone(_desired),
                    Clone(_accepted),
                    _acceptedHlc,
                    _ledger,
                    string.IsNullOrWhiteSpace(_lastError) ? _state.LastError : _lastError);
        }
    }

    public WorkerSessionRecord? DesiredWorker
    {
        get { lock (_gate) return Clone(_desired); }
    }

    public WorkerSessionRecord? AcceptedWorker
    {
        get { lock (_gate) return Clone(_accepted); }
    }

    public FcContributionLedger? ContributionLedger
    {
        get { lock (_gate) return _ledger; }
    }

    public long NextRefreshUnixMilliseconds
    {
        get { lock (_gate) return _state.NextRefreshUnixMilliseconds; }
    }

    internal FcWorkerSessionResult StartSelected(
        IEnumerable<Guid> listIds,
        FcItemQuantityMap currentPhysical,
        bool useOwnStock = false,
        IEnumerable<FcQuantityKey>? dependencyClosure = null)
    {
        try
        {
            var normalized = FcWorkerSessionSelection.Normalize(listIds);
            return Start(new FcWorkerSessionStartRequest(
                normalized,
                false,
                useOwnStock,
                currentPhysical ?? throw new ArgumentNullException(nameof(currentPhysical)),
                (dependencyClosure ?? Array.Empty<FcQuantityKey>()).OrderBy(key => key).ToArray(),
                _characterProvider() ?? throw new InvalidOperationException("Character identity is unavailable."),
                _worldFingerprintProvider()));
        }
        catch (Exception exception)
        {
            return FcWorkerSessionResult.Blocked($"Worker start failed: {exception.Message}");
        }
    }

    public FcWorkerSessionResult StartSelected(
        IEnumerable<Guid> listIds,
        FcWorkerPhysicalInventorySnapshotResult physical,
        bool useOwnStock = false,
        IEnumerable<FcQuantityKey>? dependencyClosure = null)
    {
        if (_physicalSnapshotProvider is null)
            return BlockPhysicalSnapshot(
                physical,
                "Authoritative dependency closure provider is unavailable; worker start is blocked.");
        if (physical is null || !physical.Succeeded || !physical.Complete || physical.Snapshot is null)
            return BlockPhysicalSnapshot(physical);
        try
        {
            var closure = dependencyClosure?.ToArray();
            if (closure is null || !HasCompletePhysicalSnapshot(physical.Snapshot, closure))
                return BlockPhysicalSnapshot(
                    physical,
                    "Physical inventory snapshot does not contain every requested dependency key.");
            return StartSelected(listIds, physical.Snapshot, useOwnStock, closure);
        }
        catch (Exception exception)
        {
            return BlockPhysicalSnapshot(
                physical,
                $"Worker dependency closure could not be checked: {exception.Message}");
        }
    }

    internal FcWorkerSessionResult StartAll(
        FcItemQuantityMap currentPhysical,
        bool useOwnStock = false,
        IEnumerable<FcQuantityKey>? dependencyClosure = null)
    {
        try
        {
            return Start(new FcWorkerSessionStartRequest(
                Array.Empty<Guid>(),
                true,
                useOwnStock,
                currentPhysical ?? throw new ArgumentNullException(nameof(currentPhysical)),
                (dependencyClosure ?? Array.Empty<FcQuantityKey>()).OrderBy(key => key).ToArray(),
                _characterProvider() ?? throw new InvalidOperationException("Character identity is unavailable."),
                _worldFingerprintProvider()));
        }
        catch (Exception exception)
        {
            return FcWorkerSessionResult.Blocked($"Worker start failed: {exception.Message}");
        }
    }

    public FcWorkerSessionResult StartAll(
        FcWorkerPhysicalInventorySnapshotResult physical,
        bool useOwnStock = false,
        IEnumerable<FcQuantityKey>? dependencyClosure = null)
    {
        if (_physicalSnapshotProvider is null)
            return BlockPhysicalSnapshot(
                physical,
                "Authoritative dependency closure provider is unavailable; worker start is blocked.");
        if (physical is null || !physical.Succeeded || !physical.Complete || physical.Snapshot is null)
            return BlockPhysicalSnapshot(physical);
        try
        {
            var closure = dependencyClosure?.ToArray();
            if (closure is null || !HasCompletePhysicalSnapshot(physical.Snapshot, closure))
                return BlockPhysicalSnapshot(
                    physical,
                    "Physical inventory snapshot does not contain every requested dependency key.");
            return StartAll(physical.Snapshot, useOwnStock, closure);
        }
        catch (Exception exception)
        {
            return BlockPhysicalSnapshot(
                physical,
                $"Worker dependency closure could not be checked: {exception.Message}");
        }
    }

    internal FcWorkerSessionResult Start(FcWorkerSessionStartRequest request)
    {
        if (request is null)
            return FcWorkerSessionResult.Blocked("Worker start request is unavailable.");
        try
        {
            lock (_gate)
            {
                var validation = ValidateStartLocked(request);
                if (!validation.Accepted)
                    return validation;
                var sessionId = _sessionIdProvider();
                if (sessionId == Guid.Empty)
                    return FcWorkerSessionResult.Blocked("Session ID provider returned an empty identity.");
                var generationHighWater = Math.Max(_state.SessionGeneration, ReplicaGenerationLocked());
                if (generationHighWater == ulong.MaxValue)
                    return BlockRevisionLocked("Worker session generation is exhausted; writes are permanently blocked.");
                var revisionHighWater = Math.Max(_state.LastReservedRevision, ReplicaRevisionLocked());
                if (revisionHighWater == ulong.MaxValue)
                    return BlockRevisionLocked("Worker revision is exhausted; writes are permanently blocked.");
                var generation = generationHighWater + 1UL;
                var revision = revisionHighWater + 1UL;
                var workerRegisterId = _state.WorkerRegisterId == Guid.Empty
                    ? FcWorkerSessionState.StableWorkerRegisterId(_state.AuthorId)
                    : _state.WorkerRegisterId;
                var selection = FcWorkerSessionSelection.ToSelection(
                    request.AllPublishedLists,
                    request.ListIds);
                var closure = ResolveDependencyClosureLocked(request, selection);
                if (closure.Count == 0 || closure.Count > MaxLogicalEntries)
                    return FcWorkerSessionResult.Blocked(
                        "Worker dependency closure is empty or exceeds the bounded protocol limit.");
                if (_physicalSnapshotProvider is not null
                    && !HasCompletePhysicalSnapshot(request.CurrentPhysical, closure))
                    return BlockInventoryLocked(
                        "Physical inventory snapshot does not contain every dependency key.");
                var starting = FilterPhysical(request.CurrentPhysical, closure);
                var ledgerState = new FcContributionLedgerState(
                    sessionId,
                    starting,
                    starting,
                    FcItemQuantityMap.Empty,
                    request.UseOwnStock)
                {
                    SessionGeneration = generation,
                    Selection = selection,
                };
                _ledger = new FcContributionLedger(ledgerState, starting);
                _dependencyClosure = closure.ToHashSet();
                var held = _ledger.GetPublishableHeld(_dependencyClosure);
                var worker = NewWorker(
                    workerRegisterId,
                    sessionId,
                    generation,
                    revision,
                    FcWorkerState.Active,
                    request.Character,
                    selection,
                    request.UseOwnStock,
                    held,
                    request.WorldFingerprint);
                _accepted = null;
                _acceptedHlc = null;
                _desired = worker;
                _recoveryRequired = false;
                _state = _state with
                {
                    WorkerRegisterId = workerRegisterId,
                    SessionId = sessionId,
                    SessionGeneration = generation,
                    LastReservedRevision = revision,
                    LastAcceptedWorker = null,
                    PendingWorker = worker,
                    PendingKind = FcWorkerSessionCommandKind.Start,
                    State = FcWorkerState.Active,
                    UseOwnStock = request.UseOwnStock,
                    Selection = selection,
                    DependencyClosure = closure.OrderBy(key => key).ToArray(),
                    LedgerRecovery = _ledger.ExportRecovery(selection),
                    CleanShutdown = false,
                    ExplicitUnsubscribed = false,
                    LastError = string.Empty,
                };
                return ReserveAndEnqueueLocked(FcWorkerSessionCommandKind.Start, worker);
            }
        }
        catch (Exception exception)
        {
            return FcWorkerSessionResult.Blocked($"Worker start failed: {exception.Message}");
        }
    }

    public FcWorkerSessionResult Stop()
    {
        lock (_gate)
        {
            if (_desired is null || _desired.State == FcWorkerState.Unsubscribed)
                return FcWorkerSessionResult.Blocked("Worker session is already unsubscribed.");
            var next = _desired with { State = FcWorkerState.Unsubscribed };
            return QueueAbsoluteUpdateLocked(
                next,
                FcWorkerSessionCommandKind.Stop,
                explicitUnsubscribe: true,
                allowRecovery: true);
        }
    }

    public FcWorkerSessionResult SetWaiting()
        => SetState(FcWorkerState.Waiting);

    public FcWorkerSessionResult SetActive()
        => SetState(FcWorkerState.Active);

    public FcWorkerSessionResult SetState(FcWorkerState state)
    {
        if (state is not (FcWorkerState.Active or FcWorkerState.Waiting))
            return FcWorkerSessionResult.Blocked("Only Active and Waiting are meaningful subscribed states.");
        lock (_gate)
        {
            if (_desired is null || _desired.State == FcWorkerState.Unsubscribed)
                return FcWorkerSessionResult.Blocked("No subscribed worker session exists.");
            return QueueAbsoluteUpdateLocked(_desired with { State = state }, FcWorkerSessionCommandKind.MeaningfulUpdate);
        }
    }

    internal FcWorkerSessionResult SetSelection(
        IEnumerable<Guid> listIds,
        FcItemQuantityMap currentPhysical)
    {
        var normalized = FcWorkerSessionSelection.Normalize(listIds);
        lock (_gate)
        {
            if (_desired is null || _desired.State == FcWorkerState.Unsubscribed)
                return FcWorkerSessionResult.Blocked("No subscribed worker session exists.");
            var selection = FcWorkerSessionSelection.ToSelection(false, normalized);
            var check = ValidateSelectionLocked(selection);
            if (!check.Accepted)
                return check;
            if (currentPhysical is null)
                return FcWorkerSessionResult.Blocked("Current physical inventory is unavailable.");
            IReadOnlySet<FcQuantityKey> nextClosure;
            try
            {
                nextClosure = ResolveDependencyClosureLocked(
                    new FcWorkerSessionStartRequest(
                        normalized,
                        false,
                        _desired.UseOwnStock,
                        currentPhysical,
                        Array.Empty<FcQuantityKey>(),
                        _desired.Character,
                        _desired.WorldFingerprint),
                    selection);
            }
            catch (Exception exception)
            {
                return FcWorkerSessionResult.Blocked(
                    $"Worker dependency closure failed: {exception.Message}");
            }
            if (!HasCompletePhysicalSnapshot(currentPhysical, nextClosure))
                return BlockInventoryLocked(
                    "Physical inventory snapshot does not contain every dependency key.");
            if (_ledger is null)
                return BlockRecoveryLocked("Worker contribution ledger is unavailable; selection is paused.");
            try
            {
                var previousClosure = _dependencyClosure;
                var previousLedger = _ledger;
                var filteredPhysical = FilterPhysical(currentPhysical, nextClosure);
                var recovery = _ledger.ExportRecovery(selection);
                if (!_ledger.State.UseOwnStock)
                {
                    var expandedStarting = ExpandStartingInventory(
                        _ledger.State.StartingInventory,
                        filteredPhysical,
                        nextClosure);
                    recovery = recovery with
                    {
                        StartingInventory = expandedStarting,
                        ProtectedInventory = expandedStarting,
                    };
                }
                _ledger = FcContributionLedger.Recover(recovery, filteredPhysical);
                _dependencyClosure = nextClosure.ToHashSet();
                var result = QueueAbsoluteUpdateLocked(
                    _desired with
                    {
                        Selection = selection,
                        HeldInventory = PublishableHeldLocked().Entries,
                    },
                    FcWorkerSessionCommandKind.MeaningfulUpdate);
                if (!result.Accepted)
                {
                    _dependencyClosure = previousClosure;
                    _ledger = previousLedger;
                    return result;
                }
                return result;
            }
            catch (Exception exception)
            {
                return BlockInventoryLocked(
                    $"Worker selection physical reconciliation failed: {exception.Message}");
            }
        }
    }

    public FcWorkerSessionResult SetSelection(
        IEnumerable<Guid> listIds,
        FcWorkerPhysicalInventorySnapshotResult physical)
    {
        if (_physicalSnapshotProvider is null)
            return BlockPhysicalSnapshot(
                physical,
                "Authoritative dependency closure provider is unavailable; worker selection is blocked.");
        if (physical is null || !physical.Succeeded || !physical.Complete || physical.Snapshot is null)
            return BlockPhysicalSnapshot(physical);
        return SetSelection(listIds, physical.Snapshot);
    }

    internal FcWorkerSessionResult SetUseOwnStock(bool useOwnStock, FcItemQuantityMap currentPhysical)
    {
        if (currentPhysical is null)
            return FcWorkerSessionResult.Blocked("Current physical inventory is unavailable.");
        lock (_gate)
        {
            if (_desired is null || _desired.State == FcWorkerState.Unsubscribed || _ledger is null)
                return FcWorkerSessionResult.Blocked("No recoverable subscribed worker session exists.");
            if (!HasCompletePhysicalSnapshot(currentPhysical, _dependencyClosure))
                return BlockInventoryLocked(
                    "Physical inventory snapshot does not contain every dependency key.");
            var recovery = _ledger.ExportRecovery(_desired.Selection) with { UseOwnStock = useOwnStock };
            _ledger = FcContributionLedger.Recover(recovery, FilterPhysical(currentPhysical, _dependencyClosure));
            return QueueAbsoluteUpdateLocked(
                _desired with
                {
                    UseOwnStock = useOwnStock,
                    HeldInventory = PublishableHeldLocked().Entries,
                },
                FcWorkerSessionCommandKind.MeaningfulUpdate);
        }
    }

    public FcWorkerSessionResult SetUseOwnStock(
        bool useOwnStock,
        FcWorkerPhysicalInventorySnapshotResult physical)
    {
        if (_physicalSnapshotProvider is null)
            return BlockPhysicalSnapshot(
                physical,
                "Authoritative dependency closure provider is unavailable; stock selection is blocked.");
        if (physical is null || !physical.Succeeded || !physical.Complete || physical.Snapshot is null)
            return BlockPhysicalSnapshot(physical);
        return SetUseOwnStock(useOwnStock, physical.Snapshot);
    }

    public FcWorkerSessionResult SetCurrentTarget(FcLogicalTarget? target)
    {
        if (!IsValidTarget(target))
            return FcWorkerSessionResult.Blocked("Logical worker target is invalid.");
        lock (_gate)
            return QueueFieldUpdateLocked(worker => worker with { CurrentTarget = target });
    }

    public FcWorkerSessionResult SetCraftQueue(IEnumerable<FcLogicalQueueEntry> queue)
    {
        var normalized = NormalizeQueue(queue);
        if (normalized is null)
            return FcWorkerSessionResult.Blocked("Logical craft queue is invalid or too large.");
        lock (_gate)
            return QueueFieldUpdateLocked(worker => worker with { CraftQueue = normalized });
    }

    public FcWorkerSessionResult SetGatherTargetOrder(IEnumerable<uint> order)
    {
        var normalized = (order ?? Array.Empty<uint>()).ToArray();
        if (normalized.Length > MaxLogicalEntries || normalized.Any(itemId => itemId == 0))
            return FcWorkerSessionResult.Blocked("Logical gather target order is invalid or too large.");
        lock (_gate)
            return QueueFieldUpdateLocked(worker => worker with { GatherTargetOrder = normalized });
    }

    public FcWorkerSessionResult RecordGatherYield(FcItemQuantityMap quantity)
        => RecordLedgerEvent(quantity, ledger => ledger.RecordGatherYield(quantity));

    public FcWorkerSessionResult RecordCraftOutput(FcItemQuantityMap quantity)
        => RecordLedgerEvent(quantity, ledger => ledger.RecordCraftOutput(quantity));

    public FcWorkerSessionResult RecordCraftMaterialConsumption(FcItemQuantityMap quantity)
        => RecordLedgerEvent(quantity, ledger => ledger.RecordCraftMaterialConsumption(quantity));

    public FcWorkerSessionResult RecordDiscardOrTransfer(FcItemQuantityMap quantity)
        => RecordLedgerEvent(quantity, ledger => ledger.RecordDiscardOrTransfer(quantity));

    /// <summary>
    /// Applies the worker-after side of an accepted atomic transfer locally.
    /// This updates the durable accepted register and refresh deadline; it does
    /// not emit a second worker Put for that same physical action.
    /// </summary>
    public FcWorkerSessionResult ObserveAtomicTransfer(FcInventoryTransferRecord transfer)
    {
        if (transfer is null || transfer.WorkerAfter is null)
            return FcWorkerSessionResult.Blocked("Atomic transfer observation is incomplete.");
        lock (_gate)
        {
            _forked = IsWorkerForkedLocked();
            if (_forked)
                return FcWorkerSessionResult.Blocked(
                    "Worker register is forked; atomic transfer observation is blocked.");
            var recoveryRequiredBefore = _recoveryRequired;
            if (!IsOwnWorkerLocked(transfer.WorkerAfter)
                || transfer.SessionId != _state.SessionId
                || transfer.SessionGeneration != _state.SessionGeneration)
                return FcWorkerSessionResult.Blocked("Atomic transfer does not belong to the local worker session.");
            if (_accepted is { } previous
                && transfer.WorkerAfter.Header.Revision < previous.Header.Revision)
                return FcWorkerSessionResult.Blocked("Older atomic worker-after state was already accepted.");
            if (_accepted is { } sameRevision
                && transfer.WorkerAfter.Header.Revision == sameRevision.Header.Revision
                && FcCanonical.SemanticHash(transfer.WorkerAfter) != FcCanonical.SemanticHash(sameRevision))
                return FcWorkerSessionResult.Blocked("Atomic worker-after state conflicts at an accepted revision.");
            var pending = _state.PendingWorker;
            WorkerSessionRecord? pendingToKeep = pending is not null
                && pending.Header.Revision > transfer.WorkerAfter.Header.Revision
                ? pending
                : null;
            if (pending is not null
                && pending.Header.Revision == transfer.WorkerAfter.Header.Revision
                && FcCanonical.SemanticHash(pending) != FcCanonical.SemanticHash(transfer.WorkerAfter))
            {
                var revision = checked(Math.Max(_state.LastReservedRevision, transfer.WorkerAfter.Header.Revision) + 1UL);
                pendingToKeep = pending with { Header = pending.Header with { Revision = revision } };
            }
            _accepted = transfer.WorkerAfter;
            if (_ledger is not null)
            {
                if (transfer.Kind == FcInventoryTransferKind.Withdraw)
                    _ledger.RecordChestWithdrawal(transfer.ActualTransferredMap);
                else
                    _ledger.RecordChestDeposit(transfer.ActualTransferredMap);
                if (pendingToKeep is not null)
                    pendingToKeep = pendingToKeep with { HeldInventory = PublishableHeldLocked().Entries };
            }
            var desired = pendingToKeep ?? transfer.WorkerAfter;
            _desired = desired;
            _state = _state with
            {
                LastReservedRevision = Math.Max(_state.LastReservedRevision, desired.Header.Revision),
                LastAcceptedWorker = transfer.WorkerAfter,
                PendingWorker = pendingToKeep,
                PendingKind = pendingToKeep is not null
                    ? _state.PendingKind
                    : null,
                State = desired.State,
                UseOwnStock = desired.UseOwnStock,
                Selection = desired.Selection,
                DependencyClosure = _dependencyClosure.OrderBy(key => key).ToArray(),
                LastCommunicationUnixMilliseconds = _clock.UnixMilliseconds,
                NextRefreshUnixMilliseconds = desired.State == FcWorkerState.Unsubscribed
                    ? 0
                    : checked(_clock.UnixMilliseconds + 60_000),
                CleanShutdown = transfer.WorkerAfter.State == FcWorkerState.Unsubscribed
                    && pendingToKeep is null,
                ExplicitUnsubscribed = desired.State == FcWorkerState.Unsubscribed,
                LedgerRecovery = ExportRecoveryLocked(desired.Selection),
                LastError = string.Empty,
            };
            _recoveryRequired = recoveryRequiredBefore
                && transfer.WorkerAfter.State != FcWorkerState.Unsubscribed;
            if (pendingToKeep is not null)
            {
                if (_queuedPending)
                    _reschedulePending = true;
                else
                    EnqueuePendingLocked();
            }
            else
                _reschedulePending = false;
            EnqueuePersistenceOnlyLocked();
            return new(true, "Atomic transfer observation accepted without a second worker publication.",
                FcWorkerSessionCommandStatus.AcceptedByNative,
                transfer.WorkerAfter.Header.Revision,
                transfer.SessionId,
                transfer.SessionGeneration);
        }
    }

    /// <summary>Called after the native event has authenticated the own register.</summary>
    public FcApplyResult ObserveAccepted(WorkerSessionRecord worker, FcHlcTimestamp authoredHlc)
    {
        if (worker is null)
            return FcApplyResult.Reject(new FcWorldRevision(0, string.Empty), "Worker record is unavailable.");
        lock (_gate)
        {
            _forked = IsWorkerForkedLocked();
            if (_forked)
                return FcApplyResult.Reject(
                    _transport.WorldStore?.Revision ?? new FcWorldRevision(0, string.Empty),
                    "Worker register is forked; own worker event is blocked.");
            var recoveryRequiredBefore = _recoveryRequired;
            if (!IsOwnWorkerLocked(worker)
                || authoredHlc.PhysicalUnixMs < 0
                || string.IsNullOrWhiteSpace(authoredHlc.NodeId))
                return FcApplyResult.Reject(new FcWorldRevision(0, string.Empty), "Worker event does not belong to the local author/session.");
            if (_accepted is { } previous && worker.Header.Revision < previous.Header.Revision)
                return new FcApplyResult(
                    FcApplyStatus.Duplicate,
                    "Older own worker event ignored.",
                    _transport.WorldStore?.Revision ?? new FcWorldRevision(0, string.Empty));
            if (_accepted is { } sameRevision
                && worker.Header.Revision == sameRevision.Header.Revision
                && FcCanonical.SemanticHash(worker) != FcCanonical.SemanticHash(sameRevision))
                return FcApplyResult.Reject(
                    _transport.WorldStore?.Revision ?? new FcWorldRevision(0, string.Empty),
                    "Same-revision own worker event conflicts with the accepted register.");
            var pending = _state.PendingWorker;
            WorkerSessionRecord? pendingToKeep = pending is not null
                && pending.Header.Revision > worker.Header.Revision
                ? pending
                : null;
            if (pending is not null
                && pending.Header.Revision == worker.Header.Revision
                && FcCanonical.SemanticHash(pending) != FcCanonical.SemanticHash(worker))
            {
                var revision = checked(Math.Max(_state.LastReservedRevision, worker.Header.Revision) + 1UL);
                pendingToKeep = pending with { Header = pending.Header with { Revision = revision } };
            }
            var desired = pendingToKeep ?? worker;
            _accepted = worker;
            _desired = desired;
            _acceptedHlc = authoredHlc;
            _state = _state with
            {
                LastReservedRevision = Math.Max(
                    _state.LastReservedRevision,
                    desired.Header.Revision),
                LastAcceptedWorker = worker,
                PendingWorker = pendingToKeep,
                PendingKind = pendingToKeep is not null ? _state.PendingKind : null,
                State = desired.State,
                UseOwnStock = desired.UseOwnStock,
                Selection = desired.Selection,
                DependencyClosure = _dependencyClosure.OrderBy(key => key).ToArray(),
                LastCommunicationUnixMilliseconds = _clock.UnixMilliseconds,
                NextRefreshUnixMilliseconds = desired.State == FcWorkerState.Unsubscribed
                    ? 0
                    : checked(_clock.UnixMilliseconds + 60_000),
                CleanShutdown = worker.State == FcWorkerState.Unsubscribed
                    && pendingToKeep is null,
                ExplicitUnsubscribed = desired.State == FcWorkerState.Unsubscribed,
                LedgerRecovery = ExportRecoveryLocked(desired.Selection),
                LastError = string.Empty,
            };
            _recoveryRequired = recoveryRequiredBefore
                && worker.State != FcWorkerState.Unsubscribed;
            if (pendingToKeep is not null)
            {
                if (_queuedPending)
                    _reschedulePending = true;
                else
                    EnqueuePendingLocked();
            }
            else
                _reschedulePending = false;
            EnqueuePersistenceOnlyLocked();
            return new FcApplyResult(
                FcApplyStatus.Accepted,
                "Own worker event reconciled.",
                _transport.WorldStore?.Revision ?? new FcWorldRevision(checked((long)worker.Header.Revision), string.Empty));
        }
    }

    /// <summary>
    /// Framework-thread recovery gate. It is intentionally explicit: no
    /// recovery may invent held stock without a current physical snapshot.
    /// </summary>
    internal FcWorkerSessionResult Recover(FcItemQuantityMap currentPhysical)
        => Recover(currentPhysical, null);

    private FcWorkerSessionResult Recover(
        FcItemQuantityMap currentPhysical,
        IReadOnlySet<FcQuantityKey>? requestedClosure)
    {
        if (currentPhysical is null)
            return FcWorkerSessionResult.Blocked("Current physical inventory is unavailable; recovery is paused.");
        lock (_gate)
        {
            _forked = IsWorkerForkedLocked();
            if (_forked)
                return BlockRecoveryLocked("Worker register is forked; recovery is blocked.");
            ReconcileAuthoritativeStateLocked();
            if (!_recoveryRequired)
                return FcWorkerSessionResult.Blocked("Worker session does not require recovery.");
            if (_state.ExplicitUnsubscribed)
                return BlockRecoveryLocked("Explicitly unsubscribed worker sessions never auto-resume.");
            if (_state.LedgerRecovery is not { } recovery
                || _state.LastAcceptedWorker is not { } accepted
                || accepted.State is not (FcWorkerState.Active or FcWorkerState.Waiting))
                return BlockRecoveryLocked("Durable worker ledger is missing or invalid; held stock cannot be invented.");
            if (requestedClosure is { Count: 0 }
                || requestedClosure is not null && requestedClosure.Count > MaxLogicalEntries)
                return BlockRecoveryLocked("Worker dependency closure is invalid; physical recovery is paused.");
            var closure = requestedClosure ?? _dependencyClosure;
            if (closure.Count == 0)
                return BlockRecoveryLocked("Worker dependency closure is unavailable; physical recovery is paused.");
            if (!HasCompletePhysicalSnapshot(currentPhysical, closure))
                return BlockInventoryLocked(
                    "Physical inventory snapshot does not contain every dependency key.");
            var previousClosure = _dependencyClosure;
            var previousLedger = _ledger;
            try
            {
                var filteredPhysical = FilterPhysical(currentPhysical, closure);
                if (!previousClosure.SetEquals(closure))
                {
                    var expandedStarting = ExpandStartingInventory(
                        recovery.StartingInventory,
                        filteredPhysical,
                        closure);
                    recovery = recovery with
                    {
                        StartingInventory = expandedStarting,
                        ProtectedInventory = expandedStarting,
                    };
                    _dependencyClosure = closure.ToHashSet();
                }
                _ledger = FcContributionLedger.Recover(
                    recovery,
                    filteredPhysical);
                _recoveryRequired = false;
                var recovered = accepted with { HeldInventory = PublishableHeldLocked().Entries };
                var result = QueueAbsoluteUpdateLocked(recovered, FcWorkerSessionCommandKind.MeaningfulUpdate);
                if (!result.Accepted)
                {
                    _recoveryRequired = true;
                    _dependencyClosure = previousClosure;
                    _ledger = previousLedger;
                }
                return result;
            }
            catch (Exception exception)
            {
                _dependencyClosure = previousClosure;
                _ledger = previousLedger;
                return BlockRecoveryLocked($"Worker recovery physical reconciliation failed: {exception.Message}");
            }
        }
    }

    public FcWorkerSessionResult Recover(FcWorkerPhysicalInventorySnapshotResult physical)
    {
        if (physical is null || !physical.Succeeded || !physical.Complete || physical.Snapshot is null)
            return BlockPhysicalSnapshot(physical, "Physical inventory snapshot is unavailable or incomplete; recovery is paused.");
        if (_physicalSnapshotProvider is null)
            return BlockPhysicalSnapshot(
                physical,
                "Authoritative dependency closure provider is unavailable; recovery is paused.");
        return RecoverCurrent(physical.Snapshot);
    }

    private FcWorkerSessionResult RecoverCurrent(FcItemQuantityMap currentPhysical)
    {
        FcFulfillmentSelection? selection;
        lock (_gate)
            selection = _desired?.Selection;
        if (selection is null)
            return FcWorkerSessionResult.Blocked("Worker selection is unavailable; recovery is paused.");

        FcWorkerDependencyClosureResult closure;
        try
        {
            closure = FcWorkerDependencyClosure.Build(_publicListProvider(), selection);
        }
        catch (Exception exception)
        {
            closure = FcWorkerDependencyClosureResult.Invalid(
                $"Published list dependency closure failed: {exception.Message}");
        }
        if (!closure.Succeeded)
            return BlockRecovery(closure.Error);
        if (!HasCompletePhysicalSnapshot(currentPhysical, closure.Keys))
            return BlockInventory(
                "Physical inventory snapshot does not contain every current dependency key.");
        return Recover(currentPhysical, closure.Keys);
    }

    /// <summary>
    /// Bounded framework tick. It only examines memory, native readiness, and
    /// world projection; durable writes/native puts are delegated to the worker.
    /// </summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (!CurrentAuthorMatchesLocked())
            {
                if (_queuedPending)
                    return;
                QueueCharacterRebindLocked();
                return;
            }
            _forked = IsWorkerForkedLocked();
            ReconcileAuthoritativeStateLocked();
            if (_recoveryRequired || _forked || !CanWriteLocked())
                return;
            if (!RefreshDependencyClosureLocked())
                return;
            if (_state.PendingWorker is not null)
            {
                if (!_queuedPending)
                    EnqueuePendingLocked();
                return;
            }
            if (_desired is not { State: FcWorkerState.Active or FcWorkerState.Waiting } desired
                || _clock.UnixMilliseconds < _state.NextRefreshUnixMilliseconds)
                return;
            var highWater = Math.Max(_state.LastReservedRevision, ReplicaRevisionLocked());
            if (highWater == ulong.MaxValue)
            {
                _lastError = "Worker revision is exhausted; writes are permanently blocked.";
                _state = _state with { LastError = _lastError };
                return;
            }
            var revision = highWater + 1UL;
            var refresh = desired with { Header = desired.Header with { Revision = revision } };
            _desired = refresh;
            _state = _state with
            {
                LastReservedRevision = revision,
                PendingWorker = refresh,
                PendingKind = FcWorkerSessionCommandKind.IdleRefresh,
                CleanShutdown = false,
                ExplicitUnsubscribed = false,
            };
            EnqueuePendingLocked();
        }
    }

    public FcWorkerSessionResult RetryPending()
    {
        lock (_gate)
        {
            if (_state.PendingWorker is null)
                return FcWorkerSessionResult.Blocked("No pending worker command remains.");
            var allowRecovery = _state.PendingWorker.State == FcWorkerState.Unsubscribed;
            if (_forked || !CanWriteLocked(allowRecovery))
                return FcWorkerSessionResult.Blocked(WriteBlockReasonLocked());
            EnqueuePendingLocked();
            return new(true, "Pending worker command retry queued.",
                FcWorkerSessionCommandStatus.Pending,
                _state.PendingWorker.Header.Revision,
                _state.SessionId,
                _state.SessionGeneration);
        }
    }

    public void ReconcileAuthoritativeState()
    {
        lock (_gate)
            ReconcileAuthoritativeStateLocked();
    }

    /// <summary>
    /// Requests a background load of the current character's author-scoped
    /// state. Logout keeps the old pending state durable but cannot author a
    /// frame; a later login to another character loads only that character's
    /// state file.
    /// </summary>
    public void RequestCharacterRebind()
    {
        lock (_gate)
        {
            if (!_disposed && !_queuedPending && !CurrentAuthorMatchesLocked())
                QueueCharacterRebindLocked();
        }
    }

    public Task DrainAsync(TimeSpan timeout)
        => WaitForIdleAsync(timeout);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_state.PendingWorker is not null)
                EnqueuePersistenceOnlyLocked();
            _commands.Writer.TryComplete();
        }
        _ = FinishDisposeAsync();
    }

    private FcWorkerSessionResult RecordLedgerEvent(
        FcItemQuantityMap quantity,
        Action<FcContributionLedger> apply)
    {
        if (quantity is null)
            return FcWorkerSessionResult.Blocked("Inventory quantity is unavailable.");
        lock (_gate)
        {
            if (_ledger is null || _desired is null || _desired.State == FcWorkerState.Unsubscribed)
                return FcWorkerSessionResult.Blocked("No subscribed worker ledger exists.");
            try
            {
                apply(_ledger);
                return QueueAbsoluteUpdateLocked(
                    _desired with { HeldInventory = PublishableHeldLocked().Entries },
                    FcWorkerSessionCommandKind.MeaningfulUpdate);
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                return FcWorkerSessionResult.Blocked($"Worker contribution update failed: {exception.Message}");
            }
        }
    }

    private FcWorkerSessionResult QueueFieldUpdateLocked(
        Func<WorkerSessionRecord, WorkerSessionRecord> update)
    {
        if (_desired is null || _desired.State == FcWorkerState.Unsubscribed)
            return FcWorkerSessionResult.Blocked("No subscribed worker session exists.");
        try
        {
            return QueueAbsoluteUpdateLocked(update(_desired), FcWorkerSessionCommandKind.MeaningfulUpdate);
        }
        catch (Exception exception)
        {
            return FcWorkerSessionResult.Blocked($"Worker update failed: {exception.Message}");
        }
    }

    private FcWorkerSessionResult QueueAbsoluteUpdateLocked(
        WorkerSessionRecord desired,
        FcWorkerSessionCommandKind kind,
        bool explicitUnsubscribe = false,
        bool allowRecovery = false)
    {
        if (!CanWriteLocked(allowRecovery) || _forked)
            return FcWorkerSessionResult.Blocked(WriteBlockReasonLocked());
        if (!IsOwnWorkerLocked(desired))
            return FcWorkerSessionResult.Blocked("Worker update author or session identity is invalid.");
        if (_ledger is not null)
            desired = desired with { HeldInventory = PublishableHeldLocked().Entries };
        var hadPending = _state.PendingWorker is not null;
        var pending = _state.PendingWorker;
        var revision = pending?.Header.Revision ?? 0;
        if (pending is null)
        {
            var highWater = Math.Max(_state.LastReservedRevision, ReplicaRevisionLocked());
            if (highWater == ulong.MaxValue)
                return BlockRevisionLocked("Worker revision is exhausted; writes are permanently blocked.");
            revision = highWater + 1UL;
        }
        desired = desired with { Header = desired.Header with { Revision = revision } };
        _desired = desired;
        _state = _state with
        {
            LastReservedRevision = Math.Max(_state.LastReservedRevision, revision),
            PendingWorker = desired,
            PendingKind = kind,
            State = desired.State,
            UseOwnStock = desired.UseOwnStock,
            Selection = desired.Selection,
            DependencyClosure = _dependencyClosure.OrderBy(key => key).ToArray(),
            CleanShutdown = false,
            ExplicitUnsubscribed = explicitUnsubscribe || desired.State == FcWorkerState.Unsubscribed,
            LedgerRecovery = ExportRecoveryLocked(desired.Selection),
            LastError = string.Empty,
        };
        var result = ReserveAndEnqueueLocked(kind, desired);
        if (hadPending)
            EnqueuePersistenceOnlyLocked();
        return result;
    }

    private FcWorkerSessionResult ReserveAndEnqueueLocked(
        FcWorkerSessionCommandKind kind,
        WorkerSessionRecord worker)
    {
        EnqueuePendingLocked();
        return new(
            true,
            "Worker state reserved and queued; native acceptance remains authoritative.",
            FcWorkerSessionCommandStatus.Pending,
            worker.Header.Revision,
            worker.SessionId,
            worker.SessionGeneration);
    }

    private void EnqueuePendingLocked()
    {
        if (_queuedPending || _state.PendingWorker is null || _disposed)
            return;
        _queuedPending = true;
        _pendingCommands++;
        if (!_commands.Writer.TryWrite(FcWorkerWork.PutCommand))
        {
            _queuedPending = false;
            _pendingCommands--;
            _lastError = "Worker session command queue is closed.";
        }
    }

    private void EnqueuePersistenceOnlyLocked()
    {
        _pendingCommands++;
        if (!_commands.Writer.TryWrite(FcWorkerWork.PersistCommand))
            _pendingCommands--;
    }

    private void QueueCharacterRebindLocked()
    {
        if (_rebindInFlight)
            return;
        var scope = _authorScopeProvider();
        var author = _authorIdProvider();
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(author))
            return;
        _rebindInFlight = true;
        var oldState = FcInMemoryWorkerSessionStateStore.Clone(_state);
        _pendingCommands++;
        _ = Task.Run(() => RebindCharacterAsync(scope, author, oldState));
    }

    private async Task RebindCharacterAsync(
        string scope,
        string author,
        FcWorkerSessionState oldState)
    {
        try
        {
            // Preserve any coalesced old-author command before switching the
            // in-memory view. A failure leaves the old file fail-closed.
            try
            {
                if (!string.IsNullOrWhiteSpace(oldState.AuthorScope))
                    _stateStore.Save(oldState.AuthorScope, oldState);
            }
            catch
            {
                // The old state remains pending and the new author must not
                // inherit it. The new scope is still loaded independently.
            }
            var loaded = _stateStore.Load(scope);
            var state = loaded.State;
            var status = loaded.Status;
            var error = loaded.Error;
            if (loaded.Status == FcWorkerSessionLoadStatus.Missing)
            {
                if (!string.IsNullOrWhiteSpace(oldState.AuthorId)
                    && !string.Equals(oldState.AuthorScope, scope, StringComparison.Ordinal)
                    && string.Equals(oldState.AuthorId, author, StringComparison.Ordinal))
                {
                    status = FcWorkerSessionLoadStatus.Corrupt;
                    error = "Native character author was not rebound before the character scope changed.";
                }
                else
                {
                    state = FcWorkerSessionState.Create(scope, author);
                    try
                    {
                        _stateStore.Save(scope, state);
                        status = FcWorkerSessionLoadStatus.Clean;
                    }
                    catch (Exception exception)
                    {
                        status = FcWorkerSessionLoadStatus.Corrupt;
                        error = $"Worker session state initialization failed: {exception.Message}";
                    }
                }
            }
            else if (status == FcWorkerSessionLoadStatus.Clean
                && !string.Equals(state.AuthorId, author, StringComparison.Ordinal))
            {
                status = FcWorkerSessionLoadStatus.Corrupt;
                error = "Current native author does not match persisted worker state.";
            }
            lock (_gate)
            {
                if (_disposed)
                    return;
                _state = state;
                _loadStatus = status;
                _lastError = error;
                _desired = _state.PendingWorker ?? _state.LastAcceptedWorker;
                _accepted = _state.LastAcceptedWorker;
                _acceptedHlc = null;
                _ledger = null;
                _dependencyClosure = ReadPersistedClosure(_state);
                if (_state.LedgerRecovery is { } recovery)
                {
                    try
                    {
                        _ledger = FcContributionLedger.Recover(recovery, recovery.StartingInventory);
                    }
                    catch (Exception exception)
                    {
                        _lastError = $"Contribution recovery is invalid: {exception.Message}";
                    }
                }
                _recoveryRequired = status == FcWorkerSessionLoadStatus.Clean
                    && !_state.CleanShutdown
                    && !_state.ExplicitUnsubscribed
                    && _state.LastAcceptedWorker is { State: FcWorkerState.Active or FcWorkerState.Waiting }
                    && _state.PendingWorker is null;
                _forked = IsWorkerForkedLocked();
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _loadStatus = FcWorkerSessionLoadStatus.Corrupt;
                    _lastError = $"Worker character rebind failed: {exception.Message}";
                    _state = _state with { LastError = _lastError };
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _rebindInFlight = false;
                _pendingCommands = Math.Max(0, _pendingCommands - 1);
            }
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (var work in _commands.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await ProcessCommandAsync(work).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    lock (_gate)
                        _lastError = $"Worker session command failed: {exception.Message}";
                }
                finally
                {
                    lock (_gate)
                    {
                        _pendingCommands = Math.Max(0, _pendingCommands - 1);
                        if (work.Pending)
                        {
                            _queuedPending = false;
                            if (_reschedulePending)
                            {
                                _reschedulePending = false;
                                EnqueuePendingLocked();
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private Task ProcessCommandAsync(FcWorkerWork work)
    {
        FcWorkerSessionState snapshot;
        WorkerSessionRecord? worker;
        string scope;
        FcWorkerSessionState? acceptedSnapshot = null;
        lock (_gate)
        {
            snapshot = FcInMemoryWorkerSessionStateStore.Clone(_state);
            worker = snapshot.PendingWorker;
            scope = snapshot.AuthorScope;
        }
        if (!TrySave(scope, snapshot, work.Pending))
            return Task.CompletedTask;
        if (!work.Pending || worker is null)
            return Task.CompletedTask;
        lock (_gate)
        {
            if (_disposed
                || _forked
                || !CanWriteLocked(allowRecovery: worker.State == FcWorkerState.Unsubscribed))
            {
                MarkFailureLocked("Native worker publication is not ready; pending command retained.");
                return Task.CompletedTask;
            }
            // Recheck author/readiness directly before crossing the native
            // boundary. The payload is still the durable absolute command.
            if (!string.Equals(_transport.LocalAuthorId, worker.Header.OwnerAuthorId, StringComparison.Ordinal)
                || !string.Equals(_authorIdProvider(), worker.Header.OwnerAuthorId, StringComparison.Ordinal)
                || !string.Equals(_authorScopeProvider(), snapshot.AuthorScope, StringComparison.Ordinal))
            {
                MarkFailureLocked("Native mesh author changed before worker publication; retry is required.");
                return Task.CompletedTask;
            }
            var currentPending = _state.PendingWorker;
            if (currentPending is null
                || currentPending.Header.Revision != worker.Header.Revision
                || FcCanonical.SemanticHash(currentPending) != FcCanonical.SemanticHash(worker))
            {
                // An accepted atomic transfer may supersede this queued
                // command while it is being persisted. Never cross the native
                // boundary with the stale absolute payload. A newer pending
                // payload remains eligible for the next queue turn; a cleared
                // pending register means the transfer itself is authoritative.
                if (currentPending is not null)
                    _reschedulePending = true;
                return Task.CompletedTask;
            }
            // Keep the final pending validation and the nonblocking native
            // enqueue in one critical section. An accepted atomic transfer
            // cannot clear or rewrite this command between the check and Put;
            // it is ordered after this publication and refreshes the durable
            // register without a second worker Put.
            FcNativeCallResult result;
            try
            {
                var payload = FcCanonical.SerializeUtf8(worker);
                result = _transport.Put(
                    Encoding.UTF8.GetBytes(worker.Header.RecordId.ToString("D")),
                    Encoding.UTF8.GetBytes(FcMeshKey.ForRecord(
                        FcRecordTypes.WorkerSession,
                        worker.Header.OwnerAuthorId,
                        worker.Header.RecordId.ToString("D"))),
                    Encoding.UTF8.GetBytes(FcRecordTypes.WorkerSession),
                    true,
                    worker.SessionGeneration,
                    worker.Header.Revision,
                    payload);
            }
            catch (Exception exception)
            {
                MarkFailureLocked($"Native worker publication failed: {exception.Message}");
                return Task.CompletedTask;
            }

            if (!result.Succeeded)
            {
                MarkFailureLocked($"Native worker queue rejected the record: {result.ErrorCode}.");
                return Task.CompletedTask;
            }
            var current = _state.PendingWorker;
            var same = current is not null
                && current.Header.Revision == worker.Header.Revision
                && FcCanonical.SemanticHash(current) == FcCanonical.SemanticHash(worker);
            if (!same)
            {
                // A meaningful update coalesced while Put was in flight. Keep
                // its newer desired absolute payload pending. If the payload
                // kept the in-flight revision, that revision is now consumed
                // by the successful Put and the replacement must reserve a
                // new one before retrying.
                if (current is not null && current.Header.Revision == worker.Header.Revision)
                {
                    var revision = checked(Math.Max(_state.LastReservedRevision, worker.Header.Revision) + 1UL);
                    current = current with { Header = current.Header with { Revision = revision } };
                    _state = _state with
                    {
                        LastReservedRevision = revision,
                        PendingWorker = current,
                        PendingKind = _state.PendingKind,
                    };
                    _desired = current;
                }
                _reschedulePending = true;
                return Task.CompletedTask;
            }
            _accepted = worker;
            if (worker.State == FcWorkerState.Unsubscribed)
                _recoveryRequired = false;
            _state = _state with
            {
                LastAcceptedWorker = worker,
                PendingWorker = null,
                PendingKind = null,
                LastCommunicationUnixMilliseconds = _clock.UnixMilliseconds,
                NextRefreshUnixMilliseconds = worker.State == FcWorkerState.Unsubscribed
                    ? 0
                    : checked(_clock.UnixMilliseconds + 60_000),
                State = worker.State,
                UseOwnStock = worker.UseOwnStock,
                Selection = worker.Selection,
                CleanShutdown = worker.State == FcWorkerState.Unsubscribed,
                ExplicitUnsubscribed = worker.State == FcWorkerState.Unsubscribed,
                LedgerRecovery = ExportRecoveryLocked(worker.Selection),
                LastError = string.Empty,
            };
            _desired = worker;
            _lastError = string.Empty;
            acceptedSnapshot = FcInMemoryWorkerSessionStateStore.Clone(_state);
        }
        if (acceptedSnapshot is not null)
            TrySave(scope, acceptedSnapshot, false);
        return Task.CompletedTask;
    }

    private bool TrySave(string scope, FcWorkerSessionState snapshot, bool required)
    {
        try
        {
            _stateStore.Save(scope, snapshot);
            return true;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _lastError = required
                    ? $"Worker reservation persistence failed: {exception.Message}"
                    : $"Worker state persistence failed: {exception.Message}";
                _state = _state with { LastError = _lastError };
            }
            return false;
        }
    }

    private void MarkFailureLocked(string message)
    {
        _lastError = message;
        _state = _state with { LastError = message };
        // Failed Put does not consume another revision. The exact pending
        // payload remains the retry command.
        var snapshot = FcInMemoryWorkerSessionStateStore.Clone(_state);
        _ = Task.Run(() => TrySave(snapshot.AuthorScope, snapshot, false));
    }

    private FcWorkerSessionResult ValidateStartLocked(FcWorkerSessionStartRequest request)
    {
        if (!CanWriteLocked())
            return FcWorkerSessionResult.Blocked(WriteBlockReasonLocked());
        if (_recoveryRequired)
            return FcWorkerSessionResult.Blocked("Physical worker recovery is required before starting a new session.");
        if (_state.PendingWorker is not null)
            return FcWorkerSessionResult.Blocked("A pending worker command must be accepted or retried before starting a new session.");
        if (_forked || IsWorkerForkedLocked())
            return FcWorkerSessionResult.Blocked("Worker register is forked; writes and recovery are blocked.");
        if (request.CurrentPhysical is null)
            return FcWorkerSessionResult.Blocked("Current physical inventory is unavailable.");
        if (request.DependencyClosure is null
            || (_physicalSnapshotProvider is null && request.DependencyClosure.Count == 0)
            || request.DependencyClosure.Count > MaxLogicalEntries
            || request.DependencyClosure.Distinct().Count() != request.DependencyClosure.Count
            || request.DependencyClosure.Any(key => key.ItemId == 0 || !Enum.IsDefined(key.Quality)))
            return FcWorkerSessionResult.Blocked("Worker dependency closure contains an invalid quantity key.");
        if (request.Character is null
            || string.IsNullOrWhiteSpace(request.Character.CharacterId)
            || string.IsNullOrWhiteSpace(request.Character.DisplayName)
            || string.IsNullOrWhiteSpace(request.Character.World))
            return FcWorkerSessionResult.Blocked("Character identity is incomplete.");
        if (!string.Equals(request.Character.CharacterId, _state.AuthorScope, StringComparison.Ordinal))
            return FcWorkerSessionResult.Blocked("Character identity does not match the current character scope.");
        if (string.IsNullOrWhiteSpace(request.WorldFingerprint))
            return FcWorkerSessionResult.Blocked("World compatibility fingerprint is unavailable.");
        if (request.AllPublishedLists && request.ListIds.Count != 0)
            return FcWorkerSessionResult.Blocked("Start All cannot carry explicit list IDs.");
        var selection = FcWorkerSessionSelection.ToSelection(request.AllPublishedLists, request.ListIds);
        return ValidateSelectionLocked(selection);
    }

    private FcWorkerSessionResult ValidateSelectionLocked(FcFulfillmentSelection selection)
    {
        if (selection is null)
            return FcWorkerSessionResult.Blocked("Worker selection is unavailable.");
        if (selection.ListIds is null || selection.ListIds.Length > MaxLogicalEntries)
            return FcWorkerSessionResult.Blocked("Worker selection is invalid or too large.");
        IReadOnlyList<FcPublicListView> views;
        try
        {
            views = _publicListProvider();
        }
        catch (Exception exception)
        {
            return FcWorkerSessionResult.Blocked($"Published list projection failed: {exception.Message}");
        }
        var candidates = views
            .Where(view => view.Record.Published && view.IsCompatible)
            .Where(view => !_transport.WorldStore?.IsForked(ListKey(view.Record)) == true)
            .ToArray();
        if (selection.AllPublishedLists)
        {
            if (candidates.Length == 0)
                return FcWorkerSessionResult.Blocked("No compatible published list is available for Start All.");
            return new(true, string.Empty, FcWorkerSessionCommandStatus.None);
        }
        if (selection.ListIds.Length == 0)
            return FcWorkerSessionResult.Blocked("Start Selected requires at least one list.");
        var available = candidates.Select(view => view.Record.ListId).ToHashSet();
        if (selection.ListIds.Any(listId => !available.Contains(listId)))
            return FcWorkerSessionResult.Blocked("Selection contains an incompatible, tombstoned, forked, or unknown list.");
        return new(true, string.Empty, FcWorkerSessionCommandStatus.None);
    }

    private IReadOnlySet<FcQuantityKey> ResolveDependencyClosureLocked(
        FcWorkerSessionStartRequest request,
        FcFulfillmentSelection selection)
    {
        var explicitClosure = request.DependencyClosure ?? Array.Empty<FcQuantityKey>();
        // The no-provider constructor is retained only for deterministic
        // service tests whose synthetic public lists cannot resolve Lumina
        // recipe rows. Production always supplies the framework-thread
        // physical provider and must match the complete authoritative planner
        // closure exactly; a partial caller snapshot can never narrow it.
        if (_physicalSnapshotProvider is null)
        {
            if (explicitClosure.Count == 0)
                throw new InvalidOperationException(
                    "A dependency closure provider is required for production worker startup.");
            var requestedOnly = FcWorkerDependencyClosureResult.Valid(explicitClosure);
            if (!requestedOnly.Succeeded)
                throw new InvalidOperationException(requestedOnly.Error);
            return requestedOnly.Keys;
        }

        var closure = FcWorkerDependencyClosure.Build(_publicListProvider(), selection);
        if (!closure.Succeeded)
            throw new InvalidOperationException(closure.Error);
        if (explicitClosure.Count == 0)
            return closure.Keys;

        var requested = FcWorkerDependencyClosureResult.Valid(explicitClosure);
        if (!requested.Succeeded)
            throw new InvalidOperationException(requested.Error);
        if (!requested.Keys.SetEquals(closure.Keys))
            throw new InvalidOperationException(
                "Caller dependency closure does not match the authoritative published-list closure.");
        return closure.Keys;
    }

    private bool RefreshDependencyClosureLocked()
    {
        if (_physicalSnapshotProvider is null
            || _desired is not { State: FcWorkerState.Active or FcWorkerState.Waiting } desired)
            return true;

        FcWorkerDependencyClosureResult closure;
        try
        {
            closure = FcWorkerDependencyClosure.Build(_publicListProvider(), desired.Selection);
        }
        catch (Exception exception)
        {
            closure = FcWorkerDependencyClosureResult.Invalid(
                $"Published list dependency closure failed: {exception.Message}");
        }

        if (!closure.Succeeded)
        {
            _lastError = closure.Error;
            _state = _state with { LastError = _lastError };
            if (_dependencyClosure.Count != 0)
            {
                _dependencyClosure.Clear();
                _ = QueueAbsoluteUpdateLocked(
                    desired with { HeldInventory = FcItemQuantityMap.Empty.Entries },
                    FcWorkerSessionCommandKind.MeaningfulUpdate);
            }
            return false;
        }

        if (_dependencyClosure.SetEquals(closure.Keys))
            return true;

        FcWorkerPhysicalInventorySnapshotResult physical;
        try
        {
            physical = _physicalSnapshotProvider(closure.Keys);
        }
        catch (Exception exception)
        {
            physical = FcWorkerPhysicalInventorySnapshotResult.Invalid(
                $"Dependency closure physical snapshot failed: {exception.Message}");
        }
        if (!physical.Succeeded || !physical.Complete || physical.Snapshot is null)
        {
            _lastError = physical.Error;
            _state = _state with { LastError = _lastError };
            return false;
        }
        if (!HasCompletePhysicalSnapshot(physical.Snapshot, closure.Keys))
        {
            _lastError = "Physical inventory snapshot does not contain every dependency key.";
            _state = _state with { LastError = _lastError };
            return false;
        }

        var currentPhysical = FilterPhysical(physical.Snapshot, closure.Keys);
        if (_ledger is not null)
        {
            if (!_ledger.State.UseOwnStock)
            {
                var expandedStarting = ExpandStartingInventory(
                    _ledger.State.StartingInventory,
                    currentPhysical,
                    closure.Keys);
                var recovery = _ledger.ExportRecovery(desired.Selection) with
                {
                    StartingInventory = expandedStarting,
                    ProtectedInventory = expandedStarting,
                };
                _ledger = FcContributionLedger.Recover(recovery, currentPhysical);
            }
            else
            {
                _ledger.Recover(currentPhysical);
            }
        }
        var previousClosure = _dependencyClosure;
        _dependencyClosure = closure.Keys.ToHashSet();
        var result = QueueAbsoluteUpdateLocked(
            desired with { HeldInventory = PublishableHeldLocked().Entries },
            FcWorkerSessionCommandKind.MeaningfulUpdate);
        if (!result.Accepted)
            _dependencyClosure = previousClosure;
        return result.Accepted;
    }

    private FcItemQuantityMap PublishableHeldLocked()
        => _ledger?.GetPublishableHeld(_dependencyClosure) ?? FcItemQuantityMap.Empty;

    private FcContributionLedgerRecovery? ExportRecoveryLocked(FcFulfillmentSelection selection)
        => _ledger?.ExportRecovery(selection) ?? _state.LedgerRecovery;

    private void ReconcileAuthoritativeStateLocked()
    {
        var recoveryRequiredBefore = _recoveryRequired;
        _forked = IsWorkerForkedLocked();
        var world = _transport.WorldStore;
        var author = _state.AuthorId;
        if (world is null || string.IsNullOrWhiteSpace(author) || _forked)
            return;
        if (!world.Workers.TryGetValue(author, out var worker))
            return;
        if (!string.Equals(worker.Header.OwnerAuthorId, author, StringComparison.Ordinal)
            || worker.Header.RecordId != _state.WorkerRegisterId)
            return;
        if (worker.Header.Revision < _state.LastReservedRevision)
            return;
        if (_state.ExplicitUnsubscribed && worker.State is not FcWorkerState.Unsubscribed
            && worker.Header.Revision <= _state.LastReservedRevision)
            return;
        if (_state.SessionId != Guid.Empty
            && (worker.SessionGeneration < _state.SessionGeneration
                || (worker.SessionGeneration == _state.SessionGeneration && worker.SessionId != _state.SessionId)))
            return;
        var previousRevision = _state.LastReservedRevision;
        var previousSession = _state.SessionId;
        var hadPending = _state.PendingWorker is not null;
        var previousAccepted = _accepted;
        var acceptedChanged = previousAccepted is null
            || previousAccepted.Header.Revision != worker.Header.Revision
            || FcCanonical.SemanticHash(previousAccepted) != FcCanonical.SemanticHash(worker);
        var pending = _state.PendingWorker;
        var pendingToKeep = pending is not null
            && pending.Header.Revision > worker.Header.Revision
            ? pending
            : null;
        var desired = pendingToKeep ?? worker;
        _accepted = worker;
        _desired = desired;
        _state = _state with
        {
            SessionId = worker.SessionId,
            SessionGeneration = worker.SessionGeneration,
            LastReservedRevision = Math.Max(_state.LastReservedRevision, desired.Header.Revision),
            LastAcceptedWorker = worker,
            PendingWorker = pendingToKeep,
            PendingKind = pendingToKeep is not null ? _state.PendingKind : null,
            State = desired.State,
            UseOwnStock = desired.UseOwnStock,
            Selection = desired.Selection,
            DependencyClosure = _dependencyClosure.OrderBy(key => key).ToArray(),
            LastCommunicationUnixMilliseconds = acceptedChanged
                ? _clock.UnixMilliseconds
                : _state.LastCommunicationUnixMilliseconds,
            NextRefreshUnixMilliseconds = acceptedChanged
                ? desired.State == FcWorkerState.Unsubscribed
                    ? 0
                    : checked(_clock.UnixMilliseconds + 60_000)
                : _state.NextRefreshUnixMilliseconds,
            CleanShutdown = worker.State == FcWorkerState.Unsubscribed && pendingToKeep is null,
            ExplicitUnsubscribed = desired.State == FcWorkerState.Unsubscribed,
            LedgerRecovery = ExportRecoveryLocked(desired.Selection),
        };
        if (world.GetWorkerHlc(author) is { } hlc)
            _acceptedHlc = hlc;
        if (_state.PendingWorker is null
            && (!recoveryRequiredBefore || worker.State == FcWorkerState.Unsubscribed))
            _recoveryRequired = false;
        if (acceptedChanged
            || _state.LastReservedRevision != previousRevision
            || _state.SessionId != previousSession
            || hadPending && _state.PendingWorker is null)
            EnqueuePersistenceOnlyLocked();
    }

    private ulong ReplicaRevisionLocked()
    {
        var key = _state.AuthorId + WorkerRegisterSuffix;
        return _transport.WorldStore?.RevisionHighWater.TryGetValue(key, out var value) == true ? value : 0;
    }

    private ulong ReplicaGenerationLocked()
        => _transport.WorldStore?.Workers.TryGetValue(_state.AuthorId, out var worker) == true
            ? worker.SessionGeneration
            : 0;

    private bool IsWorkerForkedLocked()
        => !string.IsNullOrWhiteSpace(_state.AuthorId)
            && (_transport.WorldStore?.IsForked(_state.AuthorId + WorkerRegisterSuffix) == true);

    private bool CurrentAuthorMatchesLocked()
        => !string.IsNullOrWhiteSpace(_state.AuthorScope)
            && !string.IsNullOrWhiteSpace(_state.AuthorId)
            && string.Equals(_authorScopeProvider(), _state.AuthorScope, StringComparison.Ordinal)
            && string.Equals(_authorIdProvider(), _state.AuthorId, StringComparison.Ordinal);

    private bool IsOwnWorkerLocked(WorkerSessionRecord worker)
        => worker.Header is not null
            && string.Equals(worker.Header.OwnerAuthorId, _state.AuthorId, StringComparison.Ordinal)
            && worker.Header.RecordId == _state.WorkerRegisterId
            && worker.SessionId == _state.SessionId
            && worker.SessionGeneration == _state.SessionGeneration;

    private bool CanWriteLocked(bool allowRecovery = false)
    {
        _forked = IsWorkerForkedLocked();
        return !_disposed
            && _loadStatus is FcWorkerSessionLoadStatus.Clean or FcWorkerSessionLoadStatus.Missing
            && (allowRecovery || !_recoveryRequired)
            && !_forked
            && _transport.IsReady
            && !string.IsNullOrWhiteSpace(_state.AuthorScope)
            && !string.IsNullOrWhiteSpace(_state.AuthorId)
            && string.Equals(_authorScopeProvider(), _state.AuthorScope, StringComparison.Ordinal)
            && string.Equals(_authorIdProvider(), _state.AuthorId, StringComparison.Ordinal)
            && _compatibilityProvider().IsValid;
    }

    private string WriteBlockReasonLocked()
    {
        if (_loadStatus == FcWorkerSessionLoadStatus.Corrupt)
            return string.IsNullOrWhiteSpace(_lastError) ? "Worker session state is corrupt; writes are blocked." : _lastError;
        if (_forked)
            return "Worker register is forked; writes are blocked.";
        if (_recoveryRequired)
            return "Worker physical recovery is required; writes are blocked.";
        if (string.IsNullOrWhiteSpace(_state.AuthorScope))
            return "Current character content ID is unavailable; worker session is read-only.";
        if (!string.Equals(_authorScopeProvider(), _state.AuthorScope, StringComparison.Ordinal))
            return "Character author scope changed; worker session state must be rebound.";
        if (!string.Equals(_authorIdProvider(), _state.AuthorId, StringComparison.Ordinal))
            return "Native character author changed; worker session state is read-only until rebound.";
        if (!_transport.IsReady)
            return "FC mesh is not ready; worker session is read-only.";
        if (!_compatibilityProvider().IsValid)
            return "Game compatibility is unavailable; worker session is read-only.";
        return "Worker session writes are unavailable.";
    }

    private WorkerSessionRecord NewWorker(
        Guid workerRegisterId,
        Guid sessionId,
        ulong generation,
        ulong revision,
        FcWorkerState state,
        CharacterIdentity character,
        FcFulfillmentSelection selection,
        bool useOwnStock,
        FcItemQuantityMap held,
        string worldFingerprint)
        => new(
            new FcRecordHeader(
                FcProtocolVersion.Current,
                FcProtocolVersion.CurrentSchema,
                FcRecordTypes.WorkerSession,
                workerRegisterId,
                _state.AuthorId,
                revision),
            sessionId,
            generation,
            state,
            character,
            selection,
            useOwnStock,
            held.Entries,
            null,
            Array.Empty<FcLogicalQueueEntry>(),
            Array.Empty<uint>(),
            worldFingerprint);

    private static FcItemQuantityMap FilterPhysical(
        FcItemQuantityMap physical,
        IEnumerable<FcQuantityKey> closure)
    {
        var keys = closure?.ToHashSet();
        return keys is null || keys.Count == 0 ? physical : physical.Filter(keys);
    }

    private static bool HasCompletePhysicalSnapshot(
        FcItemQuantityMap physical,
        IEnumerable<FcQuantityKey> closure)
    {
        if (physical is null || closure is null)
            return false;
        var keys = closure.ToHashSet();
        return keys.Count > 0 && keys.All(key => physical.Contains(key));
    }

    private static FcItemQuantityMap ExpandStartingInventory(
        FcItemQuantityMap existing,
        FcItemQuantityMap currentPhysical,
        IEnumerable<FcQuantityKey> closure)
    {
        var values = existing.Entries
            .ToDictionary(entry => (FcQuantityKey)entry.Key, entry => entry.Quantity);
        foreach (var entry in currentPhysical.Entries)
        {
            var key = (FcQuantityKey)entry.Key;
            if (closure.Contains(key)
                && (!values.TryGetValue(key, out var existingQuantity) || existingQuantity <= 0))
                values[key] = entry.Quantity;
        }
        return new FcItemQuantityMap(values.Select(pair =>
            new ItemQuantityEntry(pair.Key.ItemId, pair.Key.Quality, pair.Value)));
    }

    private static HashSet<FcQuantityKey> ReadPersistedClosure(FcWorkerSessionState state)
    {
        if (state.DependencyClosure is { Length: > 0 })
            return state.DependencyClosure.ToHashSet();
        return ReadPersistedClosure(state.LedgerRecovery);
    }

    private static HashSet<FcQuantityKey> ReadPersistedClosure(FcContributionLedgerRecovery? recovery)
        => recovery is null
            ? new HashSet<FcQuantityKey>()
            : recovery.StartingInventory.Entries
                .Select(entry => (FcQuantityKey)entry.Key)
                .Concat(recovery.ProtectedInventory.Entries.Select(entry => (FcQuantityKey)entry.Key))
                .Concat(recovery.AttributedInventory.Entries.Select(entry => (FcQuantityKey)entry.Key))
                .ToHashSet();

    private static bool IsValidTarget(FcLogicalTarget? target)
        => target is null
            || (target.ListId is not { } listId || listId != Guid.Empty)
            && (target.ItemId is not { } itemId || itemId != 0)
            && (target.RecipeId is not { } recipeId || recipeId != 0)
            && (target.Quality is not { } quality || Enum.IsDefined(quality));

    private static FcLogicalQueueEntry[]? NormalizeQueue(IEnumerable<FcLogicalQueueEntry>? queue)
    {
        var values = (queue ?? Array.Empty<FcLogicalQueueEntry>()).ToArray();
        if (values.Length > MaxLogicalEntries
            || values.Any(entry => entry is null || entry.RecipeId == 0 || entry.Remaining < 0
                || entry.ListId is { } listId && listId == Guid.Empty))
            return null;
        return values;
    }

    private FcWorkerSessionResult BlockRecoveryLocked(string message)
    {
        _lastError = message;
        _recoveryRequired = true;
        _state = _state with { LastError = message };
        return FcWorkerSessionResult.Blocked(message);
    }

    private FcWorkerSessionResult BlockRecovery(string message)
    {
        lock (_gate)
            return BlockRecoveryLocked(message);
    }

    private FcWorkerSessionResult BlockInventoryLocked(string message)
    {
        _lastError = message;
        _state = _state with { LastError = message };
        return FcWorkerSessionResult.Blocked(message);
    }

    private FcWorkerSessionResult BlockInventory(string message)
    {
        lock (_gate)
            return BlockInventoryLocked(message);
    }

    private FcWorkerSessionResult BlockPhysicalSnapshot(
        FcWorkerPhysicalInventorySnapshotResult? physical,
        string? fallback = null)
    {
        var message = string.IsNullOrWhiteSpace(physical?.Error)
            ? fallback ?? "Physical inventory snapshot is unavailable or incomplete."
            : physical!.Error;
        lock (_gate)
        {
            _lastError = message;
            _state = _state with { LastError = message };
        }
        return FcWorkerSessionResult.Blocked(message);
    }

    private FcWorkerSessionResult BlockRevisionLocked(string message)
    {
        _lastError = message;
        _state = _state with { LastError = message };
        return FcWorkerSessionResult.Blocked(message);
    }

    private async Task WaitForIdleAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_pendingCommands == 0)
                    return;
            }
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private async Task FinishDisposeAsync()
    {
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (_gate)
                _lastError = $"Worker session shutdown failed: {exception.Message}";
        }
        finally
        {
            _shutdown.Cancel();
            _shutdown.Dispose();
        }
    }

    private static WorkerSessionRecord? Clone(WorkerSessionRecord? worker)
        => worker is null ? null : FcInMemoryWorkerSessionStateStore.CloneWorker(worker);

    private static string ListKey(PublishedListRecord record)
        => record.Header.OwnerAuthorId + "/list/" + record.ListId.ToString("D");

    private sealed record FcWorkerWork(bool Pending, bool PersistenceOnly)
    {
        public static FcWorkerWork PutCommand { get; } = new(true, false);
        public static FcWorkerWork PersistCommand { get; } = new(false, true);
    }
}
