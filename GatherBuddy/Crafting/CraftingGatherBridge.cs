using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.Crafting.Acquisition;
using GatherBuddy.FcMesh.Capabilities;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.State;
using GatherBuddy.Helpers;
using GatherBuddy.Interfaces;
using Lumina.Excel.Sheets;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Crafting;

public static class CraftingGatherBridge
{
    private static AutoGatherList? _gatherList;
    private static global::GatherBuddy.GatherBuddy? _plugin;
    private static uint _recipeIdToCraft = 0;
    private static bool _waitingForGatherComplete = false;
    private static DateTime _jobSwitchTime = DateTime.MinValue;
    private static bool _waitingForJobSwitch = false;
    private static CraftingQueueProcessor? _queueProcessor = null;
    private static CraftingExecutionPlan? _activeExecutionPlan = null;
    private static bool _isQueueMode = false;
    private static List<AutoGatherList> _disabledGatherLists = new();
    private static bool _autoGatherStateCaptured;
    private static bool _autoGatherWasEnabled;
    private static int? _ephemeralListId = null;
    private static bool _waitingForCollectables = false;
    private static bool _collectablesStartPending = false;
    private static DateTime _nextCollectablesRetry = DateTime.MinValue;
    private static DateTime _lastCollectablesWaitLog = DateTime.MinValue;
    private static DateTime _lastCollectablesExitAttempt = DateTime.MinValue;
    private static DateTime _lastCollectablesHardFailLog = DateTime.MinValue;
    private static bool _waitingForCollectablesHomeReturn = false;
    private static bool _collectablesHomeReturnStarted = false;
    private static Task _queueProcessorDrain = Task.CompletedTask;
    private static CraftingQueueProcessor? _queueProcessorPendingDispose;
    private static PendingQueueStart? _pendingQueueStart;
    private static CollectableManager? _collectableManager;
    private static CraftingAutomationOwner _activeAutomationOwner;
    private static bool _restoringPersistedCraft;
    private static Dictionary<uint, int> _restoredQueueCoverage = new();
    private static bool _startupRecoveryResolved;
    private static DateTime _startupRecoveryProbeStartedUtc;
    private static DateTime _nextStartupRecoveryAttemptUtc;
    private static FcGatherYieldBoundary? _fcGatherYieldBoundary;
    private static bool _fcGatherWasInProgress;
    private static uint[] _fcGatherTargetOrder = Array.Empty<uint>();

    public static event Action<GatherYieldObserved>? FcGatherYieldObserved;

    private sealed record PendingQueueStart(
        CraftingExecutionPlan ExecutionPlan,
        CraftingListConsumableSettings? ListConsumables,
        int? EphemeralListId,
        CraftingAutomationOwner Owner,
        bool RestoringPersistedCraft,
        uint[] FcGatherTargetOrder);
    
    public static bool PreserveListOnDisable { get; set; } = false;

    public static void Initialize(global::GatherBuddy.GatherBuddy plugin)
    {
        _plugin = plugin;
        _startupRecoveryResolved = false;
        _startupRecoveryProbeStartedUtc = DateTime.UtcNow;
        _nextStartupRecoveryAttemptUtc = DateTime.MinValue;
        CraftingGameInterop.CraftFinished += OnOwnedCraftFinished;
        Dalamud.GameInventory.InventoryChanged += OnInventoryChanged;
    }

    private static int RoundUpToBatchSize(int quantity, int batchSize)
    {
        if (quantity <= 0 || batchSize <= 1)
            return quantity;

        var batchCount = ((long)quantity + batchSize - 1) / batchSize;
        var rounded = checked(batchCount * batchSize);
        if (rounded > int.MaxValue)
            throw new InvalidOperationException("Gather target exceeds the supported quantity range.");
        return (int)rounded;
    }

    public static void BindCollectableManager(CollectableManager manager)
    {
        if (_collectableManager != null)
        {
            _collectableManager.OnFinishCollecting -= OnCollectablesFinished;
            _collectableManager.OnError -= OnCollectablesError;
        }
        manager.OnFinishCollecting -= OnCollectablesFinished;
        manager.OnError -= OnCollectablesError;
        manager.OnFinishCollecting += OnCollectablesFinished;
        manager.OnError += OnCollectablesError;
        _collectableManager = manager;
    }
    
    public static uint RecipeToCraft => _recipeIdToCraft;
    public static bool WaitingForGatherComplete => _waitingForGatherComplete;
    public static bool HasActiveQueue
        => _queueProcessor != null
            || _pendingQueueStart != null
            || !_queueProcessorDrain.IsCompleted;
    public static bool IsQueuePaused => _queueProcessor?.Paused == true;

    internal static bool TryGetActiveQueueFailure(out string reason)
    {
        if (_queueProcessor?.CurrentState == CraftingQueueProcessor.QueueState.Failed)
        {
            reason = _queueProcessor.PauseReason;
            return true;
        }

        reason = string.Empty;
        return false;
    }

    public static void PauseQueue(string? reason = null)
        => _queueProcessor?.Pause(reason);

    public static void ResumeQueue()
        => _queueProcessor?.Resume();
    
    public static AutoGatherList? GetTemporaryGatherList() => _gatherList;
    public static CraftingExecutionPlan? GetActiveExecutionPlan()
        => _activeExecutionPlan;

    public static CraftingExecutionPlan? GetActiveExecutionPlan(int listId)
        => _activeExecutionPlan != null && _activeExecutionPlan.MatchesList(listId)
            ? _activeExecutionPlan
            : null;

    private static void SetFcGatherTargetOrder(IReadOnlyList<uint>? itemOrder)
    {
        _fcGatherTargetOrder = itemOrder is null
            ? Array.Empty<uint>()
            : itemOrder
                .Where(itemId => itemId != 0)
                .Distinct()
                .ToArray();
        global::GatherBuddy.GatherBuddy.AutoGather?.SetFcGatherTargetOrder(_fcGatherTargetOrder);
    }

    private static void SetFcGatherIntentProvider(CraftingExecutionPlan? plan)
    {
        Func<IReadOnlyList<uint>?>? provider = null;
        if (plan?.ExecutionSource == ExecutionSource.FcFulfillment)
            provider = plan.GetCurrentFcGatherTargetOrder;
        global::GatherBuddy.GatherBuddy.AutoGather?.SetFcGatherIntentProvider(provider);
    }

    /// <summary>
    /// Invalidates market data before a stale-listing replan. A zero item ID
    /// refreshes every dependency in the active plan; a nonzero ID refreshes
    /// only that dependency. The scope matches the list's current-world
    /// setting, so the subsequent planner cannot reuse the failed snapshot.
    /// </summary>
    internal static void InvalidateAcquisitionMarketData(uint itemId = 0)
    {
        var plan = _activeExecutionPlan;
        var service = GatherBuddy.MarketboardService;
        if (plan == null || service == null)
            return;

        var scope = plan.CurrentWorldOnly
            ? service.GetCurrentWorld()
            : service.GetDataCenter();
        var itemIds = itemId != 0
            ? new[] { itemId }
            : plan.PrecraftsView.Keys
                .Concat(plan.MaterialsView.Keys)
                .Distinct()
                .ToArray();
        foreach (var dependencyItemId in itemIds)
        {
            if (dependencyItemId != 0)
                service.ForceRefresh(dependencyItemId, scope);
        }
    }
    
    public static void DeleteTemporaryGatherList()
    {
        SetFcGatherIntentProvider(null);
        if (_gatherList != null && _plugin != null)
        {
            try
            {
                _plugin.AutoGatherListsManager.DeleteList(_gatherList);
                GatherBuddy.Log.Debug($"[CraftingGatherBridge] Deleted temporary gather list: {_gatherList.Name}");
                _gatherList = null;
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to delete temporary gather list: {ex.Message}");
            }
        }
    }
    
    public static void CreatePersistentGatherList(string listName, Dictionary<uint, int> materials)
    {
        if (_plugin == null)
        {
            GatherBuddy.Log.Warning("[CraftingGatherBridge] Cannot create gather list: plugin not initialized");
            return;
        }

        try
        {
            var gatherList = BuildPersistentGatherList(listName, materials);

            if (gatherList.Items.Count > 0)
            {
                _plugin.AutoGatherListsManager.AddList(gatherList);
                _plugin.AutoGatherListsManager.SetActiveItems();
                GatherBuddy.Log.Information($"[CraftingGatherBridge] Created gather list '{listName}' with {gatherList.Items.Count} items.");
            }
            else
            {
                GatherBuddy.Log.Warning($"[CraftingGatherBridge] No gatherable items found for list '{listName}'.");
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingGatherBridge] Failed to create gather list '{listName}': {ex.Message}");
        }
    }

