using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Fulfillment;

/// <summary>
/// Controller states exposed by the developer-only FC world driver. These
/// states describe decisions against an in-memory world; they are not game
/// execution claims.
/// </summary>
public enum FcSyntheticControllerState : byte
{
    Idle,
    Gathering,
    WaitingRemote,
    Withdrawing,
    Crafting,
    Depositing,
    Complete,
    Cancelled,
    Unsupported,
}

public enum FcSyntheticActionKind : byte
{
    Gather,
    WaitRemote,
    Withdraw,
    Craft,
    Deposit,
    Complete,
    Cancel,
    Unsupported,
}

public sealed record FcSyntheticAction(
    long Sequence,
    FcSyntheticActionKind Kind,
    uint ItemId,
    FcItemQuality Quality,
    int Quantity,
    Guid? ListId,
    string Message);

/// <summary>
/// Small deterministic recipe boundary for synthetic fulfillment. It models
/// only input preflight and attributable output accounting. Solver quality,
/// capability, and game execution remain outside this phase.
/// </summary>
public sealed record FcSyntheticCraftSpec
{
    public Guid ListId { get; }
    public uint RecipeId { get; }
    public uint InputItemId { get; }
    public FcItemQuality InputQuality { get; }
    public int InputQuantity { get; }
    public uint OutputItemId { get; }
    public FcItemQuality OutputQuality { get; }
    public int OutputQuantity { get; }

    public FcSyntheticCraftSpec(
        Guid listId,
        uint recipeId,
        uint inputItemId,
        FcItemQuality inputQuality,
        int inputQuantity,
        uint outputItemId,
        FcItemQuality outputQuality,
        int outputQuantity)
    {
        if (listId == Guid.Empty)
            throw new ArgumentException("Synthetic craft list ID is required.", nameof(listId));
        if (recipeId == 0 || inputItemId == 0 || outputItemId == 0)
            throw new ArgumentOutOfRangeException(nameof(recipeId), "Synthetic craft IDs must be non-zero.");
        if (inputQuantity <= 0 || outputQuantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputQuantity), "Synthetic craft quantities must be positive.");
        if (inputQuality != FcItemQuality.Nq || outputQuality != FcItemQuality.Nq)
            throw new NotSupportedException("Phase 3 synthetic crafting supports NQ targets only.");

        ListId = listId;
        RecipeId = recipeId;
        InputItemId = inputItemId;
        InputQuality = inputQuality;
        InputQuantity = inputQuantity;
        OutputItemId = outputItemId;
        OutputQuality = outputQuality;
        OutputQuantity = outputQuantity;
    }
}

/// <summary>
/// Explicit test/developer trust boundary for synthetic records. This is not
/// native verification and intentionally leaves <c>NativeVerified</c> false.
/// </summary>
public static class FcSyntheticTrustedContext
{
    public static FcVerifiedMeshContext For(FcMeshRecord envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return FcVerifiedMeshContext.FromEnvelope(envelope) with
        {
            SignatureValid = true,
            TransportAuthorId = envelope.ActualAuthorId,
        };
    }
}

/// <summary>
/// Developer-only local FC fulfillment world. Every injected value is still a
/// typed, enveloped record applied through <see cref="FcWorldStore"/>. The
/// synthetic verification context is explicit and never sets NativeVerified.
/// This class has no native, network, chest-probe, travel, or player-inventory
/// dependency.
/// </summary>
public sealed class FcSyntheticFulfillmentDriver
{
    public static readonly Guid DefaultListId = Guid.Parse("00000000-0000-0000-0000-00000000f301");

    private readonly IFcClock _clock;
    private readonly Guid _sessionId;
    private readonly ulong _sessionGeneration;
    private readonly string _localAuthorId;
    private readonly string _worldFingerprint;
    private readonly string _hlcNodeId;
    private readonly FcCompatibilityContext _compatibilityContext;
    private IReadOnlyList<Guid> _selectedListIds;
    private FcHlcClock _hlcClock;
    private FcWorldStore _store;
    private FcWorldProjection _projection;
    private FcProjectedWorld _world = null!;
    private readonly List<FcSyntheticAction> _actionLog = new();
    private FcSyntheticCraftSpec? _craftSpec;
    private FcSyntheticAction? _lastAction;
    private FcItemQuantityMap _pendingDeposit = FcItemQuantityMap.Empty;
    private FcSyntheticAction? _activeGather;
    private FcSyntheticAction? _activeCraft;
    private long _actionSequence;
    private long _operationSequence;
    private FcSyntheticControllerState _state;

