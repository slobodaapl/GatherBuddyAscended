using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.Crafting.Acquisition;
using GatherBuddy.Helpers;
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

    private sealed record PendingQueueStart(
        CraftingExecutionPlan ExecutionPlan,
        CraftingListConsumableSettings? ListConsumables,
        int? EphemeralListId,
        CraftingAutomationOwner Owner,
        bool RestoringPersistedCraft);
    
    public static bool PreserveListOnDisable { get; set; } = false;

    public static void Initialize(global::GatherBuddy.GatherBuddy plugin)
    {
        _plugin = plugin;
        _startupRecoveryResolved = false;
        _startupRecoveryProbeStartedUtc = DateTime.UtcNow;
        _nextStartupRecoveryAttemptUtc = DateTime.MinValue;
        CraftingGameInterop.CraftFinished += OnOwnedCraftFinished;
    }

    private static int RoundUpToBatchSize(int quantity, int batchSize)
        => batchSize <= 1
            ? quantity
            : (int)Math.Ceiling((double)quantity / batchSize) * batchSize;

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
            var gatherList = new AutoGatherList()
            {
                Name = listName,
                Enabled = false
            };

            foreach (var (itemId, quantity) in materials)
            {
                var gatherQuantity = GetCraftingGatherTargetQuantity(
                    itemId,
                    quantity,
                    quantityIsDeficit: false,
                    out var gatherItemId,
                    out var completionItemId);
                if (gatherQuantity <= 0)
                    continue;

                if (GatherBuddy.GameData.Gatherables.TryGetValue(gatherItemId, out var gatherable))
                    gatherList.Add(gatherable, (uint)gatherQuantity, completionItemId);
                else if (GatherBuddy.GameData.Fishes.TryGetValue(gatherItemId, out var fish))
                    gatherList.Add(fish, (uint)gatherQuantity, completionItemId);
                else
                    GatherBuddy.Log.Debug($"[CraftingGatherBridge] Item {gatherItemId} not found in gatherables or fish, skipping");
            }

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

    private static int GetCraftingGatherTargetQuantity(
        uint itemId,
        int quantity,
        bool quantityIsDeficit,
        out uint gatherItemId,
        out uint completionItemId)
    {
        gatherItemId = itemId;
        completionItemId = 0;
        if (quantity <= 0)
            return 0;

        if (!AutoGather.Helpers.Diadem.ApprovedToRawItemIds.TryGetValue(itemId, out var rawItemId))
        {
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
                return quantityIsDeficit
                    ? checked(GetInventoryCount(itemId) + quantity)
                    : quantity;
            }

            return quantity;
        }
        var approvedDeficit = Math.Max(0, quantity - GetInventoryCount(itemId));
        if (approvedDeficit <= 0)
            return 0;

        gatherItemId = rawItemId;
        var batchSize = AutoGather.Helpers.Diadem.ApprovedInspectionBatchSizes.TryGetValue(itemId, out var configuredBatchSize) && configuredBatchSize > 0
            ? (int)configuredBatchSize
            : 1;
        return RoundUpToBatchSize(approvedDeficit, batchSize);
    }
    
    public static void Update()
    {
        TryFinalizePendingProcessorDisposal();
        TryStartPersistedRecovery();

        if (_pendingQueueStart is { } pendingQueueStart)
        {
            if (!_queueProcessorDrain.IsCompleted)
                return;

            _pendingQueueStart = null;
            StartQueueCore(
                pendingQueueStart.ExecutionPlan,
                pendingQueueStart.ListConsumables,
                pendingQueueStart.EphemeralListId,
                pendingQueueStart.Owner,
                pendingQueueStart.RestoringPersistedCraft);
        }

        if (_isQueueMode && _queueProcessor != null)
        {
            var processor = _queueProcessor;
            try
            {
                UpdateCollectablesHomeReturnBeforeResume();
                TryStartCollectablesInterruption();
                processor.Update();
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
                _queueProcessor = null;
                _activeExecutionPlan = null;
                _isQueueMode = false;
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
        _recipeIdToCraft = recipeId;
        _waitingForGatherComplete = true;
        CreateGatherListForMissingIngredients(missing);
    }
    
    public static void StartQueueCraftAndGather(
        CraftingExecutionPlan executionPlan,
        CraftingListConsumableSettings? listConsumables = null,
        int? ephemeralListId = null,
        CraftingAutomationOwner owner = CraftingAutomationOwner.GatherBuddy,
        bool restoringPersistedCraft = false)
    {
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
        if (!_queueProcessorDrain.IsCompleted)
        {
            _pendingQueueStart = new PendingQueueStart(
                executionPlan,
                listConsumables,
                ephemeralListId,
                owner,
                restoringPersistedCraft);
            GatherBuddy.Log.Information("[CraftingGatherBridge] Waiting for the previous queue's acquisition cleanup before starting the replacement queue");
            return;
        }

        TryFinalizePendingProcessorDisposal();
        _pendingQueueStart = null;
        StartQueueCore(executionPlan, listConsumables, ephemeralListId, owner, restoringPersistedCraft);
    }

    private static void StartQueueCore(
        CraftingExecutionPlan executionPlan,
        CraftingListConsumableSettings? listConsumables,
        int? ephemeralListId,
        CraftingAutomationOwner owner,
        bool restoringPersistedCraft)
    {
        _queueProcessorDrain = Task.CompletedTask;
        _isQueueMode = true;
        _ephemeralListId = ephemeralListId;
        _activeExecutionPlan = executionPlan;
        _activeAutomationOwner = owner;
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
            var recoveryContext = CraftingContextResolver.ResolveExecutionContext(
                remainingQueue[0],
                activeRecipe,
                ticket.ListConsumables);
            if (RecoveryRequiresBaselineWarning(recoveryContext))
            {
                const string warning = "Reload recovery is replanning from the live craft without the pre-reload Raphael incumbent; the original quality result cannot be proven.";
                GatherBuddy.Log.Warning($"[CraftingRecovery] {warning}");
                Dalamud.Chat.PrintError($"[GatherBuddy Ascended] {warning}");
            }
        }
        _nextStartupRecoveryAttemptUtc = now.AddSeconds(1);
        var plan = CraftingExecutionPlan.CreateRecovery(remainingQueue);
        StartQueueCraftAndGather(
            plan,
            ticket.ListConsumables?.Clone(),
            owner: ticket.Owner,
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

        GatherBuddy.Config.CraftingRecovery = CraftingRecoveryTicket.Capture(
            _activeAutomationOwner,
            remaining,
            _queueProcessor.ListConsumables);
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
        _queueProcessor = null;
        _activeExecutionPlan = null;
        _isQueueMode = false;
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
                Enabled = true
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
                    _gatherList.Add(gatherable, (uint)gatherQuantity, completionItemId);
                else if (GatherBuddy.GameData.Fishes.TryGetValue(gatherItemId, out var fish))
                    _gatherList.Add(fish, (uint)gatherQuantity, completionItemId);
                else
                    GatherBuddy.Log.Debug($"[CraftingGatherBridge] Item {gatherItemId} not found in gatherables or fish, skipping");
            }

            if (_gatherList.Items.Count > 0)
            {
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
            var (nq, hq) = CraftingInventoryCounter.GetInventorySplitCounts(countedItemId);
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