    internal static AutoGatherList BuildPersistentGatherList(
        string listName,
        IReadOnlyDictionary<uint, int> materials)
    {
        var gatherList = new AutoGatherList
        {
            Name = listName,
            Enabled = false,
            CompletionProvider = CreateGatherCompletionProvider(),
            CompletionScope = CreateGatherCompletionScope(),
        };

        foreach (var (itemId, quantity) in materials)
        {
            var gatherQuantity = GetCraftingGatherTargetQuantity(
                itemId,
                quantity,
                quantityIsDeficit: false,
                out _,
                out _);
            if (gatherQuantity <= 0)
                continue;

            if (!TryResolvePersistentGatherItem(itemId, out var gatherable, out var completionItemId))
            {
                GatherBuddy.Log.Debug($"[CraftingGatherBridge] Item {itemId} has no supported gather target, skipping");
                continue;
            }

            if (!gatherList.Add(gatherable, (uint)gatherQuantity, completionItemId))
                GatherBuddy.Log.Debug($"[CraftingGatherBridge] Gather target {gatherable.ItemId} is duplicated in '{listName}', skipping item {itemId}");
            else
                gatherList.SetCompletionQuality(gatherable, GetGatherCompletionQuality(completionItemId == 0 ? gatherable.ItemId : completionItemId));
        }

        return gatherList;
    }

    internal static bool TryResolvePersistentGatherItem(
        uint itemId,
        out IGatherable gatherable,
        out uint completionItemId)
    {
        ResolveCraftingGatherItemIds(itemId, out var gatherItemId, out completionItemId, out _);
        if (GatherBuddy.GameData.Gatherables.TryGetValue(gatherItemId, out var normalGatherable))
        {
            gatherable = normalGatherable;
            return true;
        }
        if (GatherBuddy.GameData.Fishes.TryGetValue(gatherItemId, out var fish))
        {
            gatherable = fish;
            return true;
        }

        gatherable = null!;
        completionItemId = 0;
        return false;
    }

    private static int GetCraftingGatherTargetQuantity(
        uint itemId,
        int quantity,
        bool quantityIsDeficit,
        out uint gatherItemId,
        out uint completionItemId)
    {
        if (quantity <= 0)
        {
            gatherItemId = itemId;
            completionItemId = 0;
            return 0;
        }

        ResolveCraftingGatherItemIds(itemId, out gatherItemId, out completionItemId, out var isApprovedItem);

        if (!isApprovedItem)
        {
            if (completionItemId != 0)
                return quantityIsDeficit
                    ? checked(GetCompletionCount(itemId) + quantity)
                    : quantity;

            return quantity;
        }
        var batchSize = AutoGather.Helpers.Diadem.ApprovedInspectionBatchSizes.TryGetValue(itemId, out var configuredBatchSize) && configuredBatchSize > 0
            ? configuredBatchSize > (uint)int.MaxValue
                ? throw new InvalidOperationException("Gather batch size exceeds the supported quantity range.")
                : (int)configuredBatchSize
            : 1;
        var representedDemand = BuildRepresentedMaterialDemand(itemId, quantity, quantityIsDeficit);
        return ComputeGatherTargetQuantityForSource(
            quantity,
            quantityIsDeficit,
            isApprovedItem: true,
            isFcFulfillment: _activeExecutionPlan?.ExecutionSource == ExecutionSource.FcFulfillment,
            currentCount: representedDemand?.CurrentRepresented ?? GetCompletionCount(itemId),
            batchSize);
    }

    internal static int ComputeGatherTargetQuantityForSource(
        int quantity,
        bool quantityIsDeficit,
        bool isApprovedItem,
        bool isFcFulfillment,
        int currentCount,
        int batchSize)
    {
        if (quantity <= 0)
            return 0;

        if (!isApprovedItem)
            return quantityIsDeficit
                ? checked(Math.Max(0, currentCount) + quantity)
                : quantity;

        if (isFcFulfillment)
            return quantityIsDeficit
                ? checked(Math.Max(0, currentCount) + quantity)
                : quantity;

        var approvedDeficit = Math.Max(0, quantity - Math.Max(0, currentCount));
        return approvedDeficit <= 0
            ? 0
            : RoundUpToBatchSize(approvedDeficit, batchSize);
    }

    private static void ResolveCraftingGatherItemIds(
        uint itemId,
        out uint gatherItemId,
        out uint completionItemId,
        out bool isApprovedItem)
    {
        gatherItemId = itemId;
        completionItemId = 0;
        isApprovedItem = AutoGather.Helpers.Diadem.ApprovedToRawItemIds.TryGetValue(itemId, out var rawItemId);
        if (isApprovedItem)
        {
            gatherItemId = rawItemId;
            return;
        }

        var reductionPath = AcquisitionPlanningInputBuilder.ResolvePath(itemId, null);
        if (reductionPath is
            {
                Kind: AcquisitionPathKind.Reduction,
                SourceItemId: not 0,
                Capability.Status: AcquisitionCapabilityStatus.Usable,
            })
        {
            gatherItemId = reductionPath.SourceItemId;
            completionItemId = itemId;
        }
    }
    
    public static void Update()
    {
        TryFinalizePendingProcessorDisposal();
        TryStartPersistedRecovery();

        if (_pendingQueueStart is { } pendingQueueStart)
        {
            if (!_queueProcessorDrain.IsCompleted)
                return;

            if (pendingQueueStart.ExecutionPlan.ExecutionSource == ExecutionSource.FcFulfillment
                && !(pendingQueueStart.RestoringPersistedCraft
                    && pendingQueueStart.ExecutionPlan.IsRecoveryPlan
                    && SynthesisReader.IsSynthesisWindowOpen())
                && !TryPreflightFcQueueAdmission(
                    pendingQueueStart.ExecutionPlan.ExecutionSource,
                    pendingQueueStart.ExecutionPlan.FcContext?.CapabilityProof,
                    global::GatherBuddy.GatherBuddy.FcCapabilities,
                    out var pendingCapabilityFailure))
            {
                GatherBuddy.Log.Warning(
                    $"[CraftingGatherBridge] Deferred FC queue entry remains blocked by capability proof: {pendingCapabilityFailure}");
                if (pendingQueueStart.RestoringPersistedCraft)
                {
                    _pendingQueueStart = null;
                    _startupRecoveryResolved = false;
                    _startupRecoveryProbeStartedUtc = DateTime.UtcNow;
                }
                return;
            }

            _pendingQueueStart = null;
            StartQueueCore(
                pendingQueueStart.ExecutionPlan,
                pendingQueueStart.ListConsumables,
                pendingQueueStart.EphemeralListId,
                pendingQueueStart.Owner,
                pendingQueueStart.RestoringPersistedCraft,
                pendingQueueStart.FcGatherTargetOrder);
        }

        if (_isQueueMode && _queueProcessor != null)
        {
            var processor = _queueProcessor;
            try
            {
                UpdateCollectablesHomeReturnBeforeResume();
                TryStartCollectablesInterruption();
                processor.Update();
                TrackFcGatherInteractionBoundary(processor);
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Error($"[CraftingGatherBridge] Queue update failed: {ex.Message}");
                processor.FailFromBridge($"Crafting queue update failed: {ex.Message}");
            }
            
            if (ReferenceEquals(_queueProcessor, processor)
                && (processor.CurrentState is CraftingQueueProcessor.QueueState.Complete or CraftingQueueProcessor.QueueState.Failed)
                && !processor.HasPendingTasks())
            {
                GatherBuddy.Log.Information("[CraftingGatherBridge] All completion tasks done, cleaning up");
                GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(null);
                var completedProcessor = processor;
                try
                {
                    completedProcessor.QueueCompleted -= OnQueueCompleted;
                    completedProcessor.Reset();
                }
                catch (Exception ex)
                {
                    GatherBuddy.Log.Warning($"[CraftingGatherBridge] Queue reset failed during cleanup: {ex.Message}");
                }
                QueueProcessorForDeferredDisposal(completedProcessor);
                RestoreQueueOwnedState();
                _activeExecutionPlan?.ClearFcIntentSnapshotProvider();
                _queueProcessor = null;
                _activeExecutionPlan = null;
                _isQueueMode = false;
                SetFcGatherTargetOrder(null);
                SetFcGatherIntentProvider(null);
                AbortFcGatherYieldBoundary();
                _waitingForGatherComplete = false;
                _waitingForJobSwitch = false;
                _jobSwitchTime = DateTime.MinValue;
                ClearRecoveryTicket();
                DeleteEphemeralCraftingListSafely();
            }
        }
        
        if (!_waitingForJobSwitch)
            return;
        
        var timeSinceSwitch = (DateTime.Now - _jobSwitchTime).TotalSeconds;
        if (timeSinceSwitch >= 2)
        {
            GatherBuddy.Log.Debug($"[CraftingGatherBridge] Job switch wait complete, retrying gather-to-craft");
            _waitingForJobSwitch = false;
            _jobSwitchTime = DateTime.MinValue;
            OnGatherComplete();
        }
    }
    
    public static void OnCraftFinished(Recipe? recipe, bool cancelled)
    {
        if (_isQueueMode && _queueProcessor != null)
        {
            _queueProcessor.OnCraftFinished(recipe, cancelled);
        }
    }

    public static void StartGatherAndCraft(uint recipeId, Dictionary<uint, int> missing)
    {
        _isQueueMode = false;
        SetFcGatherTargetOrder(null);
        SetFcGatherIntentProvider(null);
        _recipeIdToCraft = recipeId;
        _waitingForGatherComplete = true;
        CreateGatherListForMissingIngredients(missing);
    }
    
