using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Lumina.Excel.Sheets;
using ElliLib;
using ElliLib.Widgets;
using ImRaii = ElliLib.Raii.ImRaii;
using GatherBuddy.Crafting;
using GatherBuddy.Crafting.Acquisition;
using GatherBuddy.Plugin;
using GatherBuddy.Marketboard;
using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Vendors;
using SearchTextNormalizer = GatherBuddy.Utility.SearchTextNormalizer;
using FuzzySearch = GatherBuddy.Utility.FuzzySearch;

namespace GatherBuddy.Gui;

public class CraftingListEditor
{
    private sealed class QueueCacheSnapshot
    {
        public string Hash { get; init; } = string.Empty;
        public List<CraftingListItem> SortedQueue { get; init; } = [];
    }

    private sealed class MaterialCacheSnapshot
    {
        public string Hash { get; init; } = string.Empty;
        public Dictionary<uint, int> Materials { get; init; } = [];
        public Dictionary<uint, int> PrecraftMaterials { get; init; } = [];
        public Dictionary<uint, IngredientQualityDemand> IngredientDemands { get; init; } = [];
        public Dictionary<uint, IngredientQualityDemand> CraftMaterialDemands { get; init; } = [];
        public Dictionary<uint, int> DisplayMaterials { get; init; } = [];
        public Dictionary<uint, int> DisplayPrecraftMaterials { get; init; } = [];
        public Dictionary<uint, IngredientQualityDemand> DisplayIngredientDemands { get; init; } = [];
        public Dictionary<uint, IngredientQualityDemand> DisplayCraftMaterialDemands { get; init; } = [];
        public CraftingMaterialFinalRoots DisplayCraftMaterialFinalRoots { get; init; } = new([]);
    }
    private CraftingListDefinition _list;
    private int _searchQuantity = 1;
    private Recipe? _selectedRecipe = null;
    private Dictionary<uint, string> _recipeLabels = new();
    private Dictionary<uint, string> _recipeSearchLabels = new();
    private string _cachedFuzzyFilter = string.Empty;
    private List<Recipe> _cachedFuzzyRecipes = new();
    private ClippedSelectableCombo<Recipe>? _recipeCombo = null;
    private List<Recipe> _allRecipes = new();
    private List<Recipe> _keywordFilteredRecipes = new();
    private string _lastComboFilter = string.Empty;
    
    private QueueCacheSnapshot? _queueCache = null;
    private int _queueGenerationVersion = 0;
    private int _selectedQueueIndex = -1;
    private bool _showPrecrafts = true;
    
    private MaterialCacheSnapshot? _materialCache = null;
    
    private Task? _queueGenerationTask = null;
    private CancellationTokenSource? _queueCancellationSource = null;
    private volatile bool _isGeneratingQueue = false;
    
    private Task? _materialsGenerationTask = null;
    private CancellationTokenSource? _materialsCancellationSource = null;
    private volatile bool _isGeneratingMaterials = false;
    private int _materialGenerationVersion = 0;
    
    private Dictionary<uint, (int NQ, int HQ)> _cachedInventorySplitCounts = new();
    private Dictionary<uint, DateTime> _inventoryRefreshTimes = new();
    private RetainerItemSnapshot _cachedRetainerSnapshot = RetainerItemSnapshot.Empty;
    private uint[] _cachedRetainerSnapshotItemIds = [];
    private DateTime _cachedRetainerSnapshotAt = DateTime.MinValue;
    private readonly HashSet<uint> _watchedInventoryItemIds = new();
    private readonly HashSet<uint> _watchedOriginalResultItemIds = new();
    private readonly HashSet<uint> _watchedPrecraftResultItemIds = new();
    private readonly object _inventoryChangeLock = new();
    private DateTime _lastGraphAffectingInventoryChange = DateTime.MinValue;
    private string _watchedInventoryHash = string.Empty;
    private bool _pendingQueueRefreshFromInventory;
    private bool _pendingMaterialsRefreshFromInventory;
    private AcquisitionPlanningResult? _acquisitionPlanningResult;
    private MarketplaceBuyListDefinition? _managedMarketplaceProjection;
    private IReadOnlyDictionary<uint, MarketplacePurchaseReason> _marketplacePurchaseReasons
        = new Dictionary<uint, MarketplacePurchaseReason>();
    private DateTime _lastAcquisitionRefresh = DateTime.MinValue;
    private bool _acquisitionEstimateDirty = true;
    private bool _acquisitionEstimateLoading;
    private string _acquisitionStatus = string.Empty;
    private static readonly TimeSpan AcquisitionEstimateTtl = TimeSpan.FromMinutes(15);
    private const double InventoryRefreshIntervalSeconds = 0.5;
    private const double RetainerSnapshotRetryIntervalSeconds = 1.0;
    private const double InventoryChangeDebounceSeconds = 0.2;

    internal readonly record struct MarketplacePurchaseReason(
        string Text,
        long CurrencyAmount = 0,
        uint CurrencyIconId = 0,
        string CurrencyTooltip = "");
    
    private RecipeCraftSettingsPopup _craftSettingsPopup = new();
    private CraftingListConsumablesPopup _consumablesPopup = new();
    
    private readonly HashSet<int> _selectedRecipeIndices = new();
    private int _lastClickedRecipeIndex = -1;
    private int _lastRaphaelActiveSolves = -1;
    private int _lastRaphaelPendingSolves = -1;
    private int _lastRaphaelCachedSolutions = -1;
    private sealed class QueueDisplayRow
    {
        public int QueueIndex { get; init; }
        public int Quantity { get; init; }
        public bool IsOriginalRecipe { get; init; }
        public Recipe Recipe { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public Vector4 BaseTextColor { get; init; }
        public bool EffectiveQuickSynth { get; init; }
        public bool ForceQuickSynth { get; init; }
        public MacroValidationResult? Validation { get; init; }
        public RaphaelAssessment? RaphaelAssessment { get; init; }
    }

    private sealed class RecipeDisplayRow
    {
        public int ListIndex { get; init; }
        public Recipe Recipe { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public Vector4 TextColor { get; init; }
        public MacroValidationResult? Validation { get; init; }
        public RaphaelAssessment? RaphaelAssessment { get; init; }
    }

    private List<QueueDisplayRow>? _cachedQueueDisplayRows = null;
    private List<QueueDisplayRow>? _cachedOriginalQueueDisplayRows = null;
    private string _cachedQueueDisplayRowsHash = string.Empty;
    private bool _cachedQueueDisplayRowsValid = false;
    private List<RecipeDisplayRow>? _cachedRecipeDisplayRows = null;
    private bool _cachedRecipeDisplayRowsValid = false;
    private (int HardFails, int Warnings) _cachedValidationIssueCounts;
    private string _cachedValidationIssueCountsHash = string.Empty;
    private bool _cachedValidationIssueCountsValid = false;

    private string _editingName        = string.Empty;
    private string _editingDescription = string.Empty;
    private bool   _nameConflict       = false;
    private bool   _editingDescActive  = false;
    private bool   _focusDescNext      = false;
    private long _materialCacheVersion;
    
    internal bool HasCachedMaterials    => GetMaterialCache() != null;
    internal bool HasCachedDisplayMaterials => GetMaterialCache() != null;
    internal bool IsGeneratingMaterials => _isGeneratingMaterials;
    internal string ListName            => GetPlanningList().Name;
    internal bool SkipIfEnoughEnabled   => GetPlanningList().SkipIfEnough;
    internal bool RetainerRestockEnabled => GetPlanningList().RetainerRestock;
    internal CraftingListDefinition PlanningList => GetPlanningList();
    internal long MaterialCacheVersion  => Interlocked.Read(ref _materialCacheVersion);
    
    public Action<CraftingListDefinition>? OnStartCrafting { get; set; }

    public CraftingListEditor(CraftingListDefinition list)
    {
        _list               = list;
        _editingName        = list.Name;
        _editingDescription = list.Description;
        _craftSettingsPopup.OnSaved = HandleEditorSettingsSaved;
        _consumablesPopup.OnSaved = HandleEditorSettingsSaved;
        RefreshInventoryCounts();
        Dalamud.GameInventory.InventoryChanged += OnInventoryChanged;
        TriggerQueueRegeneration();
    }

    private CraftingExecutionPlan? GetActiveExecutionPlan()
        => CraftingGatherBridge.GetActiveExecutionPlan(_list.ID);

    private CraftingListDefinition GetPlanningList()
        => GetActiveExecutionPlan()?.PlanningSnapshot ?? _list;

    private CraftingListDefinition CreatePlanningSnapshot()
        => GetPlanningList().CreateRetainerPlanningSnapshot();

    private QueueCacheSnapshot? GetQueueCache()
        => Volatile.Read(ref _queueCache);

    private MaterialCacheSnapshot? GetMaterialCache()
        => Volatile.Read(ref _materialCache);

    private void PublishQueueCache(QueueCacheSnapshot snapshot)
        => Volatile.Write(ref _queueCache, snapshot);

    private bool TryPublishQueueCache(QueueCacheSnapshot snapshot, int generation, CancellationToken token)
    {
        if (token.IsCancellationRequested || generation != Volatile.Read(ref _queueGenerationVersion))
        {
            GatherBuddy.Log.Debug($"[CraftingListEditor] Discarded stale queue cache for list '{_list.Name}'");
            return false;
        }

        PublishQueueCache(snapshot);
        return true;
    }

    private void InvalidateQueueCache()
    {
        Volatile.Write(ref _queueCache, null);
        Interlocked.Increment(ref _queueGenerationVersion);
        _acquisitionEstimateDirty = true;
    }

    private void PublishMaterialCache(MaterialCacheSnapshot snapshot)
        => Volatile.Write(ref _materialCache, snapshot);

    private bool TryPublishMaterialCache(MaterialCacheSnapshot snapshot, int generation, CancellationToken token)
    {
        if (token.IsCancellationRequested || generation != Volatile.Read(ref _materialGenerationVersion))
        {
            GatherBuddy.Log.Debug($"[CraftingListEditor] Discarded stale material cache for list '{_list.Name}'");
            return false;
        }

        PublishMaterialCache(snapshot);
        return true;
    }

    private bool TryCacheActiveExecutionPlan(string hash)
    {
        var activeExecutionPlan = GetActiveExecutionPlan();
        if (activeExecutionPlan == null)
            return false;

        var queueCache = GetQueueCache();
        if (queueCache == null || queueCache.Hash != hash)
            PublishQueueCache(BuildQueueCacheSnapshot(activeExecutionPlan.ResolvedPlan, hash));

        var materialCache = GetMaterialCache();
        if (materialCache == null || materialCache.Hash != hash)
            PublishMaterialCache(BuildMaterialCacheSnapshot(activeExecutionPlan.ResolvedPlan, activeExecutionPlan.PlanningSnapshot, hash));
        return true;
    }
    
    public void Dispose()
    {
        Dalamud.GameInventory.InventoryChanged -= OnInventoryChanged;
        _queueCancellationSource?.Cancel();
        _queueCancellationSource?.Dispose();
        _materialsCancellationSource?.Cancel();
        _materialsCancellationSource?.Dispose();
    }
    
    public void RefreshInventoryCounts()
    {
        _cachedInventorySplitCounts.Clear();
        _inventoryRefreshTimes.Clear();
        InvalidateRetainerSnapshot();
        _acquisitionEstimateDirty = true;
    }

    internal void RefreshFromExternalListChange()
    {
        GatherBuddy.Log.Debug($"[CraftingListEditor] Refreshing cached queue/materials for externally modified list '{_list.Name}'");
        _selectedRecipeIndices.Clear();
        _lastClickedRecipeIndex = -1;
        InvalidateQueueCache();
        InvalidateMaterialCaches();
        InvalidatePresentationCaches();
        TriggerQueueRegeneration();
        TriggerMaterialsRegeneration();
        if (!_editingDescActive)
            _editingDescription = _list.Description;
        _acquisitionPlanningResult = null;
        _managedMarketplaceProjection = null;
        _marketplacePurchaseReasons = new Dictionary<uint, MarketplacePurchaseReason>();
        _acquisitionStatus = string.Empty;
    }

    internal void PublishAcquisitionPlanningResult(AcquisitionPlanningResult result)
        => _acquisitionPlanningResult = result;

    private void HandleEditorSettingsSaved()
    {
        GatherBuddy.Log.Debug($"[CraftingListEditor] Refreshing presentation caches after settings change for list '{_list.Name}'");
        InvalidatePresentationCaches();
        InvalidateQueueCache();
        InvalidateMaterialCaches();
        TriggerQueueRegeneration();
        TriggerMaterialsRegeneration();
    }
    private void InvalidateQueuePresentationCaches()
    {
        _cachedQueueDisplayRows = null;
        _cachedOriginalQueueDisplayRows = null;
        _cachedQueueDisplayRowsHash = string.Empty;
        _cachedQueueDisplayRowsValid = false;
        _cachedValidationIssueCounts = default;
        _cachedValidationIssueCountsHash = string.Empty;
        _cachedValidationIssueCountsValid = false;
    }

