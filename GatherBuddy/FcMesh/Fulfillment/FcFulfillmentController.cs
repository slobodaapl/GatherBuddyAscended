using System;
using System.Collections.Generic;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Capabilities;
using GatherBuddy.FcMesh.Chest;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Sessions;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Fulfillment;

public enum FcFulfillmentControllerState : byte
{
    Idle,
    StartingSession,
    Recovering,
    EnsureChestKnown,
    CheckingChest,
    DeriveWorld,
    AwaitingCapability,
    Planning,
    BuildExecutionPlan,
    Gathering,
    Crafting,
    Withdrawing,
    Depositing,
    WaitingRemote,
    WaitingConnectivity,
    Blocked,
    Stopping,
    CleanupPending,
    Completed,
}

[Flags]
public enum FcFulfillmentReplanReason : ushort
{
    None = 0,
    ChestSnapshot = 1 << 0,
    Held = 1 << 1,
    ExpiryOrUnsubscribe = 1 << 2,
    PublishedList = 1 << 3,
    Capability = 1 << 4,
    LocalContribution = 1 << 5,
    Transfer = 1 << 6,
    Connectivity = 1 << 7,
    WorkerIntent = 1 << 8,
}

public enum FcFulfillmentActionKind : byte
{
    None,
    Gather,
    Craft,
    Withdraw,
    Deposit,
    WaitRemote,
    Complete,
    Blocked,
}

/// <summary>
/// Immutable decision produced by the existing FC projection/matcher/planner
/// boundary. The controller consumes it; it does not implement another
/// solver. HQ-required work is represented explicitly and cannot be started
/// by the Phase7 controller.
/// </summary>
public sealed record FcFulfillmentPlanDecision(
    bool IsValid,
    string Reason,
    FcFulfillmentActionKind Action,
    Guid? PurposeListId,
    FcLogicalTarget? CurrentTarget,
    FcLogicalQueueEntry[] LogicalQueue,
    uint[] GatherTargetOrder,
    IReadOnlyList<ItemTransferRequest> TransferItems,
    CraftingExecutionPlan? CraftPlan,
    bool RequiresCapability,
    bool HqRequired,
    bool UnsubscribeOnCompletion)
{
    public CapabilityRequestRecord? CapabilityRequest { get; init; }
    public FcCapabilityEligibility CapabilityEligibility { get; init; } = FcCapabilityEligibility.NqAllowed;

    public static FcFulfillmentPlanDecision Invalid(string reason)
        => new(
            false,
            reason,
            FcFulfillmentActionKind.Blocked,
            null,
            null,
            Array.Empty<FcLogicalQueueEntry>(),
            Array.Empty<uint>(),
            Array.Empty<ItemTransferRequest>(),
            null,
            false,
            false,
            false);

    public bool IsComplete => Action == FcFulfillmentActionKind.Complete;
}

public sealed record FcFulfillmentSessionStartOptions(
    bool AllPublishedLists,
    Guid[] ListIds,
    bool UseOwnStock)
{
    public FcFulfillmentSelection Selection
        => FcWorkerSessionSelection.ToSelection(AllPublishedLists, ListIds);
}

/// <summary>
/// Framework-thread game boundary. Implementations call the existing
/// CraftingQueueProcessor/active solver and AutoGather seams; the controller
/// never invokes game APIs, vnav, or Lifestream itself.
/// </summary>
public interface IFcFulfillmentRuntime
{
    bool ConnectivityAvailable { get; }
    bool CraftingActive { get; }
    bool GatheringInteractionActive { get; }
    bool TryStartCraft(CraftingExecutionPlan plan);
    bool TryStartGather(IReadOnlyList<uint> targetOrder);
    bool TryStartGather(CraftingExecutionPlan plan, IReadOnlyList<uint> targetOrder)
        => TryStartGather(targetOrder);
    void StopNavigation();
}

public sealed record FcFulfillmentControllerDiagnostics(
    FcFulfillmentControllerState State,
    FcFulfillmentReplanReason PendingReplanReasons,
    FcFulfillmentActionKind ActiveAction,
    bool StopRequested,
    bool HasPendingTransfer,
    bool IsSubscribed,
    string LastError);