    public FcSyntheticFulfillmentDriver(
        IFcClock? clock = null,
        Guid? sessionId = null,
        IReadOnlyList<Guid>? selectedListIds = null,
        string localAuthorId = "synthetic-local",
        ulong sessionGeneration = 1,
        string worldFingerprint = "synthetic-world",
        string hlcNodeId = "synthetic-node")
    {
        _clock = clock ?? FcSystemClock.Instance;
        _sessionId = sessionId ?? Guid.Parse("00000000-0000-0000-0000-00000000f302");
        if (_sessionId == Guid.Empty)
            throw new ArgumentException("Synthetic session ID is required.", nameof(sessionId));
        if (sessionGeneration == 0)
            throw new ArgumentOutOfRangeException(nameof(sessionGeneration));
        _sessionGeneration = sessionGeneration;
        _localAuthorId = ValidateIdentity(localAuthorId, nameof(localAuthorId));
        _worldFingerprint = ValidateFingerprint(worldFingerprint);
        _hlcNodeId = ValidateIdentity(hlcNodeId, nameof(hlcNodeId));
        _compatibilityContext = new FcCompatibilityContext(
            FcPublishedListMapper.CurrentPlannerSemanticsVersion,
            "synthetic-game");
        _selectedListIds = NormalizeListIds(selectedListIds ?? [DefaultListId]);
        _hlcClock = new FcHlcClock(_hlcNodeId, _clock);
        _store = new FcWorldStore(_clock);
        _projection = new FcWorldProjection(_store);
        SeedLocalWorker();
        RebuildWorld();
    }

    public bool IsDeveloperOnly => true;
    public bool HasPhysicalEffects => false;
    public bool CanPublishNativeRecords => false;
    public bool UsesRealChestInteraction => false;
    public IFcClock Clock => _clock;
    public FcWorldStore Store => _store;
    public FcProjectedWorld World => _world;
    public CraftingExecutionPlan? ExecutionPlan { get; private set; }
    public FcSyntheticControllerState State => _state;
    public FcSyntheticAction? LastAction => _lastAction;
    public IReadOnlyList<FcSyntheticAction> ActionLog => _actionLog;
    public IReadOnlyList<Guid> SelectedListIds => _selectedListIds;
    public FcSyntheticCraftSpec? CraftSpec => _craftSpec;
    public WorkerSessionRecord LocalWorker
        => _store.Workers.Values.Single(worker => worker.Header.OwnerAuthorId == _localAuthorId);

    /// <summary>Replaces the synthetic world with a fresh private store.</summary>
    public void Reset()
    {
        _store = new FcWorldStore(_clock);
        _projection = new FcWorldProjection(_store);
        _hlcClock = new FcHlcClock(_hlcNodeId, _clock);
        _actionLog.Clear();
        _lastAction = null;
        _craftSpec = null;
        _pendingDeposit = FcItemQuantityMap.Empty;
        _activeGather = null;
        _activeCraft = null;
        _actionSequence = 0;
        _operationSequence = 0;
        _state = FcSyntheticControllerState.Idle;
        ExecutionPlan = null;
        SeedLocalWorker();
        RebuildWorld();
    }

    public void SetCraftSpec(FcSyntheticCraftSpec? spec)
        => _craftSpec = spec;

    public FcApplyResult SetSelection(IEnumerable<Guid> listIds)
    {
        var normalized = NormalizeListIds(listIds);
        if (normalized.SequenceEqual(_selectedListIds))
            return new FcApplyResult(FcApplyStatus.Duplicate, "Synthetic selection unchanged.", _store.Revision);

        var previousSelection = _selectedListIds;
        _selectedListIds = normalized;
        var current = LocalWorker;
        var next = current with
        {
            Header = NextHeader(current.Header, FcRecordTypes.WorkerSession),
            Selection = FcFulfillmentSelection.Specific(_selectedListIds.ToArray()),
        };
        var result = ApplySynthetic(next);
        if (result.Status != FcApplyStatus.Accepted)
            _selectedListIds = previousSelection;
        return result;
    }

    public FcApplyResult InjectList(PublishedListRecord list, FcHlcTimestamp? authoredHlc = null)
        => ApplySynthetic(list, authoredHlc);

    public FcApplyResult InjectChest(ChestSnapshotRecord chest, FcHlcTimestamp? authoredHlc = null)
        => ApplySynthetic(chest, authoredHlc);

    public FcApplyResult InjectWorker(WorkerSessionRecord worker, FcHlcTimestamp? authoredHlc = null)
        => ApplySynthetic(worker, authoredHlc);

    public FcApplyResult InjectLocalHeld(FcItemQuantityMap held)
    {
        ArgumentNullException.ThrowIfNull(held);
        return PublishLocalWorker(held, FcWorkerState.Active)
            ? new FcApplyResult(FcApplyStatus.Accepted, "Synthetic local held inventory injected.", _store.Revision)
            : FcApplyResult.Reject(_store.Revision, "Synthetic local held inventory injection was rejected.");
    }

    public FcApplyResult InjectRecord<T>(T record, FcHlcTimestamp? authoredHlc = null)
        where T : class
        => ApplySynthetic(record, authoredHlc);