    public static void StartQueueCraftAndGather(
        CraftingExecutionPlan executionPlan,
        CraftingListConsumableSettings? listConsumables = null,
        int? ephemeralListId = null,
        CraftingAutomationOwner owner = CraftingAutomationOwner.GatherBuddy,
        bool restoringPersistedCraft = false,
        IReadOnlyList<uint>? fcGatherTargetOrder = null)
    {
        // A generic queue entry must not turn an FC plan into private
        // automation. Recovery may adopt only the already-open indivisible
        // synthesis; the next craft is gated again by CraftingQueueProcessor.
        if (executionPlan.ExecutionSource == ExecutionSource.FcFulfillment
            && !(restoringPersistedCraft
                && executionPlan.IsRecoveryPlan
                && SynthesisReader.IsSynthesisWindowOpen())
            && !TryPreflightFcQueueAdmission(
                executionPlan.ExecutionSource,
                executionPlan.FcContext?.CapabilityProof,
                global::GatherBuddy.GatherBuddy.FcCapabilities,
                out var capabilityFailure))
        {
            GatherBuddy.Log.Warning(
                $"[CraftingGatherBridge] FC queue entry blocked by capability proof: {capabilityFailure}");
            return;
        }

        if (!CraftingQueuePreflight.TryValidate(
                executionPlan,
                out var preflightFailure,
                validatePrecrafts: !executionPlan.AutoPurchaseBlockedDependencies,
                listConsumables: listConsumables))
        {
            if (restoringPersistedCraft)
            {
                GatherBuddy.Log.Debug(
                    $"[CraftingRecovery] Waiting for recovery preflight: {preflightFailure.Replace('\n', ' ')}");
            }
            else
            {
                GatherBuddy.Log.Warning($"[CraftingGatherBridge] Queue preflight failed: {preflightFailure.Replace('\n', ' ')}");
                Dalamud.Chat.PrintError($"[GatherBuddy Ascended] {preflightFailure}");
            }
            return;
        }

        if (!restoringPersistedCraft)
            ClearRecoveryTicket();
        CleanupPreviousQueueBeforeStart();
        var requestedFcGatherOrder = executionPlan.ExecutionSource == ExecutionSource.FcFulfillment
            ? fcGatherTargetOrder?.Where(itemId => itemId != 0).Distinct().ToArray()
                ?? Array.Empty<uint>()
            : Array.Empty<uint>();
        SetFcGatherTargetOrder(requestedFcGatherOrder);
        if (!_queueProcessorDrain.IsCompleted)
        {
            _pendingQueueStart = new PendingQueueStart(
                executionPlan,
                listConsumables,
                ephemeralListId,
                owner,
                restoringPersistedCraft,
                requestedFcGatherOrder);
            GatherBuddy.Log.Information("[CraftingGatherBridge] Waiting for the previous queue's acquisition cleanup before starting the replacement queue");
            return;
        }

        TryFinalizePendingProcessorDisposal();
        _pendingQueueStart = null;
        StartQueueCore(
            executionPlan,
            listConsumables,
            ephemeralListId,
            owner,
            restoringPersistedCraft,
            requestedFcGatherOrder);
    }

    /// <summary>
    /// Framework-thread FC entry point. It retains the normal queue processor
    /// and solver admission path while exposing the one-frame acceptance
    /// result required by the fulfillment controller.
    /// </summary>
    public static bool TryStartFcQueue(CraftingExecutionPlan executionPlan)
        => TryStartFcQueue(executionPlan, null);

    public static bool TryStartFcQueue(
        CraftingExecutionPlan executionPlan,
        IReadOnlyList<uint>? gatherTargetOrder)
    {
        if (executionPlan is null
            || executionPlan.ExecutionSource != ExecutionSource.FcFulfillment
            || HasActiveQueue)
            return false;

        var capabilities = global::GatherBuddy.GatherBuddy.FcCapabilities;
        if (!TryPreflightFcQueueAdmission(
                executionPlan.ExecutionSource,
                executionPlan.FcContext?.CapabilityProof,
                capabilities,
                out var capabilityFailure))
        {
            global::GatherBuddy.GatherBuddy.Log.Warning(
                $"[CraftingGatherBridge] FC queue admission blocked by capability proof: {capabilityFailure}");
            return false;
        }

        StartQueueCraftAndGather(
            executionPlan,
            owner: CraftingAutomationOwner.FcFulfillment,
            fcGatherTargetOrder: gatherTargetOrder);
        return HasActiveQueue || _pendingQueueStart is not null;
    }

    /// <summary>
    /// Shared side-effect-free admission boundary used immediately before the
    /// FC queue starts. Tests use this exact boundary with a fake service; the
    /// real queue entry above supplies the current per-character service.
    /// </summary>
    internal static bool TryPreflightFcQueueAdmission(
        ExecutionSource source,
        FcCapabilityExecutionProof? proof,
        FcCapabilityService? capabilities,
        out string reason)
    {
        reason = string.Empty;
        if (source != ExecutionSource.FcFulfillment)
        {
            reason = "The execution source is not FC fulfillment.";
            return false;
        }
        if (capabilities is null)
        {
            reason = "FC capability service is unavailable.";
            return false;
        }
        return capabilities.TryPreflightExecution(proof, out reason);
    }

    private static void StartQueueCore(
        CraftingExecutionPlan executionPlan,
        CraftingListConsumableSettings? listConsumables,
        int? ephemeralListId,
        CraftingAutomationOwner owner,
        bool restoringPersistedCraft,
        IReadOnlyList<uint>? fcGatherTargetOrder)
    {
        _queueProcessorDrain = Task.CompletedTask;
        _isQueueMode = true;
        _ephemeralListId = ephemeralListId;
        _activeExecutionPlan = executionPlan;
        _activeAutomationOwner = owner;
        SetFcGatherTargetOrder(
            executionPlan.ExecutionSource == ExecutionSource.FcFulfillment
                ? fcGatherTargetOrder
                : null);
        SetFcGatherIntentProvider(executionPlan);
        _fcGatherWasInProgress = false;
        _restoringPersistedCraft = restoringPersistedCraft;
        _restoredQueueCoverage = restoringPersistedCraft
            ? executionPlan.QueueView
                .Where(item => !item.Options.Skipping)
                .GroupBy(item => item.RecipeId)
                .ToDictionary(group => group.Key, group => group.Count())
            : new Dictionary<uint, int>();
        ResetCollectablesInterruptionState();
        _lastCollectablesHardFailLog = DateTime.MinValue;
        CaptureAndStopStandaloneGathering();
        DisableStandaloneGatherLists();
        _queueProcessor = new CraftingQueueProcessor();
        _queueProcessor.QueueCompleted += OnQueueCompleted;
        _waitingForGatherComplete = true;
        GatherBuddy.Log.Information($"[CraftingGatherBridge] Starting queue automation with {executionPlan.QueueView.Count} recipes, retainerRestock={executionPlan.RetainerRestock}");
        _queueProcessor.StartQueue(executionPlan, listConsumables, GatherBuddy.RaphaelSolveCoordinator);
        // CraftingQueueProcessor owns the complete retainer/acquisition/gather
        // sequence. It creates the temporary gather list only after its
        // corresponding gate has completed.

        GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(_queueProcessor);
    }