/// <summary>
/// Dynamic FC execution coordinator. World updates only set a coalesced dirty
/// bit while an indivisible craft/gather/transfer action is active. Stop and
/// unload paths contain that action, then unsubscribe; they never enqueue a
/// deposit or travel operation as a side effect of stopping.
/// </summary>
public sealed class FcFulfillmentController : IDisposable
{
    private readonly FcWorkerSessionService _worker;
    private readonly FcChestCoordinator _chest;
    private readonly IFcFulfillmentRuntime _runtime;
    private readonly Func<FcProjectedWorld?> _worldProvider;
    private readonly Func<FcProjectedWorld, FcFulfillmentPlanDecision> _planProvider;
    private readonly Func<FcWorkerSessionResult>? _sessionStarter;
    private readonly Func<FcWorkerSessionResult>? _sessionRecovery;
    private readonly FcCapabilityService? _capabilities;
    private readonly Action? _onCompleted;
    private FcFulfillmentPlanDecision? _decision;
    private FcFulfillmentReplanReason _pendingReasons;
    private string _lastError = string.Empty;
    private bool _stopRequested;
    private bool _stopPublished;
    private bool _disposed;
    private bool _started;
    private bool _sessionStartRequested;
    private bool _sessionRecoveryRequested;
    private FcProjectedIntentSnapshotProvider? _intentProvider;