    private void InvalidatePresentationCaches()
    {
        InvalidateQueuePresentationCaches();
        _cachedRecipeDisplayRows = null;
        _cachedRecipeDisplayRowsValid = false;
    }
    public void Draw()
    {
        ProcessPendingInventoryChanges();
        RefreshAcquisitionEstimate();
        RefreshRaphaelAssessmentCaches();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var availableHeight = ImGui.GetContentRegionAvail().Y;
        
        var leftPaneWidth = Math.Min(availableWidth * 0.4f, VulcanUiScaling.Scaled(640f));
        var rightPaneWidth = availableWidth - leftPaneWidth - VulcanUiScaling.Scaled(8f);
        
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
        {
            ImGui.BeginChild("LeftPane", new Vector2(leftPaneWidth, availableHeight), true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            DrawQueuePane();
            ImGui.EndChild();
        }

        ImGui.SameLine();

        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
        {
            ImGui.BeginChild("RightPane", new Vector2(rightPaneWidth, availableHeight), true);
            DrawDetailsPane();
            ImGui.EndChild();
        }
        
        _craftSettingsPopup.Draw();
        _consumablesPopup.Draw();
    }

    private void RefreshRaphaelAssessmentCaches()
    {
        var activeSolves = GatherBuddy.RaphaelSolveCoordinator.ActiveSolves;
        var pendingSolves = GatherBuddy.RaphaelSolveCoordinator.PendingSolves;
        var cachedSolutions = GatherBuddy.RaphaelSolveCoordinator.CachedSolutionCount;
        if (activeSolves == _lastRaphaelActiveSolves
         && pendingSolves == _lastRaphaelPendingSolves
         && cachedSolutions == _lastRaphaelCachedSolutions)
            return;

        _lastRaphaelActiveSolves = activeSolves;
        _lastRaphaelPendingSolves = pendingSolves;
        _lastRaphaelCachedSolutions = cachedSolutions;
        InvalidatePresentationCaches();
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        EnsureWatchedInventoryItems();
        var changedItemIds = new HashSet<uint>();
        var planningList = GetPlanningList();

        var graphAffected = false;
        foreach (var inventoryEvent in events)
        {
            foreach (var itemId in GetAffectedTrackedInventoryItemIds(inventoryEvent))
            {
                if (itemId == 0 || !_watchedInventoryItemIds.Contains(itemId))
                    continue;

                changedItemIds.Add(itemId);

                if ((planningList.SkipIfEnough && _watchedPrecraftResultItemIds.Contains(itemId))
                 || (planningList.SkipIfEnough && planningList.SkipFinalIfEnough && _watchedOriginalResultItemIds.Contains(itemId)))
                {
                    graphAffected = true;
                }
            }
        }

        if (changedItemIds.Count == 0)
            return;

        foreach (var itemId in changedItemIds)
        {
            _cachedInventorySplitCounts.Remove(itemId);
            _inventoryRefreshTimes.Remove(itemId);
        }

        Interlocked.Increment(ref _materialCacheVersion);

        // Inventory changes alter dependency deficits even when the changed
        // item is a leaf material rather than a final or precraft result.
        // Keep acquisition estimates synchronized with manual purchases.
        _acquisitionEstimateDirty = true;

        if (!graphAffected && !planningList.SkipIfEnough)
            return;

        lock (_inventoryChangeLock)
        {
            _pendingQueueRefreshFromInventory = true;
            _pendingMaterialsRefreshFromInventory = true;
            _lastGraphAffectingInventoryChange = DateTime.Now;
        }
    }

    private static IEnumerable<uint> GetAffectedTrackedInventoryItemIds(InventoryEventArgs inventoryEvent)
    {
        switch (inventoryEvent)
        {
            case InventoryComplexEventArgs complexEvent:
            {
                if (IsTrackedInventoryContainer(complexEvent.SourceInventory))
                {
                    var sourceItemId = complexEvent.SourceEvent.Item.BaseItemId != 0
                        ? complexEvent.SourceEvent.Item.BaseItemId
                        : complexEvent.SourceEvent.Item.ItemId;
                    if (sourceItemId > 0)
                        yield return sourceItemId;
                }

                if (IsTrackedInventoryContainer(complexEvent.TargetInventory))
                {
                    var targetItemId = complexEvent.TargetEvent.Item.BaseItemId != 0
                        ? complexEvent.TargetEvent.Item.BaseItemId
                        : complexEvent.TargetEvent.Item.ItemId;
                    if (targetItemId > 0)
                        yield return targetItemId;
                }

                yield break;
            }
            case InventoryItemAddedArgs addedEvent when IsTrackedInventoryContainer(addedEvent.Inventory):
            {
                var itemId = addedEvent.Item.BaseItemId != 0
                    ? addedEvent.Item.BaseItemId
                    : addedEvent.Item.ItemId;
                if (itemId > 0)
                    yield return itemId;
                yield break;
            }
            case InventoryItemRemovedArgs removedEvent when IsTrackedInventoryContainer(removedEvent.Inventory):
            {
                var itemId = removedEvent.Item.BaseItemId != 0
                    ? removedEvent.Item.BaseItemId
                    : removedEvent.Item.ItemId;
                if (itemId > 0)
                    yield return itemId;
                yield break;
            }
            case InventoryItemChangedArgs changedEvent when IsTrackedInventoryContainer(changedEvent.Inventory):
            {
                var oldItemId = changedEvent.OldItemState.BaseItemId != 0
                    ? changedEvent.OldItemState.BaseItemId
                    : changedEvent.OldItemState.ItemId;
                if (oldItemId > 0)
                    yield return oldItemId;

                var itemId = changedEvent.Item.BaseItemId != 0
                    ? changedEvent.Item.BaseItemId
                    : changedEvent.Item.ItemId;
                if (itemId > 0 && itemId != oldItemId)
                    yield return itemId;
                yield break;
            }
            default:
            {
                if (!IsTrackedInventoryContainer(inventoryEvent.Item.ContainerType))
                    yield break;

                var itemId = inventoryEvent.Item.BaseItemId != 0
                    ? inventoryEvent.Item.BaseItemId
                    : inventoryEvent.Item.ItemId;
                if (itemId > 0)
                    yield return itemId;
                yield break;
            }
        }
    }

    private void ProcessPendingInventoryChanges()
    {
        bool refreshQueue;
        bool refreshMaterials;
        lock (_inventoryChangeLock)
        {
            if (!_pendingQueueRefreshFromInventory && !_pendingMaterialsRefreshFromInventory)
                return;

            if ((DateTime.Now - _lastGraphAffectingInventoryChange).TotalSeconds < InventoryChangeDebounceSeconds)
                return;

            refreshQueue = _pendingQueueRefreshFromInventory;
            refreshMaterials = _pendingMaterialsRefreshFromInventory;
            _pendingQueueRefreshFromInventory = false;
            _pendingMaterialsRefreshFromInventory = false;
        }

        if (refreshQueue)
        {
            InvalidateQueueCache();
            InvalidateQueuePresentationCaches();
            TriggerQueueRegeneration();
        }

        if (refreshMaterials)
        {
            InvalidateMaterialCaches();
            TriggerMaterialsRegeneration();
        }
    }

    private void EnsureWatchedInventoryItems()
    {
        var currentHash = ComputeListHash();
        if (currentHash == _watchedInventoryHash)
            return;

        _watchedInventoryItemIds.Clear();
        _watchedOriginalResultItemIds.Clear();
        _watchedPrecraftResultItemIds.Clear();

        var visitedRecipes = new HashSet<uint>();
        foreach (var item in GetPlanningList().Recipes)
        {
            if (item.Options.Skipping || item.Quantity <= 0)
                continue;

            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe == null)
                continue;

            CollectWatchedInventoryItems(recipe.Value, true, visitedRecipes);
        }

        _watchedInventoryHash = currentHash;
    }

    private void CollectWatchedInventoryItems(Recipe recipe, bool isOriginalRecipe, HashSet<uint> visitedRecipes)
    {
        var resultItemId = recipe.ItemResult.RowId;
        if (resultItemId > 0)
        {
            _watchedInventoryItemIds.Add(resultItemId);
            if (isOriginalRecipe)
                _watchedOriginalResultItemIds.Add(resultItemId);
            else
                _watchedPrecraftResultItemIds.Add(resultItemId);
        }

        if (!visitedRecipes.Add(recipe.RowId))
            return;

        foreach (var (itemId, _) in RecipeManager.GetIngredients(recipe))
        {
            if (itemId > 0)
                _watchedInventoryItemIds.Add(itemId);

            var subRecipe = GetPlanningList().ResolveRecipeForItem(itemId);
            if (subRecipe.HasValue)
                CollectWatchedInventoryItems(subRecipe.Value, false, visitedRecipes);
        }
    }

    private static bool IsTrackedInventoryContainer(GameInventoryType inventoryType)
        => inventoryType is GameInventoryType.Inventory1
            or GameInventoryType.Inventory2
            or GameInventoryType.Inventory3
            or GameInventoryType.Inventory4
            or GameInventoryType.Crystals;

    private void DrawQueuePane()
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Craft Queue");
        ImGui.Separator();
        ImGui.Spacing();
        var planningList = GetPlanningList();
        var activeExecutionPlan = GetActiveExecutionPlan();
        if (planningList.Recipes.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No recipes in queue.");
            ImGui.Spacing();
            ImGui.TextWrapped("Add recipes using the panel on the right.");
            return;
        }


        var lineH = ImGui.GetTextLineHeightWithSpacing();
        var queueH = Math.Clamp(
            ImGui.GetContentRegionAvail().Y * 0.35f,
            lineH * 3,
            VulcanUiScaling.Scaled(300f));

        ImGui.BeginChild("QueueList", new Vector2(-1, queueH), false);

        if (_isGeneratingQueue)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Calculating craft queue...");
        }
        else
        {
            var displayRows = GetDisplayQueueRows(planningList, activeExecutionPlan);
            if (displayRows.Count == 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "Queue is empty.");
            }
            else
            {
                var clipper = ImGui.ImGuiListClipper();
                clipper.Begin(displayRows.Count);
                while (clipper.Step())
                {
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        DrawQueueRow(displayRows[i], planningList);
                }
                clipper.End();
                clipper.Destroy();
            }
        }

        ImGui.EndChild();

        ImGui.BeginChild("QueueFooter", new Vector2(-1, 0), false);

        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Checkbox("Show Precrafts##sp", ref _showPrecrafts);

