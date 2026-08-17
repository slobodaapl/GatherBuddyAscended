using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public sealed class CraftingExecutionPlan
{
    private readonly CraftingListDefinition _planningSnapshot;
    private readonly bool _useRetainerCraftableAvailability;
    private readonly bool _directCraft;
    private readonly List<CraftingListItem>? _recoveryQueue;
    private readonly Dictionary<uint, int> _acquiredDependencyCaps;
    private readonly HashSet<uint> _finalOutputItemIds;
    private readonly Dictionary<uint, AcquiredDependencyAvailability> _acquiredAvailability = new();

    public int ListId { get; }
    public string ListName { get; }
    public int Version { get; private set; }
    public bool SkipIfEnough { get; }
    public bool SkipFinalIfEnough { get; }
    public bool RetainerRestock { get; }
    public bool AutoPurchaseBlockedDependencies { get; }
    public bool PreferMarketForSpecialCurrency { get; }
    public bool PreferHQ { get; }
    public bool PreferVendors { get; }
    public bool CurrentWorldOnly { get; }
    public long? MaximumGilSpend { get; }
    public bool ReturnToHomeWorldBeforeCrafting { get; }
    public bool AllowMaterialAcquisition => !_directCraft;
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

    private CraftingExecutionPlan(
        CraftingListDefinition planningSnapshot,
        bool useRetainerCraftableAvailability,
        CraftingListPlan resolvedPlan,
        bool directCraft = false,
        IReadOnlyList<CraftingListItem>? recoveryQueue = null)
    {
        _planningSnapshot = planningSnapshot;
        _useRetainerCraftableAvailability = useRetainerCraftableAvailability;
        _directCraft = directCraft;
        _recoveryQueue = recoveryQueue?.Select(CloneRecoveryQueueItem).ToList();
        _acquiredDependencyCaps = new Dictionary<uint, int>(resolvedPlan.Precrafts);
        _finalOutputItemIds = resolvedPlan.OriginalRecipes
            .Select(item => RecipeManager.GetRecipe(item.RecipeId)?.ItemResult.RowId ?? 0u)
            .Where(itemId => itemId != 0)
            .ToHashSet();
        ListId = planningSnapshot.ID;
        ListName = planningSnapshot.Name;
        SkipIfEnough = planningSnapshot.SkipIfEnough;
        SkipFinalIfEnough = planningSnapshot.SkipFinalIfEnough;
        RetainerRestock = planningSnapshot.RetainerRestock;
        AutoPurchaseBlockedDependencies = planningSnapshot.AutoPurchaseBlockedDependencies;
        PreferMarketForSpecialCurrency = planningSnapshot.PreferMarketForSpecialCurrency;
        PreferHQ = planningSnapshot.PreferHQ;
        PreferVendors = planningSnapshot.PreferVendors;
        CurrentWorldOnly = planningSnapshot.CurrentWorldOnly;
        MaximumGilSpend = planningSnapshot.MaximumGilSpend;
        ReturnToHomeWorldBeforeCrafting = planningSnapshot.ReturnToHomeWorldBeforeCrafting;
        ApplyResolvedPlan(resolvedPlan);
    }

    public static CraftingExecutionPlan Create(CraftingListDefinition list)
    {
        var planningSnapshot = list.CreateRetainerPlanningSnapshot();
        var useRetainerCraftableAvailability = planningSnapshot.SkipIfEnough
            && planningSnapshot.RetainerRestock
            && AllaganTools.Enabled;
        var resolvedPlan = planningSnapshot.CreatePlan(useRetainerCraftableAvailability);
        return new CraftingExecutionPlan(planningSnapshot, useRetainerCraftableAvailability, resolvedPlan);
    }

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
        var resolvedPlan = CraftingListPlanner.BuildDirect(planningSnapshot);
        var plan = new CraftingExecutionPlan(
            planningSnapshot,
            useRetainerCraftableAvailability: false,
            resolvedPlan,
            directCraft: true,
            recoveryQueue: remainingQueue);
        return plan;
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

        ApplyResolvedPlan(_planningSnapshot.CreatePlan(true, _acquiredAvailability));
    }

    public void RefreshFromCurrentInventory()
        => ApplyResolvedPlan(_directCraft
            ? CraftingListPlanner.BuildDirect(_planningSnapshot)
            : _planningSnapshot.CreatePlan(false, _acquiredAvailability));

    internal CraftingListPlan CreateAcquisitionBoundaryPlan(Func<Recipe, bool> canCraftPrecraft)
    {
        ArgumentNullException.ThrowIfNull(canCraftPrecraft);
        return CraftingListPlanner.Build(
            _planningSnapshot,
            new CraftingListPlannerOptions(
                UseRetainerCraftableAvailability: _useRetainerCraftableAvailability,
                AcquiredAvailability: _acquiredAvailability,
                CanCraftPrecraft: canCraftPrecraft));
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
    }
}