    public FcFulfillmentController(
        FcWorkerSessionService worker,
        FcChestCoordinator chest,
        IFcFulfillmentRuntime runtime,
        Func<FcProjectedWorld?> worldProvider,
        Func<FcProjectedWorld, FcFulfillmentPlanDecision> planProvider,
        Action? onCompleted = null,
        Func<FcWorkerSessionResult>? sessionStarter = null,
        Func<FcWorkerSessionResult>? sessionRecovery = null,
        FcCapabilityService? capabilities = null)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _chest = chest ?? throw new ArgumentNullException(nameof(chest));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _worldProvider = worldProvider ?? throw new ArgumentNullException(nameof(worldProvider));
        _planProvider = planProvider ?? throw new ArgumentNullException(nameof(planProvider));
        _onCompleted = onCompleted;
        _sessionStarter = sessionStarter;
        _sessionRecovery = sessionRecovery;
        _capabilities = capabilities;
        if (_capabilities is not null)
            _capabilities.CapabilityInvalidated += OnCapabilityInvalidated;
        State = FcFulfillmentControllerState.Idle;
    }

    public FcFulfillmentControllerState State { get; private set; }
    public FcFulfillmentReplanReason PendingReplanReasons => _pendingReasons;
    public FcFulfillmentPlanDecision? CurrentDecision => _decision;
    public bool StopRequested => _stopRequested;
    public string LastError => _lastError;
    public FcFulfillmentControllerDiagnostics Diagnostics
    {
        get
        {
            var session = _worker.Status;
            return new(
                State,
                _pendingReasons,
                _decision?.Action ?? FcFulfillmentActionKind.None,
                _stopRequested,
                _chest.HasPendingTransfer,
                session.IsSubscribed,
                _lastError);
        }
    }

    public bool Start()
    {
        if (_disposed || _started
            || State is not (FcFulfillmentControllerState.Idle or FcFulfillmentControllerState.Completed))
            return false;
        _started = true;
        _stopRequested = false;
        _stopPublished = false;
        _sessionStartRequested = false;
        _sessionRecoveryRequested = false;
        DisposeIntentProvider();
        _pendingReasons = FcFulfillmentReplanReason.None;
        _lastError = string.Empty;
        State = FcFulfillmentControllerState.StartingSession;
        return true;
    }

    public void RequestStop()
    {
        if (_disposed)
            return;
        if (State == FcFulfillmentControllerState.Idle)
            return;
        _stopRequested = true;
        if (State is not FcFulfillmentControllerState.Idle)
            State = FcFulfillmentControllerState.Stopping;
    }

    public void NotifyWorldChanged(FcFulfillmentReplanReason reason)
    {
        if (reason == FcFulfillmentReplanReason.None || _disposed)
            return;
        _pendingReasons |= reason;
        if (State is FcFulfillmentControllerState.Crafting
            or FcFulfillmentControllerState.Gathering
            or FcFulfillmentControllerState.Withdrawing
            or FcFulfillmentControllerState.Depositing)
            return;
        if (State is FcFulfillmentControllerState.WaitingRemote
            or FcFulfillmentControllerState.WaitingConnectivity
            or FcFulfillmentControllerState.AwaitingCapability
            or FcFulfillmentControllerState.Planning
            or FcFulfillmentControllerState.BuildExecutionPlan
            or FcFulfillmentControllerState.Completed)
            State = FcFulfillmentControllerState.DeriveWorld;
    }

    public void Tick()
    {
        if (_disposed)
            return;
        try
        {
            if (_stopRequested)
            {
                TickStopping();
                return;
            }

            switch (State)
            {
                case FcFulfillmentControllerState.Idle:
                    return;
                case FcFulfillmentControllerState.StartingSession:
                    TickStartingSession();
                    return;
                case FcFulfillmentControllerState.Recovering:
                    TickRecovering();
                    return;
                case FcFulfillmentControllerState.EnsureChestKnown:
                    TickEnsureChestKnown();
                    return;
                case FcFulfillmentControllerState.CheckingChest:
                    TickCheckingChest();
                    return;
                case FcFulfillmentControllerState.DeriveWorld:
                    TickDeriveWorld();
                    return;
                case FcFulfillmentControllerState.AwaitingCapability:
                    TickAwaitingCapability();
                    return;
                case FcFulfillmentControllerState.Planning:
                    State = FcFulfillmentControllerState.BuildExecutionPlan;
                    return;
                case FcFulfillmentControllerState.BuildExecutionPlan:
                    TickBuildExecutionPlan();
                    return;
                case FcFulfillmentControllerState.Gathering:
                    TickGathering();
                    return;
                case FcFulfillmentControllerState.Crafting:
                    TickCrafting();
                    return;
                case FcFulfillmentControllerState.Withdrawing:
                case FcFulfillmentControllerState.Depositing:
                    TickTransfer();
                    return;
                case FcFulfillmentControllerState.WaitingRemote:
                    TickWaitingRemote();
                    return;
                case FcFulfillmentControllerState.WaitingConnectivity:
                    TickWaitingConnectivity();
                    return;
                case FcFulfillmentControllerState.CleanupPending:
                    TickCleanupPending();
                    return;
                case FcFulfillmentControllerState.Completed:
                case FcFulfillmentControllerState.Blocked:
                    return;
                case FcFulfillmentControllerState.Stopping:
                    TickStopping();
                    return;
                default:
                    Block("Unknown FC fulfillment controller state.");
                    return;
            }
        }
        catch (Exception exception)
        {
            Block($"FC fulfillment tick failed: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DisposeIntentProvider();
        if (_capabilities is not null)
            _capabilities.CapabilityInvalidated -= OnCapabilityInvalidated;
        _stopRequested = true;
        _chest.RequestStop();
        if (!_chest.HasPendingTransfer && _worker.Status.IsSubscribed)
            _ = _worker.Stop();
        // No wait/block from disposal. The next framework boundary owns any
        // physical reconciliation; the durable journal remains authoritative.
        if (_runtime.CraftingActive || _runtime.GatheringInteractionActive || _chest.HasPendingTransfer)
            State = FcFulfillmentControllerState.CleanupPending;
        else
            State = FcFulfillmentControllerState.Idle;
    }

    private void TickStartingSession()
    {
        var status = _worker.Status;
        if (status.IsRecoveryRequired)
        {
            State = FcFulfillmentControllerState.Recovering;
            return;
        }
        if (!status.IsSubscribed && _sessionStarter is not null && !_sessionStartRequested)
        {
            var started = _sessionStarter();
            if (!started.Accepted)
            {
                Block(started.Message);
                return;
            }
            _sessionStartRequested = true;
            return;
        }
        if (!status.IsSubscribed)
        {
            if (_sessionStartRequested)
                return;
            Block("Worker session is not subscribed; start must be completed by the session service before execution.");
            return;
        }
        State = FcFulfillmentControllerState.EnsureChestKnown;
    }

    private void TickRecovering()
    {
        if (_worker.Status.IsRecoveryRequired)
        {
            if (_sessionRecovery is null)
            {
                Block("Physical worker recovery callback is unavailable.");
                return;
            }
            if (_sessionRecoveryRequested)
                return;
            var recovery = _sessionRecovery();
            if (!recovery.Accepted)
            {
                Block(recovery.Message);
                return;
            }
            _sessionRecoveryRequested = true;
            return;
        }
        if (!_chest.RecoverPending())
        {
            State = _chest.Diagnostics.State == FcChestCoordinatorState.CleanupPending
                ? FcFulfillmentControllerState.CleanupPending
                : FcFulfillmentControllerState.Blocked;
            _lastError = _chest.LastError;
            return;
        }
        State = FcFulfillmentControllerState.EnsureChestKnown;
    }

    private void TickEnsureChestKnown()
    {
        if (!_runtime.ConnectivityAvailable)
        {
            State = FcFulfillmentControllerState.WaitingConnectivity;
            return;
        }
        var world = _worldProvider();
        if (world is null)
        {
            Block("FC world projection is unavailable.");
            return;
        }
        _chest.EnsureChestKnown(world.Chest.IsFresh);
        State = _chest.State == FcChestCoordinatorState.CheckingChest
            ? FcFulfillmentControllerState.CheckingChest
            : _chest.State == FcChestCoordinatorState.Ready
                ? FcFulfillmentControllerState.DeriveWorld
                : FcFulfillmentControllerState.Blocked;
        if (State == FcFulfillmentControllerState.Blocked)
            _lastError = _chest.LastError;
    }

    private void TickCheckingChest()
    {
        _chest.Tick();
        if (_chest.State == FcChestCoordinatorState.Ready)
        {
            _pendingReasons |= FcFulfillmentReplanReason.ChestSnapshot;
            State = FcFulfillmentControllerState.DeriveWorld;
        }
        else if (_chest.State is FcChestCoordinatorState.Blocked or FcChestCoordinatorState.CleanupPending)
        {
            _lastError = _chest.LastError;
            State = _chest.State == FcChestCoordinatorState.CleanupPending
                ? FcFulfillmentControllerState.CleanupPending
                : FcFulfillmentControllerState.Blocked;
        }
    }

    private void TickDeriveWorld()
    {
        if (!_runtime.ConnectivityAvailable)
        {
            State = FcFulfillmentControllerState.WaitingConnectivity;
            return;
        }
        var world = _worldProvider();
        if (world is null)
        {
            Block("FC world projection is unavailable.");
            return;
        }
        if (!world.Chest.IsFresh || _chest.State != FcChestCoordinatorState.Ready)
        {
            State = FcFulfillmentControllerState.EnsureChestKnown;
            return;
        }
        if (!_chest.AllowsWorkerPublication)
        {
            State = FcFulfillmentControllerState.CleanupPending;
            _lastError = "Pending chest transfer blocks worker publication until reconciliation.";
            return;
        }
        _decision = _planProvider(world);
        if (_decision is null || !_decision.IsValid)
        {
            Block(_decision?.Reason ?? "FC fulfillment plan is invalid.");
            return;
        }
        PublishLogicalState(_decision);
        if (State == FcFulfillmentControllerState.Blocked)
            return;
        if (_decision.RequiresCapability || _decision.HqRequired)
        {
            State = FcFulfillmentControllerState.AwaitingCapability;
            return;
        }
        State = FcFulfillmentControllerState.Planning;
    }

    private void TickAwaitingCapability()
    {
        var world = _worldProvider();
        if (world is null)
        {
            Block("FC world projection is unavailable while waiting for craft capability.");
            return;
        }
        var decision = _planProvider(world);
        if (decision is null || !decision.IsValid)
        {
            Block(decision?.Reason ?? "FC capability plan is invalid.");
            return;
        }
        _decision = decision;
        PublishLogicalState(decision);
        if (State == FcFulfillmentControllerState.Blocked)
            return;
        if (decision.RequiresCapability || decision.HqRequired)
        {
            _worker.SetWaiting();
            return;
        }
        State = FcFulfillmentControllerState.Planning;
    }

    private void TickBuildExecutionPlan()
    {
        if (_decision is null)
        {
            State = FcFulfillmentControllerState.DeriveWorld;
            return;
        }
        var decision = _decision;
        _pendingReasons = FcFulfillmentReplanReason.None;
        switch (decision.Action)
        {
            case FcFulfillmentActionKind.Gather:
                if (decision.GatherTargetOrder.Length == 0
                    || decision.CraftPlan is null)
                {
                    Block("Temporary AutoGather execution could not start.");
                    return;
                }
                BindIntentProvider(decision.CraftPlan);
                if (!_runtime.TryStartGather(decision.CraftPlan, decision.GatherTargetOrder))
                {
                    Block("Temporary AutoGather execution could not start.");
                    return;
                }
                State = FcFulfillmentControllerState.Gathering;
                return;
            case FcFulfillmentActionKind.Craft:
            {
                var craftPlan = decision.CraftPlan;
                if (craftPlan is null)
                {
                    Block("FC craft action has no existing CraftingExecutionPlan.");
                    return;
                }
                if (decision.HqRequired || decision.RequiresCapability)
                {
                    if (_capabilities is not null && decision.CapabilityRequest is { } request)
                    {
                        if (!_capabilities.TryPreflightRequest(request, out var reason))
                        {
                            _pendingReasons |= FcFulfillmentReplanReason.Capability;
                            _lastError = reason;
                            _worker.SetWaiting();
                            State = FcFulfillmentControllerState.AwaitingCapability;
                            return;
                        }
                        if (!CraftingQueuePreflight.TryValidate(
                                craftPlan,
                                out var preflightFailure,
                                validatePrecrafts: true))
                        {
                            _pendingReasons |= FcFulfillmentReplanReason.Capability;
                            _lastError = preflightFailure;
                            _worker.SetWaiting();
                            State = FcFulfillmentControllerState.AwaitingCapability;
                            return;
                        }
                    }
                    if (_capabilities is null || decision.CapabilityRequest is null)
                    {
                        State = FcFulfillmentControllerState.AwaitingCapability;
                        return;
                    }
                }
                BindIntentProvider(craftPlan);
                if (!_runtime.TryStartCraft(craftPlan))
                {
                    Block("Existing crafting queue rejected the FC execution plan.");
                    return;
                }
                State = FcFulfillmentControllerState.Crafting;
                return;
            }
            case FcFulfillmentActionKind.Withdraw:
                if (!StartTransfer(FcInventoryTransferKind.Withdraw, decision))
                    return;
                State = FcFulfillmentControllerState.Withdrawing;
                return;
            case FcFulfillmentActionKind.Deposit:
                if (!StartTransfer(FcInventoryTransferKind.Deposit, decision))
                    return;
                State = FcFulfillmentControllerState.Depositing;
                return;
            case FcFulfillmentActionKind.WaitRemote:
                if (!_worker.SetWaiting().Accepted)
                {
                    Block("Worker could not publish Waiting while remote-held contribution is pending.");
                    return;
                }
                State = FcFulfillmentControllerState.WaitingRemote;
                return;
            case FcFulfillmentActionKind.Complete:
                if (decision.UnsubscribeOnCompletion)
                {
                    if (!PublishUnsubscribe())
                        return;
                }
                _started = false;
                DisposeIntentProvider();
                State = FcFulfillmentControllerState.Completed;
                _onCompleted?.Invoke();
                return;
            default:
                Block(decision.Reason.Length == 0 ? "FC plan has no executable action." : decision.Reason);
                return;
        }
    }

    private void TickGathering()
    {
        if (!_runtime.ConnectivityAvailable)
        {
            State = FcFulfillmentControllerState.WaitingConnectivity;
            return;
        }
        if (_runtime.GatheringInteractionActive)
            return;
        if (_runtime.CraftingActive)
        {
            State = FcFulfillmentControllerState.Crafting;
            return;
        }
        _pendingReasons |= FcFulfillmentReplanReason.LocalContribution;
        State = FcFulfillmentControllerState.DeriveWorld;
    }

    private void TickCrafting()
    {
        if (_runtime.CraftingActive)
            return;
        if (_decision?.CraftPlan is { } plan)
            _ = plan.ApplyPendingWorldRevisionAtSafeBoundary(false, false);
        State = FcFulfillmentControllerState.DeriveWorld;
    }

    private void TickTransfer()
    {
        _chest.Tick();
        if (_chest.State is FcChestCoordinatorState.Withdrawing or FcChestCoordinatorState.Depositing
            or FcChestCoordinatorState.Reconciling)
            return;
        if (_chest.State == FcChestCoordinatorState.Ready && !_chest.HasPendingTransfer)
        {
            _pendingReasons |= FcFulfillmentReplanReason.Transfer;
            State = FcFulfillmentControllerState.DeriveWorld;
            return;
        }
        if (_chest.State == FcChestCoordinatorState.CleanupPending)
        {
            _lastError = _chest.LastError;
            State = FcFulfillmentControllerState.CleanupPending;
            return;
        }
        Block(_chest.LastError.Length == 0 ? "FC chest transfer was blocked." : _chest.LastError);
    }

    private void TickWaitingRemote()
    {
        if (!_runtime.ConnectivityAvailable)
        {
            State = FcFulfillmentControllerState.WaitingConnectivity;
            return;
        }
        if (_pendingReasons != FcFulfillmentReplanReason.None)
            State = FcFulfillmentControllerState.DeriveWorld;
    }

    private void TickWaitingConnectivity()
    {
        if (!_runtime.ConnectivityAvailable)
            return;
        if (_runtime.CraftingActive || _runtime.GatheringInteractionActive || _chest.HasPendingTransfer)
            return;
        _pendingReasons |= FcFulfillmentReplanReason.Connectivity;
        State = FcFulfillmentControllerState.DeriveWorld;
    }

    private void TickCleanupPending()
    {
        if (_chest.HasPendingTransfer)
        {
            _chest.Tick();
            return;
        }
        if (_chest.State == FcChestCoordinatorState.CleanupPending)
            return;
        State = FcFulfillmentControllerState.Blocked;
    }

    private void TickStopping()
    {
        _chest.RequestStop();
        _chest.Tick();
        if (_runtime.CraftingActive || _runtime.GatheringInteractionActive || _chest.HasPendingTransfer)
        {
            if (_chest.HasPendingTransfer)
                State = FcFulfillmentControllerState.CleanupPending;
            return;
        }
        _runtime.StopNavigation();
        if (!_stopPublished)
        {
            if (!PublishUnsubscribe())
                return;
            _stopPublished = true;
        }
        _started = false;
        _stopRequested = false;
        DisposeIntentProvider();
        State = FcFulfillmentControllerState.Idle;
    }

    private bool StartTransfer(FcInventoryTransferKind kind, FcFulfillmentPlanDecision decision)
    {
        if (decision.TransferItems is null || decision.TransferItems.Count != 1)
        {
            Block("FC transfer plan must contain exactly one immediate executable queue item.");
            return false;
        }
        var desired = _worker.Status.Desired;
        if (desired is null || !desired.State.Equals(FcWorkerState.Active))
        {
            Block("Subscribed worker state is unavailable for FC transfer.");
            return false;
        }
        var started = kind == FcInventoryTransferKind.Withdraw
            ? _chest.StartWithdrawal(
                desired.SessionId,
                desired.SessionGeneration,
                decision.PurposeListId,
                decision.TransferItems,
                desired.HeldInventoryMap,
                desired)
            : _chest.StartDeposit(
                desired.SessionId,
                desired.SessionGeneration,
                decision.PurposeListId,
                decision.TransferItems,
                desired.HeldInventoryMap,
                desired);
        if (!started)
        {
            if (_chest.State is FcChestCoordinatorState.CheckingChest or FcChestCoordinatorState.Idle)
            {
                State = FcFulfillmentControllerState.EnsureChestKnown;
                return false;
            }
            Block(_chest.LastError.Length == 0 ? "FC transfer could not start." : _chest.LastError);
            return false;
        }
        return true;
    }

    private void PublishLogicalState(FcFulfillmentPlanDecision decision)
    {
        if (!_worker.SetCurrentTarget(decision.CurrentTarget).Accepted)
        {
            Block("Worker logical target publication was rejected.");
            return;
        }
        if (!_worker.SetCraftQueue(decision.LogicalQueue ?? Array.Empty<FcLogicalQueueEntry>()).Accepted)
        {
            Block("Worker logical queue publication was rejected.");
            return;
        }
        if (!_worker.SetGatherTargetOrder(decision.GatherTargetOrder ?? Array.Empty<uint>()).Accepted)
            Block("Worker logical gather target publication was rejected.");
    }

    private bool PublishUnsubscribe()
    {
        var desired = _worker.Status.Desired;
        if (desired is null || desired.State == FcWorkerState.Unsubscribed)
            return true;
        var result = _worker.Stop();
        if (result.Accepted || _worker.Status.Desired?.State == FcWorkerState.Unsubscribed)
            return true;
        Block(result.Message);
        return false;
    }

    private void Block(string message)
    {
        _lastError = message;
        State = FcFulfillmentControllerState.Blocked;
    }

    private void BindIntentProvider(CraftingExecutionPlan plan)
    {
        if (plan.ExecutionSource != ExecutionSource.FcFulfillment)
            return;
        EnsureIntentProvider();
        plan.BindFcIntentSnapshotProvider(_intentProvider);
    }

    private void EnsureIntentProvider()
    {
        if (_intentProvider is not null || !_worker.Status.IsSubscribed)
            return;

        var diagnostics = _worker.Diagnostics;
        var desired = _worker.Status.Desired;
        _intentProvider = new FcProjectedIntentSnapshotProvider(
            _worldProvider,
            () => _worker.Status.Desired,
            () => _worker.Diagnostics,
            diagnostics.AuthorScope,
            diagnostics.AuthorId,
            desired?.WorldFingerprint ?? string.Empty);
    }

    private void DisposeIntentProvider()
    {
        _intentProvider?.Dispose();
        _intentProvider = null;
    }

    private void OnCapabilityInvalidated()
        => NotifyWorldChanged(FcFulfillmentReplanReason.Capability);
}