    private static void TryStartPersistedRecovery()
    {
        var now = DateTime.UtcNow;
        if (_startupRecoveryResolved || HasActiveQueue || now < _nextStartupRecoveryAttemptUtc)
            return;

        var ticket = GatherBuddy.Config.CraftingRecovery;
        if (ticket == null)
        {
            _startupRecoveryResolved = true;
            return;
        }

        var decision = CraftingRecoveryTicket.DecideStartupRecovery(
            ticket,
            Dalamud.Objects.LocalPlayer != null,
            SynthesisReader.IsSynthesisWindowOpen(),
            RecipeNoteExt.GetActiveCraftRecipeId(),
            now - _startupRecoveryProbeStartedUtc);
        if (decision == CraftingStartupRecoveryDecision.Wait)
            return;
        if (decision == CraftingStartupRecoveryDecision.Discard)
        {
            GatherBuddy.Log.Warning("[CraftingRecovery] Discarding stale or mismatched crafting ownership marker");
            ClearRecoveryTicket();
            _startupRecoveryResolved = true;
            return;
        }
        if (!ticket.TryRestore(out var remainingQueue, out var failureReason))
        {
            GatherBuddy.Log.Error($"[CraftingRecovery] Could not restore owned craft: {failureReason}");
            ClearRecoveryTicket();
            _startupRecoveryResolved = true;
            return;
        }

        GatherBuddy.Log.Information(
            $"[CraftingRecovery] Resuming owned synthesis recipe {remainingQueue[0].RecipeId} with {remainingQueue.Count} queue item(s) remaining");
        if (RecipeManager.GetRecipe(remainingQueue[0].RecipeId) is { } activeRecipe)
        {
            var activeCraftContext = CraftingContextResolver.ResolveExecutionContext(
                remainingQueue[0],
                activeRecipe,
                ticket.ListConsumables);
            if (RecoveryRequiresBaselineWarning(activeCraftContext))
            {
                const string warning = "Reload recovery is replanning from the live craft without the pre-reload Raphael incumbent; the original quality result cannot be proven.";
                GatherBuddy.Log.Warning($"[CraftingRecovery] {warning}");
                Dalamud.Chat.PrintError($"[GatherBuddy Ascended] {warning}");
            }
        }
        FcExecutionContext? recoveryContext = null;
        if (ticket.IsFcOwned)
        {
            if (ticket.FcCapability is not { } identity || !identity.IsValid)
            {
                GatherBuddy.Log.Error(
                    "[CraftingRecovery] FC recovery identity is missing; preserving the active synthesis without private fallback.");
                _startupRecoveryResolved = true;
                return;
            }

            var capabilities = global::GatherBuddy.GatherBuddy.FcCapabilities;
            var worldFingerprint = capabilities?.CurrentWorldFingerprint;
            if (capabilities is null || string.IsNullOrWhiteSpace(worldFingerprint))
            {
                _nextStartupRecoveryAttemptUtc = now.AddSeconds(1);
                return;
            }

            FcCapabilityExecutionProof? proof = null;
            if (!capabilities.TryCreateRecoveryExecutionProof(identity, out proof, out var capabilityReason))
            {
                // The current craft is already an indivisible game action. It
                // may be adopted, but no later craft may start from this
                // ticket until the service can rebuild a fresh proof.
                GatherBuddy.Log.Warning(
                    $"[CraftingRecovery] FC capability recovery is pending: {capabilityReason}");
            }

            recoveryContext = new FcExecutionContext(
                identity.SessionId,
                identity.Lists,
                worldFingerprint,
                new FcWorldRevision(0, worldFingerprint),
                proof);
        }

        _nextStartupRecoveryAttemptUtc = now.AddSeconds(1);
        var plan = CraftingExecutionPlan.CreateRecovery(remainingQueue, recoveryContext);
        StartQueueCraftAndGather(
            plan,
            ticket.ListConsumables?.Clone(),
            owner: ticket.IsFcOwned
                ? CraftingAutomationOwner.FcFulfillment
                : ticket.Owner,
            restoringPersistedCraft: true);
        _startupRecoveryResolved = HasActiveQueue;
    }

    internal static bool RecoveryRequiresBaselineWarning(CraftingExecutionContext context)
        => context.EffectiveSolverMode is VulcanSolverMode.Donatello or VulcanSolverMode.PureRaphael
            && context.DonatelloOptions?.Objective != Vulcan.DonatelloSolveObjective.ProgressOnly
            && !CraftingContextResolver.UsesSelectedMacro(context);

    internal static void PersistCurrentCraftOwnership(uint recipeId)
    {
        if (!_isQueueMode || _queueProcessor == null)
            return;

        var remaining = _queueProcessor.Queue.Skip(_queueProcessor.CurrentQueueIndex).ToList();
        if (remaining.Count == 0 || remaining[0].RecipeId != recipeId)
            return;

        var source = _activeExecutionPlan?.ExecutionSource
            ?? (_activeAutomationOwner == CraftingAutomationOwner.FcFulfillment
                ? ExecutionSource.FcFulfillment
                : ExecutionSource.PrivateList);
        FcCapabilityRecoveryIdentity? capabilityIdentity = null;
        if (source == ExecutionSource.FcFulfillment
            && _activeExecutionPlan?.FcContext is { } fcContext)
        {
            capabilityIdentity = new FcCapabilityRecoveryIdentity
            {
                RequestId = fcContext.CapabilityProof?.RequestId ?? Guid.Empty,
                SessionId = fcContext.SessionId,
                SessionGeneration = fcContext.CapabilityProof?.SessionGeneration ?? 0,
                RequiresHq = fcContext.CapabilityProof?.Eligibility != FcCapabilityEligibility.NqAllowed,
                Lists = fcContext.Lists.ToList(),
            };
        }

        GatherBuddy.Config.CraftingRecovery = CraftingRecoveryTicket.Capture(
            _activeAutomationOwner,
            remaining,
            _queueProcessor.ListConsumables,
            source,
            capabilityIdentity);
        GatherBuddy.Config.Save();
        GatherBuddy.Log.Debug(
            $"[CraftingRecovery] Persisted ownership for recipe {recipeId} with {remaining.Count} queue item(s) remaining");
    }

    private static void OnOwnedCraftFinished(Recipe? recipe, bool cancelled)
    {
        if (_isQueueMode)
            ClearRecoveryTicket();
    }

    private static void ClearRecoveryTicket()
    {
        if (GatherBuddy.Config.CraftingRecovery == null)
            return;
        GatherBuddy.Config.CraftingRecovery = null;
        GatherBuddy.Config.Save();
    }

    internal static bool TryGetPersistedArtisanRecovery(
        uint requestedRecipeId,
        out ushort activeRecipeId,
        out int alreadyQueued)
    {
        activeRecipeId = 0;
        alreadyQueued = 0;
        if (!_restoringPersistedCraft
            || _activeAutomationOwner != CraftingAutomationOwner.ArtisanIpc
            || _queueProcessor?.CurrentRecipeItem is not { } current
            || current.RecipeId > ushort.MaxValue)
            return false;

        activeRecipeId = (ushort)current.RecipeId;
        alreadyQueued = _restoredQueueCoverage.GetValueOrDefault(requestedRecipeId);
        return true;
    }