    /// <summary>
    /// Convenience input for the developer panel. It creates one NQ public
    /// target and applies it as a normal synthetic published-list record.
    /// </summary>
    public FcApplyResult InjectDemoList(
        Guid listId,
        uint itemId,
        int quantity,
        ulong revision,
        FcItemQuality quality = FcItemQuality.Nq,
        string owner = "synthetic-list")
    {
        if (quality != FcItemQuality.Nq)
            throw new NotSupportedException("Phase 3 synthetic targets support NQ only.");
        var list = new PublishedListRecord(
            Header(FcRecordTypes.PublishedList, listId, owner, revision),
            listId,
            "Synthetic FC target",
            true,
            1,
            "synthetic-game",
            [new PublishedRecipeTarget(1, itemId, quantity, quality)],
            FcQualityPolicy.Empty,
            FcQualityPolicy.Empty);
        return InjectList(list);
    }

    public FcApplyResult InjectDemoChest(
        int quantity,
        ulong revision,
        uint itemId = 100,
        FcItemQuality quality = FcItemQuality.Nq,
        string? owner = null)
    {
        if (quality != FcItemQuality.Nq)
            throw new NotSupportedException("Phase 3 synthetic chest input supports NQ only.");
        if (quantity < 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        var chestOwner = ValidateIdentity(owner ?? _localAuthorId, nameof(owner));
        var chest = new ChestSnapshotRecord(
            Header(FcRecordTypes.ChestSnapshot, DeterministicGuid($"{chestOwner}/chest/{revision}"), chestOwner, revision),
            true,
            FcRecordValidator.CompleteChestPageMask,
            quantity <= 0 ? Array.Empty<ItemQuantityEntry>() : [new ItemQuantityEntry(itemId, quality, quantity)],
            new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>()));
        return InjectChest(chest);
    }

    public FcApplyResult InjectDemoRemoteWorker(
        string owner,
        Guid sessionId,
        Guid listId,
        int heldQuantity,
        TimeSpan age,
        ulong revision = 1,
        ulong generation = 1,
        uint itemId = 100,
        FcItemQuality quality = FcItemQuality.Nq,
        FcWorkerState state = FcWorkerState.Active)
    {
        if (owner == _localAuthorId)
            throw new ArgumentException("Remote worker must have a distinct author ID.", nameof(owner));
        owner = ValidateIdentity(owner, nameof(owner));
        if (age < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(age));
        if (heldQuantity < 0)
            throw new ArgumentOutOfRangeException(nameof(heldQuantity));
        var physical = Math.Max(0, checked(_clock.UnixMilliseconds - age.Ticks / TimeSpan.TicksPerMillisecond));
        var worker = new WorkerSessionRecord(
            Header(FcRecordTypes.WorkerSession, sessionId, owner, revision),
            sessionId,
            generation,
            state,
            new CharacterIdentity(owner, owner, "synthetic-world"),
            FcFulfillmentSelection.Specific(listId),
            false,
            heldQuantity <= 0 ? Array.Empty<ItemQuantityEntry>() : [new ItemQuantityEntry(itemId, quality, heldQuantity)],
            null,
            [],
            [itemId],
            _worldFingerprint);
        return InjectWorker(worker, new FcHlcTimestamp(physical, 0, owner + "-hlc"));
    }