        var preferBestClass = _list.PreferBestClassForMultiRecipeItems;
        if (ImGui.Checkbox("Always prefer best class for items with multiple recipes##bestclass", ref preferBestClass))
        {
            _list.PreferBestClassForMultiRecipeItems = preferBestClass;
            GatherBuddy.CraftingListManager.SaveList(_list);
            InvalidateQueueCache();
            InvalidateMaterialCaches();
            InvalidatePresentationCaches();
            TriggerQueueRegeneration();
            TriggerMaterialsRegeneration();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Overrides the recipe selected for an item when multiple crafting classes can produce it. Chooses the highest-level eligible class, then the saved gearset with the highest combined Craftsmanship and Control, then CP. Disabled by default.");

        var skipIfEnough = _list.SkipIfEnough;
        if (ImGui.Checkbox("Skip if Already Have Enough##sie", ref skipIfEnough))
        {
            _list.SkipIfEnough    = skipIfEnough;
            InvalidateQueueCache();
            InvalidateMaterialCaches();
            InvalidatePresentationCaches();
            GatherBuddy.CraftingListManager.SaveList(_list);
            TriggerQueueRegeneration();
            RefreshInventoryCounts();
        }

        if (_list.SkipIfEnough)
        {
            ImGui.Indent();
            var skipFinalIfEnough = _list.SkipFinalIfEnough;
            if (ImGui.Checkbox("Include Final Crafts##sife", ref skipFinalIfEnough))
            {
                _list.SkipFinalIfEnough = skipFinalIfEnough;
                InvalidateQueueCache();
                InvalidatePresentationCaches();
                GatherBuddy.CraftingListManager.SaveList(_list);
                TriggerQueueRegeneration();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Also reduce final crafts based on how many you already have. Useful for resuming an interrupted list.");
            ImGui.Unindent();
        }

        var quickSynthAll = _list.QuickSynthAll;
        if (ImGui.Checkbox("Quick Synth##qsa", ref quickSynthAll))
        {
            _list.QuickSynthAll = quickSynthAll;
            GatherBuddy.CraftingListManager.SaveList(_list);
            InvalidateQueueCache();
            InvalidateMaterialCaches();
            InvalidatePresentationCaches();
            TriggerQueueRegeneration();
            TriggerMaterialsRegeneration();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force Quick Synthesis on eligible items in this list. Additional options appear below when enabled.");

        if (_list.QuickSynthAll)
        {
            ImGui.Indent();

            var quickSynthAllPreferNQ = _list.QuickSynthAllPreferNQ;
            if (ImGui.Checkbox("Prefer NQ##qsapnq", ref quickSynthAllPreferNQ))
            {
                _list.QuickSynthAllPreferNQ = quickSynthAllPreferNQ;
                GatherBuddy.CraftingListManager.SaveList(_list);
                InvalidateQueueCache();
                InvalidateMaterialCaches();
                InvalidatePresentationCaches();
                TriggerQueueRegeneration();
                TriggerMaterialsRegeneration();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Enable the Quick Synthesis 'Synthesize NQ items only' toggle for affected crafts.");

            var quickSynthAllPrecraftsOnly = _list.QuickSynthAllPrecraftsOnly;
            if (ImGui.Checkbox("Precrafts only##qsapo", ref quickSynthAllPrecraftsOnly))
            {
                _list.QuickSynthAllPrecraftsOnly = quickSynthAllPrecraftsOnly;
                GatherBuddy.CraftingListManager.SaveList(_list);
                InvalidateQueueCache();
                InvalidateMaterialCaches();
                InvalidatePresentationCaches();
                TriggerQueueRegeneration();
                TriggerMaterialsRegeneration();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Apply Quick Synth and Prefer NQ only to generated precrafts, leaving final list items unchanged.");

            ImGui.Unindent();
        }

        var allaganEnabled = AllaganTools.Enabled;
        using (ImRaii.Disabled(!allaganEnabled))
        {
            var retainerRestock = _list.RetainerRestock;
            if (ImGui.Checkbox("Restock from Retainers##rrr", ref retainerRestock))
            {
                _list.RetainerRestock = retainerRestock;
                GatherBuddy.CraftingListManager.SaveList(_list);
                InvalidateQueueCache();
                InvalidateMaterialCaches();
                InvalidatePresentationCaches();
                TriggerQueueRegeneration();
                TriggerMaterialsRegeneration();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(allaganEnabled
                ? "Withdraw needed materials from retainers before generating the gather list. Respects HQ/NQ preferences."
                : "Requires Allagan Tools to be installed and enabled.");

        var returnHomeWorld = _list.ReturnToHomeWorldBeforeCrafting;
        if (ImGui.Checkbox("Return to Home World before Crafting##returnHomeWorld", ref returnHomeWorld))
        {
            _list.ReturnToHomeWorldBeforeCrafting = returnHomeWorld;
            SaveAcquisitionSettings();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("After gathering and purchasing, return to the character's Home World before crafting.");

        var autoPurchase = _list.AutoPurchaseBlockedDependencies;
        if (ImGui.Checkbox("Purchase uncraftable/ungatherable dependencies automatically##autoPurchaseDependencies", ref autoPurchase))
        {
            _list.AutoPurchaseBlockedDependencies = autoPurchase;
            SaveAcquisitionSettings();
            if (!autoPurchase)
                _acquisitionPlanningResult = null;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Purchase only missing precraft dependencies whose selected craft/gather path is unusable. Final list outputs are never purchased.");

        if (_list.AutoPurchaseBlockedDependencies)
        {
            ImGui.Indent();

            var preferMarketForSpecialCurrency = _list.PreferMarketForSpecialCurrency;
            if (ImGui.Checkbox("Prefer market for special-currency purchases##acqPreferMarketCurrency", ref preferMarketForSpecialCurrency))
            {
                _list.PreferMarketForSpecialCurrency = preferMarketForSpecialCurrency;
                SaveAcquisitionSettings();
            }

            var preferHq = _list.PreferHQ;
            if (ImGui.Checkbox("Prefer HQ##acqPreferHq", ref preferHq))
            {
                _list.PreferHQ = preferHq;
                SaveAcquisitionSettings();
            }

            var preferVendors = _list.PreferVendors;
            if (ImGui.Checkbox("Prefer vendors##acqPreferVendors", ref preferVendors))
            {
                _list.PreferVendors = preferVendors;
                SaveAcquisitionSettings();
            }

            var currentWorldOnly = _list.CurrentWorldOnly;
            if (ImGui.Checkbox("Current world only##acqCurrentWorldOnly", ref currentWorldOnly))
            {
                _list.CurrentWorldOnly = currentWorldOnly;
                SaveAcquisitionSettings();
            }

            var hasMaximumGilSpend = _list.MaximumGilSpend.HasValue;
            var automaticEstimate = _acquisitionPlanningResult?.PreferredEstimate?.TotalGil
                ?? _acquisitionPlanningResult?.MinimumGilEstimate?.TotalGil
                ?? 0;
            var minimumEstimate = _acquisitionPlanningResult?.MinimumGilEstimate?.TotalGil ?? 0;
            if (ImGui.Checkbox("Set maximum Gil spend##acqSetMaxGil", ref hasMaximumGilSpend))
            {
                _list.MaximumGilSpend = hasMaximumGilSpend
                    ? Math.Max(minimumEstimate, automaticEstimate)
                    : null;
                SaveAcquisitionSettings();
            }

            if (hasMaximumGilSpend)
            {
                var maximumGilSpend = (int)Math.Clamp(_list.MaximumGilSpend ?? Math.Max(minimumEstimate, automaticEstimate), 0, int.MaxValue);
                var clampedMinimum = (int)Math.Clamp(minimumEstimate, 0, int.MaxValue);
                if (minimumEstimate > 0 && maximumGilSpend < clampedMinimum)
                {
                    maximumGilSpend = clampedMinimum;
                    _list.MaximumGilSpend = minimumEstimate;
                    SaveAcquisitionSettings();
                }
                ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(180f));
                if (ImGui.InputInt("Maximum Gil##acqMaximumGil", ref maximumGilSpend))
                {
                    _list.MaximumGilSpend = Math.Max(minimumEstimate, maximumGilSpend);
                    SaveAcquisitionSettings();
                }
                if (minimumEstimate > 0)
                    ImGui.TextColored(ImGuiColors.DalamudGrey3, $"Minimum estimate: {minimumEstimate:N0} Gil");
            }

            if (ImGui.TreeNode("Estimates##acquisitionEstimates"))
            {
                DrawAcquisitionEstimates();
                ImGui.TreePop();
            }

            ImGui.Unindent();
        }
        var buttonHeight = VulcanUiScaling.Scaled(22f);
        var acquisitionStatusText = string.IsNullOrWhiteSpace(_acquisitionStatus)
            ? string.Empty
            : $"Preview: {_acquisitionStatus}";
        var acquisitionStatusHeight = acquisitionStatusText.Length == 0
            ? 0f
            : ImGui.CalcTextSize(acquisitionStatusText, false, ImGui.GetContentRegionAvail().X).Y;
        var footerButtonHeight = ImGui.GetStyle().ItemSpacing.Y + buttonHeight * 2f;
        if (acquisitionStatusText.Length > 0)
            footerButtonHeight += ImGui.GetStyle().ItemSpacing.Y + acquisitionStatusHeight;
        var buttonStartY = ImGui.GetWindowHeight() - ImGui.GetStyle().WindowPadding.Y - footerButtonHeight;
        ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY(), buttonStartY));

        ImGui.Spacing();

        if (IPCSubscriber.IsReady("Artisan"))
        {
            ImGuiUtil.DrawDisabledButton("Artisan Detected", VulcanUiScaling.Scaled(-1f, 22f),
                "Artisan plugin is loaded. Please unload Artisan to use Vulcan's crafting system.", true);
        }
        else
        {
            var (hardFails, warnings) = CountValidationIssues();
            if (hardFails > 0)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.50f, 0.15f, 0.15f, 1f));
            else if (warnings > 0)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.40f, 0.05f, 1f));

            if (ImGui.Button("Start Gather/Crafting", new Vector2(-1f, buttonHeight)))
            {
                if (hardFails > 0)
                    ImGui.OpenPopup("ConfirmFailedMacros##startCraft");
                else
                    OnStartCrafting?.Invoke(_list);
            }

            if (acquisitionStatusText.Length > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
                ImGui.TextWrapped(acquisitionStatusText);
                ImGui.PopStyleColor();
            }

            if (hardFails > 0 || warnings > 0)
            {
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(hardFails > 0
                        ? $"{hardFails} macro(s) will fail this craft. Click to confirm and start anyway."
                        : $"{warnings} macro(s) have warnings.");
            }

            if (ImGui.BeginPopupModal("ConfirmFailedMacros##startCraft", ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextColored(new Vector4(0.78f, 0.25f, 0.25f, 1f), $"{hardFails} macro(s) are predicted to FAIL their craft.");
                ImGui.TextWrapped("These items may not be completed. Start crafting anyway?");
                ImGui.Spacing();
                if (ImGui.Button("Start Anyway", VulcanUiScaling.Scaled(120f, 0f)))
                {
                    OnStartCrafting?.Invoke(_list);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel", VulcanUiScaling.Scaled(80f, 0f)))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        var hSpacing = ImGui.GetStyle().ItemSpacing.X;
        var thirdW = (ImGui.GetContentRegionAvail().X - hSpacing * 2f) / 3f;
        if (ImGui.Button("Generate Gather List##gatherList", new Vector2(thirdW, buttonHeight)))
        {
            var materials = new Dictionary<uint, int>(GetCachedMaterials());
            CraftingGatherBridge.CreatePersistentGatherList($"{_list.Name}...Auto-Generated", materials);
        }
        ImGui.SameLine();
        var matsBtnLabel = GatherBuddy.CraftingMaterialsWindow?.IsOpen == true ? "Hide Materials" : "View Materials";
        if (ImGui.Button($"{matsBtnLabel}##viewMats", new Vector2(thirdW, buttonHeight)) && GatherBuddy.CraftingMaterialsWindow != null)
            GatherBuddy.CraftingMaterialsWindow.IsOpen = !GatherBuddy.CraftingMaterialsWindow.IsOpen;
        ImGui.SameLine();
        var treeBtnLabel = GatherBuddy.CraftingTreeWindow?.IsOpen == true ? "Hide Tree" : "View Tree";
        if (ImGui.Button($"{treeBtnLabel}##viewTree", new Vector2(-1f, buttonHeight)) && GatherBuddy.CraftingTreeWindow != null)
        {
            GatherBuddy.CraftingTreeWindow.SetEditor(this);
            GatherBuddy.CraftingTreeWindow.IsOpen = !GatherBuddy.CraftingTreeWindow.IsOpen;
        }

        ImGui.EndChild();
    }

    private void SaveAcquisitionSettings()
    {
        _acquisitionEstimateDirty = true;
        GatherBuddy.CraftingListManager.SaveList(_list);
    }

    private void RefreshAcquisitionEstimate()
    {
        if (!_list.AutoPurchaseBlockedDependencies)
        {
            _acquisitionPlanningResult = null;
            _managedMarketplaceProjection = null;
            _marketplacePurchaseReasons = new Dictionary<uint, MarketplacePurchaseReason>();
            _acquisitionStatus = string.Empty;
            _acquisitionEstimateLoading = false;
            return;
        }

        var now = DateTime.UtcNow;
        if (!_acquisitionEstimateDirty
            && !_acquisitionEstimateLoading
            && now - _lastAcquisitionRefresh < AcquisitionEstimateTtl)
            return;
        if ((now - _lastAcquisitionRefresh).TotalSeconds < 1)
            return;
        _lastAcquisitionRefresh = now;

        try
        {
            var evaluation = CraftingAcquisitionService.Evaluate(CraftingExecutionPlan.Create(_list));
            _acquisitionEstimateDirty = false;
            _acquisitionEstimateLoading = evaluation.IsLoading;
            _acquisitionStatus = evaluation.Status;
            _acquisitionPlanningResult = evaluation.Planning;
            _marketplacePurchaseReasons = BuildMarketplacePurchaseReasons(
                evaluation,
                _list.PreferMarketForSpecialCurrency);
            _managedMarketplaceProjection = evaluation.Planning == null
                ? null
                : GatherBuddy.MarketplaceBuyListManager?.CreateManagedList(
                    evaluation.Planning,
                    new LiveAcquisitionOptions
                    {
                        CurrentWorldOnly = _list.CurrentWorldOnly,
                        PreferHQ = _list.PreferHQ,
                        PreferVendors = _list.PreferVendors,
                        PreferMarketForSpecialCurrency = _list.PreferMarketForSpecialCurrency,
                        MaximumGilSpend = _list.MaximumGilSpend,
                    });
        }
        catch (Exception ex)
        {
            _acquisitionEstimateDirty = false;
            _acquisitionEstimateLoading = false;
            _acquisitionStatus = $"Acquisition estimate unavailable: {ex.Message}";
            _acquisitionPlanningResult = null;
            _managedMarketplaceProjection = null;
            _marketplacePurchaseReasons = new Dictionary<uint, MarketplacePurchaseReason>();
            GatherBuddy.Log.Warning($"[CraftingListEditor] Acquisition estimate failed: {ex.Message}");
        }
    }

    private void DrawAcquisitionEstimates()
    {
        var result = _acquisitionPlanningResult;
        if (result == null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Waiting for dependency and market estimates.");
            return;
        }

        if (result.Blockers.Count > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, result.Status switch
            {
                AcquisitionPlanStatus.BudgetExceeded => "No source plan fits the maximum Gil spend.",
                AcquisitionPlanStatus.InsufficientCurrency => "No source plan fits current currency balances.",
                AcquisitionPlanStatus.UnknownCurrencyBalance => "A required currency balance is unknown; no purchase was attempted.",
                AcquisitionPlanStatus.UnknownCurrentWorld => "Current world is unknown; current-world-only purchasing is unavailable.",
                AcquisitionPlanStatus.DeterministicLimitExceeded => "Estimate search exceeded its safe exact-search limit.",
                _ => "Some dependencies cannot be purchased automatically.",
            });
            foreach (var blocker in result.Blockers)
            {
                if (blocker.ItemId == 0 && string.IsNullOrWhiteSpace(blocker.ItemName))
                {
                    ImGui.TextWrapped(blocker.Reason);
                    continue;
                }
                var itemLabel = string.IsNullOrWhiteSpace(blocker.ItemName)
                    ? $"Item {blocker.ItemId}"
                    : blocker.ItemName;
                ImGui.TextWrapped($"{itemLabel}: {blocker.Reason}");
            }
            return;
        }

        if (result.PreferredEstimate != null)
        {
            var maximum = _list.MaximumGilSpend;
            var exceedsMaximum = maximum.HasValue && result.PreferredEstimate.TotalGil > maximum.Value;
            var preferenceExplanation = "influenced by Prefer market, Prefer HQ, and Prefer vendors";
            var preferredEstimateText = exceedsMaximum
                ? $"Preferred estimate: {result.PreferredEstimate.TotalGil:N0} Gil (over max {maximum.GetValueOrDefault():N0}; fallback will relax preferences; {preferenceExplanation})"
                : $"Preferred estimate: {result.PreferredEstimate.TotalGil:N0} Gil ({preferenceExplanation})";
            ImGui.PushStyleColor(
                ImGuiCol.Text,
                exceedsMaximum ? ImGuiColors.DalamudOrange : ImGuiColors.DalamudYellow);
            ImGui.TextWrapped(preferredEstimateText);
            ImGui.PopStyleColor();
        }
        if (result.MinimumGilEstimate != null)
            ImGui.TextColored(ImGuiColors.DalamudGrey3, $"Minimum-Gil estimate: {result.MinimumGilEstimate.TotalGil:N0} Gil");

        var estimate = result.PreferredEstimate ?? result.MinimumGilEstimate;
        if (estimate == null || estimate.Currencies.Count == 0)
        {
            DrawManagedMarketplaceProjection();
            return;
        }

        foreach (var currency in estimate.Currencies)
        {
            var name = string.IsNullOrWhiteSpace(currency.CurrencyName)
                ? $"Currency {currency.CurrencyId}"
                : currency.CurrencyName;
            var iconId = currency.IconId;
            if (iconId == 0 && currency.CurrencyId == AcquisitionCurrency.GilId)
                iconId = ResolveGilIconId();
            if (iconId != 0)
            {
                CraftingRowIcons.DrawIconsRightAligned(new[]
                {
                    new CraftingRowIcons.RowIcon(iconId, name),
                });
                ImGui.SameLine();
            }
            var available = currency.Available == long.MaxValue ? "?" : currency.Available.ToString("N0");
            ImGui.Text($"{name}: {available} / {currency.Required:N0}");
        }

        DrawManagedMarketplaceProjection();
    }

    private void DrawManagedMarketplaceProjection()
    {
        var projection = _managedMarketplaceProjection;
        if (projection == null || projection.Entries.Count == 0)
            return;

        ImGui.Separator();
        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Items to purchase from the Marketboard");
        foreach (var entry in projection.Entries)
        {
            var reason = _marketplacePurchaseReasons.GetValueOrDefault(entry.ItemId);
            if (string.IsNullOrWhiteSpace(reason.Text))
            {
                ImGui.Text($"{entry.ItemName} ×{entry.TargetQuantity:N0}");
                continue;
            }

            if (reason.CurrencyAmount <= 0)
            {
                ImGui.Text($"{entry.ItemName} ×{entry.TargetQuantity:N0} ({reason.Text})");
                continue;
            }

            ImGui.Text($"{entry.ItemName} ×{entry.TargetQuantity:N0} ({reason.Text} ({reason.CurrencyAmount:N0}");
            ImGui.SameLine(0, VulcanUiScaling.Scaled(3f));
            if (reason.CurrencyIconId != 0)
            {
                CraftingRowIcons.DrawIconsRightAligned(new[]
                {
                    new CraftingRowIcons.RowIcon(reason.CurrencyIconId, reason.CurrencyTooltip),
                }, VulcanUiScaling.Scaled(16f), 0f);
            }
            else if (!string.IsNullOrWhiteSpace(reason.CurrencyTooltip))
            {
                ImGui.Text(reason.CurrencyTooltip);
            }
            ImGui.SameLine(0, VulcanUiScaling.Scaled(3f));
            ImGui.Text("))");
        }
    }

    internal static IReadOnlyDictionary<uint, MarketplacePurchaseReason> BuildMarketplacePurchaseReasons(
        CraftingAcquisitionService.Evaluation evaluation,
        bool preferMarketForSpecialCurrency)
    {
        var marketTransactions = evaluation.Planning?.SelectedPlan?.Transactions
            .Where(transaction => transaction.SourceKind == AcquisitionSourceKind.Market)
            .ToArray() ?? [];
        if (marketTransactions.Length == 0)
            return new Dictionary<uint, MarketplacePurchaseReason>();

        var marketItemIds = marketTransactions
            .Select(transaction => transaction.ItemId)
            .Distinct()
            .ToArray();

        var specialCurrencyMarketItemIds = evaluation.Planning?.SelectedPlan?.Transactions
            .Where(transaction => transaction.SourceKind == AcquisitionSourceKind.Market
                && transaction.IsSpecialCurrencyAlternative)
            .Select(transaction => transaction.ItemId)
            .ToHashSet() ?? [];

        return marketItemIds.ToDictionary(
            itemId => itemId,
            itemId =>
            {
                if (preferMarketForSpecialCurrency && specialCurrencyMarketItemIds.Contains(itemId))
                    return new MarketplacePurchaseReason("market selected by special-currency preference");

                var selectedPath = evaluation.Snapshot.Input.Dependencies
                    .FirstOrDefault(dependency => dependency.ItemId == itemId)
                    ?.SelectedPath;
                if (selectedPath is
                    {
                        Kind: AcquisitionPathKind.Gather or AcquisitionPathKind.Fish or AcquisitionPathKind.Reduction,
                        Capability: { Status: not AcquisitionCapabilityStatus.Usable } capability,
                    })
                    return new MarketplacePurchaseReason(capability.Reason.Contains("folklore", StringComparison.OrdinalIgnoreCase)
                        ? "folklore required"
                        : capability.Reason);

                var vendorOffers = evaluation.Snapshot.Input.VendorOffers
                    .Where(offer => offer.EffectiveOutputs.Any(output => output.ItemId == itemId && output.Quantity > 0))
                    .ToArray();
                if (vendorOffers.Length == 0)
                    return new MarketplacePurchaseReason("no vendor source");

                var targetQuantity = marketTransactions
                    .Where(transaction => transaction.ItemId == itemId)
                    .Sum(transaction => (long)Math.Max(0, transaction.Quantity));
                var insufficientCurrency = FindInsufficientCurrency(
                    itemId,
                    Math.Max(1, targetQuantity),
                    vendorOffers,
                    evaluation.Snapshot.Input.CurrencyBalances);
                if (insufficientCurrency.HasValue)
                    return insufficientCurrency.Value;

                return vendorOffers.Any(offer => offer.IsAvailable)
                    ? new MarketplacePurchaseReason("market selected by estimate")
                    : new MarketplacePurchaseReason("vendor unavailable");
            });
    }

    private static MarketplacePurchaseReason? FindInsufficientCurrency(
        uint itemId,
        long targetQuantity,
        IReadOnlyList<AcquisitionVendorOffer> vendorOffers,
        IReadOnlyDictionary<uint, long> currencyBalances)
    {
        MarketplacePurchaseReason? firstInsufficient = null;
        foreach (var offer in vendorOffers
                     .Where(offer => offer.IsAvailable)
                     .OrderBy(offer => offer.OfferId, StringComparer.Ordinal))
        {
            var receiveQuantity = offer.EffectiveOutputs
                .Where(output => output.ItemId == itemId && output.Quantity > 0)
                .Sum(output => (long)output.Quantity);
            if (receiveQuantity <= 0)
                continue;

            var purchaseUnits = (targetQuantity + receiveQuantity - 1) / receiveQuantity;
            var offerSufficient = true;
            foreach (var cost in offer.Costs
                         .Where(cost => cost.IsSpecialCurrency && !cost.IsGil && cost.Amount > 0)
                         .OrderBy(cost => cost.CurrencyId))
            {
                if (!currencyBalances.TryGetValue(cost.CurrencyId, out var available))
                {
                    offerSufficient = false;
                    continue;
                }

                var required = checked(cost.Amount * purchaseUnits);
                if (available >= required)
                    continue;

                offerSufficient = false;
                firstInsufficient ??= new MarketplacePurchaseReason(
                    "not enough currency",
                    required,
                    cost.IconId,
                    cost.CurrencyName);
            }

            if (offerSufficient)
                return null;
        }

        return firstInsufficient;
    }

    private static uint ResolveGilIconId()
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        return itemSheet != null && itemSheet.TryGetRow(VendorShopResolver.GilCurrencyItemId, out var item)
            ? (uint)item.Icon
            : 0;
    }
    
    private void DrawDetailsPane()
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, "List Info");
        ImGui.Separator();
        ImGui.Spacing();
        DrawListInfoSection();

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudYellow, "List Consumables");
        ImGui.Separator();
        ImGui.Spacing();
        DrawListConsumablesSection();

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Add Recipe");
        ImGui.Separator();
        ImGui.Spacing();
        DrawAddRecipeSection();

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Recipe List");
        ImGui.Separator();
        ImGui.Spacing();
        DrawRecipeListSection();
        
    }