    private static void CleanupPreviousQueueBeforeStart()
    {
        if (_queueProcessor == null
            && _gatherList == null
            && _disabledGatherLists.Count == 0
            && !_autoGatherStateCaptured
            && !_ephemeralListId.HasValue)
            return;

        GatherBuddy.Log.Information("[CraftingGatherBridge] Cleaning up the previous craft queue before starting a new one");
        ResetCollectablesInterruptionState();
        AbortFcGatherYieldBoundary();
        _waitingForGatherComplete = false;
        _waitingForJobSwitch = false;
        _jobSwitchTime = DateTime.MinValue;
        ReleaseCraftOwnedAutoGather(disable: true);

        if (_queueProcessor != null)
        {
            var previousProcessor = _queueProcessor;
            try
            {
                previousProcessor.QueueCompleted -= OnQueueCompleted;
                previousProcessor.Reset();
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"[CraftingGatherBridge] Previous queue reset failed: {ex.Message}");
            }
            QueueProcessorForDeferredDisposal(previousProcessor);
        }
        RestoreQueueOwnedState();
        _activeExecutionPlan?.ClearFcIntentSnapshotProvider();
        _queueProcessor = null;
        _activeExecutionPlan = null;
        _isQueueMode = false;
        SetFcGatherTargetOrder(null);
        SetFcGatherIntentProvider(null);
    }
    
    public static void CreateGatherListForMissingIngredients(Dictionary<uint, int> missing)
        => CreateGatherList(missing, quantityIsDeficit: true);

    public static void CreateGatherListForRequiredIngredients(IReadOnlyDictionary<uint, int> required)
        => CreateGatherList(required, quantityIsDeficit: false);

    private static void CreateGatherList(
        IReadOnlyDictionary<uint, int> ingredients,
        bool quantityIsDeficit)
    {
        try
        {
            if (_plugin == null)
                throw new InvalidOperationException("Plugin is not initialized.");

            DisableStandaloneGatherLists();

            _gatherList = new AutoGatherList()
            {
                Name = "Crafting Materials (Auto-Generated)",
                Enabled = true,
                CompletionProvider = CreateGatherCompletionProvider(),
                CompletionScope = CreateGatherCompletionScope(),
            };
            foreach (var (itemId, quantity) in ingredients)
            {
                var gatherQuantity = GetCraftingGatherTargetQuantity(
                    itemId,
                    quantity,
                    quantityIsDeficit,
                    out var gatherItemId,
                    out var completionItemId);
                if (gatherQuantity <= 0)
                    continue;
                
                if (GatherBuddy.GameData.Gatherables.TryGetValue(gatherItemId, out var gatherable))
                {
                    _gatherList.Add(gatherable, (uint)gatherQuantity, completionItemId);
                    _gatherList.SetCompletionQuality(gatherable, GetGatherCompletionQuality(completionItemId == 0 ? gatherable.ItemId : completionItemId));
                }
                else if (GatherBuddy.GameData.Fishes.TryGetValue(gatherItemId, out var fish))
                {
                    _gatherList.Add(fish, (uint)gatherQuantity, completionItemId);
                    _gatherList.SetCompletionQuality(fish, GetGatherCompletionQuality(completionItemId == 0 ? fish.ItemId : completionItemId));
                }
                else
                    GatherBuddy.Log.Debug($"[CraftingGatherBridge] Item {gatherItemId} not found in gatherables or fish, skipping");
            }

            if (_gatherList.Items.Count > 0)
            {
                if (_activeExecutionPlan?.ExecutionSource == ExecutionSource.FcFulfillment)
                {
                    var executionOrder = new List<uint>();
                    foreach (var itemId in _fcGatherTargetOrder)
                    {
                        ResolveCraftingGatherItemIds(
                            itemId,
                            out var gatherItemId,
                            out _,
                            out _);
                        executionOrder.Add(itemId);
                        if (gatherItemId != itemId)
                            executionOrder.Add(gatherItemId);
                    }
                    global::GatherBuddy.GatherBuddy.AutoGather?.SetFcGatherTargetOrder(executionOrder);
                    SetFcGatherIntentProvider(_activeExecutionPlan);
                }
                else
                {
                    SetFcGatherTargetOrder(null);
                    SetFcGatherIntentProvider(null);
                }
                _plugin.AutoGatherListsManager.AddList(_gatherList);
                _plugin.AutoGatherListsManager.SetActiveItems();

                if (IsGatheringComplete())
                {
                    GatherBuddy.Log.Debug($"[CraftingGatherBridge] Gather list created but all items already in inventory, proceeding directly to crafting");
                    OnGatherComplete();
                }
                else
                {
                    _waitingForGatherComplete = true;
                    if (GatherBuddy.AutoGather == null)
                        throw new InvalidOperationException("AutoGather is not initialized.");
                    GatherBuddy.AutoGather.Enabled = true;
                    GatherBuddy.Log.Information($"Created crafting gather list with {_gatherList.Items.Count} items. Starting auto-gather.");
                }
            }
            else
            {
                GatherBuddy.Log.Debug($"[CraftingGatherBridge] No gatherable items needed, proceeding directly to crafting");
                OnGatherComplete();
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"Failed to create gather list: {ex.Message}");
            AbortFcGatherYieldBoundary();
            if (_queueProcessor != null && _isQueueMode)
            {
                _queueProcessor.FailFromBridge($"Cannot start crafting gather stage: {ex.Message}");
                RestoreQueueOwnedState();
            }
            else
            {
                RestoreQueueOwnedState();
                _recipeIdToCraft = 0;
                _waitingForGatherComplete = false;
            }
        }
    }

    private static bool TryBeginFcGatherYieldBoundary(
        IEnumerable<uint> itemIds,
        FcItemQuality? quality,
        out string error)
    {
        if (_activeAutomationOwner != CraftingAutomationOwner.FcFulfillment
            || _activeExecutionPlan?.ExecutionSource != ExecutionSource.FcFulfillment)
        {
            error = string.Empty;
            return true;
        }

        var expectedItemIds = (itemIds ?? Array.Empty<uint>()).Distinct().ToArray();
        var status = GatherBuddy.FcWorkerSessions?.Status;
        if (status is null
            || !status.IsSubscribed
            || status.Desired is not { } desired
            || desired.SessionId == Guid.Empty
            || desired.SessionGeneration == 0)
        {
            error = "FC gather interaction has no current subscribed worker session.";
            return false;
        }

        var boundary = new FcGatherYieldBoundary(CraftingInventoryCounter.GetInventorySplitCounts);
        var token = new FcGatherInteractionToken(
            Guid.NewGuid(),
            desired.SessionId,
            desired.SessionGeneration,
            expectedItemIds,
            quality);
        if (!boundary.Begin(expectedItemIds, token, out error))
            return false;
        _fcGatherYieldBoundary = boundary;
        return true;
    }

    private static void TrackFcGatherInteractionBoundary(CraftingQueueProcessor processor)
    {
        if (_activeAutomationOwner != CraftingAutomationOwner.FcFulfillment
            || _activeExecutionPlan?.ExecutionSource != ExecutionSource.FcFulfillment
            || !_waitingForGatherComplete
            || GatherBuddy.AutoGather is not { } autoGather)
        {
            _fcGatherWasInProgress = false;
            return;
        }

        var inGatherInteraction = autoGather.IsGathering || autoGather.IsFishing;
        if (inGatherInteraction)
        {
            if (_fcGatherWasInProgress || _fcGatherYieldBoundary is not null)
                return;

            _fcGatherWasInProgress = true;
            if (!autoGather.TryGetActiveGatherTarget(out var target))
            {
                var error = "FC gather interaction has no uniquely resolved active gather target.";
                GatherBuddy.Log.Error($"[CraftingGatherBridge] {error}");
                processor.FailFromBridge(error);
                return;
            }

            if (!TryBeginFcGatherYieldBoundary(
                new[] { target.Item.ItemId },
                target.CompletionQuality,
                out var boundaryError))
            {
                GatherBuddy.Log.Error($"[CraftingGatherBridge] FC gather yield boundary failed: {boundaryError}");
                processor.FailFromBridge(boundaryError);
            }
            return;
        }

        if (!_fcGatherWasInProgress)
            return;

        _fcGatherWasInProgress = false;
        if (_fcGatherYieldBoundary is not null)
            TryCompleteFcGatherYieldBoundary();
    }

    private static bool TryCompleteFcGatherYieldBoundary()
    {
        var boundary = _fcGatherYieldBoundary;
        if (boundary is null)
            return true;

        if (!TryValidateFcGatherYieldToken(boundary.Token, out var tokenError))
        {
            boundary.Abort();
            _fcGatherYieldBoundary = null;
            var error = $"FC gather yield interaction token became invalid: {tokenError}";
            GatherBuddy.Log.Error($"[CraftingGatherBridge] {error}");
            _queueProcessor?.FailFromBridge(error);
            return false;
        }

        var result = boundary.Complete();
        _fcGatherYieldBoundary = null;
        if (result.Outcome == FcGatherYieldBoundaryOutcome.Blocked)
        {
            var error = $"FC gather yield reconciliation blocked: {result.Error}";
            GatherBuddy.Log.Error($"[CraftingGatherBridge] {error}");
            _queueProcessor?.FailFromBridge(error);
            return false;
        }

        if (!result.Succeeded)
            return true;

        try
        {
            FcGatherYieldObserved?.Invoke(new GatherYieldObserved(result.Quantity));
            return true;
        }
        catch (Exception exception)
        {
            var error = $"FC gather yield publication failed: {exception.Message}";
            GatherBuddy.Log.Error($"[CraftingGatherBridge] {error}");
            _queueProcessor?.FailFromBridge(error);
            return false;
        }
    }

    private static bool TryValidateFcGatherYieldToken(
        FcGatherInteractionToken? token,
        out string error)
    {
        if (token is null || !token.IsValid)
        {
            error = "Token identity is invalid.";
            return false;
        }

        var status = GatherBuddy.FcWorkerSessions?.Status;
        if (status is null
            || !status.IsSubscribed
            || status.Desired is not { } desired
            || desired.SessionId != token.SessionId
            || desired.SessionGeneration != token.SessionGeneration)
        {
            error = "Worker session changed or unsubscribed before gather completion.";
            return false;
        }

        if (GatherBuddy.AutoGather is not { } autoGather
            || !autoGather.TryGetActiveGatherTarget(out var target)
            || target.Item is not { ItemId: not 0 } item
            || !token.ItemIds.Contains(item.ItemId)
            || (token.Quality is { } quality && target.CompletionQuality != quality))
        {
            error = "Active gather target changed before the interaction completed.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static void AbortFcGatherYieldBoundary()
    {
        _fcGatherYieldBoundary?.Abort();
        _fcGatherYieldBoundary = null;
        _fcGatherWasInProgress = false;
    }

    private static void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        var boundary = _fcGatherYieldBoundary;
        if (boundary is null)
            return;

        foreach (var inventoryEvent in events)
        {
            foreach (var itemId in GetAffectedPhysicalInventoryItemIds(inventoryEvent))
                boundary.ObserveInventoryItem(itemId);
        }
    }

    private static IEnumerable<uint> GetAffectedPhysicalInventoryItemIds(InventoryEventArgs inventoryEvent)
    {
        switch (inventoryEvent)
        {
            case InventoryComplexEventArgs complexEvent:
            {
                if (IsPhysicalInventory(complexEvent.SourceInventory))
                {
                    var sourceItemId = complexEvent.SourceEvent.Item.BaseItemId != 0
                        ? complexEvent.SourceEvent.Item.BaseItemId
                        : complexEvent.SourceEvent.Item.ItemId;
                    if (sourceItemId > 0)
                        yield return sourceItemId;
                }

                if (IsPhysicalInventory(complexEvent.TargetInventory))
                {
                    var targetItemId = complexEvent.TargetEvent.Item.BaseItemId != 0
                        ? complexEvent.TargetEvent.Item.BaseItemId
                        : complexEvent.TargetEvent.Item.ItemId;
                    if (targetItemId > 0)
                        yield return targetItemId;
                }
                yield break;
            }
            case InventoryItemAddedArgs addedEvent when IsPhysicalInventory(addedEvent.Inventory):
            {
                var itemId = addedEvent.Item.BaseItemId != 0
                    ? addedEvent.Item.BaseItemId
                    : addedEvent.Item.ItemId;
                if (itemId > 0)
                    yield return itemId;
                yield break;
            }
            case InventoryItemRemovedArgs removedEvent when IsPhysicalInventory(removedEvent.Inventory):
            {
                var itemId = removedEvent.Item.BaseItemId != 0
                    ? removedEvent.Item.BaseItemId
                    : removedEvent.Item.ItemId;
                if (itemId > 0)
                    yield return itemId;
                yield break;
            }
            case InventoryItemChangedArgs changedEvent when IsPhysicalInventory(changedEvent.Inventory):
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
                if (!IsPhysicalInventory(inventoryEvent.Item.ContainerType))
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

    private static bool IsPhysicalInventory(GameInventoryType inventoryType)
        => inventoryType is GameInventoryType.Inventory1
            or GameInventoryType.Inventory2
            or GameInventoryType.Inventory3
            or GameInventoryType.Inventory4
            or GameInventoryType.Crystals;

    private static void DisableStandaloneGatherLists()
    {
        if (_plugin == null)
            return;

        var enabledLists = _plugin.AutoGatherListsManager.Lists
            .Where(list => list.Enabled && !list.Fallback)
            .ToList();
        if (enabledLists.Count == 0)
            return;

        foreach (var existingList in enabledLists)
        {
            try
            {
                existingList.Enabled = false;
                if (!_disabledGatherLists.Contains(existingList))
                    _disabledGatherLists.Add(existingList);
                GatherBuddy.Log.Debug($"[CraftingGatherBridge] Disabled gather list '{existingList.Name}' before starting craft acquisition");
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to disable gather list '{existingList.Name}': {ex.Message}");
            }
        }
        try
        {
            _plugin.AutoGatherListsManager.SetActiveItems();
            _plugin.AutoGatherListsManager.Save();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to refresh disabled gather lists: {ex.Message}");
        }
    }

    private static void CaptureAndStopStandaloneGathering()
    {
        if (_autoGatherStateCaptured)
            return;

        var autoGather = GatherBuddy.AutoGather;
        if (autoGather == null)
            return;

        try
        {
            _autoGatherStateCaptured = true;
            _autoGatherWasEnabled = autoGather.Enabled;
            if (_autoGatherWasEnabled)
            {
                GatherBuddy.Log.Debug("[CraftingGatherBridge] Stopping standalone AutoGather before craft acquisition");
                autoGather.Enabled = false;
            }
        }
        catch (Exception ex)
        {
            _autoGatherStateCaptured = false;
            _autoGatherWasEnabled = false;
            GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to stop standalone AutoGather before craft acquisition: {ex.Message}");
        }
    }

    private static void RestoreAutoGatherState()
    {
        if (!_autoGatherStateCaptured)
            return;

        var wasEnabled = _autoGatherWasEnabled;
        _autoGatherStateCaptured = false;
        _autoGatherWasEnabled = false;
        try
        {
            GatherBuddy.Log.Debug($"[CraftingGatherBridge] Restoring standalone AutoGather after craft queue cleanup (enabled={wasEnabled})");
            if (GatherBuddy.AutoGather != null)
                GatherBuddy.AutoGather.Enabled = wasEnabled;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to restore standalone AutoGather: {ex.Message}");
        }
    }
    
    public static void OnGatherComplete()
    {
        if (_isQueueMode && _queueProcessor != null)
        {
            if (!TryCompleteFcGatherYieldBoundary())
                return;
            _waitingForGatherComplete = false;
            GatherBuddy.Log.Debug($"[CraftingGatherBridge] Gather complete for queue mode");
            _queueProcessor.OnGatherComplete();
            return;
        }
        
        if (_recipeIdToCraft == 0)
            return;
        
        var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (recipeSheet == null || !recipeSheet.TryGetRow(_recipeIdToCraft, out var recipe))
        {
            GatherBuddy.Log.Error($"Could not find recipe {_recipeIdToCraft}");
            _recipeIdToCraft = 0;
            _waitingForGatherComplete = false;
            return;
        }
        
        var requiredCraftJob = (uint)(recipe.CraftType.RowId + 8);
        var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
        
        if (currentJob != requiredCraftJob)
        {
            if (!_waitingForJobSwitch)
            {
                GatherBuddy.Log.Information($"Switching from job {currentJob} to job {requiredCraftJob} for crafting");
                SwitchJob(requiredCraftJob);
                _jobSwitchTime = DateTime.Now;
                _waitingForJobSwitch = true;
            }
            return;
        }
        
        _waitingForGatherComplete = false;
        _waitingForJobSwitch = false;
        GatherBuddy.Log.Information($"Gathering complete. Starting craft for recipe {_recipeIdToCraft}");
        
        DeleteTemporaryGatherList();
        
        CraftingGameInterop.StartCraft(recipe, 1);
        _recipeIdToCraft = 0;
    }
    
    private static unsafe void SwitchJob(uint jobId)
    {
        try
        {
            var gearsetModule = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
            if (gearsetModule == null)
            {
                GatherBuddy.Log.Error("Failed to get gearset module");
                return;
            }
            
            if (GearsetStatsReader.TryResolveExistingGearsetIndex(gearsetModule, jobId, out var gearsetIndex))
            {
                gearsetModule->EquipGearset(gearsetIndex);
                GatherBuddy.Log.Information($"Equipped gearset {gearsetIndex} for job {jobId}");
                return;
            }
            
            GatherBuddy.Log.Warning($"No gearset found for job {jobId}");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"Failed to switch job: {ex.Message}");
        }
    }

    public static bool IsGatheringComplete()
    {
        if (_gatherList == null)
            return _waitingForGatherComplete;

        var allComplete = true;
        foreach (var item in _gatherList.Items)
        {
            var needed = _gatherList.Quantities.TryGetValue(item, out var qty) ? qty : 0;
            var completionItemId = _gatherList.CompletionItemIds.GetValueOrDefault(item);
            var countedItemId = completionItemId == 0 ? item.ItemId : completionItemId;
            var (nq, hq) = GetCompletionSplitCounts(countedItemId);
            var demand = _activeExecutionPlan?.IngredientDemandsView.GetValueOrDefault(countedItemId) ?? default;
            if (!IsGatheringItemComplete(needed, demand, nq, hq))
            {
                allComplete = false;
                break;
            }
        }

        return allComplete;
    }

    internal static bool IsGatheringItemComplete(
        uint requiredQuantity,
        IngredientQualityDemand demand,
        int inventoryNq,
        int inventoryHq)
        => (long)Math.Max(0, inventoryNq) + Math.Max(0, inventoryHq) >= requiredQuantity
            && Math.Max(0, inventoryNq) >= Math.Max(0, demand.RequiredNQ)
            && Math.Max(0, inventoryHq) >= Math.Max(0, demand.RequiredHQ);

    private static unsafe int GetInventoryCount(uint itemId)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
                return 0;
            return inventory->GetInventoryItemCount(itemId, false, false, false);
        }
        catch
        {
            return 0;
        }
    }

    private static int GetCompletionCount(uint itemId)
    {
        if (_activeExecutionPlan?.ExecutionSource == ExecutionSource.FcFulfillment)
        {
            var source = _activeExecutionPlan.PlanningContext.RepresentedInventory;
            var total = (long)Math.Max(0, source.GetNq(itemId)) + Math.Max(0, source.GetHq(itemId));
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        return GetInventoryCount(itemId);
    }

    private static (int NQ, int HQ) GetCompletionSplitCounts(uint itemId)
    {
        if (_activeExecutionPlan?.ExecutionSource == ExecutionSource.FcFulfillment)
        {
            var source = _activeExecutionPlan.PlanningContext.RepresentedInventory;
            return (source.GetNq(itemId), source.GetHq(itemId));
        }

        try
        {
            return CraftingInventoryCounter.GetInventorySplitCounts(itemId);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static ICompletionCountProvider? CreateGatherCompletionProvider()
        => _activeExecutionPlan?.ExecutionSource == ExecutionSource.FcFulfillment
            ? new FcCompletionCountProvider(_activeExecutionPlan.PlanningContext.RepresentedInventory)
            : null;

    private static string CreateGatherCompletionScope()
    {
        if (_activeExecutionPlan?.FcContext is not { } context)
            return AutoGatherList.DefaultCompletionScope;

        return $"fc:{_activeExecutionPlan!.ListId}:{context.SessionId:N}:{string.Join(",", context.Lists.Select(list => list.ToString("N")))}:{context.WorldFingerprint}";
    }

    private static FcItemQuality? GetGatherCompletionQuality(uint itemId)
    {
        if (_activeExecutionPlan?.ExecutionSource != ExecutionSource.FcFulfillment)
            return null;

        var demand = _activeExecutionPlan.IngredientDemandsView.GetValueOrDefault(itemId);
        if (demand.RequiredHQ > 0)
            return FcItemQuality.Hq;
        if (demand.RequiredNQ > 0)
            return FcItemQuality.Nq;
        return null;
    }

    private static MaterialDemand? BuildRepresentedMaterialDemand(
        uint itemId,
        int quantity,
        bool quantityIsDeficit)
    {
        if (_activeExecutionPlan?.ExecutionSource != ExecutionSource.FcFulfillment)
            return null;

        var quality = GetGatherCompletionQuality(itemId);
        if (quality is not { } requiredQuality)
            return null;

        var source = _activeExecutionPlan.PlanningContext.RepresentedInventory;
        var current = source.GetQuantity(itemId, requiredQuality);
        var required = quantityIsDeficit
            ? checked(current + quantity)
            : quantity;
        return MaterialDemandBuilder.Build(itemId, requiredQuality, required, source);
    }
    
    public static void TestRepairSystem()
    {
        if (_queueProcessor != null && _isQueueMode)
        {
            GatherBuddy.Log.Warning("[CraftingGatherBridge] Cannot test repair - queue is already running");
            return;
        }
        
        GatherBuddy.Log.Information("[CraftingGatherBridge] Starting repair system test");
        _isQueueMode = true;
        _queueProcessor?.Dispose();
        _queueProcessor = new CraftingQueueProcessor();
        _queueProcessor.TestRepair();
        
        GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(_queueProcessor);
    }
    
    private static void RestoreDisabledGatherLists()
    {
        if (_disabledGatherLists.Count == 0 || _plugin == null)
            return;

        foreach (var list in _disabledGatherLists)
        {
            try
            {
                list.Enabled = true;
                GatherBuddy.Log.Debug($"[CraftingGatherBridge] Re-enabled gather list '{list.Name}'");
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to re-enable gather list '{list.Name}': {ex.Message}");
            }
        }
        try
        {
            _plugin.AutoGatherListsManager.SetActiveItems();
            _plugin.AutoGatherListsManager.Save();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to refresh restored gather lists: {ex.Message}");
        }
        _disabledGatherLists.Clear();
    }

    private static void OnQueueCompleted()
    {
        GatherBuddy.Log.Information("[CraftingGatherBridge] Queue completed, will clean up after tasks finish");
    }

    private static void TryStartCollectablesInterruption()
    {
        if (_queueProcessor == null
         || _queueProcessor.CurrentState is CraftingQueueProcessor.QueueState.Idle
             or CraftingQueueProcessor.QueueState.Complete
             or CraftingQueueProcessor.QueueState.Failed
         || GatherBuddy.CollectableManager == null
         || GatherBuddy.CollectableManager.IsRunning
         || _waitingForCollectablesHomeReturn
         || DateTime.UtcNow < _nextCollectablesRetry)
            return;

        if (_waitingForCollectables)
            return;

        var collectableConfig = GatherBuddy.Config.CollectableConfig;
        if (!collectableConfig.AutoTurnInCollectables)
        {
            if (_collectablesStartPending)
            {
                GatherBuddy.Log.Information("[CraftingGatherBridge] Collectables interruption was pending when auto turn-ins were forced off, resuming the queue without starting collectables");
                ResetCollectablesInterruptionState();
                _queueProcessor.Resume();
            }
            LogCollectablesHardFailState(collectableConfig.AutoTurnInHardFailReason);
            return;
        }

        if (!CollectableTurnInRequirements.IsAvailable)
        {
            if (_collectablesStartPending)
            {
                GatherBuddy.Log.Debug("[CraftingGatherBridge] Collectables interruption was pending when neither AllaganTools nor AllaganItemSearch was loaded, resuming the queue without starting collectables");
                ResetCollectablesInterruptionState();
                _queueProcessor.Resume();
            }
            return;
        }

        if (!_collectablesStartPending)
        {
            if (_queueProcessor.Paused)
                return;

            var thresholdState = CollectableInventoryHelper.GetThresholdState(GatherBuddy.Config.CollectableConfig);
            if (!thresholdState.ThresholdReached)
                return;
            _queueProcessor.Pause();
            _collectablesStartPending = true;
            _lastCollectablesWaitLog = DateTime.MinValue;
            _lastCollectablesExitAttempt = DateTime.MinValue;
        }

        TryExitCraftingUiForCollectables();
        if (!IsReadyToStartCollectables(out var waitReason))
        {
            LogCollectablesWaitReason(waitReason);
            return;
        }

        if (IsWaitingForCollectablesRouteData(out waitReason))
        {
            LogCollectablesWaitReason(waitReason);
            return;
        }

        if (GatherBuddy.CollectableManager.Start(CollectableRunSource.VulcanQueue, returnHomeAfterCompletion: true))
        {
            _collectablesStartPending = false;
            _waitingForCollectables = true;
            _lastCollectablesWaitLog = DateTime.MinValue;
            _lastCollectablesExitAttempt = DateTime.MinValue;
            return;
        }

        if (IsWaitingForCollectablesRouteData(out waitReason))
        {
            LogCollectablesWaitReason(waitReason);
            return;
        }

        GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to start collectables interruption: {GatherBuddy.CollectableManager.StatusText}");
        ResetCollectablesInterruptionState();
        _nextCollectablesRetry = DateTime.UtcNow.AddSeconds(5);
        _queueProcessor.Resume();
    }

    private static void TryExitCraftingUiForCollectables()
    {
        if (CraftingGameInterop.CurrentState != CraftingGameInterop.CraftState.IdleBetween)
            return;

        if (_lastCollectablesExitAttempt != DateTime.MinValue
         && (DateTime.UtcNow - _lastCollectablesExitAttempt) < TimeSpan.FromMilliseconds(500))
            return;

        _lastCollectablesExitAttempt = DateTime.UtcNow;
        CraftingTasks.TaskExitCraft();
    }

    private static bool IsReadyToStartCollectables(out string waitReason)
    {
        if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51])
        {
            waitReason = "area transition is still active";
            return false;
        }

        if (Lifestream.Enabled && Lifestream.IsBusy())
        {
            waitReason = "Lifestream is still busy";
            return false;
        }

        if (!GenericHelpers.IsScreenReady())
        {
            waitReason = "the screen is not ready";
            return false;
        }

        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
        {
            waitReason = "a crafting action is still executing";
            return false;
        }

        if (Dalamud.Conditions[ConditionFlag.PreparingToCraft])
        {
            waitReason = "craft preparation is still active";
            return false;
        }

        if (Dalamud.Conditions[ConditionFlag.Crafting])
        {
            waitReason = $"crafting state is still {CraftingGameInterop.CurrentState}";
            return false;
        }

        if (CraftingGameInterop.CurrentState != CraftingGameInterop.CraftState.IdleNormal)
        {
            waitReason = $"crafting has not returned to IdleNormal yet ({CraftingGameInterop.CurrentState})";
            return false;
        }

        if (IsCraftingAddonVisible("RecipeNote") || IsCraftingAddonVisible("Synthesis") || IsCraftingAddonVisible("SynthesisSimple") || IsCraftingAddonVisible("WKSRecipeNotebook"))
        {
            waitReason = "crafting windows are still visible";
            return false;
        }

        waitReason = string.Empty;
        return true;
    }

    private static unsafe bool IsCraftingAddonVisible(string addonName)
    {
        var addon = (AtkUnitBase*)(nint)Dalamud.GameGui.GetAddonByName(addonName);
        return addon != null && addon->IsVisible;
    }

    private static bool IsWaitingForCollectablesRouteData(out string waitReason)
    {
        if (!CollectableTurnInRouteResolver.HasLookupData)
        {
            waitReason = string.Empty;
            return false;
        }

        var collectableNpcIds = CollectableTurnInRouteResolver.GetCollectableNpcIds();
        if (collectableNpcIds.Count == 0)
        {
            waitReason = string.Empty;
            return false;
        }

        VendorNpcLocationCache.InitializeAsync(collectableNpcIds);
        if (VendorNpcLocationCache.IsInitialized)
        {
            waitReason = string.Empty;
            return false;
        }

        waitReason = VendorNpcLocationCache.IsInitializing
            ? $"collectables route locations are still loading ({VendorNpcLocationCache.ResolvedNpcCount}/{VendorNpcLocationCache.RequestedNpcCount} NPCs resolved)"
            : "collectables route locations are still loading";
        return true;
    }

    private static void LogCollectablesWaitReason(string waitReason)
    {
        if (_lastCollectablesWaitLog != DateTime.MinValue && (DateTime.UtcNow - _lastCollectablesWaitLog) < TimeSpan.FromSeconds(10))
            return;

        GatherBuddy.Log.Debug($"[CraftingGatherBridge] Waiting to start collectables interruption: {waitReason}");
        _lastCollectablesWaitLog = DateTime.UtcNow;
    }

    private static void OnCollectablesFinished()
    {
        if (!_waitingForCollectables && !_collectablesStartPending)
            return;
        ResetCollectablesInterruptionState();
        _queueProcessor?.Resume();
    }

    private static void OnCollectablesError(string error)
    {
        if (!_waitingForCollectables && !_collectablesStartPending)
            return;

        GatherBuddy.Log.Error($"[CraftingGatherBridge] Collectables interruption failed: {error}");
        var hardFailReason = GatherBuddy.Config.CollectableConfig.AutoTurnInHardFailReason;
        if (!GatherBuddy.Config.CollectableConfig.AutoTurnInCollectables && !string.IsNullOrWhiteSpace(hardFailReason))
        {
            LogCollectablesHardFailState(hardFailReason);
            StartCollectablesHomeReturnBeforeResume(hardFailReason);
            return;
        }

        ResetCollectablesInterruptionState();
        _nextCollectablesRetry = DateTime.UtcNow.AddSeconds(5);
        _lastCollectablesWaitLog = DateTime.MinValue;
        _lastCollectablesExitAttempt = DateTime.MinValue;
        _lastCollectablesHardFailLog = DateTime.MinValue;
        _queueProcessor?.Resume();
    }

    private static void StartCollectablesHomeReturnBeforeResume(string hardFailReason)
    {
        _collectablesStartPending = false;
        _waitingForCollectables = false;
        _waitingForCollectablesHomeReturn = true;
        _collectablesHomeReturnStarted = false;
        _nextCollectablesRetry = DateTime.MinValue;
        _lastCollectablesWaitLog = DateTime.MinValue;
        _lastCollectablesExitAttempt = DateTime.MinValue;
        GatherBuddy.Log.Warning("[CraftingGatherBridge] Returning home before resuming the queue after collectables hard fail");
    }

    private static void UpdateCollectablesHomeReturnBeforeResume()
    {
        if (!_waitingForCollectablesHomeReturn)
            return;

        if (!_collectablesHomeReturnStarted)
        {
            if (Lifestream.Enabled && Lifestream.IsBusy())
                return;

            if (!HomeNavigationHelper.TryStartReturnHome(out var error))
            {
                if (string.IsNullOrWhiteSpace(error))
                    return;

                GatherBuddy.Log.Warning($"[CraftingGatherBridge] {error}");
                GatherBuddy.Log.Warning("[CraftingGatherBridge] Resuming the queue without a home return after collectables hard fail");
                ResetCollectablesInterruptionState();
                _queueProcessor?.Resume();
                return;
            }

            _collectablesHomeReturnStarted = true;
            return;
        }

        if (!HomeNavigationHelper.IsReturnComplete())
            return;

        GatherBuddy.Log.Information("[CraftingGatherBridge] Home return complete, resuming the queue after collectables hard fail");
        ResetCollectablesInterruptionState();
        _queueProcessor?.Resume();
    }
    
    public static void StopQueue()
        => StopQueueInternal(clearRecoveryTicket: true);

    private static void StopQueueInternal(bool clearRecoveryTicket)
    {
        if (clearRecoveryTicket)
            ClearRecoveryTicket();
        _pendingQueueStart = null;
        AbortFcGatherYieldBoundary();
        if (_queueProcessor != null)
        {
            GatherBuddy.Log.Information("[CraftingGatherBridge] Stopping queue processor");
            ResetCollectablesInterruptionState();
            _lastCollectablesHardFailLog = DateTime.MinValue;
            ReleaseCraftOwnedAutoGather(disable: true);
            var stoppedProcessor = _queueProcessor;
            try
            {
                stoppedProcessor.QueueCompleted -= OnQueueCompleted;
                stoppedProcessor.Reset();
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"[CraftingGatherBridge] Queue reset failed while stopping: {ex.Message}");
            }
            QueueProcessorForDeferredDisposal(stoppedProcessor);
            _queueProcessor = null;
            _activeExecutionPlan = null;
            _isQueueMode = false;
            _waitingForGatherComplete = false;
            _waitingForJobSwitch = false;
            _jobSwitchTime = DateTime.MinValue;
            RestoreQueueOwnedState();
            GatherBuddy.CraftingStatusWindow?.SetQueueProcessor(null);
        }
        else
        {
            GatherBuddy.Log.Information("[CraftingGatherBridge] No queue processor running");
            _waitingForGatherComplete = false;
            _waitingForJobSwitch = false;
            _jobSwitchTime = DateTime.MinValue;
            RestoreQueueOwnedState();
        }
        TryFinalizePendingProcessorDisposal();
    }

    /// <summary>
    /// Stops queue-owned work and waits for acquisition cleanup before the
    /// plugin unloads services used by the processor. The caller must invoke
    /// this before disposing vendor/native acquisition dependencies.
    /// </summary>
    public static async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        StopQueueInternal(clearRecoveryTicket: false);
        var drain = _queueProcessorDrain;
        try
        {
            // Cleanup is mandatory before dependent vendor/native services
            // are disposed; do not let a caller cancellation bypass it.
            await drain.ConfigureAwait(false);
        }
        finally
        {
            TryFinalizePendingProcessorDisposal();
            if (_collectableManager != null)
            {
                _collectableManager.OnFinishCollecting -= OnCollectablesFinished;
                _collectableManager.OnError -= OnCollectablesError;
            }
            _collectableManager = null;
            CraftingGameInterop.CraftFinished -= OnOwnedCraftFinished;
            Dalamud.GameInventory.InventoryChanged -= OnInventoryChanged;
            AbortFcGatherYieldBoundary();
            _plugin = null;
            _queueProcessor = null;
            _activeExecutionPlan = null;
            _isQueueMode = false;
            _recipeIdToCraft = 0;
            _jobSwitchTime = DateTime.MinValue;
            _waitingForGatherComplete = false;
            _waitingForJobSwitch = false;
            _waitingForCollectables = false;
            _collectablesStartPending = false;
            _collectablesHomeReturnStarted = false;
            _waitingForCollectablesHomeReturn = false;
            _nextCollectablesRetry = DateTime.MinValue;
            _lastCollectablesWaitLog = DateTime.MinValue;
            _lastCollectablesExitAttempt = DateTime.MinValue;
            _lastCollectablesHardFailLog = DateTime.MinValue;
            _autoGatherStateCaptured = false;
            _autoGatherWasEnabled = false;
            _pendingQueueStart = null;
            _activeAutomationOwner = CraftingAutomationOwner.GatherBuddy;
            _restoringPersistedCraft = false;
            _restoredQueueCoverage.Clear();
            _startupRecoveryResolved = false;
            _startupRecoveryProbeStartedUtc = DateTime.MinValue;
            _nextStartupRecoveryAttemptUtc = DateTime.MinValue;
            _gatherList = null;
            _disabledGatherLists.Clear();
            _ephemeralListId = null;
            _queueProcessorPendingDispose = null;
            _queueProcessorDrain = Task.CompletedTask;
            PreserveListOnDisable = false;
        }
    }

    private static void QueueProcessorForDeferredDisposal(CraftingQueueProcessor processor)
    {
        _queueProcessorDrain = processor.AcquisitionDrainTask;
        if (_queueProcessorDrain.IsCompleted)
        {
            processor.Dispose();
            _queueProcessorPendingDispose = null;
            return;
        }

        _queueProcessorPendingDispose = processor;
    }

    private static void TryFinalizePendingProcessorDisposal()
    {
        var processor = _queueProcessorPendingDispose;
        if (processor == null || !_queueProcessorDrain.IsCompleted)
            return;

        _queueProcessorPendingDispose = null;
        try
        {
            _queueProcessorDrain.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingGatherBridge] Queue acquisition drain failed during cleanup: {ex.Message}");
        }
        finally
        {
            processor.Dispose();
        }
    }

    private static void ReleaseCraftOwnedAutoGather(bool disable)
    {
        var autoGather = GatherBuddy.AutoGather;
        if (autoGather == null)
            return;

        try
        {
            autoGather.SetCraftOwnedGathering(false);
            if (disable)
                autoGather.Enabled = false;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to release AutoGather queue ownership: {ex.Message}");
        }
    }

    private static void RestoreQueueOwnedState()
    {
        CraftingGameInterop.SetDonatelloOptions(null);
        try
        {
            DeleteTemporaryGatherList();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to delete temporary gather list: {ex.Message}");
        }

        try
        {
            RestoreDisabledGatherLists();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to restore gather lists: {ex.Message}");
        }

        try
        {
            RestoreAutoGatherState();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to restore standalone AutoGather: {ex.Message}");
        }

        DeleteEphemeralCraftingListSafely();
        ReleaseCraftOwnedAutoGather(disable: false);
    }

    private static void DeleteEphemeralCraftingListSafely()
    {
        try
        {
            DeleteEphemeralCraftingList();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingGatherBridge] Failed to delete ephemeral crafting list: {ex.Message}");
        }
    }

    private static void DeleteEphemeralCraftingList()
    {
        if (!_ephemeralListId.HasValue)
            return;

        var listId = _ephemeralListId.Value;
        _ephemeralListId = null;
        GatherBuddy.Log.Information($"[CraftingGatherBridge] Deleting ephemeral crafting list {listId}");
        GatherBuddy.CraftingListManager.DeleteList(listId);
    }

    private static void LogCollectablesHardFailState(string hardFailReason)
    {
        if (string.IsNullOrWhiteSpace(hardFailReason))
            return;

        if (_lastCollectablesHardFailLog != DateTime.MinValue && (DateTime.UtcNow - _lastCollectablesHardFailLog) < TimeSpan.FromSeconds(30))
            return;

        GatherBuddy.Log.Warning($"[CraftingGatherBridge] Skipping collectables interruption because auto turn-ins were forced off: {hardFailReason}");
        _lastCollectablesHardFailLog = DateTime.UtcNow;
    }

    private static void ResetCollectablesInterruptionState()
    {
        _collectablesStartPending = false;
        _waitingForCollectables = false;
        _waitingForCollectablesHomeReturn = false;
        _collectablesHomeReturnStarted = false;
        _nextCollectablesRetry = DateTime.MinValue;
        _lastCollectablesWaitLog = DateTime.MinValue;
        _lastCollectablesExitAttempt = DateTime.MinValue;
    }
}
