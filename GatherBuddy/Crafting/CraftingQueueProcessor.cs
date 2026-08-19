using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Crafting.Acquisition;
using GatherBuddy.Automation;
using GatherBuddy.Helpers;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public class CraftingQueueProcessor : IDisposable
{
    private static readonly List<CraftingListItem> EmptyQueue = [];
    private static readonly Dictionary<uint, int> EmptyCounts = [];
    private static readonly Dictionary<uint, IngredientQualityDemand> EmptyIngredientDemands = [];
    public enum QueueState
    {
        Idle,
        NavigatingToRetainerBell,
        WithdrawingFromRetainer,
        WaitingForGather,
        WaitingForAcquisitionData,
        PurchasingDependencies,
        ReturningToHomeWorld,
        ReturningToInn,
        WaitingForJobSwitch,
        Repairing,
        ExtractingMateria,
        WaitingForRaphaelSolution,
        ReadyForCraft,
        Crafting,
        Failed,
        Complete
    }

    private QueueState _currentState = QueueState.Idle;
    private CraftingExecutionPlan? _executionPlan = null;
    private int _currentQueueIndex = 0;
    private List<Func<CraftingTasks.TaskResult>> _tasks = new();
    private RaphaelSolveCoordinator? _raphaelCoordinator = null;
    private CraftingListConsumableSettings? _listConsumables = null;
    private DateTime _consumableDelayUntil = DateTime.MinValue;
    private bool _retainerRestock = false;
    private RetainerTaskExecutor? _retainerExecutor = null;
    private RetainerBellNavigator? _retainerBellNavigator = null;
    private Task<LiveAcquisitionResult>? _acquisitionTask;
    private CancellationTokenSource? _acquisitionCancellation;
    private LiveAcquisitionExecutor? _acquisitionExecutor;
    private readonly AcquisitionRunGenerationGate _acquisitionRuns = new();
    private readonly object _acquisitionSync = new();
    private long _acquisitionGeneration;
    private readonly Dictionary<uint, (int NQ, int HQ)> _acquisitionInventoryBefore = new();
    private readonly Dictionary<uint, int> _acquisitionPlannedQuantities = new();
    private bool _navigationStarted;

    private readonly record struct AcquisitionRunSnapshot(
        long Generation,
        LiveAcquisitionExecutor? Executor,
        CancellationTokenSource? Cancellation,
        Task<LiveAcquisitionResult>? Task);

    private bool _paused = false;
    private readonly DeferredResumeRequest _resumeRequest = new();
    private bool _pausedDuringGather = false;
    private uint _currentProcessedRecipeId = 0;
    private int _currentProcessedRecipeCount = 0;
    private int _currentProcessedRecipeTotal = 0;
    private DateTime _craftHangSince = DateTime.MinValue;
    private bool _lastCraftWasQuickSynth = false;
    private Dictionary<string, RaphaelSolveRequest> _enqueuedRaphaelRequests = new();
    private uint _jobSwitchRequestedFor = 0u;
    private DateTime _jobSwitchRequestedAt = DateTime.MinValue;
    private DateTime _jobSwitchStartedAt = DateTime.MinValue;
    private DateTime _jobSwitchReadySince = DateTime.MinValue;
    private int _jobSwitchAttempts;
    private string? _jobSwitchFailure;
    private Dictionary<uint, int> _missingIngredientFailures = new();
    private string _pauseReason = string.Empty;

    private List<CraftingListItem> QueueItems => _executionPlan?.Queue ?? EmptyQueue;
    private Dictionary<uint, int> MaterialTargets => _executionPlan?.Materials ?? EmptyCounts;
    private Dictionary<uint, int> RetainerPrecraftTargets => _executionPlan?.RetainerConsumedCraftables ?? EmptyCounts;
    private Dictionary<uint, IngredientQualityDemand> IngredientDemandTargets => _executionPlan?.IngredientDemands ?? EmptyIngredientDemands;
    private CraftingListDefinition? PlanningSnapshot => _executionPlan?.PlanningSnapshot;

    public QueueState CurrentState => _currentState;
    public int CurrentQueueIndex => _currentQueueIndex;
    public int QueueCount => QueueItems.Count;
    public IReadOnlyList<CraftingListItem> Queue => QueueItems;
    public CraftingListItem? CurrentRecipeItem => _currentQueueIndex < QueueItems.Count ? QueueItems[_currentQueueIndex] : null;
    public bool Paused => _paused;
    public string PauseReason => _pauseReason;
    internal Task AcquisitionDrainTask => _acquisitionRuns.DrainTask;
    public uint CurrentProcessedRecipeId => _currentProcessedRecipeId;
    public int CurrentProcessedRecipeCount => _currentProcessedRecipeCount;
    public int CurrentProcessedRecipeTotal => _currentProcessedRecipeTotal;
    public CraftingListConsumableSettings? ListConsumables => _listConsumables;
    public bool HasPendingTasks() => _tasks.Count > 0;

    public delegate void StateChangedHandler(QueueState state);
    public delegate void QueueCompletedHandler();
    
    public event StateChangedHandler? StateChanged;
    public event QueueCompletedHandler? QueueCompleted;
    
    public CraftingQueueProcessor()
    {
        CraftingGameInterop.CraftFinished += OnCraftFinished;
        CraftingGameInterop.CraftActionExecuted += OnCraftActionExecuted;
        CraftingGameInterop.QuickSynthProgress += OnQuickSynthProgress;
        CraftingGameInterop.AutomationFaulted += OnAutomationFaulted;
    }

    private void OnAutomationFaulted(string reason)
        => Pause(reason);

    private void OnCraftActionExecuted(VulcanSkill action)
    {
        if (_currentState == QueueState.Crafting && CurrentRecipeItem is { } item)
            item.ExecutedActions.Add(action);
    }

    public void Dispose()
    {
        CancelAcquisition();
        CraftingGameInterop.CraftFinished -= OnCraftFinished;
        CraftingGameInterop.CraftActionExecuted -= OnCraftActionExecuted;
        CraftingGameInterop.QuickSynthProgress -= OnQuickSynthProgress;
        CraftingGameInterop.AutomationFaulted -= OnAutomationFaulted;
        StateChanged = null;
        QueueCompleted = null;
    }

    public void StartQueue(CraftingExecutionPlan executionPlan, CraftingListConsumableSettings? listConsumables = null, RaphaelSolveCoordinator? raphaelCoordinator = null)
    {
        YesAlready.Lock();
        CraftingGameInterop.SetAutomationPaused(false);
        _executionPlan = executionPlan;
        _currentQueueIndex = 0;
        _raphaelCoordinator = raphaelCoordinator;
        _listConsumables = listConsumables;
        _consumableDelayUntil = DateTime.MinValue;
        _enqueuedRaphaelRequests.Clear();
        ResetJobSwitchWatchdog();
        _missingIngredientFailures.Clear();
        _pauseReason = string.Empty;
        _resumeRequest.Cancel();
        _retainerRestock = executionPlan.RetainerRestock;
        _retainerExecutor = null;
        _retainerBellNavigator = null;
        CancelAcquisition();
        _navigationStarted = false;
        var hasRetainerWork = _retainerRestock && AllaganTools.Enabled
            && (MaterialTargets.Count > 0 || RetainerPrecraftTargets.Count > 0);

        if (TryPrepareLiveCraftAdoption())
        {
            GatherBuddy.Log.Information("[CraftingQueueProcessor] Preparing to adopt the active craft after plugin reload");
        }
        else if (hasRetainerWork)
        {
            GatherBuddy.Log.Information("[CraftingQueueProcessor] Retainer restock enabled");
            if (GatherBuddy.Config.VulcanRetainerBellConfig.AutoNavigateToRetainerBell)
            {
                GatherBuddy.Log.Debug("[CraftingQueueProcessor] Auto-navigation to retainer bell enabled");
                _currentState = QueueState.NavigatingToRetainerBell;
                QueueRetainerBellNavigationTasks();
            }
            else
            {
                GatherBuddy.Log.Debug("[CraftingQueueProcessor] Proceeding directly to retainer withdrawal");
                _currentState = QueueState.WithdrawingFromRetainer;
                QueueRetainerWithdrawalTasks();
            }
        }
        else
        {
            BeginAcquisitionOrGather();
        }
        GatherBuddy.Log.Information($"[CraftingQueueProcessor] Starting queue with {QueueItems.Count} recipes");
        StateChanged?.Invoke(_currentState);
        
        if (_raphaelCoordinator != null)
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Evaluating queue for upfront Raphael solves using effective execution context");
            EnqueueRaphaelSolvesFromCraftStates(QueueItems);
        }
    }

    private bool TryPrepareLiveCraftAdoption()
    {
        if (!SynthesisReader.IsSynthesisWindowOpen() || _currentQueueIndex >= QueueItems.Count)
            return false;

        var recipeItem = QueueItems[_currentQueueIndex];
        var recipe = RecipeManager.GetRecipe(recipeItem.RecipeId);
        if (recipe == null)
        {
            FailQueue($"Active synthesis cannot be recovered because recipe {recipeItem.RecipeId} is unavailable.");
            return true;
        }

        var activeRecipeId = RecipeNoteExt.GetActiveCraftRecipeId();
        if (activeRecipeId.HasValue && activeRecipeId.Value != recipe.Value.RowId)
        {
            FailQueue(
                $"Active synthesis recipe {activeRecipeId.Value} does not match queued recipe {recipe.Value.RowId}.");
            return true;
        }

        _currentState = QueueState.ReadyForCraft;
        return true;
    }

    public void OnGatherComplete()
    {
        if (_currentState != QueueState.WaitingForGather)
            return;

        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Gather complete, preparing post-acquisition navigation");
        YesAlready.Lock();
        CraftingGatherBridge.DeleteTemporaryGatherList();
        if (_executionPlan != null)
            _executionPlan.RefreshFromCurrentInventory();
        BeginPostGatherNavigation();
    }

    private void BeginAcquisitionOrGather()
    {
        if (_executionPlan == null || _currentState is QueueState.Failed or QueueState.Complete)
            return;

        if (!_executionPlan.AllowMaterialAcquisition)
        {
            BeginFinalPreflight();
            return;
        }

        if (!WaitForAcquisitionDrain())
            return;

        var evaluation = CraftingAcquisitionService.Evaluate(_executionPlan);
        if (evaluation.IsLoading)
        {
            var stateChanged = _currentState != QueueState.WaitingForAcquisitionData;
            _currentState = QueueState.WaitingForAcquisitionData;
            _pauseReason = evaluation.Status;
            if (stateChanged)
                StateChanged?.Invoke(_currentState);
            return;
        }

        if (!string.IsNullOrWhiteSpace(evaluation.Snapshot.ErrorReason))
        {
            FailQueue(CraftingAcquisitionService.FormatFailure(evaluation));
            return;
        }

        if (!_executionPlan.AutoPurchaseBlockedDependencies)
        {
            BeginGatherStage();
            return;
        }

        var planning = evaluation.Planning;
        if (planning == null)
        {
            FailQueue("Automatic acquisition did not produce a planning result.");
            return;
        }
        if (!planning.IsSuccess)
        {
            FailQueue(CraftingAcquisitionService.FormatFailure(evaluation));
            return;
        }

        if (planning.SelectedPlan == null || planning.SelectedPlan.Transactions.Count == 0)
        {
            BeginGatherStage();
            return;
        }

        if (!_acquisitionRuns.TryBeginRun(out var generation))
        {
            _currentState = QueueState.WaitingForAcquisitionData;
            _pauseReason = "Waiting for the previous automatic acquisition cleanup to finish.";
            return;
        }

        var executor = GatherBuddy.CreateLiveAcquisitionExecutor(new LiveAcquisitionOptions
        {
            CurrentWorldOnly = _executionPlan.CurrentWorldOnly,
            PreferHQ = _executionPlan.PreferHQ,
            PreferVendors = _executionPlan.PreferVendors,
            PreferMarketForSpecialCurrency = _executionPlan.PreferMarketForSpecialCurrency,
            MaximumGilSpend = _executionPlan.MaximumGilSpend,
        });
        if (executor == null)
        {
            _acquisitionRuns.TryReleaseActive(generation);
            FailQueue("Automatic acquisition is unavailable because its live executor is not initialized.");
            return;
        }

        CaptureAcquisitionInventory(planning);
        var cancellation = new CancellationTokenSource();
        Task<LiveAcquisitionResult> task;
        try
        {
            task = executor.ExecuteAsync(
                new AcquisitionResult(planning),
                cancellation.Token);
        }
        catch (Exception ex)
        {
            cancellation.Dispose();
            _acquisitionRuns.TryReleaseActive(generation);
            GatherBuddy.ReleaseLiveAcquisitionExecutor(executor);
            FailQueue($"Automatic acquisition could not start: {ex.Message}");
            return;
        }

        lock (_acquisitionSync)
        {
            _acquisitionGeneration = generation;
            _acquisitionExecutor = executor;
            _acquisitionCancellation = cancellation;
            _acquisitionTask = task;
        }
        _currentState = QueueState.PurchasingDependencies;
        _pauseReason = string.Empty;
        GatherBuddy.Log.Information($"[CraftingQueueProcessor] Starting automatic acquisition with {planning.SelectedPlan.Transactions.Count} transaction(s)");
        StateChanged?.Invoke(_currentState);
    }

    private void UpdateAcquisition()
    {
        if (_acquisitionTask == null || !_acquisitionTask.IsCompleted)
            return;

        try
        {
            var result = _acquisitionTask.GetAwaiter().GetResult();
            if (result.Status is not LiveAcquisitionStatus.Completed)
            {
                FailQueue(result.Message);
                return;
            }

            GatherBuddy.Log.Information($"[CraftingQueueProcessor] Automatic acquisition complete; spent {result.GilSpent:N0} Gil");
            var acquiredAvailability = BuildVerifiedAcquiredAvailability(result);
            _executionPlan?.RegisterAcquiredAvailability(acquiredAvailability);
            ReleaseAcquisition();
            RebuildQueueAndMaterialsFromCurrentInventory();
            BeginGatherStage();
        }
        catch (Exception ex)
        {
            FailQueue($"Automatic acquisition failed: {ex.Message}");
        }
    }

    private void BeginGatherStage()
    {
        if (_executionPlan == null)
            return;

        _currentState = QueueState.WaitingForGather;
        _pauseReason = string.Empty;
        StateChanged?.Invoke(_currentState);
        try
        {
            GatherBuddy.AutoGather?.SetCraftOwnedGathering(true);
        }
        catch (Exception ex)
        {
            FailQueue($"Cannot start crafting gather stage: {ex.Message}");
            return;
        }
        var gatherTargets = SelectRequiredGatherTargets(MaterialTargets, BuildCurrentMaterialDeficits());
        CraftingGatherBridge.CreateGatherListForRequiredIngredients(gatherTargets);
    }

    internal static Dictionary<uint, int> SelectRequiredGatherTargets(
        IReadOnlyDictionary<uint, int> requiredQuantities,
        IReadOnlyDictionary<uint, int> deficits)
        => deficits
            .Where(pair => pair.Value > 0
                && requiredQuantities.TryGetValue(pair.Key, out var required)
                && required > 0)
            .ToDictionary(pair => pair.Key, pair => requiredQuantities[pair.Key]);

    private Dictionary<uint, int> BuildCurrentMaterialDeficits()
        => ComputeCurrentMaterialDeficits(
            MaterialTargets,
            IngredientDemandTargets,
            CraftingInventoryCounter.GetInventorySplitCounts);

    internal static Dictionary<uint, int> ComputeCurrentMaterialDeficits(
        IReadOnlyDictionary<uint, int> materialTargets,
        IReadOnlyDictionary<uint, IngredientQualityDemand> ingredientDemands,
        Func<uint, (int NQ, int HQ)> inventoryCounts)
    {
        ArgumentNullException.ThrowIfNull(materialTargets);
        ArgumentNullException.ThrowIfNull(ingredientDemands);
        ArgumentNullException.ThrowIfNull(inventoryCounts);

        var deficits = new Dictionary<uint, int>();
        foreach (var (itemId, requiredQuantity) in materialTargets)
        {
            if (itemId == 0 || requiredQuantity <= 0)
                continue;

            var (nq, hq) = inventoryCounts(itemId);
            var demand = ingredientDemands.GetValueOrDefault(itemId);
            var totalMissing = Math.Max(0, requiredQuantity - Math.Max(0, nq) - Math.Max(0, hq));
            var requiredHqMissing = Math.Max(0, demand.RequiredHQ - Math.Max(0, hq));
            var requiredNqMissing = Math.Max(0, demand.RequiredNQ - Math.Max(0, nq));
            var missing = Math.Max(totalMissing, requiredHqMissing + requiredNqMissing);
            if (missing > 0)
                deficits[itemId] = missing;
        }

        return deficits;
    }

    private void BeginPostGatherNavigation()
    {
        _navigationStarted = false;
        if (_executionPlan?.ReturnToHomeWorldBeforeCrafting == true && !HomeNavigationHelper.IsAtHomeWorld())
        {
            _currentState = QueueState.ReturningToHomeWorld;
            StateChanged?.Invoke(_currentState);
            return;
        }

        if (GatherBuddy.Config.GoToInnBeforeCrafting)
        {
            _currentState = QueueState.ReturningToInn;
            StateChanged?.Invoke(_currentState);
            return;
        }

        BeginFinalPreflight();
    }

    private void UpdatePostGatherNavigation()
    {
        if (Lifestream.Enabled && Lifestream.IsBusy())
            return;

        if (!_navigationStarted)
        {
            string? homeError;
            var started = _currentState == QueueState.ReturningToHomeWorld
                ? HomeNavigationHelper.TryStartReturnHomeWorld(out homeError)
                : HomeNavigationHelper.TryStartInn(out homeError);
            if (!started)
            {
                FailQueue(homeError ?? "Lifestream navigation could not be started.");
                return;
            }

            _navigationStarted = true;
            return;
        }

        if (Lifestream.Enabled && Lifestream.IsBusy())
            return;

        if (_currentState == QueueState.ReturningToHomeWorld && GatherBuddy.Config.GoToInnBeforeCrafting)
        {
            if (!HomeNavigationHelper.IsAtHomeWorld())
            {
                FailQueue("Lifestream finished, but the character did not return to the Home World.");
                return;
            }
            _navigationStarted = false;
            _currentState = QueueState.ReturningToInn;
            StateChanged?.Invoke(_currentState);
            return;
        }

        if (_currentState == QueueState.ReturningToHomeWorld && !HomeNavigationHelper.IsAtHomeWorld())
        {
            FailQueue("Lifestream finished, but the character did not return to the Home World.");
            return;
        }

        BeginFinalPreflight();
    }

    private void BeginFinalPreflight()
    {
        DisableAutoGatherSafely();
        if (_executionPlan == null)
        {
            FailQueue("Crafting execution plan was lost before final preflight.");
            return;
        }

        _executionPlan.RefreshFromCurrentInventory();
        if (!CraftingQueuePreflight.TryValidate(_executionPlan, out var failure, validatePrecrafts: true, listConsumables: _listConsumables)
            || !_executionPlan.UsesMissionProvidedMaterials
                && !CraftingQueuePreflight.TryValidateMaterials(_executionPlan, out failure))
        {
            FailQueue(failure);
            return;
        }

        _currentState = QueueState.WaitingForJobSwitch;
        StateChanged?.Invoke(_currentState);
    }

    private void FailQueue(string reason)
    {
        reason = string.IsNullOrWhiteSpace(reason)
            ? "Crafting queue stopped because its acquisition requirements could not be satisfied."
            : reason;
        _pauseReason = reason;
        _tasks.Clear();
        CancelAcquisition();
        DisableAutoGatherSafely();
        YesAlready.Unlock();
        GatherBuddy.Log.Error($"[CraftingQueueProcessor] {reason}");
        Dalamud.Chat.PrintError($"[GatherBuddy Ascended] {reason}");
        _currentState = QueueState.Failed;
        StateChanged?.Invoke(_currentState);
    }

    internal void FailFromBridge(string reason)
        => FailQueue(reason);

    private static void DisableAutoGatherSafely()
    {
        var autoGather = GatherBuddy.AutoGather;
        if (autoGather == null)
            return;

        try
        {
            autoGather.SetCraftOwnedGathering(false);
            autoGather.Enabled = false;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Failed to restore AutoGather state: {ex.Message}");
        }
    }

    private void CaptureAcquisitionInventory(AcquisitionPlanningResult planning)
    {
        _acquisitionInventoryBefore.Clear();
        _acquisitionPlannedQuantities.Clear();
        if (_executionPlan == null || planning.SelectedPlan == null)
            return;

        foreach (var (itemId, plannedQuantity) in planning.SelectedPlan.PurchasedQuantities)
        {
            if (itemId == 0
                || !_executionPlan.PrecraftsView.TryGetValue(itemId, out var precraftDemand))
                continue;

            var cappedQuantity = Math.Min(Math.Max(0, plannedQuantity), Math.Max(0, precraftDemand));
            if (cappedQuantity <= 0)
                continue;

            _acquisitionPlannedQuantities[itemId] = cappedQuantity;
            _acquisitionInventoryBefore[itemId] = CraftingInventoryCounter.GetInventorySplitCounts(itemId);
        }
    }

    private Dictionary<uint, AcquiredDependencyAvailability> BuildVerifiedAcquiredAvailability(
        LiveAcquisitionResult result)
    {
        var verified = new Dictionary<uint, AcquiredDependencyAvailability>();
        foreach (var (itemId, plannedQuantity) in _acquisitionPlannedQuantities)
        {
            var reportedQuantity = Math.Max(0, result.PurchasedQuantities.GetValueOrDefault(itemId));
            var purchaseCap = Math.Min(plannedQuantity, reportedQuantity);
            if (purchaseCap <= 0 || !_acquisitionInventoryBefore.TryGetValue(itemId, out var before))
                continue;

            var after = CraftingInventoryCounter.GetInventorySplitCounts(itemId);
            var deltaNQ = Math.Max(0, after.NQ - before.NQ);
            var deltaHQ = Math.Max(0, after.HQ - before.HQ);
            var verifiedTotal = Math.Min(purchaseCap, checked(deltaNQ + deltaHQ));
            if (verifiedTotal <= 0)
                continue;

            // Inventory deltas are authoritative for quality. HQ can satisfy
            // ordinary quantity demand; NQ is never relabeled as HQ.
            var verifiedHQ = Math.Min(deltaHQ, verifiedTotal);
            var verifiedNQ = Math.Min(deltaNQ, verifiedTotal - verifiedHQ);
            if (verifiedNQ > 0 || verifiedHQ > 0)
            {
                verified[itemId] = new AcquiredDependencyAvailability(verifiedNQ, verifiedHQ);
            }
        }

        return verified;
    }

    private void CancelAcquisition()
    {
        AcquisitionRunSnapshot run;
        TaskCompletionSource<bool>? completion;
        lock (_acquisitionSync)
        {
            run = new AcquisitionRunSnapshot(
                _acquisitionGeneration,
                _acquisitionExecutor,
                _acquisitionCancellation,
                _acquisitionTask);
            if (run.Executor == null && run.Cancellation == null && run.Task == null)
                return;
        }

        if (!_acquisitionRuns.TryBeginDrain(run.Generation, out completion)
            || completion == null)
            return;

        try
        {
            run.Cancellation?.Cancel();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Automatic acquisition cancellation callback failed: {ex.Message}");
        }

        Task drain;
        try
        {
            drain = run.Executor?.StopAsync() ?? run.Task ?? Task.CompletedTask;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Automatic acquisition stop failed: {ex.Message}");
            drain = Task.CompletedTask;
        }

        _ = FinishAcquisitionDrainAsync(run, drain, completion);
    }

    private bool WaitForAcquisitionDrain()
    {
        if (_acquisitionRuns.IsReadyToBegin())
            return true;

        var stateChanged = _currentState != QueueState.WaitingForAcquisitionData;
        _currentState = QueueState.WaitingForAcquisitionData;
        _pauseReason = "Waiting for the previous automatic acquisition cleanup to finish.";
        if (stateChanged)
            StateChanged?.Invoke(_currentState);
        return false;
    }

    private async Task FinishAcquisitionDrainAsync(
        AcquisitionRunSnapshot run,
        Task drain,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await drain.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Automatic acquisition cleanup failed: {ex.Message}");
        }
        finally
        {
            ReleaseAcquisition(run, completion.Task);
            _acquisitionRuns.TryCompleteDrain(run.Generation, completion);
        }
    }

    private void ReleaseAcquisition()
    {
        AcquisitionRunSnapshot run;
        lock (_acquisitionSync)
        {
            run = new AcquisitionRunSnapshot(
                _acquisitionGeneration,
                _acquisitionExecutor,
                _acquisitionCancellation,
                _acquisitionTask);
        }
        ReleaseAcquisition(run, null);
    }

    private void ReleaseAcquisition(AcquisitionRunSnapshot run, Task? expectedDrain)
    {
        var releaseExecutor = false;
        var releaseCancellation = false;
        lock (_acquisitionSync)
        {
            var isCurrent = _acquisitionRuns.IsCurrent(run.Generation)
                && run.Generation == _acquisitionGeneration
                && ReferenceEquals(run.Executor, _acquisitionExecutor)
                && ReferenceEquals(run.Cancellation, _acquisitionCancellation)
                && ReferenceEquals(run.Task, _acquisitionTask);
            if (expectedDrain == null && _acquisitionRuns.IsDrainPending())
                isCurrent = false;

            releaseExecutor = run.Executor != null
                && (isCurrent || !ReferenceEquals(run.Executor, _acquisitionExecutor));
            releaseCancellation = run.Cancellation != null
                && (isCurrent || !ReferenceEquals(run.Cancellation, _acquisitionCancellation));
            if (isCurrent)
            {
                _acquisitionExecutor = null;
                _acquisitionCancellation = null;
                _acquisitionTask = null;
                _acquisitionInventoryBefore.Clear();
                _acquisitionPlannedQuantities.Clear();
                _acquisitionRuns.TryReleaseActive(run.Generation);
            }
        }

        if (releaseCancellation)
            run.Cancellation!.Dispose();
        if (releaseExecutor)
            GatherBuddy.ReleaseLiveAcquisitionExecutor(run.Executor);
    }

    public void Update()
    {
        if (_paused)
        {
            if (_resumeRequest.Requested)
                TryCompleteResume();
            return;
        }

        ProcessTasks();
        
        switch (_currentState)
        {
            case QueueState.Idle:
                break;
            case QueueState.NavigatingToRetainerBell:
                break;
            case QueueState.WithdrawingFromRetainer:
                break;
            case QueueState.WaitingForGather:
                break;
            case QueueState.WaitingForAcquisitionData:
                BeginAcquisitionOrGather();
                break;
            case QueueState.PurchasingDependencies:
                UpdateAcquisition();
                break;
            case QueueState.ReturningToHomeWorld:
            case QueueState.ReturningToInn:
                UpdatePostGatherNavigation();
                break;
            case QueueState.WaitingForJobSwitch:
                UpdateJobSwitch();
                break;
            case QueueState.Repairing:
                break;
            case QueueState.ExtractingMateria:
                break;
            case QueueState.WaitingForRaphaelSolution:
                CheckRaphaelSolutionReady();
                break;
            case QueueState.ReadyForCraft:
                StartNextCraft();
                break;
            case QueueState.Crafting:
                if (CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.QuickSynthesis &&
                    (NeedsRepair() || NeedsMateria()))
                {
                    GatherBuddy.Log.Information("[CraftingQueueProcessor] Interrupting quick synth for repair/materia");
                    CloseQuickSynthWindow();
                }
                else if (CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleNormal)
                {
                    if (HandlePreparationFailure())
                        break;
                    if (_craftHangSince == DateTime.MinValue)
                    {
                        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Game returned to IdleNormal while in Crafting state, starting hang watchdog");
                        _craftHangSince = DateTime.Now;
                    }
                    else if ((DateTime.Now - _craftHangSince).TotalSeconds > 3.0)
                    {
                        if (IsInventoryFull())
                        {
                            _craftHangSince = DateTime.MinValue;
                            PauseForInventoryFull("Queue paused because crafting cannot continue with a full inventory.");
                            break;
                        }
                        GatherBuddy.Log.Warning("[CraftingQueueProcessor] Craft hang detected: game idle but craft never started, auto-recovering to WaitingForJobSwitch");
                        _craftHangSince = DateTime.MinValue;
                        _currentState = QueueState.WaitingForJobSwitch;
                        StateChanged?.Invoke(_currentState);
                    }
                }
                else
                {
                    _craftHangSince = DateTime.MinValue;
                }
                break;
            case QueueState.Complete:
            case QueueState.Failed:
                break;
        }
    }

    private void ProcessTasks()
    {
        while (_tasks.Count > 0)
        {
            var result = _tasks[0]();
            switch (result)
            {
                case CraftingTasks.TaskResult.Done:
                    _tasks.RemoveAt(0);
                    break;
                case CraftingTasks.TaskResult.Retry:
                    return;
                case CraftingTasks.TaskResult.Abort:
                    _tasks.Clear();
                    if (_currentState == QueueState.Repairing)
                    {
                        GatherBuddy.Log.Warning("[CraftingQueueProcessor] Repair task aborted, recovering to WaitingForJobSwitch");
                        CraftingTasks.ResetRepairState();
                        _currentState = QueueState.WaitingForJobSwitch;
                        StateChanged?.Invoke(_currentState);
                    }
                    return;
            }
        }
    }

    private unsafe void UpdateJobSwitch()
    {
        if (_jobSwitchFailure is { Length: > 0 } switchFailure)
        {
            _jobSwitchFailure = null;
            FailQueue(switchFailure);
            return;
        }

        if (_currentQueueIndex >= QueueItems.Count)
        {
            CompleteQueue();
            return;
        }

        while (_currentQueueIndex < QueueItems.Count && QueueItems[_currentQueueIndex].Options.Skipping)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] UpdateJobSwitch: skipping recipe {QueueItems[_currentQueueIndex].RecipeId}");
            _currentQueueIndex++;
        }

        if (_currentQueueIndex >= QueueItems.Count)
        {
            CompleteQueue();
            return;
        }

        var recipeItem = QueueItems[_currentQueueIndex];
        var recipe = RecipeManager.GetRecipe(recipeItem.RecipeId);
        if (recipe == null)
        {
            GatherBuddy.Log.Error($"[CraftingQueueProcessor] Could not find recipe {recipeItem.RecipeId}");
            SkipToNextRecipe();
            return;
        }

        var requiredJob = (uint)(recipe.Value.CraftType.RowId + 8);
        var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;

        if (currentJob != requiredJob)
        {
            var now = DateTime.UtcNow;
            if (_jobSwitchStartedAt == DateTime.MinValue)
                _jobSwitchStartedAt = now;
            var busy = Dalamud.Conditions[ConditionFlag.BetweenAreas]
                || Dalamud.Conditions[ConditionFlag.BetweenAreas51]
                || Lifestream.Enabled && Lifestream.IsBusy()
                || !GenericHelpers.IsScreenReady();
            if (busy)
            {
                var failure = GetJobSwitchWatchdogFailure(
                    now - _jobSwitchStartedAt,
                    TimeSpan.Zero,
                    _jobSwitchAttempts,
                    busy: true);
                if (failure != null)
                {
                    FailQueue(failure);
                    return;
                }
                GatherBuddy.Log.Debug("[CraftingQueueProcessor] Deferring job switch: zone transition, Lifestream active, or screen not ready");
                _jobSwitchRequestedFor = 0u;
                _jobSwitchReadySince = DateTime.MinValue;
                return;
            }

            if (_jobSwitchReadySince == DateTime.MinValue)
                _jobSwitchReadySince = now;
            var watchdogFailure = GetJobSwitchWatchdogFailure(
                now - _jobSwitchStartedAt,
                now - _jobSwitchReadySince,
                _jobSwitchAttempts,
                busy: false);
            if (watchdogFailure != null)
            {
                FailQueue(watchdogFailure);
                return;
            }

            if (_tasks.Count == 0 && _jobSwitchRequestedFor != requiredJob)
            {
                GatherBuddy.Log.Information($"[CraftingQueueProcessor] Job switch needed: {requiredJob}");
                bool needExitCraft = CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleBetween;

                if (needExitCraft)
                {
                    GatherBuddy.Log.Debug("[CraftingQueueProcessor] Queueing TaskExitCraft before job switch");
                    _tasks.Add(() => CraftingTasks.TaskExitCraft());
                }

                _tasks.Add(() =>
                {
                    if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51])
                    {
                        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Waiting for zone transition to complete before job switch");
                        return CraftingTasks.TaskResult.Retry;
                    }
                    if (Lifestream.Enabled && Lifestream.IsBusy())
                    {
                        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Waiting for Lifestream to finish before job switch");
                        return CraftingTasks.TaskResult.Retry;
                    }
                    if (!GenericHelpers.IsScreenReady())
                    {
                        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Waiting for screen ready before job switch");
                        return CraftingTasks.TaskResult.Retry;
                    }
                    if (!TrySwitchJob(requiredJob, out var failureReason))
                        _jobSwitchFailure = failureReason;
                    return CraftingTasks.TaskResult.Done;
                });
                _jobSwitchRequestedFor = requiredJob;
                _jobSwitchRequestedAt = DateTime.UtcNow;
                _jobSwitchAttempts++;
            }
            else if (_tasks.Count == 0 && _jobSwitchRequestedFor == requiredJob)
            {
                if (DateTime.UtcNow - _jobSwitchRequestedAt >= TimeSpan.FromSeconds(2))
                {
                    GatherBuddy.Log.Debug("[CraftingQueueProcessor] Job switch not acknowledged after 2 seconds, resetting for retry");
                    _jobSwitchRequestedFor = 0u;
                    _jobSwitchRequestedAt = DateTime.MinValue;
                }
            }
        }
        else
        {
            ResetJobSwitchWatchdog();
            TransitionToRaphaelOrCraft();
        }
    }

    internal static string? GetJobSwitchWatchdogFailure(
        TimeSpan totalElapsed,
        TimeSpan readyElapsed,
        int attempts,
        bool busy)
    {
        if (totalElapsed >= TimeSpan.FromMinutes(2))
            return "Job switch timed out after two minutes without reaching the required crafting job.";
        if (!busy && (readyElapsed >= TimeSpan.FromSeconds(30) || attempts >= 5))
            return $"Job switch failed after {attempts} equip attempt(s) while the game was ready.";
        return null;
    }

    private void ResetJobSwitchWatchdog()
    {
        _jobSwitchRequestedFor = 0u;
        _jobSwitchRequestedAt = DateTime.MinValue;
        _jobSwitchStartedAt = DateTime.MinValue;
        _jobSwitchReadySince = DateTime.MinValue;
        _jobSwitchAttempts = 0;
        _jobSwitchFailure = null;
    }

    private void TransitionToRaphaelOrCraft()
    {
        if (NeedsMateria())
        {
            GatherBuddy.Log.Information("[CraftingQueueProcessor] Equipment has 100% spiritbond, extracting materia");
            QueueMateriaTasks();
            _currentState = QueueState.ExtractingMateria;
            StateChanged?.Invoke(_currentState);
            return;
        }

        if (NeedsRepair())
        {
            GatherBuddy.Log.Information("[CraftingQueueProcessor] Equipment needs repair before crafting");
            QueueRepairTasks();
            _currentState = QueueState.Repairing;
            StateChanged?.Invoke(_currentState);
            return;
        }


        if (_currentQueueIndex < QueueItems.Count)
        {
            var currentItem = QueueItems[_currentQueueIndex];
            var currentRecipe = RecipeManager.GetRecipe(currentItem.RecipeId);
            if (currentRecipe != null)
            {
                var executionContext = CraftingContextResolver.ResolveExecutionContext(currentItem, currentRecipe.Value, _listConsumables);
                if (_raphaelCoordinator == null || !CraftingContextResolver.UsesRaphaelSolver(executionContext))
                {
                    _currentState = QueueState.ReadyForCraft;
                    StateChanged?.Invoke(_currentState);
                    return;
                }
                var r = currentRecipe.Value;
                var isNQOnly = !r.CanHq && !r.IsExpert && !r.ItemResult.Value.AlwaysCollectable && r.RequiredQuality == 0;
                if (isNQOnly || executionContext.UseQuickSynthesis)
                {
                    _currentState = QueueState.ReadyForCraft;
                    StateChanged?.Invoke(_currentState);
                    return;
                }
            }
        }

        if (_raphaelCoordinator == null)
        {
            _currentState = QueueState.ReadyForCraft;
            StateChanged?.Invoke(_currentState);
            return;
        }

        if (_currentQueueIndex >= QueueItems.Count)
        {
            _currentState = QueueState.ReadyForCraft;
            StateChanged?.Invoke(_currentState);
            return;
        }

        var recipeItem = QueueItems[_currentQueueIndex];
        var currentRequest = BuildRaphaelRequestForItem(recipeItem);
        if (currentRequest != null && !_enqueuedRaphaelRequests.ContainsKey(currentRequest.GetKey()))
        {
            if (_enqueuedRaphaelRequests.Values.Any(r => r.RecipeId == currentRequest.RecipeId))
                GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Enqueuing additional Raphael variant for recipe {currentRequest.RecipeId} (key: {currentRequest.GetKey()})");

            _enqueuedRaphaelRequests[currentRequest.GetKey()] = currentRequest;
            _raphaelCoordinator!.EnqueueSolvesFromRequests(new[] { currentRequest });
            _currentState = QueueState.WaitingForRaphaelSolution;
            StateChanged?.Invoke(_currentState);
            return;
        }

        if (IsRaphaelSolutionReady(recipeItem))
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Raphael solution ready for recipe {recipeItem.RecipeId}");
            _currentState = QueueState.ReadyForCraft;
            StateChanged?.Invoke(_currentState);
        }
        else if (IsRaphaelSolutionFailed(recipeItem))
        {
            SkipFailedRaphaelItem(recipeItem);
        }
        else
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Waiting for Raphael solution for recipe {recipeItem.RecipeId}");
            _currentState = QueueState.WaitingForRaphaelSolution;
            StateChanged?.Invoke(_currentState);
        }
    }

    private void CheckRaphaelSolutionReady()
    {
        if (_currentQueueIndex >= QueueItems.Count)
        {
            _currentState = QueueState.ReadyForCraft;
            StateChanged?.Invoke(_currentState);
            return;
        }

        var recipeItem = QueueItems[_currentQueueIndex];

        if (IsRaphaelSolutionReady(recipeItem))
        {
            GatherBuddy.Log.Information($"[CraftingQueueProcessor] Raphael solution ready for recipe {recipeItem.RecipeId}");
            _currentState = QueueState.ReadyForCraft;
            StateChanged?.Invoke(_currentState);
        }
        else if (IsRaphaelSolutionFailed(recipeItem))
        {
            SkipFailedRaphaelItem(recipeItem);
        }
        else
        {
            var request = BuildRaphaelRequestForItem(recipeItem);
            if (request != null && _raphaelCoordinator != null)
                _raphaelCoordinator.ReenqueueIfMissing(request);
        }
    }
    
    private bool IsRaphaelSolutionReady(CraftingListItem recipeItem)
    {
        if (_raphaelCoordinator == null)
            return false;
        
        var request = BuildRaphaelRequestForItem(recipeItem);
        if (request == null)
            return false;
        
        return _raphaelCoordinator.TryGetSolution(request, out var solution) && solution != null && !solution.IsFailed;
    }
    
    private bool IsRaphaelSolutionFailed(CraftingListItem recipeItem)
    {
        if (_raphaelCoordinator == null)
            return false;
        var request = BuildRaphaelRequestForItem(recipeItem);
        return request != null && _raphaelCoordinator.HasFailedSolution(request, out _);
    }

    private bool EnsureRaphaelSolutionReadyForCurrentCraft(
        CraftingListItem recipeItem,
        Recipe recipe,
        CraftingExecutionContext executionContext,
        bool activeSynthesis = false)
    {
        if (_raphaelCoordinator == null || !CraftingContextResolver.UsesRaphaelSolver(executionContext))
            return true;

        var isNQOnly = !recipe.CanHq && !recipe.IsExpert && !recipe.ItemResult.Value.AlwaysCollectable && recipe.RequiredQuality == 0;
        if (isNQOnly)
            return true;

        var currentRequest = BuildRaphaelRequestForItem(recipeItem);
        if (currentRequest == null)
            return true;

        if (!_enqueuedRaphaelRequests.ContainsKey(currentRequest.GetKey()))
        {
            if (_enqueuedRaphaelRequests.Values.Any(r => r.RecipeId == currentRequest.RecipeId))
                GatherBuddy.Log.Debug($@"[CraftingQueueProcessor] Raphael request changed before craft start for recipe {currentRequest.RecipeId}, enqueueing key {currentRequest.GetKey()}");

            _enqueuedRaphaelRequests[currentRequest.GetKey()] = currentRequest;
            _raphaelCoordinator.EnqueueSolvesFromRequests(new[] { currentRequest });
            _currentState = QueueState.WaitingForRaphaelSolution;
            StateChanged?.Invoke(_currentState);
            return false;
        }

        if (_raphaelCoordinator.TryGetSolution(currentRequest, out var solution) && solution != null && !solution.IsFailed)
            return true;

        if (_raphaelCoordinator.HasFailedSolution(currentRequest, out _))
        {
            if (activeSynthesis)
                FailQueue($"Active synthesis cannot be recovered because Raphael seed generation failed for recipe {recipeItem.RecipeId}.");
            else
                SkipFailedRaphaelItem(recipeItem);
            return false;
        }
        _raphaelCoordinator.ReenqueueIfMissing(currentRequest);
        _currentState = QueueState.WaitingForRaphaelSolution;
        StateChanged?.Invoke(_currentState);
        return false;
    }

    private unsafe void StartNextCraft()
    {
        if (_currentQueueIndex >= QueueItems.Count)
        {
            CompleteQueue();
            return;
        }

        if (_consumableDelayUntil != DateTime.MinValue)
        {
            if (DateTime.Now < _consumableDelayUntil)
                return;
            _consumableDelayUntil = DateTime.MinValue;
        }

        if (CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.QuickSynthesis)
        {
            var isComplete = IsQuickSynthesisComplete();
            if (isComplete)
            {
                GatherBuddy.Log.Information($"[CraftingQueueProcessor] Quick synthesis complete, closing window");
                CloseQuickSynthWindow();
            }
            return;
        }
        
        if (CraftingGameInterop.CurrentState != CraftingGameInterop.CraftState.IdleNormal && 
            CraftingGameInterop.CurrentState != CraftingGameInterop.CraftState.IdleBetween)
            return;

        if (IsInventoryFull())
        {
            PauseForInventoryFull("Queue paused because crafting cannot start with a full inventory.");
            return;
        }

        var recipeItem = QueueItems[_currentQueueIndex];

        if (recipeItem.Options.Skipping)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Skipping recipe {recipeItem.RecipeId} (Skipping flag)");
            SkipToNextRecipe();
            return;
        }

        var recipe = RecipeManager.GetRecipe(recipeItem.RecipeId);
        if (recipe == null)
        {
            GatherBuddy.Log.Error($"[CraftingQueueProcessor] Could not find recipe {recipeItem.RecipeId}");
            SkipToNextRecipe();
            return;
        }

        var executionContext = CraftingContextResolver.ResolveExecutionContext(recipeItem, recipe.Value, _listConsumables);
        if (SynthesisReader.IsSynthesisWindowOpen())
        {
            CraftingGatherBridge.PersistCurrentCraftOwnership(recipe.Value.RowId);
            if (executionContext.EffectiveSolverMode is not (VulcanSolverMode.Donatello or VulcanSolverMode.Gabriel)
                && !EnsureRaphaelSolutionReadyForCurrentCraft(
                    recipeItem,
                    recipe.Value,
                    executionContext,
                    activeSynthesis: true))
                return;
            if (CraftingGameInterop.TryAdoptLiveCraft(recipe.Value, executionContext, out var failureReason))
            {
                UpdateCurrentRecipeTracking(1);
                _lastCraftWasQuickSynth = false;
                _currentState = QueueState.Crafting;
                StateChanged?.Invoke(_currentState);
                return;
            }

            if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction]
                || failureReason.Contains("temporarily unavailable", StringComparison.Ordinal))
                return;

            FailQueue($"Active synthesis cannot be recovered: {failureReason}.");
            return;
        }
        var consumableSettings = executionContext.ConsumableSettings;
        if (consumableSettings != null)
        {
            var allApplied = ConsumableChecker.ApplyConsumables(consumableSettings);
            if (!allApplied)
            {
                if (CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleBetween)
                {
                    GatherBuddy.Log.Debug("[CraftingQueueProcessor] Need to apply consumables, exiting crafting log first");
                    _tasks.Add(() => CraftingTasks.TaskExitCraft());
                    _consumableDelayUntil = DateTime.Now.AddSeconds(5);
                }
                else
                {
                    GatherBuddy.Log.Debug("[CraftingQueueProcessor] Applied consumables, delaying craft start by 3 seconds");
                    _consumableDelayUntil = DateTime.Now.AddSeconds(3);
                }
                return;
            }
        }

        if (recipeItem.Options.NQOnly && recipe.Value.CanQuickSynth && !executionContext.HasCraftedBefore)
            GatherBuddy.Log.Information($"[CraftingQueueProcessor] Recipe not yet crafted — using normal craft first: {recipe.Value.ItemResult.Value.Name.ExtractText()}");
        var useQuickSynthesis = executionContext.UseQuickSynthesis;
        var qualityPolicy = executionContext.QualityPolicy;
        uint craftQuantity = (uint)recipeItem.Quantity;
        
        if (useQuickSynthesis)
        {
            var batchConsumableSettings = consumableSettings;
            var batchQualityPolicy = qualityPolicy;
            var maxBatchSize = 1;
            for (var i = _currentQueueIndex + 1; i < QueueItems.Count && maxBatchSize < 99; i++)
            {
                var nextItem = QueueItems[i];
                if (!CanBatchQuickSynth(recipeItem, nextItem, recipe.Value, batchConsumableSettings, batchQualityPolicy))
                    break;
                maxBatchSize++;
            }

            craftQuantity = (uint)maxBatchSize;
            GatherBuddy.Log.Information($"[CraftingQueueProcessor] Using Quick Synthesis for {recipe.Value.ItemResult.Value.Name.ExtractText()} x{craftQuantity}");
        }
        
        UpdateCurrentRecipeTracking((int)craftQuantity);

        CraftingGameInterop.SetQualityPolicy(qualityPolicy);

        var forceProgressOnlyUnlockCraft = executionContext.ForceProgressOnlyUnlockCraft;

        if (forceProgressOnlyUnlockCraft)
        {
            GatherBuddy.Log.Debug(
                $"[CraftingQueueProcessor] Forcing ProgressOnly solver for unlock craft of {recipe.Value.ItemResult.Value.Name.ExtractText()} to preserve NQ output");
        }

        var selectedMacroId = executionContext.SelectedMacroId;
        CraftingGameInterop.SetSelectedMacro(selectedMacroId);
        CraftingGameInterop.SetDonatelloOptions(executionContext.DonatelloOptions);
        if (!string.IsNullOrEmpty(selectedMacroId))
        {
            GatherBuddy.Log.Information($"[CraftingQueueProcessor] Using macro: {selectedMacroId}");
        }
        var effectiveSolverMode = executionContext.EffectiveSolverMode;
        if (!EnsureRaphaelSolutionReadyForCurrentCraft(recipeItem, recipe.Value, executionContext))
            return;
        CraftingGameInterop.ReloadSolversForCraft(effectiveSolverMode, !forceProgressOnlyUnlockCraft);
        GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Effective solver mode for this craft: {effectiveSolverMode}");

        _lastCraftWasQuickSynth = useQuickSynthesis;
        GatherBuddy.Log.Information($"[CraftingQueueProcessor] Starting craft {_currentQueueIndex + 1}/{QueueItems.Count}: {recipe.Value.ItemResult.Value.Name} x{craftQuantity}");
        CraftingGatherBridge.PersistCurrentCraftOwnership(recipe.Value.RowId);
        CraftingGameInterop.StartCraft(recipe.Value, craftQuantity, useQuickSynthesis);
        _currentState = QueueState.Crafting;
        StateChanged?.Invoke(_currentState);
    }

    private bool CanBatchQuickSynth(
        CraftingListItem currentItem,
        CraftingListItem nextItem,
        Recipe recipe,
        RecipeCraftSettings? currentConsumableSettings,
        CraftingQualityPolicy currentQualityPolicy)
    {
        if (nextItem.Options.Skipping || nextItem.RecipeId != currentItem.RecipeId)
            return false;

        var nextExecutionContext = CraftingContextResolver.ResolveExecutionContext(nextItem, recipe, _listConsumables);
        if (!nextExecutionContext.UseQuickSynthesis)
            return false;

        if (!AreConsumableSettingsEquivalent(currentConsumableSettings, nextExecutionContext.ConsumableSettings))
            return false;

        return AreQualityPoliciesEquivalent(currentQualityPolicy, nextExecutionContext.QualityPolicy);
    }

    private static bool AreConsumableSettingsEquivalent(RecipeCraftSettings? left, RecipeCraftSettings? right)
    {
        if (left == null || right == null)
            return left == right;

        return left.FoodItemId == right.FoodItemId
            && left.FoodHQ == right.FoodHQ
            && left.MedicineItemId == right.MedicineItemId
            && left.MedicineHQ == right.MedicineHQ
            && left.ManualItemId == right.ManualItemId
            && left.SquadronManualItemId == right.SquadronManualItemId;
    }

    private static bool AreQualityPoliciesEquivalent(CraftingQualityPolicy left, CraftingQualityPolicy right)
    {
        if (left.OverrideMode != right.OverrideMode || left.HasExplicitHQRequirements != right.HasExplicitHQRequirements)
            return false;

        if (left.IngredientDemands.Count != right.IngredientDemands.Count)
            return false;

        foreach (var (itemId, leftDemand) in left.IngredientDemands)
        {
            if (!right.IngredientDemands.TryGetValue(itemId, out var rightDemand) || leftDemand != rightDemand)
                return false;
        }

        return true;
    }


    private void OnQuickSynthProgress(int current, int max)
    {
        if (current == 0)
            return;
        
        GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Quick synth progress: {current}/{max}, incrementing index");
        _currentQueueIndex++;
        _currentProcessedRecipeCount++;
        
        if (current == max)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Quick synth batch complete");
        }
    }
    
    public void OnCraftFinished(Recipe? recipe, bool cancelled)
    {
        if (_currentState != QueueState.Crafting)
            return;

        _craftHangSince = DateTime.MinValue;

        if (recipe != null)
            _missingIngredientFailures.Remove(recipe.Value.RowId);

        if (cancelled)
        {
            if (IsInventoryFull())
            {
                PauseForInventoryFull("Queue paused because the inventory filled during crafting.");
                return;
            }
            GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Craft cancelled at index {_currentQueueIndex}");
            CompleteQueue();
            return;
        }

        if (!_lastCraftWasQuickSynth)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Normal craft completed, moving to next");
            _currentQueueIndex++;
            UpdateCurrentRecipeTracking();
        }
        else
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Quick synth batch completed (index already advanced by progress events)");
        }

        if (_currentQueueIndex >= QueueItems.Count)
        {
            CompleteQueue();
        }
        else
        {
            _currentState = QueueState.WaitingForJobSwitch;
            StateChanged?.Invoke(_currentState);
        }
    }

    private bool HandlePreparationFailure()
    {
        if (!CraftingGameInterop.TryConsumePreparationFailure(out var failure))
            return false;

        if (_currentQueueIndex >= QueueItems.Count)
            return false;

        var currentRecipeId = QueueItems[_currentQueueIndex].RecipeId;
        if (failure.RecipeId != currentRecipeId)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Ignoring stale preparation failure for recipe {failure.RecipeId}; current recipe is {currentRecipeId}");
            return false;
        }

        _craftHangSince = DateTime.MinValue;
        var recipe = RecipeManager.GetRecipe(failure.RecipeId);
        var itemName = recipe != null ? recipe.Value.ItemResult.Value.Name.ExtractText() : $"Recipe {failure.RecipeId}";
        var priorFailures = _missingIngredientFailures.GetValueOrDefault(failure.RecipeId);
        var failureContext = failure.Reason switch
        {
            CraftingGameInterop.CraftPreparationFailureReason.MissingMaterialsUnableToQuickSynth => "quick synthesis material pre-check",
            _ => "RecipeNote ingredient assignment",
        };

        if (priorFailures == 0)
        {
            _missingIngredientFailures[failure.RecipeId] = 1;
            GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Missing materials caused {failureContext} failure for '{itemName}' (recipe {failure.RecipeId}): {failure.Details}. Retrying once before skipping remaining instances.");
            _currentState = QueueState.WaitingForJobSwitch;
            StateChanged?.Invoke(_currentState);
            return true;
        }
        GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Missing materials caused {failureContext} to fail again for '{itemName}' (recipe {failure.RecipeId}): {failure.Details}. Skipping this and remaining instances of the recipe.");
        SkipRemainingRecipeInstances(failure.RecipeId);
        return true;
    }

    private void SkipRemainingRecipeInstances(uint recipeId)
    {
        var skippedCount = 0;
        for (var i = _currentQueueIndex; i < QueueItems.Count; i++)
        {
            var queueItem = QueueItems[i];
            if (queueItem.RecipeId != recipeId || queueItem.Options.Skipping)
                continue;

            queueItem.Options.Skipping = true;
            skippedCount++;
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Marked queue index {i} for recipe {recipeId} as skipped after repeated missing-material preparation failure");
        }

        _missingIngredientFailures.Remove(recipeId);
        GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Marked {skippedCount} remaining instance(s) of recipe {recipeId} to skip");
        SkipToNextRecipe();
    }


    private void SkipFailedRaphaelItem(CraftingListItem recipeItem)
    {
        var recipeId = recipeItem.RecipeId;
        var recipe = RecipeManager.GetRecipe(recipeId);
        var itemName = recipe != null ? recipe.Value.ItemResult.Value.Name.ExtractText() : $"Recipe {recipeId}";

        string? failureReason = "unknown";
        if (_raphaelCoordinator != null)
        {
            var request = BuildRaphaelRequestForItem(recipeItem);
            if (request != null)
                _raphaelCoordinator.HasFailedSolution(request, out failureReason);
        }

        GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Skipping '{itemName}' (recipe {recipeId}) - Raphael solution failed: {failureReason ?? "unknown"}");
        _currentQueueIndex++;

        if (_currentQueueIndex >= QueueItems.Count)
            CompleteQueue();
        else
        {
            _currentState = QueueState.WaitingForJobSwitch;
            StateChanged?.Invoke(_currentState);
        }
    }

    private RaphaelSolveRequest? BuildRaphaelRequestForItem(CraftingListItem recipeItem)
    {
        var recipeId = recipeItem.RecipeId;
        var recipe = RecipeManager.GetRecipe(recipeId);
        if (recipe == null)
            return null;
        var isNQOnly = !recipe.Value.CanHq && !recipe.Value.IsExpert && !recipe.Value.ItemResult.Value.AlwaysCollectable && recipe.Value.RequiredQuality == 0;
        if (isNQOnly)
            return null;

        var executionContext = CraftingContextResolver.ResolveExecutionContext(recipeItem, recipe.Value, _listConsumables);
        if (!CraftingContextResolver.UsesRaphaelSolver(executionContext))
            return null;

        if (!CraftingContextResolver.TryBuildSimulationContext(
                recipe.Value,
                executionContext,
                CraftingStatsSource.PreferCurrentJobStats,
                out var simulationContext))
            return null;

        return simulationContext.RaphaelRequest;
    }

    private void SkipToNextRecipe()
    {
        _currentQueueIndex++;
        if (_currentQueueIndex >= QueueItems.Count)
        {
            CompleteQueue();
        }
        else
        {
            _currentState = QueueState.WaitingForJobSwitch;
            StateChanged?.Invoke(_currentState);
        }
    }

    private void CompleteQueue()
    {
        GatherBuddy.Log.Information($"[CraftingQueueProcessor] Queue complete!");
        YesAlready.Unlock();
        DisableAutoGatherSafely();
        
        var craftState = CraftingGameInterop.CurrentState;
        bool needExitCraft = craftState == CraftingGameInterop.CraftState.IdleBetween ||
                            craftState == CraftingGameInterop.CraftState.WaitFinish ||
                            craftState == CraftingGameInterop.CraftState.QuickSynthesis;
        if (needExitCraft)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Queueing TaskExitCraft to close crafting log");
            _tasks.Add(() => CraftingTasks.TaskExitCraft());
        }
        
        _currentState = QueueState.Complete;
        StateChanged?.Invoke(_currentState);
        QueueCompleted?.Invoke();
    }

    private string GetJobName(uint jobId)
    {
        return jobId switch
        {
            8 => "Carpenter",
            9 => "Blacksmith",
            10 => "Armorer",
            11 => "Goldsmith",
            12 => "Leatherworker",
            13 => "Weaver",
            14 => "Alchemist",
            15 => "Culinarian",
            _ => $"Job {jobId}"
        };
    }

    private unsafe bool TrySwitchJob(uint jobId, out string failureReason)
    {
        try
        {
            var gearsetModule = RaptureGearsetModule.Instance();
            if (gearsetModule == null)
            {
                failureReason = "Cannot switch crafting job because the gearset module is unavailable.";
                return false;
            }

            if (GearsetStatsReader.TryResolveExistingGearsetIndex(gearsetModule, jobId, out var gearsetIndex))
            {
                gearsetModule->EquipGearset(gearsetIndex);
                GatherBuddy.Log.Information($"Equipped gearset {gearsetIndex} for job {jobId}");
                failureReason = string.Empty;
                return true;
            }

            var jobName = GetJobName(jobId);
            failureReason = $"Cannot continue crafting: no gearset exists for {jobName} (job {jobId}).";
            return false;
        }
        catch (Exception ex)
        {
            failureReason = $"Failed to switch to crafting job {jobId}: {ex.Message}";
            return false;
        }
    }

    private unsafe void EnqueueRaphaelSolvesFromCraftStates(List<CraftingListItem> queue)
    {
        if (_raphaelCoordinator == null)
            return;

        var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (recipeSheet == null)
        {
            GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Cannot get recipe sheet for Raphael enqueue");
            return;
        }

        var requests = new List<RaphaelSolveRequest>();
        foreach (var item in queue)
        {
            try
            {
                if (!recipeSheet.TryGetRow(item.RecipeId, out var recipe))
                    continue;

                var executionContext = CraftingContextResolver.ResolveExecutionContext(item, recipe, _listConsumables);
                if (!CraftingContextResolver.UsesRaphaelSolver(executionContext))
                    continue;
                var isNQOnly = !recipe.CanHq && !recipe.IsExpert && !recipe.ItemResult.Value.AlwaysCollectable && recipe.RequiredQuality == 0;
                if (isNQOnly || executionContext.UseQuickSynthesis)
                    continue;

                if (!CraftingContextResolver.TryBuildSimulationContext(recipe, executionContext, CraftingStatsSource.AlwaysGearsetStats, out var simulationContext))
                {
                    var requiredJob = (uint)(recipe.CraftType.RowId + 8);
                    GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Could not read gearset stats for job {requiredJob}, no gearset found");
                    continue;
                }

                requests.Add(simulationContext.RaphaelRequest);
                _enqueuedRaphaelRequests.TryAdd(simulationContext.RaphaelRequest.GetKey(), simulationContext.RaphaelRequest);
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Failed to read gearset stats for recipe {item.RecipeId}: {ex.Message}");
            }
        }

        if (requests.Count > 0)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Enqueuing {requests.Count} requests with effective consumables");
            _raphaelCoordinator.ClearIfAutoEnabled();
            _raphaelCoordinator.EnqueueSolvesFromRequests(requests, RaphaelSolvePriority.Background);
        }
    }


    private bool NeedsRepair()
    {
        if (!GatherBuddy.Config.VulcanRepairConfig.Enabled)
            return false;

        var repairThreshold = GatherBuddy.Config.VulcanRepairConfig.RepairThreshold;
        return RepairManager.NeedsRepair(repairThreshold);
    }

    private unsafe void QueueRepairTasks()
    {
        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Queueing repair tasks");
        CraftingTasks.ResetRepairState();
        
        bool needExitCraft = CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleBetween;
        if (needExitCraft)
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Queueing TaskExitCraft before repair");
            _tasks.Add(() => CraftingTasks.TaskExitCraft());
        }

        var canSelfRepair = RepairManager.CanRepairAny();
        var prioritizeNPC = GatherBuddy.Config.VulcanRepairConfig.PrioritizeNPCRepair;
        var preferredNPC = GatherBuddy.Config.VulcanRepairConfig.PreferredRepairNPC;
        var repairPrice = RepairManager.GetNPCRepairPrice();
        var gilCount = InventoryManager.Instance()->GetInventoryItemCount(1);
        var npcRoute = RepairNPCHelper.FindBestRepairRoute(prioritizeNPC ? preferredNPC : null);
        if (npcRoute.HasValue
            && (ulong)gilCount < (ulong)repairPrice + npcRoute.Value.TeleportCost
            && prioritizeNPC
            && preferredNPC != null)
        {
            npcRoute = RepairNPCHelper.FindBestRepairRoute();
        }

        var canAffordNPC = npcRoute.HasValue
            && (ulong)gilCount >= (ulong)repairPrice + npcRoute.Value.TeleportCost;

        if (prioritizeNPC && canAffordNPC && npcRoute is { } prioritizedRoute)
        {
            QueueNPCRepairTasks(prioritizedRoute, repairPrice);
            return;
        }

        if (canSelfRepair)
        {
            GatherBuddy.Log.Information("[CraftingQueueProcessor] Using self-repair");
            _tasks.Add(() => CraftingTasks.TaskOpenRepairWindow());
            _tasks.Add(() => CraftingTasks.TaskExecuteRepair(isSelfRepair: true));
            _tasks.Add(() => CraftingTasks.TaskWaitForRepairAutoClose());
            _tasks.Add(() => CraftingTasks.TaskCloseRepairWindow());
            _tasks.Add(() => { TransitionFromRepairComplete(); return CraftingTasks.TaskResult.Done; });
            return;
        }

        if (!prioritizeNPC && canAffordNPC && npcRoute is { } fallbackRoute)
        {
            QueueNPCRepairTasks(fallbackRoute, repairPrice);
            return;
        }

        var reason = !npcRoute.HasValue
            ? "no reachable repair NPC is available"
            : $"NPC repair and travel cost {(ulong)repairPrice + npcRoute.Value.TeleportCost} gil, but only {gilCount} gil is available";
        GatherBuddy.Log.Error($"[CraftingQueueProcessor] Cannot repair: self-repair unavailable and {reason}");
        _tasks.Add(() => { CompleteQueue(); return CraftingTasks.TaskResult.Abort; });
    }

    private void QueueNPCRepairTasks(RepairNPCHelper.RepairNPCRoute route, int repairPrice)
    {
        GatherBuddy.Log.Information(
            $"[CraftingQueueProcessor] Using NPC repair at {route.NPC.Name} " +
            $"(repair: {repairPrice} gil, teleport: {route.TeleportCost} gil)");
        _tasks.Add(() => CraftingTasks.TaskNavigateToRepairNPC(route.NPC, route.AetheryteId));
        _tasks.Add(() => CraftingTasks.TaskInteractWithRepairNPC());
        _tasks.Add(() => CraftingTasks.TaskSelectRepairFromMenu());
        _tasks.Add(() => CraftingTasks.TaskExecuteRepair());
        _tasks.Add(() => CraftingTasks.TaskCloseRepairWindow());
        _tasks.Add(() => { TransitionFromRepairComplete(); return CraftingTasks.TaskResult.Done; });
    }

    private void QueueRetainerBellNavigationTasks()
    {
        var bell = RetainerTaskExecutor.FindNearestBellForNavigation();
        if (bell == null)
        {
            GatherBuddy.Log.Warning("[CraftingQueueProcessor] No retainer bell found in current zone, skipping navigation");
            _currentState = QueueState.WithdrawingFromRetainer;
            QueueRetainerWithdrawalTasks();
            return;
        }

        _retainerBellNavigator = new RetainerBellNavigator();
        if (!_retainerBellNavigator.StartNavigation(bell))
        {
            GatherBuddy.Log.Warning("[CraftingQueueProcessor] Failed to start retainer bell navigation");
            _currentState = QueueState.WithdrawingFromRetainer;
            QueueRetainerWithdrawalTasks();
            return;
        }

        _tasks.Add(() =>
        {
            if (_retainerBellNavigator == null)
                return CraftingTasks.TaskResult.Done;

            _retainerBellNavigator.Update();
            if (_retainerBellNavigator.IsComplete)
            {
                if (_retainerBellNavigator.IsFailed)
                    GatherBuddy.Log.Warning("[CraftingQueueProcessor] Retainer bell navigation failed, proceeding to withdrawal anyway");
                return CraftingTasks.TaskResult.Done;
            }
            return CraftingTasks.TaskResult.Retry;
        });

        _tasks.Add(() =>
        {
            _currentState = QueueState.WithdrawingFromRetainer;
            QueueRetainerWithdrawalTasks();
            return CraftingTasks.TaskResult.Done;
        });
    }

    private void QueueRetainerWithdrawalTasks()
    {
        RefreshRetainerRestockPlanForWithdrawal();
        GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Building retainer withdrawal plan ({RetainerPrecraftTargets.Count} craftable pull target(s), {MaterialTargets.Count} leaf material(s))");

        var combinedItems = new Dictionary<uint, int>(MaterialTargets);
        foreach (var (k, v) in RetainerPrecraftTargets)
        {
            if (combinedItems.ContainsKey(k)) combinedItems[k] += v;
            else combinedItems[k] = v;
        }

        var qualityTargets = _executionPlan?.BuildQualityTargetsForItems(combinedItems) ?? new Dictionary<uint, IngredientQualityDemand>();
        _retainerExecutor = new RetainerTaskExecutor(combinedItems, qualityTargets, RetainerPrecraftTargets.Keys.ToHashSet());

        QueueRetainerWithdrawalExecutionTasks();
    }

    private void RefreshRetainerRestockPlanForWithdrawal()
    {
        if (!_retainerRestock || !AllaganTools.Enabled || _executionPlan == null)
            return;

        var previousMaterials = new Dictionary<uint, int>(MaterialTargets);
        var previousPrecraftItems = new Dictionary<uint, int>(RetainerPrecraftTargets);
        var previousQueueCount = QueueItems.Count;
        _executionPlan.RefreshForRetainerWithdrawal();

        LogRetainerPlanDifferences("leaf material", previousMaterials, MaterialTargets);
        LogRetainerPlanDifferences("craftable pull", previousPrecraftItems, RetainerPrecraftTargets);

        if (previousQueueCount != QueueItems.Count)
        {
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Retainer queue plan refreshed: {previousQueueCount} -> {QueueItems.Count} crafts");
        }
    }

    private static void LogRetainerPlanDifferences(string label, Dictionary<uint, int> previousPlan, Dictionary<uint, int> refreshedPlan)
    {
        var changes = previousPlan.Keys
            .Union(refreshedPlan.Keys)
            .Select(itemId => (ItemId: itemId, PreviousAmount: previousPlan.GetValueOrDefault(itemId), RefreshedAmount: refreshedPlan.GetValueOrDefault(itemId)))
            .Where(change => change.PreviousAmount != change.RefreshedAmount)
            .OrderBy(change => change.ItemId)
            .ToList();

        if (changes.Count == 0)
            return;

        GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Retainer {label} plan refreshed with {changes.Count} change(s)");
        foreach (var change in changes)
            GatherBuddy.Log.Debug($"[CraftingQueueProcessor]   {label}: item {change.ItemId} {change.PreviousAmount} -> {change.RefreshedAmount}");
    }

    private void QueueRetainerWithdrawalExecutionTasks()
    {
        if (_retainerExecutor == null)
        {
            GatherBuddy.Log.Warning("[CraftingQueueProcessor] Retainer withdrawal executor unavailable, proceeding to gather stage");
            TransitionFromRetainerWithdrawComplete();
            return;
        }

        _tasks.Add(() =>
        {
            if (_retainerExecutor == null)
                return CraftingTasks.TaskResult.Done;
            var result = _retainerExecutor.Tick();
            if (result == CraftingTasks.TaskResult.Done)
            {
                if (_retainerExecutor.IsAborted)
                    GatherBuddy.Log.Warning("[CraftingQueueProcessor] Retainer withdrawal aborted, rebuilding from current inventory");
                else
                    GatherBuddy.Log.Information("[CraftingQueueProcessor] Retainer withdrawal complete");
            }
            return result;
        });

        _tasks.Add(() =>
        {
            TransitionFromRetainerWithdrawComplete();
            return CraftingTasks.TaskResult.Done;
        });
    }

    private unsafe void TransitionFromRetainerWithdrawComplete()
    {
        RebuildQueueAndMaterialsFromCurrentInventory();
        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Computing remaining materials after retainer withdrawal");

        var stillGatherCount = BuildCurrentMaterialDeficits().Count;

        GatherBuddy.Log.Information($"[CraftingQueueProcessor] After retainer withdrawal: {stillGatherCount} item(s) still need gathering");

        // Retainer withdrawal changes both the material deficits and the
        // selected craft queue. Re-run the exact same acquisition gate before
        // starting gathering; otherwise a dependency that became purchasable
        // after withdrawal would be gathered or left unresolved.
        BeginAcquisitionOrGather();
    }
    private void RebuildQueueAndMaterialsFromCurrentInventory()
    {
        if (_executionPlan == null)
            return;

        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Rebuilding queue and materials from current inventory after retainer stage");
        _executionPlan.RefreshFromCurrentInventory();
        GatherBuddy.Log.Debug($"[CraftingQueueProcessor] Rebuilt post-retainer queue with {QueueItems.Count} craft(s) and {MaterialTargets.Count} leaf material(s)");
    }

    private void TransitionFromRepairComplete()
    {
        GatherBuddy.Log.Information("[CraftingQueueProcessor] Repair complete, continuing to craft");
        _currentState = QueueState.WaitingForJobSwitch;
        StateChanged?.Invoke(_currentState);
    }

    private bool NeedsMateria()
    {
        if (!GatherBuddy.Config.VulcanMateriaConfig.Enabled)
            return false;
        if (!MateriaManager.IsExtractionUnlocked())
            return false;
        if (!MateriaManager.HasFreeInventorySlots())
            return false;
        return MateriaManager.IsSpiritbondReadyAny();
    }

    private void QueueMateriaTasks()
    {
        GatherBuddy.Log.Debug("[CraftingQueueProcessor] Queueing materia extraction tasks");

        if (CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleBetween)
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Queueing TaskExitCraft before materia extraction");
            _tasks.Add(() => CraftingTasks.TaskExitCraft());
        }

        _tasks.Add(() => CraftingTasks.TaskExtractAllMateria());
        _tasks.Add(() => { TransitionFromMateriaComplete(); return CraftingTasks.TaskResult.Done; });
    }

    private void TransitionFromMateriaComplete()
    {
        GatherBuddy.Log.Information("[CraftingQueueProcessor] Materia extraction complete, continuing to craft");
        _currentState = QueueState.WaitingForJobSwitch;
        StateChanged?.Invoke(_currentState);
    }
    
    private unsafe bool IsQuickSynthesisComplete()
    {
        try
        {
            var quickSynthAddon = Dalamud.GameGui.GetAddonByName("SynthesisSimple");
            if (quickSynthAddon == null || quickSynthAddon.Address == nint.Zero)
                return false;
                
            var atkUnit = (AtkUnitBase*)quickSynthAddon.Address;
            if (atkUnit == null || !atkUnit->IsVisible || atkUnit->AtkValuesCount < 5)
                return false;
                
            var current = atkUnit->AtkValues[3].Int;
            var max = atkUnit->AtkValues[4].Int;
            
            return current >= max && max > 0;
        }
        catch
        {
            return false;
        }
    }
    
    private unsafe void CloseQuickSynthWindow()
    {
        try
        {
            var quickSynthAddon = Dalamud.GameGui.GetAddonByName("SynthesisSimple");
            if (quickSynthAddon == null || quickSynthAddon.Address == nint.Zero)
                return;
                
            var atkUnit = (AtkUnitBase*)quickSynthAddon.Address;
            if (atkUnit == null)
                return;
                
            Callback.Fire(atkUnit, true, -1);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingQueueProcessor] Failed to close quick synth window: {ex.Message}");
        }
    }
    
    public void Pause(string? reason = null)
    {
        if (_paused || _currentState == QueueState.Complete || _currentState == QueueState.Idle)
            return;

        GatherBuddy.Log.Information("[CraftingQueueProcessor] Pausing queue");
        _paused = true;
        _resumeRequest.Cancel();
        CraftingGameInterop.SetAutomationPaused(true);
        _pauseReason = reason ?? string.Empty;
        if (_currentState == QueueState.NavigatingToRetainerBell)
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Pausing retainer bell navigation");
            _retainerBellNavigator?.Stop();
            _retainerBellNavigator = null;
        }
        _tasks.Clear();
        YesAlready.Unlock();
        
        if (_currentState == QueueState.WaitingForGather)
        {
            var gatherList = CraftingGatherBridge.GetTemporaryGatherList();
            if (gatherList != null)
            {
                GatherBuddy.Log.Debug("[CraftingQueueProcessor] Pausing auto-gather but keeping list");
                _pausedDuringGather = true;
                CraftingGatherBridge.PreserveListOnDisable = true;
                try
                {
                    if (GatherBuddy.AutoGather != null)
                        GatherBuddy.AutoGather.Enabled = false;
                }
                catch (Exception ex)
                {
                    GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Failed to pause AutoGather: {ex.Message}");
                }
                CraftingGatherBridge.PreserveListOnDisable = false;
            }
            // Pausing hands standalone AutoGather policy back to the user;
            // resume reclaims ownership only when the temporary list exists.
            try
            {
                GatherBuddy.AutoGather?.SetCraftOwnedGathering(false);
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"[CraftingQueueProcessor] Failed to release AutoGather ownership while pausing: {ex.Message}");
            }
        }
    }

    public void Resume()
    {
        if (!_paused)
            return;

        if (_resumeRequest.Request())
            GatherBuddy.Log.Information("[CraftingQueueProcessor] Resume requested");
        TryCompleteResume();
    }

    private void TryCompleteResume()
    {
        if (!_paused || !_resumeRequest.Requested)
            return;

        var liveStateReady = !(_currentState == QueueState.Crafting
            && CraftingGameInterop.CurrentState is CraftingGameInterop.CraftState.InProgress or CraftingGameInterop.CraftState.WaitAction
            && !CraftingGameInterop.TryResumeLiveCraft());
        if (!_resumeRequest.TryComplete(liveStateReady))
            return;

        GatherBuddy.Log.Information("[CraftingQueueProcessor] Resuming queue");
        _paused = false;
        CraftingGameInterop.SetAutomationPaused(false);
        _pauseReason = string.Empty;
        YesAlready.Lock();

        if (_currentState == QueueState.NavigatingToRetainerBell)
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Resuming retainer bell navigation");
            QueueRetainerBellNavigationTasks();
            return;
        }

        if (_currentState == QueueState.WithdrawingFromRetainer)
        {
            GatherBuddy.Log.Debug("[CraftingQueueProcessor] Resuming retainer withdrawal");
            if (_retainerExecutor == null)
                QueueRetainerWithdrawalTasks();
            else
                QueueRetainerWithdrawalExecutionTasks();
            return;
        }
        
        if (_pausedDuringGather && _currentState == QueueState.WaitingForGather)
        {
            var gatherList = CraftingGatherBridge.GetTemporaryGatherList();
            if (gatherList != null && gatherList.Items.Count > 0)
            {
                GatherBuddy.Log.Debug("[CraftingQueueProcessor] Resuming auto-gather with existing list");
                try
                {
                    if (GatherBuddy.AutoGather != null)
                    {
                        GatherBuddy.AutoGather.SetCraftOwnedGathering(true);
                        GatherBuddy.AutoGather.Enabled = true;
                    }
                }
                catch (Exception ex)
                {
                    FailQueue($"Cannot resume crafting gather stage: {ex.Message}");
                    return;
                }
                _pausedDuringGather = false;
            }
            else
            {
                GatherBuddy.Log.Debug("[CraftingQueueProcessor] No items to gather, moving to job switch");
                _pausedDuringGather = false;
                _currentState = QueueState.WaitingForJobSwitch;
                StateChanged?.Invoke(_currentState);
            }
        }
        else if (_currentState == QueueState.Crafting && CraftingGameInterop.CurrentState == CraftingGameInterop.CraftState.IdleNormal)
        {
            _currentState = QueueState.WaitingForJobSwitch;
            StateChanged?.Invoke(_currentState);
        }
    }

    public void Stop()
    {
        GatherBuddy.Log.Information("[CraftingQueueProcessor] Stopping queue");
        CraftingGameInterop.SetAutomationPaused(true);
        _paused = false;
        _resumeRequest.Cancel();
        _pauseReason = string.Empty;
        CancelAcquisition();
        _tasks.Clear();
        _retainerBellNavigator?.Stop();
        _retainerBellNavigator = null;
        YesAlready.Unlock();
        
        DisableAutoGatherSafely();
        CraftingGatherBridge.DeleteTemporaryGatherList();
        
        CompleteQueue();
    }

    private void UpdateCurrentRecipeTracking(int batchSize = 1)
    {
        if (_currentQueueIndex >= QueueItems.Count)
            return;
        
        var currentRecipeId = QueueItems[_currentQueueIndex].RecipeId;
        if (_currentProcessedRecipeId != currentRecipeId)
        {
            _currentProcessedRecipeId = currentRecipeId;
            _currentProcessedRecipeCount = 1;
            _currentProcessedRecipeTotal = batchSize;
        }
        else
        {
            _currentProcessedRecipeCount++;
        }
    }
    
    public void Reset()
    {
        YesAlready.Unlock();
        CancelAcquisition();
        DisableAutoGatherSafely();
        _executionPlan = null;
        _currentQueueIndex = 0;
        _currentState = QueueState.Idle;
        _pauseReason = string.Empty;
        _tasks.Clear();
        _currentProcessedRecipeId = 0;
        _currentProcessedRecipeCount = 0;
        _currentProcessedRecipeTotal = 0;
        _craftHangSince = DateTime.MinValue;
        _missingIngredientFailures.Clear();
        _enqueuedRaphaelRequests.Clear();
        ResetJobSwitchWatchdog();
        _retainerRestock = false;
        _retainerExecutor = null;
        _retainerBellNavigator?.Stop();
        _retainerBellNavigator = null;
    }
    
    public void TestRepair()
    {
        GatherBuddy.Log.Information("[CraftingQueueProcessor] Testing repair system...");
        YesAlready.Lock();
        _currentState = QueueState.Repairing;
        StateChanged?.Invoke(_currentState);
        QueueRepairTasks();
    }

    private unsafe bool IsInventoryFull()
    {
        var inventoryManager = InventoryManager.Instance();
        return inventoryManager != null && inventoryManager->GetEmptySlotsInBag() == 0;
    }

    private void PauseForInventoryFull(string message)
    {
        var pauseReason = $"{message} Clear inventory, then press Resume to continue the current queue.";
        GatherBuddy.Log.Warning($"[CraftingQueueProcessor] {pauseReason}");
        Pause(pauseReason);
    }
}
