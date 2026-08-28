using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Crafting.Acquisition;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.State;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public sealed class CraftingExecutionPlan
{
    private readonly CraftingListDefinition _planningSnapshot;
    private readonly bool _useRetainerCraftableAvailability;
    private readonly bool _directCraft;
    private readonly List<CraftingListItem>? _recoveryQueue;
    private readonly Func<CraftingListDefinition, CraftingListPlan>? _refreshPlanFactory;
    private readonly Dictionary<uint, int> _acquiredDependencyCaps;
    private readonly HashSet<uint> _finalOutputItemIds;
    private readonly Dictionary<uint, AcquiredDependencyAvailability> _acquiredAvailability = new();
    private CraftingPlanningContext _planningContext;
    private CraftingPlanningContext? _pendingWorldRevisionContext;
    private WorkerSessionRecord[]? _fcIntentWorkers;
    private WorkerSessionRecord? _fcIntentLocalWorker;
    private FcIntentTieBreaker.ICraftQueueFactsResolver? _fcIntentFactsResolver;
    private IFcIntentSnapshotProvider? _fcIntentSnapshotProvider;
    private uint[]? _fcIntentGatherCandidates;

    public int ListId { get; }
    public string ListName { get; }
    public int Version { get; private set; }
    public bool SkipIfEnough { get; }
    public bool SkipFinalIfEnough { get; }
    public bool RetainerRestock { get; }
    public bool AutoPurchaseBlockedDependencies { get; }
    public bool CurrentWorldOnly { get; }
    public long? MaximumGilSpend { get; }
    public bool ReturnToHomeWorldBeforeCrafting { get; }
    public bool AllowMaterialAcquisition => !_directCraft;
    public CraftingPlanningContext PlanningContext => _planningContext;
    public ExecutionSource ExecutionSource => _planningContext.Source;
    public FcExecutionContext? FcContext => _planningContext.FcContext;
    internal bool IsRecoveryPlan => _recoveryQueue is not null;
    public FcWorldRevision? WorldRevision => _planningContext.FcContext?.WorldRevision;
    public FcWorldRevision? CurrentWorldRevision => WorldRevision;
    public bool IsWorldRevisionDirty => _pendingWorldRevisionContext != null;
    public CraftingPlanningContext? PendingWorldRevisionContext => _pendingWorldRevisionContext;
    public bool UsesMissionProvidedMaterials => _directCraft
        && OriginalRecipes.Count > 0
        && OriginalRecipes.All(item => RecipeManager.GetRecipe(item.RecipeId) is { Number: 0 });
    public CraftingListPlan ResolvedPlan { get; private set; } = null!;

    internal List<CraftingListItem> Queue { get; private set; } = [];
    internal List<CraftingListItem> OriginalRecipes { get; private set; } = [];
    internal Dictionary<uint, int> Materials { get; private set; } = [];
    internal Dictionary<uint, int> Precrafts { get; private set; } = [];
    internal Dictionary<uint, int> RetainerConsumedCraftables { get; private set; } = [];
    internal Dictionary<uint, IngredientQualityDemand> IngredientDemands { get; private set; } = [];
    internal CraftingListDefinition PlanningSnapshot => _planningSnapshot;

    public IReadOnlyList<CraftingListItem> QueueView => Queue;
    public IReadOnlyList<CraftingListItem> OriginalRecipesView => OriginalRecipes;
    public IReadOnlyDictionary<uint, int> MaterialsView => Materials;
    public IReadOnlyDictionary<uint, int> PrecraftsView => Precrafts;
    public IReadOnlyDictionary<uint, int> RetainerConsumedCraftablesView => RetainerConsumedCraftables;
    public IReadOnlyDictionary<uint, IngredientQualityDemand> IngredientDemandsView => IngredientDemands;
    public IReadOnlyDictionary<uint, AcquiredDependencyAvailability> AcquiredAvailabilityView => _acquiredAvailability;
    internal AcquisitionPlanningResult? LatestAcquisitionPlanning { get; private set; }
    internal Action<AcquisitionPlanningResult>? AcquisitionPlanningPublished { get; set; }

    internal void PublishAcquisitionPlanning(AcquisitionPlanningResult planning)
    {
        LatestAcquisitionPlanning = planning;
        AcquisitionPlanningPublished?.Invoke(planning);
    }

    /// <summary>
    /// Applies the FC-only advisory intent order to the already-resolved
    /// queue.  The existing planner remains the source of queue membership,
    /// dependency order, material accounting, and quality admission; an
    /// unknown recipe shape leaves its incumbent queue untouched.
    /// </summary>
    internal void ApplyFcIntentQueueOrder(
        IReadOnlyList<WorkerSessionRecord> activeWorkers,
        WorkerSessionRecord localWorker,
        FcIntentTieBreaker.ICraftQueueFactsResolver? factsResolver = null)
    {
        if (ExecutionSource != ExecutionSource.FcFulfillment
            || Queue.Count < 2)
            return;

        ArgumentNullException.ThrowIfNull(activeWorkers);
        ArgumentNullException.ThrowIfNull(localWorker);
        _fcIntentWorkers = activeWorkers.ToArray();
        _fcIntentLocalWorker = localWorker;
        _fcIntentFactsResolver = factsResolver
            ?? FcIntentTieBreaker.ProductionCraftQueueFactsResolver;
        ReapplyFcIntentQueueOrder();
    }

    internal void BindFcIntentSnapshotProvider(IFcIntentSnapshotProvider? provider)
    {
        if (ExecutionSource == ExecutionSource.FcFulfillment)
            _fcIntentSnapshotProvider = provider;
    }

    internal void ClearFcIntentSnapshotProvider()
        => _fcIntentSnapshotProvider = null;

    internal void SetFcIntentGatherCandidates(IReadOnlyList<uint> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (ExecutionSource != ExecutionSource.FcFulfillment)
            return;
        _fcIntentGatherCandidates = candidates
            .Where(itemId => itemId != 0)
            .Distinct()
            .ToArray();
    }

    internal uint[] GetCurrentFcGatherTargetOrder()
    {
        var candidates = _fcIntentGatherCandidates;
        if (ExecutionSource != ExecutionSource.FcFulfillment
            || candidates is not { Length: > 0 })
            return Array.Empty<uint>();

        var provider = _fcIntentSnapshotProvider;
        if (provider is null
            || !provider.TryGetCurrent(out var snapshot))
            return Array.Empty<uint>();

        var preferred = FcIntentTieBreaker.PreferGatherOrder(
            candidates,
            snapshot.ActiveWorkers,
            snapshot.LocalWorker);
        return preferred;
    }

    internal bool RefreshFcIntentOrderAtSafeBoundary(int immutablePrefixCount)
    {
        if (ExecutionSource != ExecutionSource.FcFulfillment
            || _fcIntentSnapshotProvider is null
            || _fcIntentFactsResolver is null
            || immutablePrefixCount < 0
            || immutablePrefixCount >= Queue.Count)
            return false;

        FcIntentSnapshot snapshot;
        try
        {
            if (!_fcIntentSnapshotProvider.TryGetCurrent(out snapshot))
                return false;
        }
        catch (Exception)
        {
            return false;
        }
        if (snapshot.ActiveWorkers is null || snapshot.LocalWorker is null)
            return false;

        var suffix = Queue.Skip(immutablePrefixCount).ToArray();
        if (suffix.Length < 2)
            return false;

        var candidates = FcIntentTieBreaker.BuildCraftQueueCandidates(
            suffix,
            _fcIntentFactsResolver);
        if (candidates.Length != suffix.Length)
            return false;

        var reordered = FcIntentTieBreaker.PreferCraftQueue(
            candidates,
            snapshot.ActiveWorkers,
            snapshot.LocalWorker);
        if (reordered.Length != suffix.Length)
            return false;

        _fcIntentWorkers = snapshot.ActiveWorkers.ToArray();
        _fcIntentLocalWorker = snapshot.LocalWorker;
        Queue = Queue.Take(immutablePrefixCount)
            .Concat(reordered.Select(candidate => candidate.Item))
            .ToList();
        return true;
    }

    private void ReapplyFcIntentQueueOrder()
    {
        var factsResolver = _fcIntentFactsResolver;
        if (ExecutionSource != ExecutionSource.FcFulfillment
            || Queue.Count < 2
            || _fcIntentWorkers is null
            || _fcIntentLocalWorker is null
            || factsResolver is null)
            return;

        var candidates = FcIntentTieBreaker.BuildCraftQueueCandidates(
            Queue,
            factsResolver);
        if (candidates.Length != Queue.Count)
            return;

        var reordered = FcIntentTieBreaker.PreferCraftQueue(
            candidates,
            _fcIntentWorkers,
            _fcIntentLocalWorker);
        if (reordered.Length != Queue.Count)
            return;

        Queue = reordered.Select(candidate => candidate.Item).ToList();
    }

    private CraftingExecutionPlan(
        CraftingListDefinition planningSnapshot,
        bool useRetainerCraftableAvailability,
        CraftingListPlan resolvedPlan,
        bool directCraft = false,
        IReadOnlyList<CraftingListItem>? recoveryQueue = null,
        CraftingPlanningContext? planningContext = null,
        Func<CraftingListDefinition, CraftingListPlan>? refreshPlanFactory = null)
    {
        _planningSnapshot = planningSnapshot;
        _planningContext = planningContext ?? CraftingPlanningContext.CreatePrivate();
        _useRetainerCraftableAvailability = useRetainerCraftableAvailability;
        _directCraft = directCraft;
        _recoveryQueue = recoveryQueue?.Select(CloneRecoveryQueueItem).ToList();
        _refreshPlanFactory = refreshPlanFactory;
        _acquiredDependencyCaps = new Dictionary<uint, int>(resolvedPlan.Precrafts);
        _finalOutputItemIds = recoveryQueue is not null
            ? []
            : resolvedPlan.OriginalRecipes
                .Select(item => RecipeManager.GetRecipe(item.RecipeId)?.ItemResult.RowId ?? 0u)
                .Where(itemId => itemId != 0)
                .ToHashSet();
        ListId = planningSnapshot.ID;
        ListName = planningSnapshot.Name;
        SkipIfEnough = planningSnapshot.SkipIfEnough;
        SkipFinalIfEnough = planningSnapshot.SkipFinalIfEnough;
        RetainerRestock = planningSnapshot.RetainerRestock;
        AutoPurchaseBlockedDependencies = planningSnapshot.AutoPurchaseBlockedDependencies;
        CurrentWorldOnly = planningSnapshot.CurrentWorldOnly;
        MaximumGilSpend = planningSnapshot.MaximumGilSpend;
        ReturnToHomeWorldBeforeCrafting = planningSnapshot.ReturnToHomeWorldBeforeCrafting;
        ApplyResolvedPlan(resolvedPlan);
    }

    public static CraftingExecutionPlan Create(CraftingListDefinition list)
        => Create(list, CraftingPlanningContext.CreatePrivate());

    public static CraftingExecutionPlan Create(
        CraftingListDefinition list,
        CraftingPlanningContext planningContext)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(planningContext);
        var planningSnapshot = list.CreateRetainerPlanningSnapshot();
        var useRetainerCraftableAvailability = planningSnapshot.SkipIfEnough
            && planningSnapshot.RetainerRestock
            && AllaganTools.Enabled;
        var resolvedPlan = planningSnapshot.CreatePlan(
            useRetainerCraftableAvailability,
            planningContext: planningContext);
        return new CraftingExecutionPlan(
            planningSnapshot,
            useRetainerCraftableAvailability,
            resolvedPlan,
            planningContext: planningContext);
    }

    /// <summary>
    /// Creates an FC execution plan through the same planner used by private
    /// lists. The explicit name keeps synthetic/native callers from
    /// accidentally falling back to the private physical-inventory context.
    /// </summary>
    public static CraftingExecutionPlan CreateFc(
        CraftingListDefinition list,
        CraftingPlanningContext planningContext)
    {
        ArgumentNullException.ThrowIfNull(planningContext);
        if (planningContext.Source != ExecutionSource.FcFulfillment)
            throw new ArgumentException("FC execution requires an FC planning context.", nameof(planningContext));
        return Create(list, planningContext);
    }

    public static CraftingExecutionPlan CreateFc(
        CraftingListDefinition list,
        IItemQuantitySource representedInventory,
        IItemQuantitySource localConsumableInventory,
        FcExecutionContext fcContext)
        => CreateFc(
            list,
            CraftingPlanningContext.CreateFc(
                representedInventory,
                localConsumableInventory,
                fcContext));

    public static CraftingExecutionPlan CreateDirect(CraftingListDefinition list)
    {
        var planningSnapshot = list.CreateRetainerPlanningSnapshot();
        return new CraftingExecutionPlan(
            planningSnapshot,
            useRetainerCraftableAvailability: false,
            CraftingListPlanner.BuildDirect(planningSnapshot),
            directCraft: true);
    }

    internal static CraftingExecutionPlan CreateRecovery(IReadOnlyList<CraftingListItem> remainingQueue)
        => CreateRecovery(remainingQueue, null, null);

    internal static CraftingExecutionPlan CreateRecovery(
        IReadOnlyList<CraftingListItem> remainingQueue,
        FcExecutionContext? fcContext,
        Func<CraftingListDefinition, CraftingListPlan>? recoveryPlanFactory = null)
    {
        ArgumentNullException.ThrowIfNull(remainingQueue);
        if (remainingQueue.Count == 0)
            throw new ArgumentException("Recovery queue cannot be empty.", nameof(remainingQueue));

        var list = new CraftingListDefinition
        {
            ID = int.MinValue,
            Name = "Recovered crafting automation",
            SkipIfEnough = false,
            SkipFinalIfEnough = false,
            RetainerRestock = false,
            AutoPurchaseBlockedDependencies = false,
            ReturnToHomeWorldBeforeCrafting = false,
            // The first queue entry is the already-open craft. Its ingredients
            // were consumed before the reload and must not fail material preflight.
            Recipes = remainingQueue.Skip(1).Select(CloneRecoveryQueueItem).ToList(),
        };
        var planningSnapshot = list.CreateRetainerPlanningSnapshot();
        var resolvedPlan = (recoveryPlanFactory ?? CraftingListPlanner.BuildDirect)(planningSnapshot);
        if (!HasCompleteRecoveryPlan(resolvedPlan, remainingQueue))
            throw new InvalidOperationException("Recovery plan does not resolve every queued recipe after the active craft.");
        var planningContext = fcContext is null
            ? CraftingPlanningContext.CreatePrivate()
            : CraftingPlanningContext.CreateFc(
                new FcRepresentedInventorySource(FcItemQuantityMap.Empty),
                new CraftingPhysicalInventorySource(),
                fcContext);
        var plan = new CraftingExecutionPlan(
            planningSnapshot,
            useRetainerCraftableAvailability: false,
            resolvedPlan,
            directCraft: true,
            recoveryQueue: remainingQueue,
            planningContext: planningContext,
            refreshPlanFactory: recoveryPlanFactory);
        return plan;
    }

    private static bool HasCompleteRecoveryPlan(
        CraftingListPlan resolvedPlan,
        IReadOnlyList<CraftingListItem> remainingQueue)
    {
        if (resolvedPlan is null)
            return false;
        var expected = remainingQueue
            .Skip(1)
            .Where(item => !item.Options.Skipping && item.Quantity > 0)
            .GroupBy(item => item.RecipeId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
        var actual = resolvedPlan.Recipes
            .Where(item => !item.Options.Skipping && item.Quantity > 0)
            .GroupBy(item => item.RecipeId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
        return expected.Count == actual.Count
            && expected.All(pair => actual.TryGetValue(pair.Key, out var quantity) && quantity == pair.Value);
    }

    private static CraftingListItem CloneRecoveryQueueItem(CraftingListItem item)
        => new(item.RecipeId, 1)
        {
            Options = new ListItemOptions
            {
                Skipping = item.Options.Skipping,
                NQOnly = item.Options.NQOnly,
            },
            IngredientPreferences = new Dictionary<uint, int>(item.IngredientPreferences),
            ConsumableOverrides = item.ConsumableOverrides.Clone(),
            IsOriginalRecipe = item.IsOriginalRecipe,
            CraftSettings = item.CraftSettings?.Clone(),
        };

    public bool MatchesList(int listId)
        => ListId == listId;

    public void RefreshForRetainerWithdrawal()
    {
        if (!_useRetainerCraftableAvailability)
            return;

        var planningContext = GetRefreshPlanningContext();
        var resolvedPlan = _planningSnapshot.CreatePlan(
            true,
            _acquiredAvailability,
            planningContext);
        _planningContext = planningContext;
        ApplyResolvedPlan(resolvedPlan);
    }

    public void RefreshFromCurrentInventory()
    {
        if (_directCraft)
        {
            var freshPlanningContext = GetRefreshPlanningContext();
            var refreshedPlan = _refreshPlanFactory is { } factory
                ? factory(_planningSnapshot)
                : CraftingListPlanner.BuildDirect(_planningSnapshot);
            if (refreshedPlan is null)
                return;
            ApplyResolvedPlan(refreshedPlan);
            _planningContext = freshPlanningContext;
            return;
        }

        var planningContext = GetRefreshPlanningContext();
        var resolvedPlan = _planningSnapshot.CreatePlan(
                false,
                _acquiredAvailability,
                planningContext);
        _planningContext = planningContext;
        ApplyResolvedPlan(resolvedPlan);
    }

    internal CraftingListPlan CreateAcquisitionBoundaryPlan(Func<Recipe, bool> canCraftPrecraft)
    {
        ArgumentNullException.ThrowIfNull(canCraftPrecraft);
        return CraftingListPlanner.Build(
            _planningSnapshot,
            new CraftingListPlannerOptions(
                UseRetainerCraftableAvailability: _useRetainerCraftableAvailability,
                AcquiredAvailability: _acquiredAvailability,
                CanCraftPrecraft: canCraftPrecraft,
                PlanningContext: GetRefreshPlanningContext()));
    }

    /// <summary>
    /// Records a newer relevant FC world revision for application at a safe
    /// framework boundary. It never rebuilds an active craft or gather action.
    /// </summary>
    public bool MarkWorldRevisionChanged(CraftingPlanningContext updatedContext)
    {
        ArgumentNullException.ThrowIfNull(updatedContext);
        var baseline = _pendingWorldRevisionContext ?? _planningContext;
        if (!IsRelevantNewerContext(updatedContext, baseline))
            return false;

        _pendingWorldRevisionContext = updatedContext;
        return true;
    }

    public bool MarkWorldRevisionChanged(FcExecutionContext updatedContext)
    {
        ArgumentNullException.ThrowIfNull(updatedContext);
        var source = _pendingWorldRevisionContext ?? _planningContext;
        return MarkWorldRevisionChanged(new CraftingPlanningContext(
            source.RepresentedInventory,
            source.LocalConsumableInventory,
            ExecutionSource.FcFulfillment,
            updatedContext));
    }

    public bool ApplyPendingWorldRevisionAtSafeBoundary(
        bool isCrafting,
        bool isGatheringInteraction)
    {
        if (isCrafting || isGatheringInteraction || _pendingWorldRevisionContext is not { } pending)
            return false;

        var resolvedPlan = _directCraft
            ? CraftingListPlanner.BuildDirect(_planningSnapshot)
            : _planningSnapshot.CreatePlan(
                _useRetainerCraftableAvailability,
                _acquiredAvailability,
                pending);
        ApplyResolvedPlan(resolvedPlan);
        _planningContext = pending;
        _pendingWorldRevisionContext = null;
        return true;
    }

    public bool ApplyPendingWorldRevisionAtSafeBoundary(
        CraftingPlanningContext updatedContext,
        bool isCrafting,
        bool isGatheringInteraction)
    {
        ArgumentNullException.ThrowIfNull(updatedContext);
        MarkWorldRevisionChanged(updatedContext);
        return ApplyPendingWorldRevisionAtSafeBoundary(isCrafting, isGatheringInteraction);
    }

    public bool ConsumeWorldRevisionReplanAtSafeBoundary(
        CraftingPlanningContext updatedContext,
        bool isCrafting,
        bool isGatheringInteraction)
        => ApplyPendingWorldRevisionAtSafeBoundary(updatedContext, isCrafting, isGatheringInteraction);

    public bool ConsumeWorldRevisionReplanAtSafeBoundary(
        bool isCrafting,
        bool isGatheringInteraction)
        => ApplyPendingWorldRevisionAtSafeBoundary(isCrafting, isGatheringInteraction);

    private bool IsRelevantNewerContext(
        CraftingPlanningContext updatedContext,
        CraftingPlanningContext baseline)
        => baseline.Source == ExecutionSource.FcFulfillment
        && updatedContext.Source == ExecutionSource.FcFulfillment
        && baseline.FcContext is { } current
        && updatedContext.FcContext is { } updated
        && current.MatchesScope(updated)
        && updated.WorldRevision.Number > current.WorldRevision.Number;

    private CraftingPlanningContext GetRefreshPlanningContext()
    {
        if (_planningContext.Source != ExecutionSource.PrivateList)
            return _planningContext;

        var physical = new CraftingPhysicalInventorySource();
        var local = _planningContext.LocalConsumableInventory is CraftingPhysicalInventorySource
            ? physical
            : _planningContext.LocalConsumableInventory;
        return new CraftingPlanningContext(
            physical,
            local,
            ExecutionSource.PrivateList,
            null);
    }

    /// <summary>
    /// Registers verified quantities purchased for blocked precraft
    /// dependencies. The overlay is intentionally narrower than inventory
    /// availability: direct final outputs and unrelated precrafts cannot be
    /// suppressed, while an item ID shared with intermediate demand remains
    /// eligible and is capped at that original precraft demand.
    /// </summary>
    public void RegisterAcquiredAvailability(
        IReadOnlyDictionary<uint, AcquiredDependencyAvailability> acquired)
    {
        ArgumentNullException.ThrowIfNull(acquired);
        foreach (var (itemId, availability) in FilterAcquiredAvailability(
                     _acquiredDependencyCaps,
                     _finalOutputItemIds,
                     acquired))
        {
            var existing = _acquiredAvailability.GetValueOrDefault(itemId);
            var cap = _acquiredDependencyCaps[itemId];
            var total = Math.Min(cap, checked(existing.Total + availability.Total));
            var hq = Math.Min(total, checked(existing.HQ + availability.HQ));
            var nq = Math.Min(total - hq, checked(existing.NQ + availability.NQ));
            _acquiredAvailability[itemId] = new AcquiredDependencyAvailability(nq, hq);
        }
    }

    internal static Dictionary<uint, AcquiredDependencyAvailability> FilterAcquiredAvailability(
        IReadOnlyDictionary<uint, int> precraftCaps,
        IReadOnlyCollection<uint> finalOutputItemIds,
        IReadOnlyDictionary<uint, AcquiredDependencyAvailability> acquired)
    {
        ArgumentNullException.ThrowIfNull(precraftCaps);
        ArgumentNullException.ThrowIfNull(finalOutputItemIds);
        ArgumentNullException.ThrowIfNull(acquired);

        var result = new Dictionary<uint, AcquiredDependencyAvailability>();
        foreach (var (itemId, availability) in acquired)
        {
            if (itemId == 0
                || (finalOutputItemIds.Contains(itemId) && !precraftCaps.ContainsKey(itemId))
                || !precraftCaps.TryGetValue(itemId, out var cap)
                || cap <= 0)
                continue;

            var normalized = availability.Normalize();
            var total = Math.Min(cap, normalized.Total);
            if (total <= 0)
                continue;

            var hq = Math.Min(total, normalized.HQ);
            var nq = Math.Min(total - hq, normalized.NQ);
            result[itemId] = new AcquiredDependencyAvailability(nq, hq);
        }

        return result;
    }

    public Dictionary<uint, IngredientQualityDemand> BuildQualityTargetsForItems(IReadOnlyDictionary<uint, int> requestedItems)
    {
        var targets = requestedItems.Keys.ToDictionary(
            itemId => itemId,
            itemId => IngredientDemands.GetValueOrDefault(itemId));

        foreach (var (itemId, totalNeeded) in requestedItems)
        {
            var target = targets[itemId];
            if (target.Total < totalNeeded)
            {
                targets[itemId] = target.Add(IngredientQualityDemand.FromPreferHQ(totalNeeded - target.Total));
                continue;
            }

            if (target.Total > totalNeeded)
                targets[itemId] = target.ConsumeUnknownQuality(target.Total - totalNeeded, out _);
        }

        return targets;
    }

    private void ApplyResolvedPlan(CraftingListPlan resolvedPlan)
    {
        Version++;
        ResolvedPlan = resolvedPlan;
        Materials = new Dictionary<uint, int>(resolvedPlan.Materials);
        Precrafts = new Dictionary<uint, int>(resolvedPlan.Precrafts);
        RetainerConsumedCraftables = new Dictionary<uint, int>(resolvedPlan.RetainerConsumedCraftables);
        IngredientDemands = new Dictionary<uint, IngredientQualityDemand>(resolvedPlan.IngredientDemands);
        if (_recoveryQueue != null)
        {
            Queue = _recoveryQueue.Select(CloneRecoveryQueueItem).ToList();
            OriginalRecipes = Queue.Select(CloneRecoveryQueueItem).ToList();
        }
        else
        {
            OriginalRecipes = resolvedPlan.OriginalRecipes
                .Select(item => new CraftingListItem(item.RecipeId, item.Quantity)
                {
                    IsOriginalRecipe = true,
                })
                .ToList();
            Queue = CraftingListQueueBuilder.CreateExpandedQueue(_planningSnapshot, resolvedPlan);
        }
        if (_fcIntentSnapshotProvider is null)
            ReapplyFcIntentQueueOrder();
    }
}