    /// <summary>
    /// Runs synthetic decisions until completion, remote waiting, cancellation,
    /// or the bounded step count. Gather/craft actions commit on the next safe
    /// boundary, which allows callers to inject a newer world revision while
    /// the interaction is active.
    /// </summary>
    public IReadOnlyList<FcSyntheticAction> Run(int maxSteps = 64)
    {
        if (maxSteps <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSteps));
        var actions = new List<FcSyntheticAction>();
        for (var i = 0; i < maxSteps; i++)
        {
            var action = Step();
            actions.Add(action);
            if (State is FcSyntheticControllerState.Complete
                or FcSyntheticControllerState.WaitingRemote
                or FcSyntheticControllerState.Cancelled
                or FcSyntheticControllerState.Unsupported)
                break;
        }
        return actions;
    }

    public FcSyntheticAction Step(
        bool gatheringInteractionActive = false,
        bool craftingInteractionActive = false)
    {
        var world = RebuildWorld();
        if (_state == FcSyntheticControllerState.Cancelled)
            return _lastAction ?? Record(
                FcSyntheticActionKind.Cancel,
                0,
                FcItemQuality.Nq,
                0,
                null,
                "Synthetic controller remains cancelled until reset; no transfer is implied.");
        EnsureExecutionPlan(world);

        if (_activeGather is { } gather)
        {
            if (gatheringInteractionActive)
                return gather;
            if (ExecutionPlan?.IsWorldRevisionDirty == true)
            {
                ApplyPendingPlanAtSafeBoundary(isCrafting: false, isGatheringInteraction: false);
                _activeGather = null;
                _state = FcSyntheticControllerState.Idle;
                return Evaluate(RebuildWorld());
            }

            _activeGather = null;
            if (!CommitGather(gather))
            {
                _state = FcSyntheticControllerState.Cancelled;
                return Record(
                    FcSyntheticActionKind.Cancel,
                    gather.ItemId,
                    gather.Quality,
                    0,
                    gather.ListId,
                    "Synthetic gather completion rejected; local state unchanged.");
            }
            _state = FcSyntheticControllerState.Idle;
            var gatheredWorld = RebuildWorld();
            EnsureExecutionPlan(gatheredWorld);
            if (ExecutionPlan?.IsWorldRevisionDirty == true)
                ApplyPendingPlanAtSafeBoundary(isCrafting: false, isGatheringInteraction: false);
            return Evaluate(RebuildWorld());
        }

        if (_activeCraft is { } craft)
        {
            if (craftingInteractionActive)
                return craft;
            if (ExecutionPlan?.IsWorldRevisionDirty == true)
            {
                ApplyPendingPlanAtSafeBoundary(isCrafting: false, isGatheringInteraction: false);
                _activeCraft = null;
                _state = FcSyntheticControllerState.Idle;
                return Evaluate(RebuildWorld());
            }

            _activeCraft = null;
            if (!CommitCraft())
            {
                _state = FcSyntheticControllerState.Idle;
                return Evaluate(RebuildWorld());
            }
            RebuildWorld();
            return craft;
        }

        if (_state == FcSyntheticControllerState.Complete
            && world.Revision.Number > (ExecutionPlan?.WorldRevision?.Number ?? -1))
            _state = FcSyntheticControllerState.Idle;

        if (ExecutionPlan?.IsWorldRevisionDirty == true)
            ApplyPendingPlanAtSafeBoundary(isCrafting: false, isGatheringInteraction: false);
        return Evaluate(RebuildWorld());
    }

    public FcSyntheticAction CompleteGather(int? quantity = null)
    {
        if (_activeGather is null)
            throw new InvalidOperationException("No synthetic gathering interaction is active.");
        if (quantity is { } completed && completed <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        if (quantity is { } value && value != _activeGather.Quantity)
            _activeGather = _activeGather with { Quantity = value };
        return Step();
    }

    public FcSyntheticAction CompleteCraft()
    {
        if (_activeCraft is null)
            throw new InvalidOperationException("No synthetic crafting interaction is active.");
        return Step();
    }

    /// <summary>Stops only the synthetic controller; held items stay held.</summary>
    public FcSyntheticAction Cancel()
    {
        _activeGather = null;
        _activeCraft = null;
        _state = FcSyntheticControllerState.Cancelled;
        return Record(
            FcSyntheticActionKind.Cancel,
            0,
            FcItemQuality.Nq,
            0,
            null,
            "Synthetic stop/cancel leaves held inventory untouched; no deposit is implied.");
    }

    private FcSyntheticAction Evaluate(FcProjectedWorld world)
    {
        if (world.ActiveListIds.Count == 0 || !_selectedListIds.Any(world.ActiveListIds.Contains))
        {
            _state = FcSyntheticControllerState.Complete;
            return Record(FcSyntheticActionKind.Complete, 0, FcItemQuality.Nq, 0, null,
                "No selected published synthetic goal is active.");
        }

        var selectedTargets = world.ActiveLists
            .Where(list => _selectedListIds.Contains(list.ListId))
            .SelectMany(list => list.FinalTargets.Select(target => (list.ListId, Target: target)))
            .OrderBy(value => value.ListId)
            .ThenBy(value => value.Target.ItemId)
            .ThenBy(value => value.Target.Quality)
            .ToArray();
        if (selectedTargets.Any(value => value.Target.Quality != FcItemQuality.Nq))
        {
            _state = FcSyntheticControllerState.Unsupported;
            return Record(FcSyntheticActionKind.Unsupported, 0, FcItemQuality.Nq, 0, null,
                "HQ synthetic targets are unsupported in Phase 3 and were not flattened to NQ.");
        }

        if (_pendingDeposit.Entries.Length > 0)
            return CommitTransfer(_pendingDeposit, FcInventoryTransferKind.Deposit, "Deposit crafted output explicitly requested by the controller.");

        if (selectedTargets.Length == 0)
        {
            _state = FcSyntheticControllerState.Complete;
            return Record(FcSyntheticActionKind.Complete, 0, FcItemQuality.Nq, 0, null,
                "Selected synthetic goal has no final targets.");
        }

        foreach (var selected in selectedTargets)
        {
            var target = selected.Target;
            var key = new FcQuantityKey(target.ItemId, target.Quality);
            var contributions = world.Fulfillment.Contributions
                .Where(contribution => contribution.ListId == selected.ListId && contribution.Key == key)
                .ToArray();
            var localQuantity = contributions
                .Where(contribution => !contribution.IsChest
                    && string.Equals(contribution.SourceId, _localAuthorId, StringComparison.Ordinal))
                .Sum(contribution => contribution.Quantity);
            var chestQuantity = contributions
                .Where(contribution => contribution.IsChest)
                .Sum(contribution => contribution.Quantity);
            var remoteQuantity = contributions
                .Where(contribution => !contribution.IsChest
                    && !string.Equals(contribution.SourceId, _localAuthorId, StringComparison.Ordinal))
                .Sum(contribution => contribution.Quantity);
            var remaining = Math.Max(0, target.Quantity - localQuantity - chestQuantity - remoteQuantity);

            if (remaining == 0)
            {
                if (localQuantity >= target.Quantity)
                    continue;
                if (_craftSpec is { } depositedSpec
                    && depositedSpec.ListId == selected.ListId
                    && depositedSpec.OutputItemId == target.ItemId
                    && depositedSpec.OutputQuality == target.Quality
                    && chestQuantity > 0)
                    continue;
                if (chestQuantity > 0)
                    return CommitTransfer(
                        new FcItemQuantityMap([new ItemQuantityEntry(target.ItemId, target.Quality, chestQuantity)]),
                        FcInventoryTransferKind.Withdraw,
                        "Withdraw fresh global FC chest stock allocated to this list.");
                _state = FcSyntheticControllerState.WaitingRemote;
                return Record(FcSyntheticActionKind.WaitRemote, target.ItemId, target.Quality,
                    remoteQuantity, selected.ListId,
                    "Target is represented by an active selected remote worker; local gathering is unnecessary.");
            }

            if (_craftSpec is { } spec
                && spec.ListId == selected.ListId
                && spec.OutputItemId == target.ItemId
                && spec.OutputQuality == target.Quality)
            {
                var localInput = new FcCompletionCountProvider(
                    new FcLocalConsumableInventorySource(LocalWorker.HeldInventoryMap))
                    .GetCompletionCount(spec.InputItemId, spec.InputQuality);
                if (localInput >= spec.InputQuantity)
                {
                    _state = FcSyntheticControllerState.Crafting;
                    _activeCraft = Record(
                        FcSyntheticActionKind.Craft,
                        spec.OutputItemId,
                        spec.OutputQuality,
                        spec.OutputQuantity,
                        spec.ListId,
                        "Craft action selected only after synthetic local consumable preflight.");
                    return _activeCraft;
                }

                var chestInput = world.Chest.IsFresh
                    ? world.Chest.Items.Get(spec.InputItemId, spec.InputQuality)
                    : 0;
                var remoteInput = world.ActiveWorkers
                    .Where(worker => !string.Equals(worker.Header.OwnerAuthorId, _localAuthorId, StringComparison.Ordinal))
                    .Where(worker => EligibleFor(worker, spec.ListId))
                    .Sum(worker => worker.HeldInventoryMap.Get(spec.InputItemId, spec.InputQuality));
                var missingInput = checked(spec.InputQuantity - localInput);
                if (chestInput > 0)
                    return CommitTransfer(
                        new FcItemQuantityMap([new ItemQuantityEntry(
                            spec.InputItemId,
                            spec.InputQuality,
                            Math.Min(missingInput, chestInput))]),
                        FcInventoryTransferKind.Withdraw,
                        "Withdraw fresh chest input before synthetic craft preflight.");
                if (remoteInput > 0)
                {
                    _state = FcSyntheticControllerState.WaitingRemote;
                    return Record(FcSyntheticActionKind.WaitRemote, spec.InputItemId, spec.InputQuality,
                        remoteInput, spec.ListId,
                        "Craft input is represented only by an active selected remote worker.");
                }

                _state = FcSyntheticControllerState.Gathering;
                _activeGather = Record(
                    FcSyntheticActionKind.Gather,
                    spec.InputItemId,
                    spec.InputQuality,
                    missingInput,
                    spec.ListId,
                    "Gather synthetic craft input missing from every represented source.");
                return _activeGather;
            }

            if (chestQuantity > 0)
                return CommitTransfer(
                    new FcItemQuantityMap([new ItemQuantityEntry(
                        target.ItemId,
                        target.Quality,
                        Math.Min(remaining, chestQuantity))]),
                    FcInventoryTransferKind.Withdraw,
                    "Withdraw fresh global FC chest stock allocated to this list.");
            if (remoteQuantity > 0)
            {
                _state = FcSyntheticControllerState.WaitingRemote;
                return Record(FcSyntheticActionKind.WaitRemote, target.ItemId, target.Quality,
                    remoteQuantity, selected.ListId,
                    "Target is represented only by an active selected remote worker.");
            }

            _state = FcSyntheticControllerState.Gathering;
            _activeGather = Record(
                FcSyntheticActionKind.Gather,
                target.ItemId,
                target.Quality,
                remaining,
                selected.ListId,
                "Gather target quantity not represented by chest or active selected workers.");
            return _activeGather;
        }

        _state = FcSyntheticControllerState.Complete;
        return Record(FcSyntheticActionKind.Complete, 0, FcItemQuality.Nq, 0, null,
            "Selected synthetic goals are fulfilled by the projected world.");
    }

    private bool CommitGather(FcSyntheticAction action)
    {
        var worker = LocalWorker;
        var held = worker.HeldInventoryMap.Add(new FcItemQuantityMap([
            new ItemQuantityEntry(action.ItemId, action.Quality, action.Quantity)]));
        return PublishLocalWorker(held, FcWorkerState.Active);
    }

    private bool CommitCraft()
    {
        if (_craftSpec is not { } spec)
            return false;
        var worker = LocalWorker;
        var localInput = new FcCompletionCountProvider(
            new FcLocalConsumableInventorySource(worker.HeldInventoryMap))
            .GetCompletionCount(spec.InputItemId, spec.InputQuality);
        if (localInput < spec.InputQuantity)
            return false;
        var held = worker.HeldInventoryMap
            .SubtractClamped(new FcItemQuantityMap([
                new ItemQuantityEntry(spec.InputItemId, spec.InputQuality, spec.InputQuantity)]))
            .Add(new FcItemQuantityMap([
                new ItemQuantityEntry(spec.OutputItemId, spec.OutputQuality, spec.OutputQuantity)]));
        if (!PublishLocalWorker(held, FcWorkerState.Active))
            return false;
        _pendingDeposit = new FcItemQuantityMap([
            new ItemQuantityEntry(spec.OutputItemId, spec.OutputQuality, spec.OutputQuantity)]);
        _state = FcSyntheticControllerState.Depositing;
        return true;
    }

    private FcSyntheticAction CommitTransfer(
        FcItemQuantityMap actual,
        FcInventoryTransferKind kind,
        string message)
    {
        if (actual.Entries.Length == 0)
            return Record(FcSyntheticActionKind.Complete, 0, FcItemQuality.Nq, 0, null,
                "Synthetic transfer had no positive quantity and was not emitted.");
        var chest = _store.Chests.Values
            .SingleOrDefault(value => value.Header.OwnerAuthorId == _localAuthorId);
        var projectedChest = _world.Chest.Snapshot;
        var localChestHlc = chest is null
            ? null
            : _store.GetChestHlc(chest.Header.OwnerAuthorId);
        var projectedChestHlc = projectedChest is null
            ? null
            : _store.GetChestHlc(projectedChest.Header.OwnerAuthorId);
        var localChestIsProjectedWinner = projectedChest is not null
            && chest is not null
            && string.Equals(projectedChest.Header.OwnerAuthorId, _localAuthorId, StringComparison.Ordinal)
            && projectedChest.Header.Revision == chest.Header.Revision
            && projectedChest.Header.RecordId == chest.Header.RecordId
            && string.Equals(
                FcCanonical.SemanticHash(projectedChest),
                FcCanonical.SemanticHash(chest),
                StringComparison.Ordinal)
            && localChestHlc is { } localStamp
            && projectedChestHlc is { } projectedStamp
            && localStamp == projectedStamp;
        if (chest is null
            || !chest.Complete
            || !_world.Chest.IsFresh
            || !localChestIsProjectedWinner)
        {
            _state = FcSyntheticControllerState.WaitingRemote;
            return Record(FcSyntheticActionKind.WaitRemote, actual.Entries[0].ItemId,
                actual.Entries[0].Quality, actual.Entries[0].Quantity, null,
                "No fresh locally observed chest snapshot is available; no physical action is attempted.");
        }

        var worker = LocalWorker;
        var nextChestItems = kind == FcInventoryTransferKind.Withdraw
            ? new FcItemQuantityMap(chest.Items).SubtractClamped(actual)
            : new FcItemQuantityMap(chest.Items).Add(actual);
        var nextWorkerItems = kind == FcInventoryTransferKind.Withdraw
            ? worker.HeldInventoryMap.Add(actual)
            : worker.HeldInventoryMap.SubtractClamped(actual);
        var nextWorker = worker with
        {
            Header = NextHeader(worker.Header, FcRecordTypes.WorkerSession),
            HeldInventory = nextWorkerItems.Entries,
        };
        var nextChest = chest with
        {
            Header = chest.Header with
            {
                Revision = checked(chest.Header.Revision + 1),
                RecordId = DeterministicGuid($"{_localAuthorId}/chest/{chest.Header.Revision + 1}"),
            },
            Items = nextChestItems.Entries,
        };
        var operationId = DeterministicGuid($"{_sessionId:D}/transfer/{++_operationSequence}");
        var transfer = new FcInventoryTransferRecord(
            Header(FcRecordTypes.InventoryTransfer, operationId, _localAuthorId, (ulong)_operationSequence),
            operationId,
            worker.SessionId,
            worker.SessionGeneration,
            _selectedListIds.FirstOrDefault(),
            kind,
            FcInventoryTransferOutcome.Committed,
            actual.Entries,
            nextChest,
            nextWorker);
        var result = ApplySynthetic(transfer);
        if (result.Status is not (FcApplyStatus.Accepted or FcApplyStatus.Duplicate))
        {
            _state = FcSyntheticControllerState.Idle;
            return Record(FcSyntheticActionKind.Cancel, actual.Entries[0].ItemId,
                actual.Entries[0].Quality, actual.Entries[0].Quantity, transfer.PurposeListId,
                $"Atomic synthetic transfer rejected: {result.Message}");
        }

        _pendingDeposit = FcItemQuantityMap.Empty;
        _state = FcSyntheticControllerState.Idle;
        return Record(
            kind == FcInventoryTransferKind.Withdraw
                ? FcSyntheticActionKind.Withdraw
                : FcSyntheticActionKind.Deposit,
            actual.Entries[0].ItemId,
            actual.Entries[0].Quality,
            actual.Entries.Sum(entry => entry.Quantity),
            transfer.PurposeListId,
            message + " One FcInventoryTransferRecord committed both after-states.");
    }

    private bool PublishLocalWorker(FcItemQuantityMap held, FcWorkerState state)
    {
        var current = LocalWorker;
        var next = current with
        {
            Header = NextHeader(current.Header, FcRecordTypes.WorkerSession),
            State = state,
            HeldInventory = held.Entries,
            CurrentTarget = null,
        };
        var result = ApplySynthetic(next);
        return result.Status is FcApplyStatus.Accepted or FcApplyStatus.Duplicate;
    }

    private void EnsureExecutionPlan(FcProjectedWorld world)
    {
        var represented = BuildRepresentedInventory(world);
        var local = new FcLocalConsumableInventorySource(LocalWorker.HeldInventoryMap);
        var worldRevision = world.Revision.Fingerprint.Length == 0
            ? new FcWorldRevision(world.Revision.Number, "synthetic-empty-world")
            : world.Revision;
        var context = new FcExecutionContext(
            _sessionId,
            _selectedListIds,
            _worldFingerprint,
            worldRevision);
        var planning = CraftingPlanningContext.CreateFc(
            new FcRepresentedInventorySource(represented),
            local,
            context);
        if (ExecutionPlan is null)
        {
            ExecutionPlan = CraftingExecutionPlan.CreateFc(
                new CraftingListDefinition { ID = int.MinValue + 301, Name = "Synthetic FC fulfillment" },
                planning);
            return;
        }

        _ = ExecutionPlan.MarkWorldRevisionChanged(planning);
    }

    private void ApplyPendingPlanAtSafeBoundary(bool isCrafting, bool isGatheringInteraction)
        => ExecutionPlan?.ApplyPendingWorldRevisionAtSafeBoundary(isCrafting, isGatheringInteraction);

    private FcItemQuantityMap BuildRepresentedInventory(FcProjectedWorld world)
    {
        var values = new Dictionary<FcQuantityKey, int>();
        foreach (var contribution in world.Fulfillment.Contributions
                     .Where(contribution => _selectedListIds.Contains(contribution.ListId)))
        {
            values[contribution.Key] = checked(values.GetValueOrDefault(contribution.Key) + contribution.Quantity);
        }
        return FcItemQuantityMap.FromDictionary(values);
    }

    private bool EligibleFor(WorkerSessionRecord worker, Guid listId)
        => worker.Selection.AllPublishedLists || worker.Selection.ListIds.Contains(listId);

    private FcProjectedWorld RebuildWorld()
    {
        _world = _projection.Build(_clock, _compatibilityContext);
        return _world;
    }

    private void SeedLocalWorker()
    {
        var worker = new WorkerSessionRecord(
            Header(FcRecordTypes.WorkerSession, _sessionId, _localAuthorId, 1),
            _sessionId,
            _sessionGeneration,
            FcWorkerState.Active,
            new CharacterIdentity(_localAuthorId, _localAuthorId, "synthetic-world"),
            FcFulfillmentSelection.Specific(_selectedListIds.ToArray()),
            false,
            Array.Empty<ItemQuantityEntry>(),
            null,
            [],
            [],
            _worldFingerprint);
        _ = ApplySynthetic(worker);
    }

    private FcApplyResult ApplySynthetic<T>(T record, FcHlcTimestamp? authoredHlc = null)
        where T : class
    {
        var header = HeaderOf(record);
        var owner = header.OwnerAuthorId;
        var hlc = authoredHlc ?? (string.Equals(owner, _localAuthorId, StringComparison.Ordinal)
            ? _hlcClock.Next(_clock)
            : new FcHlcTimestamp(_clock.UnixMilliseconds, 0, owner + "-hlc"));
        if (hlc.PhysicalUnixMs < 0 || string.IsNullOrWhiteSpace(hlc.NodeId))
            return FcApplyResult.Reject(_store.Revision, "Synthetic HLC is invalid.");
        try
        {
            if (string.Equals(owner, _localAuthorId, StringComparison.Ordinal))
                _hlcClock.Observe(hlc, _clock);
        }
        catch (Exception exception)
        {
            return FcApplyResult.Reject(_store.Revision, $"Synthetic HLC update failed: {exception.Message}");
        }

        var envelope = FcMeshRecord.Create(
            record,
            owner,
            hlc,
            GenerationOf(record),
            header.Revision,
            header.RecordType,
            header.RecordId.ToString("D")) with
        {
            ProtocolVersion = (ushort)FcProtocolVersion.Current,
            Signature = "synthetic-developer-signature",
            SignatureAlgorithm = "synthetic-developer",
        };
        envelope = envelope with { OriginalEnvelopeBytes = FcCanonical.SerializeUtf8(envelope) };
        var context = FcSyntheticTrustedContext.For(envelope);
        var result = _store.Apply(envelope, record, context);
        if (result.Status is FcApplyStatus.Accepted or FcApplyStatus.Duplicate)
        {
            if (!string.Equals(owner, _localAuthorId, StringComparison.Ordinal))
                _hlcClock.Observe(hlc, _clock);
            RebuildWorld();
            NotifyExecutionPlanOfWorldRevision();
        }
        return result;
    }

    private void NotifyExecutionPlanOfWorldRevision()
    {
        if (ExecutionPlan is null)
            return;
        var worldRevision = _world.Revision.Fingerprint.Length == 0
            ? new FcWorldRevision(_world.Revision.Number, "synthetic-empty-world")
            : _world.Revision;
        var planning = CraftingPlanningContext.CreateFc(
            new FcRepresentedInventorySource(BuildRepresentedInventory(_world)),
            new FcLocalConsumableInventorySource(LocalWorker.HeldInventoryMap),
            new FcExecutionContext(
                _sessionId,
                _selectedListIds,
                _worldFingerprint,
                worldRevision));
        _ = ExecutionPlan.MarkWorldRevisionChanged(planning);
    }

    private FcRecordHeader NextHeader(FcRecordHeader current, string recordType)
        => current with
        {
            ProtocolVersion = FcProtocolVersion.Current,
            SchemaVersion = FcProtocolVersion.CurrentSchema,
            RecordType = recordType,
            RecordId = DeterministicGuid($"{_localAuthorId}/{recordType}/{current.Revision + 1}"),
            OwnerAuthorId = _localAuthorId,
            Revision = checked(current.Revision + 1),
        };

    private static FcRecordHeader Header(string type, Guid recordId, string owner, ulong revision)
        => new(FcProtocolVersion.Current, FcProtocolVersion.CurrentSchema, type, recordId, owner, revision);

    private static ulong? GenerationOf<T>(T record)
        => record switch
        {
            WorkerSessionRecord worker => worker.SessionGeneration,
            FcInventoryTransferRecord transfer => transfer.SessionGeneration,
            _ => null,
        };

    private static FcRecordHeader HeaderOf<T>(T record)
        => record switch
        {
            PublishedListRecord list => list.Header,
            WorkerSessionRecord worker => worker.Header,
            ChestSnapshotRecord chest => chest.Header,
            CapabilityRequestRecord request => request.Header,
            CapabilityResponseRecord response => response.Header,
            FcInventoryTransferRecord transfer => transfer.Header,
            _ => throw new ArgumentException("Unsupported synthetic FC record.", nameof(record)),
        } ?? throw new ArgumentException("Synthetic FC record header is null.", nameof(record));

    private FcSyntheticAction Record(
        FcSyntheticActionKind kind,
        uint itemId,
        FcItemQuality quality,
        int quantity,
        Guid? listId,
        string message)
    {
        var action = new FcSyntheticAction(
            ++_actionSequence,
            kind,
            itemId,
            quality,
            quantity,
            listId,
            message);
        _lastAction = action;
        _actionLog.Add(action);
        return action;
    }

    private static IReadOnlyList<Guid> NormalizeListIds(IEnumerable<Guid> listIds)
    {
        ArgumentNullException.ThrowIfNull(listIds);
        var normalized = listIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        if (normalized.Length == 0)
            throw new ArgumentException("Synthetic fulfillment requires at least one selected list.", nameof(listIds));
        return normalized;
    }

    private static string ValidateIdentity(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || value.Contains('/')
            || value.Contains('\\'))
            throw new ArgumentException("Synthetic identity contains an invalid path character.", parameterName);
        return value;
    }

    private static string ValidateFingerprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Synthetic world fingerprint is invalid.", nameof(value));
        return value;
    }

    private static Guid DeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }
}