    private void DrawListInfoSection()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##listName", ref _editingName, 128))
            _nameConflict = false;

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var trimmed = _editingName.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                _editingName = _list.Name;
            }
            else if (GatherBuddy.CraftingListManager.IsNameUnique(trimmed, _list.ID))
            {
                _list.Name   = trimmed;
                _editingName = trimmed;
                GatherBuddy.CraftingListManager.SaveList(_list);
                GatherBuddy.Log.Debug($"[CraftingListEditor] Renamed list to '{trimmed}'");
            }
            else
            {
                _nameConflict = true;
            }
        }

        if (_nameConflict)
            ImGui.TextColored(ImGuiColors.DalamudRed, "A list with that name already exists.");

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Notes");

        if (_editingDescActive)
        {
            if (_focusDescNext)
            {
                ImGui.SetKeyboardFocusHere();
                _focusDescNext = false;
            }
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextMultiline("##listDesc", ref _editingDescription, 512, VulcanUiScaling.Scaled(-1f, 60f));
            if (ImGui.IsItemDeactivated())
            {
                _list.Description = _editingDescription;
                GatherBuddy.CraftingListManager.SaveList(_list);
                _editingDescActive = false;
                GatherBuddy.Log.Debug($"[CraftingListEditor] Updated description for list '{_list.Name}'");
            }
        }
        else
        {
            using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.15f, 0.15f, 0.18f, 1f)))
            {
                ImGui.BeginChild("##notesDisplay", VulcanUiScaling.Scaled(-1f, 60f), true);

                if (string.IsNullOrEmpty(_editingDescription))
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "Click to add notes...");
                else
                {
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudGrey3))
                        ImGui.TextWrapped(_editingDescription);
                }

                if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    _editingDescActive = true;
                    _focusDescNext     = true;
                }

                ImGui.EndChild();
            }
        }

        ImGui.Spacing();
        var buttonWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
        if (ImGui.Button("Export List##exportList", new Vector2(buttonWidth, 0)))
        {
            var exported = GatherBuddy.CraftingListManager.ExportList(_list.ID);
            if (exported != null)
            {
                ImGui.SetClipboardText(exported);
                GatherBuddy.Log.Information($"[CraftingListEditor] Exported list '{_list.Name}' to clipboard");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy GatherBuddy's list export string to the clipboard.");

        ImGui.SameLine();
        if (ImGui.Button("TeamCraft Export##teamCraftExport", new Vector2(-1, 0)))
        {
            var (exported, error) = GatherBuddy.CraftingListManager.ExportListToTeamCraft(_list.ID);
            if (exported != null)
            {
                ImGui.SetClipboardText(exported);
                GatherBuddy.Log.Information($"[CraftingListEditor] Exported list '{_list.Name}' to TeamCraft and copied the link to the clipboard");
            }
            else if (!string.IsNullOrEmpty(error))
            {
                GatherBuddy.Log.Warning($"[CraftingListEditor] Failed to export '{_list.Name}' to TeamCraft: {error}");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy a TeamCraft import link built from this list's Recipe List entries.");
    }

    private void DrawListConsumablesSection()
    {
        var labelColor = new Vector4(0.80f, 0.80f, 0.80f, 1f);
        var valueX     = VulcanUiScaling.Scaled(80f);
        var hasAny     = false;

        if (_list.Consumables.FoodItemId.HasValue)
        {
            ImGui.TextColored(labelColor, "Food:");
            ImGui.SameLine(valueX);
            ImGui.TextColored(labelColor, GetItemLabel(_list.Consumables.FoodItemId.Value, _list.Consumables.FoodHQ));
            hasAny = true;
        }
        if (_list.Consumables.MedicineItemId.HasValue)
        {
            ImGui.TextColored(labelColor, "Medicine:");
            ImGui.SameLine(valueX);
            ImGui.TextColored(labelColor, GetItemLabel(_list.Consumables.MedicineItemId.Value, _list.Consumables.MedicineHQ));
            hasAny = true;
        }
        if (_list.Consumables.ManualItemId.HasValue)
        {
            ImGui.TextColored(labelColor, "Manual:");
            ImGui.SameLine(valueX);
            ImGui.TextColored(labelColor, GetItemLabel(_list.Consumables.ManualItemId.Value, false));
            hasAny = true;
        }
        if (_list.Consumables.SquadronManualItemId.HasValue)
        {
            ImGui.TextColored(labelColor, "Squadron:");
            ImGui.SameLine(valueX);
            ImGui.TextColored(labelColor, GetItemLabel(_list.Consumables.SquadronManualItemId.Value, false));
            hasAny = true;
        }
        if (_list.UseAllHQ)
        {
            ImGui.TextColored(labelColor, "HQ Mats:");
            ImGui.SameLine(valueX);
            ImGui.TextColored(labelColor, "All HQ");
            hasAny = true;
        }
        if (!hasAny)
            ImGui.TextColored(ImGuiColors.DalamudGrey, "None set.");

        ImGui.Spacing();
        if (ImGui.Button("Edit Consumables & Macros##editConsumables", new Vector2(0, 0)))
            _consumablesPopup.OpenListDefaults(_list);
    }
    
    private void DrawAddRecipeSection()
    {
        if (_recipeCombo == null)
            InitializeRecipeCombo();

        DrawRecipeComboWithKeywordFilter();

        ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(120f));
        ImGui.InputInt("##quantity", ref _searchQuantity, 1);
        if (_searchQuantity < 1)
            _searchQuantity = 1;
        ImGui.SameLine();

        using (ImRaii.Disabled(_selectedRecipe == null))
        {
            var clicked = ImGui.Button("Add to List##addRecipeBtn", new Vector2(0, 0));
            if (!clicked && ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                clicked = true;
            if (clicked && _selectedRecipe != null)
            {
                _list.AddRecipe(_selectedRecipe.Value.RowId, _searchQuantity);
                GatherBuddy.CraftingListManager.SaveList(_list);
                RaphaelAssessmentService.QueueWarmupForAddedListRecipe(_selectedRecipe.Value.RowId, _list);
                InvalidateQueueCache();
                InvalidateMaterialCaches();
                InvalidatePresentationCaches();
                TriggerQueueRegeneration();
                _selectedRecipe = null;
                _searchQuantity = 1;
            }
        }

        if (ImGui.IsItemHovered() && _selectedRecipe != null)
            ImGui.SetTooltip($"Add {_recipeLabels[_selectedRecipe.Value.RowId]} x{_searchQuantity} to list");
    }

    private void DrawRecipeComboWithKeywordFilter()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##recipeComboCustom", _selectedRecipe.HasValue ? _recipeLabels.GetValueOrDefault(_selectedRecipe.Value.RowId, "Select recipe") : "Select recipe"))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##filterRecipes", "Type to filter...", ref _lastComboFilter, 256);

            var filterKeywords = _lastComboFilter.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(SearchTextNormalizer.Normalize)
                .Where(keyword => keyword.Length > 0)
                .ToArray();

            var displayRecipes = _allRecipes;
            if (filterKeywords.Length > 0)
            {
                displayRecipes = _allRecipes.Where(r =>
                {
                    var label = _recipeSearchLabels[r.RowId];
                    return filterKeywords.All(keyword => label.Contains(keyword));
                }).ToList();

                if (displayRecipes.Count == 0)
                {
                    var fuzzyFilter = string.Join('\0', filterKeywords);
                    if (_cachedFuzzyFilter != fuzzyFilter)
                    {
                        _cachedFuzzyFilter = fuzzyFilter;
                        _cachedFuzzyRecipes = _allRecipes
                            .Select(recipe => (Recipe: recipe, Score: FuzzySearch.Score(_recipeSearchLabels[recipe.RowId], filterKeywords)))
                            .Where(match => match.Score.HasValue)
                            .OrderBy(match => match.Score)
                            .ThenBy(match => _recipeLabels[match.Recipe.RowId], StringComparer.CurrentCultureIgnoreCase)
                            .Take(20)
                            .Select(match => match.Recipe)
                            .ToList();
                    }
                    displayRecipes = _cachedFuzzyRecipes;
                }
            }

            var height = ImGui.GetTextLineHeightWithSpacing();
            void DrawRecipeItem(Recipe recipe)
            {
                if (ImGui.Selectable(_recipeLabels[recipe.RowId], _selectedRecipe?.RowId == recipe.RowId))
                {
                    _selectedRecipe = recipe;
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGuiClip.ClippedDraw(displayRecipes, DrawRecipeItem, height);

            ImGui.EndCombo();
        }
    }

    private void InitializeRecipeCombo()
    {
        var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (recipeSheet == null)
            return;

        _allRecipes.Clear();
        foreach (var recipe in recipeSheet)
        {
            try
            {
                if (recipe.ItemResult.RowId == 0 || recipe.Number == 0)
                    continue;

                var recipeNameOriginal = recipe.ItemResult.Value.Name.ExtractText();
                if (!_recipeLabels.ContainsKey(recipe.RowId))
                {
                    var jobName = GetCraftingJobName(recipe.CraftType.RowId);
                    _recipeLabels[recipe.RowId] = $"{recipeNameOriginal} ({jobName} {recipe.RecipeLevelTable.Value.ClassJobLevel})";
                    _recipeSearchLabels[recipe.RowId] = SearchTextNormalizer.Normalize(_recipeLabels[recipe.RowId]);
                }

                _allRecipes.Add(recipe);
            }
            catch
            {
            }
        }

        _allRecipes.Sort((a, b) =>
        {
            var levelCmp = b.RecipeLevelTable.Value.ClassJobLevel.CompareTo(a.RecipeLevelTable.Value.ClassJobLevel);
            if (levelCmp != 0) return levelCmp;
            return a.ItemResult.Value.Name.ExtractText().CompareTo(b.ItemResult.Value.Name.ExtractText());
        });

        _recipeCombo = new ClippedSelectableCombo<Recipe>("RecipeCombo", "Recipe", 300, _allRecipes, r => _recipeLabels[r.RowId]);
    }

    private void DrawRecipeListSection()
    {
        if (_list.Recipes.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No recipes added yet.");
            return;
        }

        var indicesToRemove = new List<int>();

        if (_selectedRecipeIndices.Count > 1)
        {
            var selectionCount = _selectedRecipeIndices.Count;

            if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Ban.ToIconString() + "##skipSelected", Interface.IconButtonSize,
                    $"Mark all {selectionCount} selected recipes as skipped.", false, true))
                BulkSetSkipping(true);

            ImGui.SameLine();
            if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Check.ToIconString() + "##enableSelected", Interface.IconButtonSize,
                    $"Re-enable all {selectionCount} selected recipes.", false, true))
                BulkSetSkipping(false);

            ImGui.SameLine();
            if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Trash.ToIconString() + "##removeSelected", Interface.IconButtonSize,
                    $"Remove the {selectionCount} selected recipes from this list.", false, true))
                indicesToRemove.AddRange(_selectedRecipeIndices);

            ImGui.SameLine();
            ImGui.TextDisabled($"({selectionCount} selected)");
        }
        var recipeRows = GetRecipeDisplayRows();
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(recipeRows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                DrawRecipeListRow(recipeRows[i], indicesToRemove);
        }
        clipper.End();
        clipper.Destroy();

        if (indicesToRemove.Count > 0)
        {
            foreach (var idx in indicesToRemove.Distinct().OrderByDescending(x => x))
                _list.Recipes.RemoveAt(idx);
            _selectedRecipeIndices.Clear();
            _lastClickedRecipeIndex = -1;
            GatherBuddy.CraftingListManager.SaveList(_list);
            InvalidateQueueCache();
            InvalidateMaterialCaches();
            InvalidatePresentationCaches();
            TriggerQueueRegeneration();
        }
    }

    private IReadOnlyList<QueueDisplayRow> GetDisplayQueueRows(CraftingListDefinition planningList, CraftingExecutionPlan? activeExecutionPlan)
    {
        EnsureQueueDisplayRows(planningList, activeExecutionPlan);
        return _showPrecrafts
            ? (IReadOnlyList<QueueDisplayRow>)(_cachedQueueDisplayRows ?? new List<QueueDisplayRow>())
            : (IReadOnlyList<QueueDisplayRow>)(_cachedOriginalQueueDisplayRows ?? new List<QueueDisplayRow>());
    }

    private void EnsureQueueDisplayRows(CraftingListDefinition planningList, CraftingExecutionPlan? activeExecutionPlan)
    {
        var queueCache = GetQueueCache();
        if (queueCache == null)
            return;

        var currentHash = ComputeListHash();
        if (_cachedQueueDisplayRowsValid && _cachedQueueDisplayRowsHash == currentHash
         && _cachedQueueDisplayRows != null && _cachedOriginalQueueDisplayRows != null)
            return;

        try
        {
            var sortedQueue = queueCache.SortedQueue;
            _cachedQueueDisplayRows = BuildQueueDisplayRows(sortedQueue, planningList);
            IReadOnlyList<CraftingListItem> originalQueue = activeExecutionPlan != null
                ? activeExecutionPlan.OriginalRecipesView
                : sortedQueue
                    .Where(queueItem => queueItem.IsOriginalRecipe)
                    .Select(queueItem => new CraftingListItem(queueItem.RecipeId, queueItem.Quantity)
                    {
                        IsOriginalRecipe = true,
                    })
                    .ToList();
            _cachedOriginalQueueDisplayRows = BuildQueueDisplayRows(originalQueue, planningList);
            _cachedQueueDisplayRowsHash = currentHash;
            _cachedQueueDisplayRowsValid = true;
            _cachedValidationIssueCountsHash = string.Empty;
            _cachedValidationIssueCountsValid = false;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingListEditor] Failed to rebuild queue display cache for list '{_list.Name}': {ex.Message}");
            _cachedQueueDisplayRows = new List<QueueDisplayRow>();
            _cachedOriginalQueueDisplayRows = new List<QueueDisplayRow>();
            _cachedQueueDisplayRowsHash = currentHash;
            _cachedQueueDisplayRowsValid = true;
            _cachedValidationIssueCountsHash = string.Empty;
            _cachedValidationIssueCountsValid = false;
        }
    }

    private List<QueueDisplayRow> BuildQueueDisplayRows(IReadOnlyList<CraftingListItem> sourceQueue, CraftingListDefinition planningList)
    {
        var rows = new List<QueueDisplayRow>(sourceQueue.Count);
        for (var i = 0; i < sourceQueue.Count; i++)
        {
            var queueItem = sourceQueue[i];
            var recipeData = RecipeManager.GetRecipe(queueItem.RecipeId);
            if (recipeData == null)
                continue;

            var itemName = recipeData.Value.ItemResult.Value.Name.ExtractText();
            var jobName = GetCraftingJobName(recipeData.Value.CraftType.RowId);
            var recipeOptions = planningList.GetRecipeOptions(queueItem.RecipeId, queueItem.IsOriginalRecipe);
            var effectiveQuickSynth = recipeOptions.NQOnly || planningList.ShouldForceQuickSynth(recipeData.Value, queueItem.IsOriginalRecipe);
            var forceQuickSynth = planningList.ShouldForceQuickSynth(recipeData.Value, queueItem.IsOriginalRecipe);
            var forcePreferNQNoQuickSynth = !recipeData.Value.CanQuickSynth && planningList.ShouldForcePreferNQ(queueItem.IsOriginalRecipe);
            var queueItemCraftSettings = GetEffectiveCraftSettings(queueItem.RecipeId, queueItem.IsOriginalRecipe);
            var hasExecutionContext = CraftingContextResolver.TryResolveListExecutionContext(
                planningList,
                queueItem.RecipeId,
                queueItem.IsOriginalRecipe,
                out var executionContext);
            var usesQuickSynth = hasExecutionContext
                ? executionContext.UseQuickSynthesis
                : WillUseQuickSynth(recipeData.Value, queueItem.RecipeId, queueItem.IsOriginalRecipe);
            var validation = usesQuickSynth
                ? null
                : MacroValidator.GetOrCompute(queueItem.RecipeId,
                    ResolveEffectiveMacroId(queueItemCraftSettings, !queueItem.IsOriginalRecipe),
                    queueItemCraftSettings,
                    planningList.Consumables);
            RaphaelAssessment? raphaelAssessment = null;
            if (hasExecutionContext
             && CraftingContextResolver.UsesSolverAssessment(executionContext))
            {
                RaphaelAssessmentService.TryAssessListQueueItem(queueItem.RecipeId, queueItem.IsOriginalRecipe, planningList, out var resolvedAssessment);
                raphaelAssessment = resolvedAssessment;
            }
            rows.Add(new QueueDisplayRow
            {
                QueueIndex = i,
                Quantity = queueItem.Quantity,
                IsOriginalRecipe = queueItem.IsOriginalRecipe,
                Recipe = recipeData.Value,
                ItemName = itemName,
                Label = $"{(effectiveQuickSynth ? "[QS] " : forcePreferNQNoQuickSynth ? "[NQ] " : string.Empty)}{i + 1}. {itemName} x{queueItem.Quantity} ({jobName})",
                BaseTextColor = effectiveQuickSynth
                    ? new Vector4(0.3f, 0.9f, 0.9f, 1f)
                    : queueItem.IsOriginalRecipe
                        ? new Vector4(1f, 1f, 1f, 1f)
                        : new Vector4(0.7f, 0.7f, 0.7f, 1f),
                EffectiveQuickSynth = effectiveQuickSynth,
                ForceQuickSynth = forceQuickSynth,
                Validation = validation,
                RaphaelAssessment = raphaelAssessment,
            });
        }

        return rows;
    }

    private void DrawQueueRow(QueueDisplayRow row, CraftingListDefinition planningList)
    {
        var willBeSkipped = planningList.SkipIfEnough
            && (!row.IsOriginalRecipe
                ? WillBeSkippedDueToInventory(row.Recipe)
                : planningList.SkipFinalIfEnough && row.Quantity == 0);
        var textColor = willBeSkipped
            ? new Vector4(1f, 0.3f, 0.3f, 1f)
            : row.BaseTextColor;
        if (row.RaphaelAssessment != null)
        {
            ImGui.AlignTextToFramePadding();
            DrawRaphaelAssessmentMarker(row.RaphaelAssessment);
        }

        if (row.Validation != null)
        {
            ImGui.AlignTextToFramePadding();
            DrawValidationMarker(row.Validation);
        }

        var crafterIcon     = CraftingRowIcons.GetCrafterIcon(row.Recipe);
        var innerSpacing    = ImGui.GetStyle().ItemInnerSpacing.X;
        var crafterIconSize = VulcanUiScaling.Scaled(16f);
        var selectableWidth = Math.Max(50f, ImGui.GetContentRegionAvail().X - crafterIconSize - innerSpacing);

        ImGui.PushStyleColor(ImGuiCol.Text, textColor);
        var isSelected = _selectedQueueIndex == row.QueueIndex;
        if (ImGui.Selectable(row.Label, isSelected, ImGuiSelectableFlags.None, new Vector2(selectableWidth, 0)))
            _selectedQueueIndex = row.QueueIndex;
        ImGui.PopStyleColor();

        var isPopupOpen = GatherBuddy.ControllerSupport != null
            ? GatherBuddy.ControllerSupport.ContextMenu.BeginPopupContextItemWithGamepad($"queue_ctx_{row.QueueIndex}", Dalamud.GamepadState)
            : ImGui.BeginPopupContextItem($"queue_ctx_{row.QueueIndex}");

        if (!isPopupOpen)
        {
            ImGui.SameLine(0, innerSpacing);
            CraftingRowIcons.DrawIconsRightAligned(new[] { crafterIcon }, crafterIconSize);
            return;
        }

        if (ImGui.MenuItem("Craft Settings..."))
        {
            if (row.IsOriginalRecipe)
            {
                var listItem = _list.Recipes.FirstOrDefault(recipeItem => recipeItem.RecipeId == row.Recipe.RowId);
                if (listItem != null)
                    _craftSettingsPopup.OpenForListItem(listItem, _list, row.ItemName);
            }
            else
            {
                _craftSettingsPopup.OpenForPrecraft(row.Recipe.RowId, row.ItemName, _list);
            }
        }

        var resultItemId = row.Recipe.ItemResult.RowId;
        var altRecipes = RecipeManager.GetRecipesForItem(resultItemId);
        if (altRecipes.Count > 1 && ImGui.BeginMenu("Change Job..."))
        {
            if (row.IsOriginalRecipe)
            {
                foreach (var alt in altRecipes)
                {
                    var altJob = GetCraftingJobName(alt.CraftType.RowId);
                    var isCurrent = alt.RowId == row.Recipe.RowId;
                    if (ImGui.MenuItem(altJob, string.Empty, isCurrent) && !isCurrent)
                    {
                        var listItem = _list.Recipes.FirstOrDefault(recipeItem => recipeItem.RecipeId == row.Recipe.RowId);
                        if (listItem != null)
                        {
                            listItem.RecipeId = alt.RowId;
                            GatherBuddy.CraftingListManager.SaveList(_list);
                            InvalidateQueueCache();
                            InvalidateMaterialCaches();
                            InvalidatePresentationCaches();
                            TriggerQueueRegeneration();
                            TriggerMaterialsRegeneration();
                        }
                    }
                }
            }
            else
            {
                var activeRecipeId = _list.PrecraftRecipeOverrides.TryGetValue(resultItemId, out var overrideRecipeId)
                    ? overrideRecipeId
                    : altRecipes[0].RowId;
                foreach (var alt in altRecipes)
                {
                    var altJob = GetCraftingJobName(alt.CraftType.RowId);
                    var isCurrent = alt.RowId == activeRecipeId;
                    if (ImGui.MenuItem(altJob, string.Empty, isCurrent) && !isCurrent)
                    {
                        _list.PrecraftRecipeOverrides[resultItemId] = alt.RowId;
                        GatherBuddy.CraftingListManager.SaveList(_list);
                        InvalidateQueueCache();
                        InvalidateMaterialCaches();
                        InvalidatePresentationCaches();
                        TriggerQueueRegeneration();
                        TriggerMaterialsRegeneration();
                    }
                }
                if (_list.PrecraftRecipeOverrides.ContainsKey(resultItemId))
                {
                    ImGui.Separator();
                    if (ImGui.MenuItem("Reset to Default"))
                    {
                        _list.PrecraftRecipeOverrides.Remove(resultItemId);
                        GatherBuddy.CraftingListManager.SaveList(_list);
                        InvalidateQueueCache();
                        InvalidateMaterialCaches();
                        InvalidatePresentationCaches();
                        TriggerQueueRegeneration();
                        TriggerMaterialsRegeneration();
                    }
                }
            }
            ImGui.EndMenu();
        }

        ImGui.Separator();

        var recipeOptions = planningList.GetRecipeOptions(row.Recipe.RowId, row.IsOriginalRecipe);
        if (row.Recipe.CanQuickSynth)
        {
            using (ImRaii.Disabled(row.ForceQuickSynth))
            {
                if (ImGui.MenuItem("Quick Synthesis", "", row.EffectiveQuickSynth))
                {
                    _list.SetRecipeQuickSynth(row.Recipe.RowId, !recipeOptions.NQOnly, row.IsOriginalRecipe);
                    GatherBuddy.CraftingListManager.SaveList(_list);
                    InvalidateQueueCache();
                    InvalidateMaterialCaches();
                    InvalidatePresentationCaches();
                    TriggerQueueRegeneration();
                    TriggerMaterialsRegeneration();
                }
            }
            if (ImGui.IsItemHovered(row.ForceQuickSynth ? ImGuiHoveredFlags.AllowWhenDisabled : ImGuiHoveredFlags.None))
                ImGui.SetTooltip(row.ForceQuickSynth
                    ? "Forced on by Quick Synth All for this recipe. Disable the list-level override to edit the per-item quick synth setting."
                    : "Use quick synthesis for this recipe (NQ only)");
        }
        else
        {
            ImGui.TextDisabled("Quick Synthesis not available");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Recipe must be unlocked and previously crafted to use Quick Synthesis");
        }

        ImGui.EndPopup();

        ImGui.SameLine(0, innerSpacing);
        CraftingRowIcons.DrawIconsRightAligned(new[] { crafterIcon });
    }

    private IReadOnlyList<RecipeDisplayRow> GetRecipeDisplayRows()
    {
        if (_cachedRecipeDisplayRowsValid && _cachedRecipeDisplayRows != null)
            return _cachedRecipeDisplayRows;

        try
        {
            _cachedRecipeDisplayRows = BuildRecipeDisplayRows();
            _cachedRecipeDisplayRowsValid = true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingListEditor] Failed to rebuild recipe display cache for list '{_list.Name}': {ex.Message}");
            _cachedRecipeDisplayRows = new List<RecipeDisplayRow>();
            _cachedRecipeDisplayRowsValid = true;
        }

        return _cachedRecipeDisplayRows;
    }

    private List<RecipeDisplayRow> BuildRecipeDisplayRows()
    {
        var planningList = GetPlanningList();
        var rows = new List<RecipeDisplayRow>(_list.Recipes.Count);
        for (var i = 0; i < _list.Recipes.Count; i++)
        {
            var item = _list.Recipes[i];
            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe == null)
                continue;

            var itemName = recipe.Value.ItemResult.Value.Name.ExtractText();
            var jobName = GetCraftingJobName(recipe.Value.CraftType.RowId);
            var effectiveCraftSettings = GetEffectiveCraftSettings(item.RecipeId, true);
            var effectiveQuickSynth = IsEffectivelyQuickSynth(recipe.Value, item.RecipeId, true);
            var forcePreferNQNoQuickSynth = !recipe.Value.CanQuickSynth && planningList.ShouldForcePreferNQ(true);
            var hasExecutionContext = CraftingContextResolver.TryResolveListExecutionContext(
                planningList,
                item,
                out var executionContext);
            var usesQuickSynth = hasExecutionContext
                ? executionContext.UseQuickSynthesis
                : WillUseQuickSynth(recipe.Value, item.RecipeId, true);
            var validation = usesQuickSynth
                ? null
                : MacroValidator.GetOrCompute(item.RecipeId,
                    ResolveEffectiveMacroId(effectiveCraftSettings, false),
                    effectiveCraftSettings,
                    planningList.Consumables);
            RaphaelAssessment? raphaelAssessment = null;
            if (hasExecutionContext
             && CraftingContextResolver.UsesSolverAssessment(executionContext)
             && RaphaelAssessmentService.TryAssessListRecipe(item.RecipeId, planningList, effectiveCraftSettings, out var resolvedAssessment))
                raphaelAssessment = resolvedAssessment;
            rows.Add(new RecipeDisplayRow
            {
                ListIndex = i,
                Recipe = recipe.Value,
                ItemName = itemName,
                Label = $"{(effectiveQuickSynth ? "[QS] " : forcePreferNQNoQuickSynth ? "[NQ] " : string.Empty)}{(item.CraftSettings?.HasAnySettings() == true ? "[SET] " : string.Empty)}{(effectiveCraftSettings?.IngredientPreferences.Count > 0 ? "[HQ] " : string.Empty)}{(item.Options.Skipping ? "[SKIP] " : string.Empty)}{itemName} ({jobName})##recipe_{i}",
                TextColor = item.Options.Skipping
                    ? new Vector4(0.7f, 0.7f, 0.7f, 1f)
                    : effectiveQuickSynth
                        ? new Vector4(0.3f, 0.9f, 0.9f, 1f)
                        : new Vector4(1f, 1f, 1f, 1f),
                Validation = validation,
                RaphaelAssessment = raphaelAssessment,
            });
        }

        return rows;
    }

    private void DrawRecipeListRow(RecipeDisplayRow row, List<int> indicesToRemove)
    {
        if (row.ListIndex >= _list.Recipes.Count)
            return;

        var item = _list.Recipes[row.ListIndex];
        if (row.RaphaelAssessment != null)
        {
            ImGui.AlignTextToFramePadding();
            DrawRaphaelAssessmentMarker(row.RaphaelAssessment);
        }
        if (row.Validation != null)
        {
            ImGui.AlignTextToFramePadding();
            DrawValidationMarker(row.Validation);
        }

        var isSelected = _selectedRecipeIndices.Contains(row.ListIndex);
        const float qtyTextWidth = 50f;
        var innerSpacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var frameHeight = ImGui.GetFrameHeight();
        var crafterIconSize = frameHeight;
        var rowStartY = ImGui.GetCursorPosY();
        var qtyTotalWidth = qtyTextWidth + 2 * (frameHeight + innerSpacing);
        var iconBtnSize = new Vector2(frameHeight, frameHeight);
        var selectableWidth = Math.Max(50f, ImGui.GetContentRegionAvail().X - qtyTotalWidth - 3 * frameHeight - 4 * innerSpacing - crafterIconSize - innerSpacing);
        var crafterIcon = CraftingRowIcons.GetCrafterIcon(row.Recipe);
        ImGui.PushStyleColor(ImGuiCol.Text, row.TextColor);
        using var selectableAlign = ImRaii.PushStyle(ImGuiStyleVar.SelectableTextAlign, new Vector2(0f, 0.5f));
        var clicked = ImGui.Selectable(row.Label, isSelected, ImGuiSelectableFlags.None, new Vector2(selectableWidth, frameHeight));
        ImGui.PopStyleColor();

        if (clicked)
        {
            if (ImGui.GetIO().KeyShift && _lastClickedRecipeIndex >= 0)
            {
                if (!ImGui.GetIO().KeyCtrl)
                    _selectedRecipeIndices.Clear();
                var min = Math.Min(_lastClickedRecipeIndex, row.ListIndex);
                var max = Math.Max(_lastClickedRecipeIndex, row.ListIndex);
                for (var i = min; i <= max; i++)
                    _selectedRecipeIndices.Add(i);
            }
            else if (ImGui.GetIO().KeyCtrl)
            {
                if (!_selectedRecipeIndices.Remove(row.ListIndex))
                    _selectedRecipeIndices.Add(row.ListIndex);
                _lastClickedRecipeIndex = row.ListIndex;
            }
            else
            {
                _selectedRecipeIndices.Clear();
                _selectedRecipeIndices.Add(row.ListIndex);
                _lastClickedRecipeIndex = row.ListIndex;
            }
        }

        ImGui.SameLine(0, innerSpacing);
        ImGui.SetCursorPosY(rowStartY);
        CraftingRowIcons.DrawIconsRightAligned(new[] { crafterIcon }, crafterIconSize);

        ImGui.SameLine(0, innerSpacing);
        ImGui.SetCursorPosY(rowStartY);
        var qty = item.Quantity;
        var qtyStep = ImGui.GetIO().KeyShift ? 100 : ImGui.GetIO().KeyCtrl ? 10 : 1;
        ImGui.SetNextItemWidth(qtyTotalWidth);
        if (ImGui.InputInt($"##qty_{row.ListIndex}", ref qty, qtyStep, qtyStep))
        {
            qty = Math.Max(1, qty);
            if (qty != item.Quantity)
            {
                _list.UpdateRecipeQuantity(item.RecipeId, qty);
                GatherBuddy.CraftingListManager.SaveList(_list);
                InvalidateQueueCache();
                InvalidateMaterialCaches();
                InvalidatePresentationCaches();
                TriggerQueueRegeneration();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Click +/- to adjust quantity by 1.\nHold Ctrl: ±10\nHold Shift: ±100");

        ImGui.SameLine(0, innerSpacing);
        if (ImGuiUtil.DrawDisabledButton(
                FontAwesomeIcon.Cog.ToIconString() + $"##craft_settings_{row.ListIndex}",
                iconBtnSize,
                "Craft settings for this recipe.",
                false,
                true))
        {
            _craftSettingsPopup.OpenForListItem(item, _list, row.ItemName);
        }

        ImGui.SameLine(0, innerSpacing);
        var skipIcon = item.Options.Skipping ? FontAwesomeIcon.Check : FontAwesomeIcon.Ban;
        var skipTooltip = item.Options.Skipping ? "Re-enable this recipe in the queue." : "Skip this recipe in the queue.";
        if (ImGuiUtil.DrawDisabledButton(skipIcon.ToIconString() + $"##skip_{row.ListIndex}", iconBtnSize, skipTooltip, false, true))
        {
            item.Options.Skipping = !item.Options.Skipping;
            GatherBuddy.CraftingListManager.SaveList(_list);
            InvalidateQueueCache();
            InvalidateMaterialCaches();
            InvalidatePresentationCaches();
            TriggerQueueRegeneration();
        }

        ImGui.SameLine(0, innerSpacing);
        if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Trash.ToIconString() + $"##remove_{row.ListIndex}", iconBtnSize, "Remove this recipe from the list.", false, true))
            indicesToRemove.Add(row.ListIndex);

        var isPopupOpen = GatherBuddy.ControllerSupport != null
            ? GatherBuddy.ControllerSupport.ContextMenu.BeginPopupContextItemWithGamepad($"context_{row.ListIndex}", Dalamud.GamepadState)
            : ImGui.BeginPopupContextItem($"context_{row.ListIndex}");

        if (isPopupOpen)
        {
            if (ImGui.MenuItem("Craft Settings..."))
                _craftSettingsPopup.OpenForListItem(item, _list, row.ItemName);

            var resultItemId = row.Recipe.ItemResult.RowId;
            var altRecipes = RecipeManager.GetRecipesForItem(resultItemId);
            if (altRecipes.Count > 1 && ImGui.BeginMenu("Change Job..."))
            {
                foreach (var alt in altRecipes)
                {
                    var altJob = GetCraftingJobName(alt.CraftType.RowId);
                    var isCurrent = alt.RowId == item.RecipeId;
                    if (ImGui.MenuItem(altJob, string.Empty, isCurrent) && !isCurrent)
                    {
                        item.RecipeId = alt.RowId;
                        GatherBuddy.CraftingListManager.SaveList(_list);
                        InvalidateQueueCache();
                        InvalidateMaterialCaches();
                        InvalidatePresentationCaches();
                        TriggerQueueRegeneration();
                        TriggerMaterialsRegeneration();
                    }
                }
                ImGui.EndMenu();
            }

            ImGui.EndPopup();
        }
    }

    private void BulkSetSkipping(bool skipping)
    {
        var changed = false;
        foreach (var idx in _selectedRecipeIndices)
        {
            if (idx < 0 || idx >= _list.Recipes.Count)
                continue;
            var recipe = _list.Recipes[idx];
            if (recipe.Options.Skipping == skipping)
                continue;
            recipe.Options.Skipping = skipping;
            changed = true;
        }
        if (!changed)
            return;
        GatherBuddy.CraftingListManager.SaveList(_list);
        InvalidateQueueCache();
        InvalidateMaterialCaches();
        InvalidatePresentationCaches();
        TriggerQueueRegeneration();
    }

    private static void DrawValidationMarker(MacroValidationResult validation)
    {
        var dotColor = validation.IsValid
            ? new Vector4(0.30f, 0.70f, 0.30f, 1f)
            : (validation.Failure is MacroValidationFailure.InsufficientProgress or MacroValidationFailure.ActionUnusable
                ? new Vector4(0.78f, 0.62f, 0.15f, 1f)
                : new Vector4(0.78f, 0.25f, 0.25f, 1f));
        ImGui.TextColored(dotColor, "\u25cf");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(validation.IsValid
                ? $"Macro: PASS\nProgress: {validation.FinalProgress}/{validation.RequiredProgress}\nQuality: {validation.FinalQuality}\nDurability: {validation.FinalDurability}"
                : $"Macro: {validation.Failure} at step {validation.FailedAtStep}\nProgress: {validation.FinalProgress}/{validation.RequiredProgress}");
        ImGui.SameLine();
    }

    private static void DrawRaphaelAssessmentMarker(RaphaelAssessment assessment)
    {
        var dotColor = assessment.State switch
        {
            RaphaelAssessmentState.Ready when assessment.Outcome is RaphaelAssessmentOutcome.FullQuality
                or RaphaelAssessmentOutcome.CollectibleTier3
                or RaphaelAssessmentOutcome.MinimumQualityMet
                or RaphaelAssessmentOutcome.NoQualityRequired
                => new Vector4(0.30f, 0.70f, 0.30f, 1f),
            RaphaelAssessmentState.Ready => new Vector4(0.78f, 0.62f, 0.15f, 1f),
            RaphaelAssessmentState.Generating => new Vector4(0.35f, 0.65f, 0.90f, 1f),
            RaphaelAssessmentState.Failed => new Vector4(0.78f, 0.25f, 0.25f, 1f),
            RaphaelAssessmentState.Unavailable => new Vector4(0.78f, 0.62f, 0.15f, 1f),
            _ => new Vector4(0.55f, 0.55f, 0.55f, 1f),
        };
        ImGui.TextColored(dotColor, "\u25cf");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{assessment.SolverName}: {assessment.Summary}\n{assessment.Details}");
        ImGui.SameLine();
    }

    private string ComputeListHash()
    {
        var activeExecutionPlan = GetActiveExecutionPlan();
        var planningList = activeExecutionPlan?.PlanningSnapshot ?? _list;
        var hashParts = new List<string>();
        hashParts.Add($"SkipIfEnough:{planningList.SkipIfEnough}");
        hashParts.Add($"SkipFinalIfEnough:{planningList.SkipFinalIfEnough}");
        hashParts.Add($"RetainerRestock:{planningList.RetainerRestock}");
        hashParts.Add($"PreferBestClass:{planningList.PreferBestClassForMultiRecipeItems}");
        foreach (var item in planningList.Recipes)
        {
            hashParts.Add($"{item.RecipeId}:{item.Quantity}:{item.Options.Skipping}");
        }
        if (activeExecutionPlan != null)
            hashParts.Add($"ExecutionPlanVersion:{activeExecutionPlan.Version}");
        return string.Join("|", hashParts);
    }

    private void InvalidateMaterialCaches()
    {
        Volatile.Write(ref _materialCache, null);
        Interlocked.Increment(ref _materialGenerationVersion);
        Interlocked.Increment(ref _materialCacheVersion);
    }
    private QueueCacheSnapshot BuildQueueCacheSnapshot(CraftingListPlan plan, string hash)
        => new()
        {
            Hash = hash,
            SortedQueue = BuildDisplayQueue(plan),
        };

    private MaterialCacheSnapshot BuildMaterialCacheSnapshot(CraftingListDefinition planningList, string hash)
        => BuildMaterialCacheSnapshot(
            planningList.CreatePlan(ShouldUseRetainerCraftablePlanning(planningList)),
            planningList,
            hash);

    private MaterialCacheSnapshot BuildMaterialCacheSnapshot(CraftingListPlan plan, CraftingListDefinition planningList, string hash)
    {
        var precraftMaterials = BuildCraftPanelMaterials(plan);
        var displayPlan = BuildDisplayMaterialPlan(planningList);
        var displayPrecraftMaterials = BuildCraftPanelMaterials(displayPlan);
        return new MaterialCacheSnapshot
        {
            Hash = hash,
            Materials = new Dictionary<uint, int>(plan.Materials),
            PrecraftMaterials = precraftMaterials,
            IngredientDemands = new Dictionary<uint, IngredientQualityDemand>(plan.IngredientDemands),
            CraftMaterialDemands = BuildCraftPanelDemands(plan, precraftMaterials),
            DisplayMaterials = new Dictionary<uint, int>(displayPlan.Materials),
            DisplayPrecraftMaterials = displayPrecraftMaterials,
            DisplayIngredientDemands = new Dictionary<uint, IngredientQualityDemand>(displayPlan.IngredientDemands),
            DisplayCraftMaterialDemands = BuildCraftPanelDemands(displayPlan, displayPrecraftMaterials),
            DisplayCraftMaterialFinalRoots = new CraftingMaterialFinalRoots(displayPlan.CraftMaterialRoots.ToArray()),
        };
    }


    private static CraftingListPlan BuildDisplayMaterialPlan(CraftingListDefinition planningList)
        => CraftingListPlanner.Build(planningList, new CraftingListPlannerOptions(
            UseRetainerCraftableAvailability: ShouldUseRetainerCraftablePlanning(planningList),
            ConsumeIntermediateAvailability: true,
            ConsumeFinalAvailability: true));

    private static bool ShouldUseRetainerCraftablePlanning(CraftingListDefinition planningList)
        => planningList.SkipIfEnough && planningList.RetainerRestock && AllaganTools.Enabled;

    private MaterialCacheSnapshot EnsureMaterialCache(string hash)
    {
        if (TryCacheActiveExecutionPlan(hash))
            return GetMaterialCache()!;

        var materialCache = GetMaterialCache();
        if (materialCache != null && materialCache.Hash == hash)
            return materialCache;

        var planningSnapshot = CreatePlanningSnapshot();
        materialCache = BuildMaterialCacheSnapshot(planningSnapshot, hash);
        PublishMaterialCache(materialCache);
        return materialCache;
    }

    private Dictionary<uint, int> BuildCraftPanelMaterials(CraftingListPlan plan)
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        var craftPanelMaterials = new Dictionary<uint, int>();

        foreach (var recipeItem in plan.Recipes.Where(item => !item.IsOriginalRecipe))
        {
            var recipe = RecipeManager.GetRecipe(recipeItem.RecipeId);
            if (!recipe.HasValue)
                continue;
            var itemId = recipe.Value.ItemResult.RowId;
            var quantity = plan.Precrafts.GetValueOrDefault(itemId);
            if (quantity <= 0)
                continue;

            if (itemSheet != null && itemSheet.TryGetRow(itemId, out var item) && IsEquippableCraftPanelItem(item))
                continue;

            craftPanelMaterials[itemId] = craftPanelMaterials.GetValueOrDefault(itemId) + quantity;
        }

        return craftPanelMaterials;
    }

    private static bool IsEquippableCraftPanelItem(Item item)
        => item.RowId > 0 && item.EquipSlotCategory.RowId > 0;

    private static Dictionary<uint, IngredientQualityDemand> BuildCraftPanelDemands(
        CraftingListPlan plan,
        IReadOnlyDictionary<uint, int> craftPanelMaterials)
        => plan.IngredientDemands
            .Where(kvp => craftPanelMaterials.ContainsKey(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    
    private void TriggerQueueRegeneration()
    {
        var currentHash = ComputeListHash();
        var queueCache = GetQueueCache();
        if (queueCache != null && queueCache.Hash == currentHash)
            return;

        var generation = Interlocked.Increment(ref _queueGenerationVersion);

        if (TryCacheActiveExecutionPlan(currentHash))
        {
            _queueCancellationSource?.Cancel();
            _queueCancellationSource?.Dispose();
            _queueCancellationSource = null;
            _queueGenerationTask = null;
            _isGeneratingQueue = false;
            return;
        }

        var planningSnapshot = CreatePlanningSnapshot();
        
        _queueCancellationSource?.Cancel();
        _queueCancellationSource?.Dispose();
        _queueCancellationSource = new CancellationTokenSource();
        
        _isGeneratingQueue = true;
        var token = _queueCancellationSource.Token;
        var hash = currentHash;
        
        _queueGenerationTask = Task.Run(() =>
        {
            try
            {
                if (token.IsCancellationRequested) return;

                var queueCacheSnapshot = BuildQueueCacheSnapshot(
                    planningSnapshot.CreatePlan(ShouldUseRetainerCraftablePlanning(planningSnapshot)),
                    hash);
                TryPublishQueueCache(queueCacheSnapshot, generation, token);
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Error($"Error generating queue: {ex.Message}");
            }
            finally
            {
                if (generation == Volatile.Read(ref _queueGenerationVersion))
                    _isGeneratingQueue = false;
            }
        }, token);
    }

    internal void TriggerMaterialsRegeneration()
    {
        ProcessPendingInventoryChanges();
        var currentHash = ComputeListHash();
        var materialCache = GetMaterialCache();
        if (materialCache != null && materialCache.Hash == currentHash)
            return;

        var generation = Interlocked.Increment(ref _materialGenerationVersion);

        if (TryCacheActiveExecutionPlan(currentHash))
        {
            _materialsCancellationSource?.Cancel();
            _materialsCancellationSource?.Dispose();
            _materialsCancellationSource = null;
            _materialsGenerationTask = null;
            _isGeneratingMaterials = false;
            return;
        }

        var planningSnapshot = CreatePlanningSnapshot();
        
        _materialsCancellationSource?.Cancel();
        _materialsCancellationSource?.Dispose();
        _materialsCancellationSource = new CancellationTokenSource();
        
        _isGeneratingMaterials = true;
        var token = _materialsCancellationSource.Token;
        var hash = currentHash;
        
        _materialsGenerationTask = Task.Run(() =>
        {
            try
            {
                if (token.IsCancellationRequested) return;
                var materialCacheSnapshot = BuildMaterialCacheSnapshot(planningSnapshot, hash);
                TryPublishMaterialCache(materialCacheSnapshot, generation, token);
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Error($"Error generating materials: {ex.Message}");
            }
            finally
            {
                if (generation == Volatile.Read(ref _materialGenerationVersion))
                    _isGeneratingMaterials = false;
            }
        }, token);
    }
    
    private List<CraftingListItem> GetSortedQueue()
    {
        ProcessPendingInventoryChanges();
        var queueCache = GetQueueCache();
        if (queueCache != null)
            return queueCache.SortedQueue;
        return new List<CraftingListItem>();
    }
    

    private List<CraftingListItem> BuildDisplayQueue(CraftingListPlan plan)
        => CraftingListQueueBuilder.CreateGroupedQueue(plan);

    private RecipeCraftSettings? GetEffectiveCraftSettings(uint recipeId, bool isOriginalRecipe)
    {
        var planningList = GetPlanningList();
        var sourceSettings = isOriginalRecipe
            ? planningList.Recipes.FirstOrDefault(r => r.RecipeId == recipeId)?.CraftSettings
            : planningList.PrecraftCraftSettings.GetValueOrDefault(recipeId);
        var recipe = RecipeManager.GetRecipe(recipeId);
        if (!recipe.HasValue)
            return sourceSettings?.Clone();

        var forcePreferNQ = !recipe.Value.CanQuickSynth && planningList.ShouldForcePreferNQ(isOriginalRecipe);
        return CraftingQualityPolicyResolver.BuildEffectiveSettings(recipe.Value, sourceSettings, planningList.UseAllHQ, forcePreferNQ);
    }

    private bool IsEffectivelyQuickSynth(Recipe recipe, uint recipeId, bool isOriginalRecipe)
    {
        var planningList = GetPlanningList();
        var recipeOptions = planningList.GetRecipeOptions(recipeId, isOriginalRecipe);
        return recipeOptions.NQOnly || planningList.ShouldForceQuickSynth(recipe, isOriginalRecipe);
    }

    private bool WillUseQuickSynth(Recipe recipe, uint recipeId, bool isOriginalRecipe)
        => IsEffectivelyQuickSynth(recipe, recipeId, isOriginalRecipe) && recipe.CanQuickSynth && HasRecipeCraftedBefore(recipe);

    private static bool HasRecipeCraftedBefore(Recipe recipe)
    {
        if (recipe.SecretRecipeBook.RowId > 0)
            return true;

        return FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsRecipeComplete(recipe.RowId);
    }
    
    internal Dictionary<uint, int> GetCachedMaterials()
    {
        ProcessPendingInventoryChanges();
        var materialCache = EnsureMaterialCache(ComputeListHash());
        return materialCache.Materials;
    }

    internal Dictionary<uint, int> GetCachedPrecraftMaterials()
    {
        ProcessPendingInventoryChanges();
        var materialCache = EnsureMaterialCache(ComputeListHash());
        return materialCache.PrecraftMaterials;
    }

    internal Dictionary<uint, int> GetDisplayMaterials()
    {
        ProcessPendingInventoryChanges();
        var materialCache = EnsureMaterialCache(ComputeListHash());
        return materialCache.DisplayMaterials;
    }

    internal Dictionary<uint, int> GetDisplayPrecraftMaterials()
    {
        ProcessPendingInventoryChanges();
        var materialCache = EnsureMaterialCache(ComputeListHash());
        return materialCache.DisplayPrecraftMaterials;
    }

    internal CraftingMaterialFinalRoots GetDisplayCraftMaterialFinalRoots()
    {
        ProcessPendingInventoryChanges();
        var materialCache = EnsureMaterialCache(ComputeListHash());
        return materialCache.DisplayCraftMaterialFinalRoots;
    }

    internal IReadOnlyDictionary<uint, IngredientQualityDemand> GetCachedIngredientDemands()
    {
        ProcessPendingInventoryChanges();
        var materialCache = EnsureMaterialCache(ComputeListHash());
        return materialCache.IngredientDemands;
    }

    internal IReadOnlyDictionary<uint, IngredientQualityDemand> GetDisplayIngredientDemands()
    {
        ProcessPendingInventoryChanges();
        var materialCache = EnsureMaterialCache(ComputeListHash());
        return materialCache.DisplayIngredientDemands;
    }

    private static string GetConsumableSummary(CraftingListConsumableSettings settings)
    {
        var parts = new List<string>();

        if (settings.FoodItemId.HasValue)
            parts.Add($"Food: {GetItemLabel(settings.FoodItemId.Value, settings.FoodHQ)}");
        if (settings.MedicineItemId.HasValue)
            parts.Add($"Medicine: {GetItemLabel(settings.MedicineItemId.Value, settings.MedicineHQ)}");
        if (settings.ManualItemId.HasValue)
            parts.Add($"Manual: {GetItemLabel(settings.ManualItemId.Value, false)}");
        if (settings.SquadronManualItemId.HasValue)
            parts.Add($"Squadron: {GetItemLabel(settings.SquadronManualItemId.Value, false)}");

        return parts.Count > 0 ? string.Join(" | ", parts) : "None";
    }

    private static string GetItemLabel(uint itemId, bool hq)
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet != null && itemSheet.TryGetRow(itemId, out var item))
            return item.Name.ExtractText() + (hq ? " HQ" : "");
        return itemId.ToString();
    }
    
    internal int GetInventoryCount(uint itemId)
    {
        var (nqCount, hqCount) = GetInventorySplitCounts(itemId);
        return nqCount + hqCount;
    }

    internal (int NQ, int HQ) GetInventorySplitCounts(uint itemId)
    {
        var now = DateTime.Now;
        
        if (_inventoryRefreshTimes.TryGetValue(itemId, out var lastRefresh))
        {
            if ((now - lastRefresh).TotalSeconds < InventoryRefreshIntervalSeconds
             && _cachedInventorySplitCounts.TryGetValue(itemId, out var cachedCounts))
                return cachedCounts;
        }
        
        try
        {
            var counts = CraftingInventoryCounter.GetInventorySplitCounts(itemId);
            _cachedInventorySplitCounts[itemId] = counts;
            _inventoryRefreshTimes[itemId] = now;
            return counts;
        }
        catch
        {
            return (0, 0);
        }
    }

    internal int GetRetainerCount(uint itemId)
        => RetainerItemQuery.GetTotalCount(itemId);
    internal void InvalidateRetainerSnapshot()
    {
        _cachedRetainerSnapshot = RetainerItemSnapshot.Empty;
        _cachedRetainerSnapshotItemIds = [];
        _cachedRetainerSnapshotAt = DateTime.MinValue;
        Interlocked.Increment(ref _materialCacheVersion);
    }

    internal RetainerItemSnapshot GetRetainerSnapshot(IEnumerable<uint> itemIds, bool forceRefresh = false)
    {
        if (!AllaganTools.Enabled)
            return RetainerItemSnapshot.Empty;

        var snapshotItemIds = itemIds
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        if (snapshotItemIds.Length == 0)
            return RetainerItemSnapshot.Empty;

        if (!forceRefresh && _cachedRetainerSnapshotItemIds.SequenceEqual(snapshotItemIds))
        {
            if (_cachedRetainerSnapshot.IsComplete)
                return _cachedRetainerSnapshot;

            if ((DateTime.Now - _cachedRetainerSnapshotAt).TotalSeconds < RetainerSnapshotRetryIntervalSeconds)
                return _cachedRetainerSnapshot;
        }

        _cachedRetainerSnapshot = RetainerItemQuery.CreateSnapshot(snapshotItemIds);
        _cachedRetainerSnapshotItemIds = snapshotItemIds;
        _cachedRetainerSnapshotAt = DateTime.Now;
        return _cachedRetainerSnapshot;
    }

    internal int GetQualityAwareAvailableCount(uint itemId, int retNQ, int retHQ, bool countRetainersTowardNeed)
        => GetQualityAwareAvailableCount(itemId, GetIngredientDemand(itemId), retNQ, retHQ, countRetainersTowardNeed);

    internal int GetCraftMaterialAvailableCount(uint itemId, int retNQ, int retHQ, bool countRetainersTowardNeed)
    {
        var materialCache = GetMaterialCache();
        var demand = materialCache != null && materialCache.CraftMaterialDemands.TryGetValue(itemId, out var craftDemand)
            ? craftDemand
            : GetIngredientDemand(itemId);
        return GetQualityAwareAvailableCount(itemId, demand, retNQ, retHQ, countRetainersTowardNeed);
    }

    internal int GetDisplayMaterialAvailableCount(uint itemId, int retNQ, int retHQ, bool countRetainersTowardNeed)
        => GetQualityAwareAvailableCount(itemId, GetDisplayIngredientDemand(itemId), retNQ, retHQ, countRetainersTowardNeed);

    internal int GetDisplayCraftMaterialAvailableCount(uint itemId, int retNQ, int retHQ, bool countRetainersTowardNeed)
    {
        var materialCache = GetMaterialCache();
        var demand = materialCache != null && materialCache.DisplayCraftMaterialDemands.TryGetValue(itemId, out var craftDemand)
            ? craftDemand
            : GetDisplayIngredientDemand(itemId);
        return GetQualityAwareAvailableCount(itemId, demand, retNQ, retHQ, countRetainersTowardNeed);
    }

    private int GetQualityAwareAvailableCount(uint itemId, IngredientQualityDemand demand, int retNQ, int retHQ, bool countRetainersTowardNeed)
    {
        var (inventoryNQ, inventoryHQ) = GetInventorySplitCounts(itemId);
        var availableNQ = inventoryNQ + (countRetainersTowardNeed ? retNQ : 0);
        var availableHQ = inventoryHQ + (countRetainersTowardNeed ? retHQ : 0);
        if (demand.Total <= 0)
            return availableNQ + availableHQ;

        var remaining = demand.ConsumeSplit(availableNQ, availableHQ, out _, out _);
        return demand.Total - remaining.Total;
    }

    private IngredientQualityDemand GetIngredientDemand(uint itemId)
    {
        var ingredientDemands = GetCachedIngredientDemands();
        return ingredientDemands.TryGetValue(itemId, out var demand)
            ? demand
            : default;
    }

    private IngredientQualityDemand GetDisplayIngredientDemand(uint itemId)
    {
        var ingredientDemands = GetDisplayIngredientDemands();
        return ingredientDemands.TryGetValue(itemId, out var demand)
            ? demand
            : default;
    }
    

    private bool WillBeSkippedDueToInventory(Recipe recipe)
    {
        var demand = GetIngredientDemand(recipe.ItemResult.RowId);
        if (demand.Total <= 0)
            return false;

        var (nqCount, hqCount) = GetInventorySplitCounts(recipe.ItemResult.RowId);
        return demand.ConsumeSplit(nqCount, hqCount, out _, out _).Total == 0;
    }

    private void ProcessPrecraftWithDependencies(CraftingListItem recipeItem, List<CraftingListItem> allRecipes, HashSet<uint> processed, List<CraftingListItem> result)
    {
        if (processed.Contains(recipeItem.RecipeId))
            return;
        
        var recipe = RecipeManager.GetRecipe(recipeItem.RecipeId);
        if (recipe == null)
            return;
        
        var ingredients = RecipeManager.GetIngredients(recipe.Value);
        foreach (var (itemId, _) in ingredients)
        {
            var depItem = allRecipes.FirstOrDefault(candidate =>
            {
                if (candidate.IsOriginalRecipe)
                    return false;
                var candidateRecipe = RecipeManager.GetRecipe(candidate.RecipeId);
                return candidateRecipe.HasValue && candidateRecipe.Value.ItemResult.RowId == itemId;
            });
            if (depItem != null)
                ProcessPrecraftWithDependencies(depItem, allRecipes, processed, result);
        }
        
        processed.Add(recipeItem.RecipeId);
        result.Add(recipeItem);
    }
    
    private string? ResolveEffectiveMacroId(RecipeCraftSettings? settings, bool isPrecraft)
    {
        var planningList = GetPlanningList();
        var isSpecific = settings != null
            && (settings.MacroMode == MacroOverrideMode.Specific
                || (settings.MacroMode == MacroOverrideMode.Inherit
                    && (!string.IsNullOrEmpty(settings.SelectedMacroId) || settings.SolverOverride != SolverOverrideMode.Default)));
        if (isSpecific)
            return settings?.SolverOverride == SolverOverrideMode.Default ? settings?.SelectedMacroId : null;
        var defaultSolverOverride = isPrecraft ? planningList.DefaultPrecraftSolverOverride : planningList.DefaultFinalSolverOverride;
        if (defaultSolverOverride != SolverOverrideMode.Default)
            return null;
        return isPrecraft ? planningList.DefaultPrecraftMacroId : planningList.DefaultFinalMacroId;
    }

    private (int hardFails, int warnings) CountValidationIssues()
    {
        ProcessPendingInventoryChanges();

        var currentHash = ComputeListHash();
        if (_cachedValidationIssueCountsValid && _cachedValidationIssueCountsHash == currentHash)
            return _cachedValidationIssueCounts;

        var hardFails = 0;
        var warnings  = 0;

        foreach (var row in GetRecipeDisplayRows())
            AccumulateValidationIssue(row.Validation, ref hardFails, ref warnings);
        if (GetQueueCache() != null)
        {
            EnsureQueueDisplayRows(GetPlanningList(), GetActiveExecutionPlan());
            if (_cachedQueueDisplayRows != null)
            {
                foreach (var row in _cachedQueueDisplayRows)
                {
                    if (row.IsOriginalRecipe)
                        continue;

                    AccumulateValidationIssue(row.Validation, ref hardFails, ref warnings);
                }
            }

            _cachedValidationIssueCounts = (hardFails, warnings);
            _cachedValidationIssueCountsHash = currentHash;
            _cachedValidationIssueCountsValid = true;
        }

        return (hardFails, warnings);
    }

    private static void AccumulateValidationIssue(MacroValidationResult? validation, ref int hardFails, ref int warnings)
    {
        if (validation == null || validation.IsValid || validation.Failure == MacroValidationFailure.NoStats)
            return;

        if (validation.Failure is MacroValidationFailure.CPExhausted or MacroValidationFailure.DurabilityFailed)
            hardFails++;
        else
            warnings++;
    }

    private string GetCraftingJobName(uint craftTypeId)
    {
        var classJobSheet = Dalamud.GameData.GetExcelSheet<ClassJob>();
        if (classJobSheet != null)
        {
            var classJobId = craftTypeId + 8;
            var classJob = classJobSheet.GetRow(classJobId);
            if (classJob.RowId > 0)
                return classJob.Abbreviation.ExtractText();
        }
        return "Unknown";
    }
}
