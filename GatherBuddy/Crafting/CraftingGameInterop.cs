using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Crafting;

public static partial class CraftingGameInterop
{
    public enum CraftPreparationFailureReason
    {
        MissingIngredientsUnableToSelect,
        MissingMaterialsUnableToQuickSynth,
    }

    private static string GetItemName(uint itemId)
        => Dalamud.GameData.GetExcelSheet<Item>()?.TryGetRow(itemId, out var item) == true
            ? item.Name.ExtractText()
            : $"Item {itemId}";

    public sealed record CraftPreparationFailure(
        uint RecipeId,
        CraftPreparationFailureReason Reason,
        uint ItemId,
        int Needed,
        int AvailableNQ,
        int AvailableHQ,
        string Details);

    private enum IngredientAssignmentResult
    {
        Success,
        Retry,
        Fatal,
    }

    private static readonly IngredientSelectionSequencer IngredientSelection = new();


    public enum CraftState
    {
        IdleNormal,
        PreparingCraft,
        WaitStart,
        InProgress,
        WaitAction,
        WaitFinish,
        IdleBetween,
        QuickSynthesis,
        InvalidState
    }

    private static CraftState _currentState = CraftState.IdleNormal;
    private static Recipe? _currentRecipe = null;
    private static uint? _currentRecipeId = null;
    private static Vulcan.CraftState? _vulcanCraftState = null;
    private static Vulcan.StepState? _vulcanStepState = null;
    private static bool _vulcanPredictionPendingObservation;
    private static Vulcan.StepState? _vulcanPreActionStepState;
    private static VulcanSkill _vulcanPendingAction;
    private static long _vulcanPendingActionIssuedTick;
    private static bool _vulcanSolverStartDeferredForMaterialMiracle;
    private static Vulcan.StepState? _unreconciledObservedState;
    private static DateTime _unreconciledObservedSince = DateTime.MinValue;
    private static CraftingActionExecutor? _actionExecutor = null;
    private static CraftingQualityPolicy? _currentQualityPolicy = null;
    private static Dictionary<uint, int>? _currentIngredientPreferences = null;
    private static bool _currentUseAllNQ = false;
    private static int _quickSynthTarget = 0;
    private static int _quickSynthCompleted = 0;
    private static bool _quickSynthWindowSeen = false;
    private static Dictionary<uint, bool> _equipmentItemCache = new();
    private static Vulcan.UserMacroLibrary? _userMacroLibrary = null;
    private static string? _currentSelectedMacroId = null;
    private static DonatelloExecutionOptions? _currentDonatelloOptions;
    private static VulcanSolverMode _currentSolverMode;
    private static DateTime _taskManagerIdleSince = DateTime.MinValue;
    private static DateTime _nextActionAllowedAt = DateTime.MinValue;
    private static StepState? _actionDelayState;
    private static readonly List<VulcanSkill> _executedActions = [];
    private static CraftPreparationFailure? _lastPreparationFailure = null;
    private static bool _automationFaultReported;
    private static bool _automationPaused;
    private static bool _currentCraftIsTrial;
    private static readonly TimeSpan ReconciliationGracePeriod = TimeSpan.FromMilliseconds(500);


    public static Vulcan.UserMacroLibrary UserMacroLibrary => _userMacroLibrary ??= new();
    public static event Action<CraftState>? StateChanged;
    public static event Action<Recipe?, uint>? CraftStarted;
    public static event Action<Recipe?, bool>? CraftFinished;
    public static event Action<Recipe?>? CraftAdvanced;
    public static event Action<VulcanSkill>? CraftActionExecuted;
    public static event Action<int, int>? QuickSynthProgress;
    public static event Action<string>? AutomationFaulted;

    public static CraftState CurrentState => _currentState;
    internal static bool HasOwnedCraft => _currentRecipeId.HasValue && _currentState != CraftState.IdleNormal;
    internal static bool AutomationPaused => _automationPaused;

    internal static void SetAutomationPaused(bool paused)
        => _automationPaused = paused;
    public static Recipe? CurrentRecipe => _currentRecipe;

    public static string? GetTrialSynthesisStartBlockReason()
    {
        if (GatherBuddy.Config.ExpertConditionSamplingEnabled)
            return "Disable the expert condition sampler before starting an autosolved Trial Synthesis.";
        if (CraftingGatherBridge.HasActiveQueue)
            return "Stop the active crafting queue before starting an autosolved Trial Synthesis.";
        if (_currentState is not (CraftState.IdleNormal or CraftState.IdleBetween)
            || Dalamud.Conditions[ConditionFlag.Crafting]
            || Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
            return "Another craft is already active.";
        return null;
    }

    public static bool TryResumeLiveCraft()
    {
        if (_currentState is not (CraftState.InProgress or CraftState.WaitAction)
            || !Dalamud.Conditions[ConditionFlag.Crafting]
            || Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction]
            || _vulcanCraftState == null
            || _currentRecipeId == null)
            return false;

        var liveState = SynthesisReader.ReadCurrentStepState(_vulcanCraftState, _vulcanStepState);
        if (liveState == null)
        {
            if (!Dalamud.Conditions[ConditionFlag.PreparingToCraft])
                return false;

            var completedState = Finish(cancelled: false);
            if (completedState != _currentState)
            {
                GatherBuddy.Log.Debug($"[Crafting] State transition: {_currentState} -> {completedState}");
                _currentState = completedState;
                StateChanged?.Invoke(completedState);
            }
            return true;
        }

        if (!CraftingProcessor.TryResumeCraft(_vulcanCraftState, liveState))
            return false;

        _vulcanStepState = liveState;
        _vulcanPredictionPendingObservation = false;
        BeginActionDelay(liveState, restart: true);
        _automationFaultReported = false;
        ResetReconciliationTracking();
        GatherBuddy.Log.Information($"[Crafting] Resumed from live state: {liveState}");
        return true;
    }

    private static bool TryBuildLiveCraft(
        Recipe recipe,
        CraftingExecutionContext executionContext,
        out Vulcan.CraftState craft,
        out string failureReason)
    {
        craft = null!;
        var activeRecipeId = RecipeNoteExt.GetActiveCraftRecipeId();
        if (!activeRecipeId.HasValue)
        {
            failureReason = "active synthesis recipe is temporarily unavailable";
            return false;
        }
        if (activeRecipeId.Value != recipe.RowId)
        {
            failureReason = $"active synthesis recipe {activeRecipeId.Value} does not match requested recipe {recipe.RowId}";
            return false;
        }
        if (!CraftingContextResolver.TryBuildSimulationContext(
                recipe,
                executionContext,
                CraftingStatsSource.PreferCurrentJobStats,
                out var simulationContext))
        {
            failureReason = "current crafter stats are temporarily unavailable";
            return false;
        }

        if (!SynthesisReader.TryReadLiveCraftMetrics(out var liveMetrics))
        {
            failureReason = "live synthesis parameters are temporarily unavailable";
            return false;
        }

        craft = SynthesisReader.ApplyLiveCraftMetrics(simulationContext.SimulationState, liveMetrics);
        if (!SynthesisReader.MatchesCraftMetrics(
                simulationContext.SimulationState,
                liveMetrics.MaxProgress,
                liveMetrics.MaxQuality,
                liveMetrics.MaxDurability))
        {
            GatherBuddy.Log.Warning(
                $"[Crafting] Using authoritative live synthesis parameters for recipe {recipe.RowId}: "
                + $"expected={simulationContext.SimulationState.CraftProgress}/"
                + $"{simulationContext.SimulationState.CraftQualityMax}/"
                + $"{simulationContext.SimulationState.CraftDurability}, "
                + $"live={liveMetrics.MaxProgress}/{liveMetrics.MaxQuality}/{liveMetrics.MaxDurability}");
        }

        failureReason = string.Empty;
        return true;
    }

    public static bool TryAdoptLiveCraft(
        Recipe recipe,
        CraftingExecutionContext executionContext,
        out string failureReason)
    {
        if (!SynthesisReader.IsSynthesisWindowOpen())
        {
            failureReason = "no live synthesis is open";
            return false;
        }
        if (!Dalamud.Conditions[ConditionFlag.Crafting])
        {
            failureReason = "live synthesis condition is temporarily unavailable";
            return false;
        }
        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
        {
            failureReason = "the current crafting action is still executing";
            return false;
        }
        if (!TryBuildLiveCraft(recipe, executionContext, out var craft, out failureReason))
            return false;

        SetQualityPolicy(executionContext.QualityPolicy);
        SetSelectedMacro(executionContext.SelectedMacroId);
        SetDonatelloOptions(executionContext.DonatelloOptions);
        ReloadSolversForCraft(
            executionContext.EffectiveSolverMode,
            !executionContext.ForceProgressOnlyUnlockCraft);

        var conservativePrior = BuildConservativeRecoveryPrior(craft, executionContext.DonatelloOptions);
        var liveStep = SynthesisReader.ReadCurrentStepState(craft, conservativePrior);
        if (liveStep == null)
        {
            failureReason = "live synthesis state is temporarily unavailable";
            return false;
        }
        if (RequiresLiveSolver(craft, liveStep))
        {
            if (!CraftingProcessor.TryAdoptLiveCraft(
                    craft,
                    liveStep,
                    executionContext.EffectiveSolverMode is VulcanSolverMode.Donatello or VulcanSolverMode.Gabriel
                        ? executionContext.EffectiveSolverMode
                        : null,
                    out failureReason))
                return false;
        }
        else
        {
            GatherBuddy.Log.Information(
                $"[Crafting] Live craft is already final; waiting for the game completion lifecycle: state={liveStep}, "
                + $"targets={craft.CraftProgress}/{craft.CraftQualityMax}/{craft.CraftDurability}");
        }

        _currentRecipe = recipe;
        _currentRecipeId = recipe.RowId;
        _currentCraftIsTrial = false;
        _vulcanCraftState = craft;
        _vulcanStepState = liveStep;
        _vulcanPredictionPendingObservation = false;
        _executedActions.Clear();
        ResetReconciliationTracking();
        _automationFaultReported = false;
        BeginActionDelay(liveStep, restart: true);
        _currentState = CraftState.InProgress;
        CraftStarted?.Invoke(recipe, recipe.RowId);
        StateChanged?.Invoke(_currentState);
        GatherBuddy.Log.Information(
            $"[Crafting] Adopted live craft after plugin reload: recipe={recipe.RowId}, state={liveStep}, "
            + $"targets={craft.CraftProgress}/{craft.CraftQualityMax}/{craft.CraftDurability}");
        failureReason = string.Empty;
        return true;
    }

    internal static bool RequiresLiveSolver(Vulcan.CraftState craft, Vulcan.StepState step)
        => Vulcan.SolverUtils.Status(craft, step) == Vulcan.SolverUtils.CraftStatus.InProgress;

    public static void Initialize()
    {
        ResetManualSynthesisTakeoverTracking();
        _currentState = CraftState.IdleNormal;
        _currentCraftIsTrial = false;
        _actionExecutor = new CraftingActionExecutor();
        _userMacroLibrary = new Vulcan.UserMacroLibrary();
        _userMacroLibrary.LoadFromConfig();
        CraftingProcessor.Setup();
        // Register UserMacro solver first (highest priority)
        CraftingProcessor.RegisterSolver(new Vulcan.UserMacroSolverDefinition(_userMacroLibrary));
        
        var configuredSolverMode = GatherBuddy.Config.RaphaelSolverConfig.SolverMode;
        var solverMode = CraftingContextResolver.ResolveGlobalSolverMode(configuredSolverMode);
        if (configuredSolverMode != solverMode)
            GatherBuddy.Log.Warning("[CraftingGameInterop] Gabriel is per-item only; using Donatello for the invalid global selection");
        _currentSolverMode = solverMode;
        switch (solverMode)
        {
            case VulcanSolverMode.PureRaphael:
                CraftingProcessor.RegisterSolver(new Vulcan.RaphaelSolverDefinition(GatherBuddy.RaphaelSolveCoordinator));
                break;
            case VulcanSolverMode.StandardSolver:
                CraftingProcessor.RegisterSolver(new Vulcan.StandardSolverDefinition());
                GatherBuddy.Log.Debug($"[CraftingGameInterop] Registered StandardSolver");
                break;
            case VulcanSolverMode.Donatello:
                CraftingProcessor.RegisterSolver(new Vulcan.DonatelloSolverDefinition(GatherBuddy.RaphaelSolveCoordinator));
                break;
        }
    }

    public static void ReloadSolvers()
    {
        CraftingProcessor.Setup();
        
        // Re-register UserMacro solver first (highest priority)
        if (_userMacroLibrary != null)
        {
            CraftingProcessor.RegisterSolver(new Vulcan.UserMacroSolverDefinition(_userMacroLibrary));
        }
        
        var configuredSolverMode = GatherBuddy.Config.RaphaelSolverConfig.SolverMode;
        var solverMode = CraftingContextResolver.ResolveGlobalSolverMode(configuredSolverMode);
        if (configuredSolverMode != solverMode)
            GatherBuddy.Log.Warning("[CraftingGameInterop] Gabriel is per-item only; using Donatello for the invalid global selection");
        _currentSolverMode = solverMode;
        switch (solverMode)
        {
            case VulcanSolverMode.PureRaphael:
                CraftingProcessor.RegisterSolver(new Vulcan.RaphaelSolverDefinition(GatherBuddy.RaphaelSolveCoordinator));
                break;
            case VulcanSolverMode.StandardSolver:
                CraftingProcessor.RegisterSolver(new Vulcan.StandardSolverDefinition());
                GatherBuddy.Log.Debug($"[CraftingGameInterop] Reloaded: Registered StandardSolver");
                break;
            case VulcanSolverMode.Donatello:
                CraftingProcessor.RegisterSolver(new Vulcan.DonatelloSolverDefinition(GatherBuddy.RaphaelSolveCoordinator));
                break;
        }
    }

    public static void ReloadSolversForCraft(VulcanSolverMode mode, bool registerUserMacroSolver = true)
    {
        GatherBuddy.Log.Debug($"[CraftingGameInterop] ReloadSolversForCraft: {mode}");
        _currentSolverMode = mode;
        CraftingProcessor.Setup();
        if (registerUserMacroSolver && _userMacroLibrary != null)
        {
            if (_userMacroLibrary != null)
            {
                CraftingProcessor.RegisterSolver(new Vulcan.UserMacroSolverDefinition(_userMacroLibrary));
            }
        }

        switch (mode)
        {
            case VulcanSolverMode.PureRaphael:
                CraftingProcessor.RegisterSolver(new Vulcan.RaphaelSolverDefinition(GatherBuddy.RaphaelSolveCoordinator));
                break;
            case VulcanSolverMode.StandardSolver:
                CraftingProcessor.RegisterSolver(new Vulcan.StandardSolverDefinition());
                GatherBuddy.Log.Debug($"[CraftingGameInterop] Registered StandardSolver");
                break;
            case VulcanSolverMode.Donatello:
                CraftingProcessor.RegisterSolver(new Vulcan.DonatelloSolverDefinition(GatherBuddy.RaphaelSolveCoordinator));
                break;
            case VulcanSolverMode.Gabriel:
                CraftingProcessor.RegisterSolver(new Vulcan.GabrielSolverDefinition());
                break;
        }
    }

    public static void Dispose()
    {
        ResetManualSynthesisTakeoverTracking();
        _currentRecipe = null;
        _currentRecipeId = null;
        _currentState = CraftState.IdleNormal;
        _vulcanCraftState = null;
        _vulcanStepState = null;
        _vulcanPredictionPendingObservation = false;
        _executedActions.Clear();
        ResetReconciliationTracking();
        _currentQualityPolicy = null;
        _currentSelectedMacroId = null;
        _currentDonatelloOptions = null;
        _lastPreparationFailure = null;
        _automationFaultReported = false;
        _currentCraftIsTrial = false;
        StateChanged = null;
        CraftStarted = null;
        CraftFinished = null;
        CraftAdvanced = null;
        CraftActionExecuted = null;
        QuickSynthProgress = null;
        AutomationFaulted = null;
        CraftingProcessor.Dispose();
    }

    public static void SetIngredientPreferences(Dictionary<uint, int>? preferences, bool useAllNQ = false)
    {
        _currentIngredientPreferences = preferences;
        _currentUseAllNQ = useAllNQ;
        _currentQualityPolicy = null;
    }

    public static void SetQualityPolicy(CraftingQualityPolicy? qualityPolicy)
    {
        _currentQualityPolicy = qualityPolicy;
        _currentIngredientPreferences = qualityPolicy?.BuildGuaranteedHQPreferences();
        _currentUseAllNQ = false;
    }
    
    public static void SetSelectedMacro(string? macroId)
    {
        _currentSelectedMacroId = macroId;
    }
    
    public static string? GetSelectedMacro()
    {
        return _currentSelectedMacroId;
    }

    public static void SetDonatelloOptions(DonatelloExecutionOptions? options)
        => _currentDonatelloOptions = options;

    public static bool TryConsumePreparationFailure(out CraftPreparationFailure failure)
    {
        if (_lastPreparationFailure == null)
        {
            failure = null!;
            return false;
        }

        failure = _lastPreparationFailure;
        _lastPreparationFailure = null;
        return true;
    }
    
    public static void StartCraft(Recipe recipe, uint quantity, bool useQuickSynthesis = false)
    {
        if (recipe.RowId == 0)
            return;

        _currentRecipe = recipe;
        _currentRecipeId = recipe.RowId;
        _currentCraftIsTrial = false;
        _currentState = CraftState.PreparingCraft;
        _taskManagerIdleSince = DateTime.MinValue;
        _lastPreparationFailure = null;
        ResetIngredientSelectionState();
        GatherBuddy.Log.Debug($"[Crafting] StartCraft - entering PreparingCraft state (QuickSynth={useQuickSynthesis})");
        
        var tm = GatherBuddy.AutoGather?.TaskManager;
        if (tm == null)
            return;
        
        tm.Enqueue(() => OpenRecipe(recipe.RowId), 3000, "OpenRecipe");
        tm.Enqueue(() => WaitForRecipeOpen(), 3000, true, "WaitForRecipeOpen");
        
        if (useQuickSynthesis)
        {
            tm.DelayNext(500);
            tm.Enqueue(() => { ExecuteQuickSynthesis((int)quantity); return true; }, 3000, "ExecuteQuickSynthesis");
        }
        else
        {
            tm.DelayNext(1500);
            tm.Enqueue(() => WaitForIngredientsAssigned(), 3000, true, "WaitForIngredientsAssigned");
            tm.Enqueue(() => ExecuteCraft(), 3000, "ExecuteCraft");
        }
        
        GatherBuddy.Log.Information($"[Crafting] Starting craft of {recipe.ItemResult.Value.Name.ExtractText()} (qty: {quantity}, QuickSynth={useQuickSynthesis})");
    }

    public static bool StartTrialSynthesis(Recipe recipe)
    {
        if (recipe.RowId == 0)
            return false;
        if (GetTrialSynthesisStartBlockReason() is { } blockReason)
        {
            GatherBuddy.Log.Warning($"[Crafting] Trial Synthesis not started: {blockReason}");
            return false;
        }

        var tm = GatherBuddy.AutoGather?.TaskManager;
        if (tm == null)
        {
            GatherBuddy.Log.Error("[Crafting] Trial Synthesis not started: TaskManager unavailable");
            return false;
        }

        _currentRecipe = recipe;
        _currentRecipeId = recipe.RowId;
        _currentCraftIsTrial = true;
        _currentState = CraftState.PreparingCraft;
        _taskManagerIdleSince = DateTime.MinValue;
        _lastPreparationFailure = null;
        ResetIngredientSelectionState();

        tm.Enqueue(() => OpenRecipe(recipe.RowId), 3000, "OpenTrialRecipe");
        tm.Enqueue(() => WaitForRecipeOpen(), 3000, true, "WaitForTrialRecipeOpen");
        tm.DelayNext(1500);
        tm.Enqueue(() => RequestTrialSynthesis(recipe.RowId), 3000, "RequestTrialSynthesis");
        tm.Enqueue(() => ConfirmTrialSynthesis(recipe.RowId), 3000, "ConfirmTrialSynthesis");
        tm.Enqueue(() => WaitForTrialSynthesisStart(recipe.RowId), 5000, true, "WaitForTrialSynthesisStart");

        GatherBuddy.Log.Information(
            $"[Crafting] Starting autosolved Trial Synthesis of {recipe.ItemResult.Value.Name.ExtractText()}");
        return true;
    }

    private static bool RequestTrialSynthesis(uint recipeId)
    {
        try
        {
            if (!TrialSynthesisUi.TryRequestStart(recipeId))
                return false;
            GatherBuddy.Log.Information($"[Crafting] Requested Trial Synthesis for recipe {recipeId}");
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Failed to request Trial Synthesis: {ex.Message}");
            return false;
        }
    }

    private static bool ConfirmTrialSynthesis(uint recipeId)
    {
        try
        {
            if (TrialSynthesisUi.IsActive(recipeId))
                return true;
            if (!TrialSynthesisUi.TryConfirmStart(recipeId))
                return false;
            GatherBuddy.Log.Information("[Crafting] Confirmed Trial Synthesis");
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Failed to confirm Trial Synthesis: {ex.Message}");
            return false;
        }
    }

    private static bool WaitForTrialSynthesisStart(uint recipeId)
    {
        try
        {
            return TrialSynthesisUi.IsActive(recipeId)
                && SynthesisReader.IsSynthesisWindowOpen();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Failed while waiting for Trial Synthesis: {ex.Message}");
            return false;
        }
    }

    private static unsafe bool OpenRecipe(uint recipeId)
    {
        try
        {
            var selectedRecipe = RecipeNoteExt.GetSelectedRecipeEntry();
            if (selectedRecipe != null && selectedRecipe->RecipeId == recipeId)
            {
                GatherBuddy.Log.Debug($"[Crafting] Recipe {recipeId} already selected, skipping OpenRecipe");
                return true;
            }
            
            var agent = AgentRecipeNote.Instance();
            if (agent == null)
                return false;
            
            agent->OpenRecipeByRecipeId(recipeId);
            GatherBuddy.Log.Debug($"[Crafting] Opened recipe {recipeId}");
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Failed to open recipe: {ex.Message}");
            return false;
        }
    }

    private static unsafe bool WaitForRecipeOpen()
    {
        try
        {
            var cosmic = _currentRecipe is { } recipe && recipe.Number == 0;
            var addon = Dalamud.GameGui.GetAddonByName(cosmic ? "WKSRecipeNotebook" : "RecipeNote");
            if (addon != null && addon.Address != nint.Zero)
            {
                var atkUnit = (AtkUnitBase*)addon.Address;
                if (atkUnit != null && atkUnit->IsVisible)
                    return !cosmic || SelectCosmicRecipe(atkUnit);
            }
        }
        catch { }
        
        return false;
    }

    private static unsafe bool? WaitForIngredientsAssigned()
    {
        try
        {
            var addonName = _currentRecipe is { } recipe && recipe.Number == 0
                ? "WKSRecipeNotebook"
                : "RecipeNote";
            var addon = Dalamud.GameGui.GetAddonByName(addonName);
            if (addon == null || addon.Address == nint.Zero)
            {
                GatherBuddy.Log.Debug($"[Crafting] WaitForIngredientsAssigned: {addonName} not found, re-opening");
                if (_currentRecipeId.HasValue)
                    OpenRecipe(_currentRecipeId.Value);
                return false;
            }

            var atkUnit = (AtkUnitBase*)addon.Address;
            if (atkUnit == null || !atkUnit->IsVisible)
            {
                GatherBuddy.Log.Debug($"[Crafting] WaitForIngredientsAssigned: {addonName} not visible, re-opening");
                if (_currentRecipeId.HasValue)
                    OpenRecipe(_currentRecipeId.Value);
                return false;
            }
            return SelectIngredientsForCraft() switch
            {
                IngredientAssignmentResult.Success => true,
                IngredientAssignmentResult.Fatal => null,
                _ => false,
            };
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Failed to validate ingredients: {ex.Message}");
            return false;
        }
    }

    private static unsafe IngredientAssignmentResult SelectIngredientsForCraft()
    {
        try
        {
            var cosmic = _currentRecipe is { } currentRecipe && currentRecipe.Number == 0;
            var addon = Dalamud.GameGui.GetAddonByName(cosmic ? "WKSRecipeNotebook" : "RecipeNote");
            if (addon == null || addon.Address == nint.Zero)
            {
                GatherBuddy.Log.Debug($"[Crafting] RecipeNote addon not found");
                return IngredientAssignmentResult.Retry;
            }

            var atkUnit = (AtkUnitBase*)addon.Address;
            var selectedRecipe = RecipeNoteExt.GetSelectedRecipeEntry();
            if (selectedRecipe == null)
            {
                GatherBuddy.Log.Debug($"[Crafting] SelectedRecipe is null");
                return IngredientAssignmentResult.Retry;
            }

            var now = Environment.TickCount64;
            if (!IngredientSelection.IsReady(now))
                return IngredientAssignmentResult.Retry;

            if (IngredientSelection.Phase == EquipmentIngredientSelectionPhase.WaitingForMenu)
            {
                if (!SelectEquipmentIngredientFromMenu(
                        IngredientSelection.ItemId,
                        IngredientSelection.HighQuality))
                {
                    IngredientSelection.DelayNormalAssignment(now);
                    return IngredientAssignmentResult.Retry;
                }

                IngredientSelection.MarkMenuSelectionComplete(now);
                return IngredientAssignmentResult.Retry;
            }

            if (IngredientSelection.Phase == EquipmentIngredientSelectionPhase.WaitingForAssignment)
                IngredientSelection.CompleteEquipmentAssignment();

            var qualityPolicy = GetActiveQualityPolicy();
            if (qualityPolicy == null)
            {
                GatherBuddy.Log.Debug("[Crafting] Quality policy unavailable during ingredient assignment");
                return IngredientAssignmentResult.Retry;
            }

            if (cosmic)
            {
                ResetIngredientSelectionState();
                var (firstButton, secondButton) = CosmicIngredientButtonOrder(_currentDonatelloOptions);
                if (!ClickCosmicIngredientButton(atkUnit, firstButton)
                    || !ClickCosmicIngredientButton(atkUnit, secondButton))
                    return IngredientAssignmentResult.Retry;
                return AreIngredientsAssigned()
                    ? IngredientAssignmentResult.Success
                    : IngredientAssignmentResult.Retry;
            }

            var ingredients = RecipeNoteExt.GetIngredientsSpan(selectedRecipe);
            var clickedMaterial = false;
            for (int i = 0; i < ingredients.Length; i++)
            {
                var ingredient = ingredients[i];
                if (ingredient.ItemId == 0)
                    break;

                if (ingredient.NumTotal == 0)
                    continue;

                var availableCounts = GetInventoryAvailableCounts(ingredient.ItemId);
                if (availableCounts.NQ + availableCounts.HQ < ingredient.NumTotal)
                {
                    SetMissingIngredientFailure(ingredient.ItemId, ingredient.NumTotal, availableCounts.NQ, availableCounts.HQ);
                    return IngredientAssignmentResult.Fatal;
                }
                if (!qualityPolicy.TryResolveIngredientSelection(
                        ingredient.ItemId,
                        ingredient.NumAvailableNQ,
                        ingredient.NumAvailableHQ,
                        out var desiredNQ,
                        out var desiredHQ,
                        out var failureDetails))
                {
                    SetMissingIngredientFailure(ingredient.ItemId, ingredient.NumTotal, ingredient.NumAvailableNQ, ingredient.NumAvailableHQ, failureDetails);
                    return IngredientAssignmentResult.Fatal;
                }
                if (ingredient.NumAssignedNQ == desiredNQ && ingredient.NumAssignedHQ == desiredHQ)
                    continue;

                if (qualityPolicy.UsesHQFallbackForNQPreference(ingredient.ItemId, desiredHQ))
                {
                    GatherBuddy.Log.Debug(
                        $"[Crafting] Using HQ fallback for NQ-preferred ingredient {ingredient.ItemId}: desired NQ={desiredNQ}, HQ={desiredHQ}");
                }
                else if (qualityPolicy.UsesNQFallbackForHQPreference(ingredient.ItemId, desiredNQ))
                {
                    GatherBuddy.Log.Debug(
                        $"[Crafting] Using NQ fallback for HQ-preferred ingredient {ingredient.ItemId}: desired NQ={desiredNQ}, HQ={desiredHQ}");
                }

                if (IsEquipmentIngredient(ingredient.ItemId))
                {
                    if (!OpenEquipmentIngredientMenu(atkUnit, (uint)i))
                    {
                        GatherBuddy.Log.Debug(
                            $"[Crafting] Equipment ingredient selection did not apply for item {ingredient.ItemId}, retrying");
                        return IngredientAssignmentResult.Retry;
                    }

                    IngredientSelection.BeginEquipment(ingredient.ItemId, desiredHQ > 0, now);
                    return IngredientAssignmentResult.Retry;
                }

                var missingHQ = Math.Max(0, desiredHQ - ingredient.NumAssignedHQ);
                if (missingHQ > 0 && ingredient.NumAvailableHQ >= missingHQ)
                {
                    for (int m = 0; m < missingHQ; m++)
                        ClickMaterial(atkUnit, (uint)i, true);
                    clickedMaterial = true;
                }
                var missingNQ = Math.Max(0, desiredNQ - ingredient.NumAssignedNQ);
                if (missingNQ > 0 && ingredient.NumAvailableNQ >= missingNQ)
                {
                    for (int m = 0; m < missingNQ; m++)
                        ClickMaterial(atkUnit, (uint)i, false);
                    clickedMaterial = true;
                }
            }

            if (clickedMaterial)
            {
                IngredientSelection.DelayNormalAssignment(now);
                return IngredientAssignmentResult.Retry;
            }
            if (!AreIngredientsAssigned())
                return IngredientAssignmentResult.Retry;

            ResetIngredientSelectionState();
            _lastPreparationFailure = null;
            GatherBuddy.Log.Debug($"[Crafting] Ingredients assigned, ready to craft");
            return IngredientAssignmentResult.Success;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[Crafting] Error selecting ingredients: {ex.Message}\n{ex.StackTrace}");
            return IngredientAssignmentResult.Retry;
        }
    }

    private static unsafe void ClickMaterial(AtkUnitBase* recipeNoteUnit, uint index, bool hq)
    {
        try
        {
            uint callbackIndex = index;
            if (hq)
                callbackIndex += 0x10_000;
            Callback.Fire(recipeNoteUnit, false, 6, callbackIndex, 0);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[Crafting] Error clicking material: {ex.Message}");
        }
    }
    
    private static bool IsEquipmentIngredient(uint itemId)
    {
        if (_equipmentItemCache.TryGetValue(itemId, out var cached))
            return cached;
        
        try
        {
            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            if (itemSheet == null)
                return false;
            
            if (!itemSheet.TryGetRow(itemId, out var item))
                return false;
            
            bool isEquipment = item.EquipSlotCategory.RowId > 0;
            _equipmentItemCache[itemId] = isEquipment;
            return isEquipment;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[Crafting] Error checking if item {itemId} is equipment: {ex.Message}");
            return false;
        }
    }
    
    private static unsafe bool OpenEquipmentIngredientMenu(AtkUnitBase* recipeNoteUnit, uint index)
    {
        try
        {
            var componentNode = recipeNoteUnit->GetComponentNodeById(89 + index);
            if (componentNode == null || !componentNode->AtkResNode.IsVisible())
            {
                GatherBuddy.Log.Warning($"[Crafting] Ingredient node {index} not found or not visible");
                return false;
            }
            
            if (componentNode->Component == null)
            {
                GatherBuddy.Log.Warning($"[Crafting] Component is null for ingredient {index}");
                return false;
            }
            
            var selectionButton = componentNode->Component->GetNodeById(7);
            if (selectionButton == null || !selectionButton->IsVisible())
                return false;
            
            var clickButtonNode = componentNode->Component->GetNodeById(5);
            if (clickButtonNode == null)
            {
                GatherBuddy.Log.Warning($"[Crafting] Click button node not found for ingredient {index}");
                return false;
            }
            
            var clickButton = clickButtonNode->GetAsAtkComponentButton();
            if (clickButton == null)
            {
                GatherBuddy.Log.Warning($"[Crafting] Click button not found for ingredient {index}");
                return false;
            }
            
            var buttonClickEvent = stackalloc AtkEvent[1];
            var eventData = (AtkEventData*)clickButtonNode;
            recipeNoteUnit->ReceiveEvent(AtkEventType.ButtonClick, 5, buttonClickEvent, eventData);
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Error opening equipment ingredient menu: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    private static unsafe bool SelectEquipmentIngredientFromMenu(uint itemId, bool preferHQ)
    {
        try
        {
            var contextMenuAddon = Dalamud.GameGui.GetAddonByName("ContextIconMenu");
            if (contextMenuAddon.Address == nint.Zero)
            {
                GatherBuddy.Log.Warning($"[Crafting] ContextIconMenu addon not found after button click");
                return false;
            }
            
            var contextMenu = (AtkUnitBase*)contextMenuAddon.Address;
            if (contextMenu == null || !contextMenu->IsVisible)
            {
                GatherBuddy.Log.Warning($"[Crafting] ContextIconMenu not visible after button click");
                return false;
            }
            
            var selectItemId = preferHQ ? itemId + 1_000_000 : itemId;
            
            Callback.Fire(contextMenu, true, 0, 0, 0, selectItemId, 0);
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Error selecting equipment ingredient from menu: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    private static void ResetIngredientSelectionState()
        => IngredientSelection.Reset();
    
    public static void DebugClickRecipeNote(uint ingredientIndex, int clickCount, bool isHQ, bool autoOpen = false, uint recipeId = 0)
    {
        _ = DebugClickRecipeNoteAsync(ingredientIndex, clickCount, isHQ, autoOpen, recipeId);
    }
    
    private static async System.Threading.Tasks.Task DebugClickRecipeNoteAsync(uint ingredientIndex, int clickCount, bool isHQ, bool autoOpen, uint recipeId)
    {
        try
        {
            if (autoOpen)
            {
                GatherBuddy.Log.Information(recipeId > 0 
                    ? $"[Debug] Opening Recipe Note for recipe {recipeId}..." 
                    : "[Debug] Opening Recipe Note...");
                    
                if (!OpenRecipeNoteUI(recipeId))
                {
                    GatherBuddy.Log.Warning("[Debug] Failed to open Recipe Note");
                    return;
                }
                
                GatherBuddy.Log.Information("[Debug] Waiting for Recipe Note to open...");
                for (int i = 0; i < 50; i++) // 5 second timeout
                {
                    var (isOpen, _, _, _, _, _, _) = GetIngredientState(ingredientIndex);
                    if (isOpen)
                    {
                        GatherBuddy.Log.Information("[Debug] Recipe Note opened successfully");
                        await System.Threading.Tasks.Task.Delay(200);
                        break;
                    }
                    await System.Threading.Tasks.Task.Delay(100);
                }
            }
            
            var (valid, beforeNQ, beforeHQ, itemId, availNQ, availHQ, needed) = GetIngredientState(ingredientIndex);
            if (!valid) return;
            
            GatherBuddy.Log.Information($"[Debug] Testing clicks on ingredient {ingredientIndex}: ItemId={itemId}, " +
                $"NQ avail={availNQ}, HQ avail={availHQ}, needed={needed}, clicking {clickCount} times (HQ={isHQ})");
            GatherBuddy.Log.Information($"[Debug] Before: Assigned NQ={beforeNQ}, Assigned HQ={beforeHQ}");
            
            for (int i = 0; i < clickCount; i++)
            {
                ClickMaterialSafe(ingredientIndex, isHQ);
                await System.Threading.Tasks.Task.Delay(50); 
            }
            
            await System.Threading.Tasks.Task.Delay(200);
            
            var (validAfter, afterNQ, afterHQ, _, _, _, _) = GetIngredientState(ingredientIndex);
            if (!validAfter) return;
            
            GatherBuddy.Log.Information($"[Debug] After: Assigned NQ={afterNQ}, Assigned HQ={afterHQ}");
            var actualClicks = isHQ ? (afterHQ - beforeHQ) : (afterNQ - beforeNQ);
            GatherBuddy.Log.Information($"[Debug] Result: Requested {clickCount} clicks, {actualClicks} were registered (success rate: {actualClicks * 100.0 / clickCount:F1}%)");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Debug] Error testing Recipe Note clicks: {ex.Message}\n{ex.StackTrace}");
        }
    }
    
    private static unsafe (bool valid, int assignedNQ, int assignedHQ, uint itemId, int availNQ, int availHQ, int needed) GetIngredientState(uint ingredientIndex)
    {
        var addon = Dalamud.GameGui.GetAddonByName("RecipeNote");
        if (addon == null || addon.Address == nint.Zero)
        {
            GatherBuddy.Log.Warning("[Debug] RecipeNote window not open");
            return (false, 0, 0, 0, 0, 0, 0);
        }
        
        var atkUnit = (AtkUnitBase*)addon.Address;
        if (atkUnit == null || !atkUnit->IsVisible)
        {
            GatherBuddy.Log.Warning("[Debug] RecipeNote window not visible");
            return (false, 0, 0, 0, 0, 0, 0);
        }
        
        var recipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote.Instance();
        if (recipeNote == null || recipeNote->RecipeList == null)
        {
            GatherBuddy.Log.Warning("[Debug] RecipeNote data not available");
            return (false, 0, 0, 0, 0, 0, 0);
        }
        
        var selectedRecipe = recipeNote->RecipeList->SelectedRecipe;
        if (selectedRecipe == null)
        {
            GatherBuddy.Log.Warning("[Debug] No recipe selected");
            return (false, 0, 0, 0, 0, 0, 0);
        }
        
        var ingredients = RecipeNoteExt.GetIngredientsSpan(selectedRecipe);
        if (ingredientIndex >= ingredients.Length)
        {
            GatherBuddy.Log.Warning($"[Debug] Ingredient index {ingredientIndex} out of range (recipe has {ingredients.Length} ingredients)");
            return (false, 0, 0, 0, 0, 0, 0);
        }
        
        var ingredient = ingredients[(int)ingredientIndex];
        return (true, ingredient.NumAssignedNQ, ingredient.NumAssignedHQ, ingredient.ItemId, 
                ingredient.NumAvailableNQ, ingredient.NumAvailableHQ, ingredient.NumTotal);
    }
    
    private static unsafe void ClickMaterialSafe(uint ingredientIndex, bool isHQ)
    {
        var addon = Dalamud.GameGui.GetAddonByName("RecipeNote");
        if (addon == null || addon.Address == nint.Zero) return;
        
        var atkUnit = (AtkUnitBase*)addon.Address;
        if (atkUnit == null) return;
        
        ClickMaterial(atkUnit, ingredientIndex, isHQ);
    }
    
    private static unsafe bool OpenRecipeNoteUI(uint recipeId = 0)
    {
        try
        {
            var addon = Dalamud.GameGui.GetAddonByName("RecipeNote");
            if (addon != null && addon.Address != nint.Zero)
            {
                var atkUnit = (AtkUnitBase*)addon.Address;
                if (atkUnit != null && atkUnit->IsVisible)
                {
                    if (recipeId > 0)
                    {
                        GatherBuddy.Log.Debug($"[Debug] Recipe Note already open, switching to recipe {recipeId}");
                    }
                    else
                    {
                        GatherBuddy.Log.Debug("[Debug] Recipe Note already open");
                        return true;
                    }
                }
            }
            
            var agent = AgentRecipeNote.Instance();
            if (agent == null)
            {
                GatherBuddy.Log.Warning("[Debug] AgentRecipeNote not available");
                return false;
            }
            
            agent->OpenRecipeByRecipeId(recipeId);
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Debug] Failed to open Recipe Note: {ex.Message}");
            return false;
        }
    }

    private static unsafe bool ExecuteCraft()
    {
        try
        {
            var cosmic = _currentRecipe is { } recipe && recipe.Number == 0;
            var addon = Dalamud.GameGui.GetAddonByName(cosmic ? "WKSRecipeNotebook" : "RecipeNote");
            if (addon == null || addon.Address == nint.Zero)
                return false;

            var atkUnit = (AtkUnitBase*)addon.Address;
            if (atkUnit == null || !atkUnit->IsVisible)
                return false;

            Callback.Fire(atkUnit, true, cosmic ? 6 : 8);
            GatherBuddy.Log.Information($"[Crafting] Craft started");
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Failed to execute craft: {ex.Message}");
            return false;
        }
    }
    
    public static unsafe void ExecuteQuickSynthesis(int quantity)
    {
        try
        {
            var recipeNoteAddon = Dalamud.GameGui.GetAddonByName("RecipeNote");
            if (recipeNoteAddon == null || recipeNoteAddon.Address == nint.Zero)
            {
                GatherBuddy.Log.Warning("[Crafting] RecipeNote not open for quick synthesis");
                return;
            }

            var atkUnit = (AtkUnitBase*)recipeNoteAddon.Address;
            if (atkUnit == null || !atkUnit->IsVisible)
            {
                GatherBuddy.Log.Warning("[Crafting] RecipeNote not visible for quick synthesis");
                return;
            }
            var qualityPolicy = _currentQualityPolicy;
            var allowHQMaterials = qualityPolicy?.AllowHQMaterialsInQuickSynthesis ?? true;
            if (!TryPrepareQuickSynthesisQuantity(quantity, allowHQMaterials, out var adjustedQuantity))
                return;

            _quickSynthTarget = adjustedQuantity;
            _quickSynthCompleted = 0;
            _quickSynthWindowSeen = false;
            
            GatherBuddy.Log.Debug($"[Crafting] Opening quick synthesis dialog for {_quickSynthTarget} item(s)");
            Callback.Fire(atkUnit, true, 9);
            
            var tm = GatherBuddy.AutoGather?.TaskManager;
            if (tm == null)
                return;
                
            tm.DelayNext(200);
            tm.Enqueue(() => ConfirmQuickSynthesis(_quickSynthTarget), 3000, "ConfirmQuickSynthesis");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Failed to execute quick synthesis: {ex.Message}");
        }
    }

    private static unsafe bool TryPrepareQuickSynthesisQuantity(int requestedQuantity, bool allowHQMaterials, out int adjustedQuantity)
    {
        adjustedQuantity = Math.Clamp(requestedQuantity, 1, 99);

        var (canEvaluate, maxCraftable, blockingItemId, neededPerCraft, availableNQ, availableHQ) =
            EvaluateQuickSynthAvailability(allowHQMaterials);
        var hasRecipeNoteCraftableCount = TryReadRecipeNoteCraftableCount(out var recipeNoteCraftableCount);
        if (hasRecipeNoteCraftableCount)
        {
            GatherBuddy.Log.Debug($"[Crafting] Quick synthesis RecipeNote craftable count: {recipeNoteCraftableCount}");
            if (!canEvaluate || recipeNoteCraftableCount < maxCraftable)
                maxCraftable = recipeNoteCraftableCount;
        }

        if (!canEvaluate && !hasRecipeNoteCraftableCount)
        {
            GatherBuddy.Log.Debug("[Crafting] Quick synthesis material precheck unavailable, proceeding without clamp");
            return true;
        }

        GatherBuddy.Log.Debug(
            $"[Crafting] Quick synthesis material precheck: requested={requestedQuantity}, clamped={adjustedQuantity}, maxCraftable={maxCraftable}, allowHQMaterials={allowHQMaterials}");

        if (maxCraftable <= 0)
        {
            if (blockingItemId != 0)
            {
                var itemName = GetItemName(blockingItemId);
                var modeText = allowHQMaterials ? "using all available materials" : "using NQ-only materials";
                SetPreparationFailure(
                    CraftPreparationFailureReason.MissingMaterialsUnableToQuickSynth,
                    blockingItemId,
                    neededPerCraft,
                    availableNQ,
                    availableHQ,
                    $"unable to quick synth '{itemName}' (item {blockingItemId}) {modeText}: needed per craft {neededPerCraft}, available NQ={availableNQ}, HQ={availableHQ}");
                GatherBuddy.Log.Warning(
                    $"[Crafting] Quick synthesis blocked by missing materials for '{itemName}' (item {blockingItemId})");
            }
            else
            {
                SetPreparationFailure(
                    CraftPreparationFailureReason.MissingMaterialsUnableToQuickSynth,
                    0,
                    0,
                    0,
                    0,
                    "RecipeNote reports 0 craftable items from current inventory for quick synthesis");
                GatherBuddy.Log.Warning("[Crafting] Quick synthesis blocked because RecipeNote reports 0 craftable items from current inventory");
            }
            return false;
        }

        if (maxCraftable < adjustedQuantity)
        {
            var itemName = GetItemName(blockingItemId);
            GatherBuddy.Log.Warning(
                $"[Crafting] Quick synthesis batch reduced from {adjustedQuantity} to {maxCraftable} because '{itemName}' (item {blockingItemId}) is the limiting ingredient");
            adjustedQuantity = maxCraftable;
        }

        return true;
    }

    private static unsafe (bool CanEvaluate, int MaxCraftable, uint BlockingItemId, int NeededPerCraft, int AvailableNQ, int AvailableHQ) EvaluateQuickSynthAvailability(bool allowHQMaterials)
    {
        var recipeNote = FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote.Instance();
        if (recipeNote == null || recipeNote->RecipeList == null || recipeNote->RecipeList->SelectedRecipe == null)
            return (false, 0, 0, 0, 0, 0);

        var ingredients = RecipeNoteExt.GetIngredientsSpan(recipeNote->RecipeList->SelectedRecipe);
        var maxCraftable = int.MaxValue;
        uint blockingItemId = 0;
        var blockingNeeded = 0;
        var blockingAvailableNQ = 0;
        var blockingAvailableHQ = 0;

        for (var i = 0; i < ingredients.Length; i++)
        {
            var ingredient = ingredients[i];
            if (ingredient.ItemId == 0)
                break;

            if (ingredient.NumTotal == 0)
                continue;

            var availableNQ = ingredient.NumAvailableNQ;
            var availableHQ = ingredient.NumAvailableHQ;
            var availableTotal = allowHQMaterials ? availableNQ + availableHQ : availableNQ;
            var craftableByIngredient = availableTotal / ingredient.NumTotal;

            GatherBuddy.Log.Debug(
                $"[Crafting] Quick synthesis ingredient check: item={ingredient.ItemId}, needed={ingredient.NumTotal}, availableNQ={availableNQ}, availableHQ={availableHQ}, allowHQMaterials={allowHQMaterials}, craftable={craftableByIngredient}");

            if (craftableByIngredient >= maxCraftable)
                continue;

            maxCraftable = craftableByIngredient;
            blockingItemId = ingredient.ItemId;
            blockingNeeded = ingredient.NumTotal;
            blockingAvailableNQ = availableNQ;
            blockingAvailableHQ = availableHQ;
        }

        if (maxCraftable == int.MaxValue)
            return (false, 0, 0, 0, 0, 0);

        return (true, maxCraftable, blockingItemId, blockingNeeded, blockingAvailableNQ, blockingAvailableHQ);
    }

    private static unsafe bool TryReadRecipeNoteCraftableCount(out int craftableCount)
    {
        craftableCount = 0;
        var addon = (AddonRecipeNote*)Dalamud.GameGui.GetAddonByName("RecipeNote").Address;
        if (addon == null || !addon->AtkUnitBase.IsVisible || addon->SelectedRecipeQuantityCraftableFromMaterialsInInventory == null)
            return false;

        return int.TryParse(addon->SelectedRecipeQuantityCraftableFromMaterialsInInventory->NodeText.ToString(), out craftableCount);
    }
    
    private static unsafe bool ConfirmQuickSynthesis(int quantity)
    {
        try
        {
            var quickSynthDialogAddon = Dalamud.GameGui.GetAddonByName("SynthesisSimpleDialog");
            if (quickSynthDialogAddon == null || quickSynthDialogAddon.Address == nint.Zero)
                return false;

            var dialogUnit = (AtkUnitBase*)quickSynthDialogAddon.Address;
            if (dialogUnit == null || !dialogUnit->IsVisible)
                return false;

            var clampedQuantity = Math.Min(quantity, 99);
            GatherBuddy.Log.Information($"[Crafting] Confirming quick synthesis for {clampedQuantity} items");
            var qualityPolicy = _currentQualityPolicy;
            var allowHQMaterials = qualityPolicy?.AllowHQMaterialsInQuickSynthesis ?? true;
            var synthesizeNQOnly = qualityPolicy?.OverrideMode == CraftingQualityOverrideMode.RequireNQOnly;
            GatherBuddy.Log.Debug(
                $"[Crafting] Quick synthesis flags: allowHQMaterials={allowHQMaterials}, synthesizeNQOnly={synthesizeNQOnly}");
            
            var values = stackalloc AtkValue[3];
            values[0] = new()
            {
                Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int,
                Int = clampedQuantity,
            };
            values[1] = new()
            {
                Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool,
                Byte = allowHQMaterials ? (byte)1 : (byte)0,
            };
            values[2] = new()
            {
                Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool,
                Byte = synthesizeNQOnly ? (byte)1 : (byte)0
            };
            Callback.Fire(dialogUnit, true, values[0], values[1], values[2]);
            
            _currentState = CraftState.QuickSynthesis;
            StateChanged?.Invoke(_currentState);
            
            return true;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Failed to confirm quick synthesis: {ex.Message}");
            return false;
        }
    }

    private static async void ExecuteSolverRecommendation(Vulcan.CraftState craft, Vulcan.StepState step, Solver.Recommendation recommendation)
    {
        if (_automationPaused || _actionExecutor == null || recommendation.Action == VulcanSkill.None)
            return;

        try
        {
            var liveStep = SynthesisReader.ReadCurrentStepState(craft, step);
            if (liveStep == null || !RecommendationStateStillCurrent(step, liveStep))
            {
                GatherBuddy.Log.Debug(
                    $"[Crafting] Discarding stale solver recommendation {recommendation.Action}; "
                    + $"solved={step}; live={liveStep?.ToString() ?? "unavailable"}");
                return;
            }

            if (ShouldRejectCarefulObservation(recommendation.Action, _executedActions))
            {
                const string reason = "Careful Observation was refused during an active crafting combo";
                _automationFaultReported = true;
                GatherBuddy.Log.Error($"[Crafting] {reason}. History=[{string.Join(",", _executedActions)}]");
                AutomationFaulted?.Invoke(reason);
                return;
            }

            var canExecute = _actionExecutor.CanExecuteAction(recommendation.Action, craft, step);
            if (!canExecute)
            {
                GatherBuddy.Log.Debug($"[Crafting] Solver recommendation {recommendation.Action} could not be executed at {step}");
                return;
            }

            var success = await _actionExecutor.TryExecuteActionAsync(recommendation.Action);
            if (!success)
            {
                GatherBuddy.Log.Debug($"[Crafting] Solver recommendation {recommendation.Action} failed to execute after passing pre-check");
                return;
            }

            if (CraftingProcessor.ActiveSolver is DonatelloSolver issued)
                issued.NotifyOpportunisticActionIssued();

            GatherBuddy.Log.Debug(
                $"[CraftingTrace] Issued recipe={craft.RecipeId} solver={_currentSolverMode} action={recommendation.Action}({(uint)recommendation.Action}) "
                + $"source={recommendation.Comment} status={Vulcan.SolverUtils.Status(craft, step)} step={step.Index} "
                + $"condition={step.Condition} progress={step.Progress}/{craft.CraftProgress} quality={step.Quality}/{craft.CraftQualityMax} "
                + $"durability={step.Durability}/{craft.CraftDurability} cp={step.RemainingCP}/{craft.StatCP} state={step}");

            var (result, nextStep) = Vulcan.Simulator.Execute(craft, step, recommendation.Action, 0.5f, 0.5f);
            if (result == Vulcan.Simulator.ExecuteResult.Succeeded || result == Vulcan.Simulator.ExecuteResult.Failed)
            {
                _vulcanPreActionStepState = step;
                _vulcanPendingAction = recommendation.Action;
                _vulcanPendingActionIssuedTick = Environment.TickCount64;
                _vulcanStepState = nextStep;
                _vulcanPredictionPendingObservation = true;
                ResetUnreconciledObservation();
                if (recommendation.Action == VulcanSkill.MaterialMiracle)
                    GatherBuddy.Log.Information("[Crafting] Material Miracle issued; awaiting live condition/charge acknowledgement before starting solver");
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Error executing solver action: {ex.Message}");
        }
    }

    internal static bool RecommendationStateStillCurrent(StepState solved, StepState live)
        => StepStateReconciler.ObservableEquivalent(solved, live);

    internal static bool ShouldRejectCarefulObservation(
        VulcanSkill action,
        IReadOnlyList<VulcanSkill> executedActions)
        => action == VulcanSkill.CarefulObservation
            && CraftingActionHistory.ActiveCombo(executedActions) != VulcanSkill.None;

    private static void CheckGatherToCraftTransition()
    {
        if (!CraftingGatherBridge.WaitingForGatherComplete)
            return;
        
        if (!GatherBuddy.AutoGather.Enabled && CraftingGatherBridge.IsGatheringComplete())
        {
            CraftingGatherBridge.OnGatherComplete();
        }
    }
    
    private static DateTime _lastJobSwitchAttempt = DateTime.MinValue;
    
    private static void RetryGatherToCraftAfterJobSwitch()
    {
        var timeSinceAttempt = (DateTime.Now - _lastJobSwitchAttempt).TotalSeconds;
        if (timeSinceAttempt < 2)
            return;
        
        _lastJobSwitchAttempt = DateTime.MinValue;
        CraftingGatherBridge.OnGatherComplete();
    }
    

    public static void Update()
    {
        CraftingProcessor.Update();
        if (!_automationFaultReported && CraftingProcessor.FaultReason is { Length: > 0 } solverFault)
        {
            _automationFaultReported = true;
            GatherBuddy.Log.Error($"[Crafting] {solverFault}");
            AutomationFaulted?.Invoke(solverFault);
        }
        CheckGatherToCraftTransition();

        if (TryAutoTakeOverManualSynthesis())
            return;
        
        if (_currentState != CraftState.IdleNormal)
            GatherBuddy.Log.Verbose($"[Crafting] Update: state={_currentState}, Crafting={Dalamud.Conditions[ConditionFlag.Crafting]}, ExecutingAction={Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction]}");
        
        var newState = _currentState switch
        {
            CraftState.IdleNormal => TransitionFromIdleNormal(),
            CraftState.PreparingCraft => TransitionFromPreparingCraft(),
            CraftState.WaitStart => TransitionFromWaitStart(),
            CraftState.InProgress => TransitionFromInProgress(),
            CraftState.WaitAction => TransitionFromWaitAction(),
            CraftState.WaitFinish => TransitionFromWaitFinish(),
            CraftState.IdleBetween => TransitionFromIdleBetween(),
            CraftState.QuickSynthesis => TransitionFromQuickSynthesis(),
            _ => TransitionFromInvalid()
        };

        if (newState != _currentState)
        {
            GatherBuddy.Log.Debug($"[Crafting] State transition: {_currentState} -> {newState}");
            _currentState = newState;
            StateChanged?.Invoke(newState);
        }
    }

    private static CraftState TransitionFromIdleNormal()
    {
        if (Dalamud.Conditions[ConditionFlag.Crafting])
            return SynthesisReader.IsSynthesisWindowOpen()
                ? CraftState.IdleNormal
                : CraftState.IdleBetween;

        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
            return CraftState.WaitStart;

        return CraftState.IdleNormal;
    }


    private static unsafe bool AreIngredientsAssigned()
    {
        var selectedRecipe = RecipeNoteExt.GetSelectedRecipeEntry();
        if (selectedRecipe == null)
            return false;

        var ingredients = RecipeNoteExt.GetIngredientsSpan(selectedRecipe);
        for (int i = 0; i < ingredients.Length; i++)
        {
            var ingredient = ingredients[i];
            if (ingredient.ItemId == 0)
                break;

            if (ingredient.NumTotal == 0)
                continue;

            if (ingredient.NumAssignedNQ + ingredient.NumAssignedHQ < ingredient.NumTotal)
            {
                GatherBuddy.Log.Debug($"[Crafting] Ingredient assignment incomplete for item {ingredient.ItemId}: assigned NQ={ingredient.NumAssignedNQ}, HQ={ingredient.NumAssignedHQ}, needed={ingredient.NumTotal}");
                return false;
            }
        }

        return true;
    }

    private static unsafe bool SelectCosmicRecipe(AtkUnitBase* addon)
    {
        if (!_currentRecipeId.HasValue)
            return false;
        var selected = RecipeNoteExt.GetSelectedRecipeEntry();
        if (selected != null && selected->RecipeId == _currentRecipeId.Value)
            return true;

        var data = RecipeNoteExt.GetRecipeData();
        if (data == null || data->Recipes == null)
            return false;
        for (var index = 0; index < data->RecipesCount; index++)
        {
            Callback.Fire(addon, false, 0, index);
            selected = RecipeNoteExt.GetSelectedRecipeEntry();
            if (selected != null && selected->RecipeId == _currentRecipeId.Value)
                return true;
        }
        return false;
    }

    private static unsafe bool ClickCosmicIngredientButton(AtkUnitBase* addon, uint nodeId)
    {
        var node = addon->GetNodeById(nodeId);
        if (node == null)
            return false;
        var button = node->GetAsAtkComponentButton();
        if (button == null || button->AtkComponentBase.OwnerNode == null)
            return false;
        var buttonNode = button->AtkComponentBase.OwnerNode;
        var eventData = buttonNode->AtkResNode.AtkEventManager.Event;
        if (eventData == null)
            return false;
        var atkEvent = (AtkEvent*)eventData;
        addon->ReceiveEvent(atkEvent->State.EventType, (int)atkEvent->Param, eventData);
        return true;
    }

    internal static (uint First, uint Second) CosmicIngredientButtonOrder(
        DonatelloExecutionOptions? options)
        => options?.Objective == DonatelloSolveObjective.MaximizeQuality
            ? (40u, 39u)
            : (39u, 40u);

    private static unsafe (int NQ, int HQ) GetInventoryAvailableCounts(uint itemId)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return (0, 0);

        return (
            (int)inventoryManager->GetInventoryItemCount(itemId, false, false, false),
            (int)inventoryManager->GetInventoryItemCount(itemId, true, false, false));
    }
    private static CraftingQualityPolicy? GetActiveQualityPolicy()
    {
        if (_currentQualityPolicy != null)
            return _currentQualityPolicy;

        if (!_currentRecipeId.HasValue)
            return null;

        return TryGetRecipe(_currentRecipeId.Value, out var recipe) && recipe.HasValue
            ? CraftingQualityPolicyResolver.Resolve(recipe.Value, null)
            : null;
    }

    private static void SetMissingIngredientFailure(uint itemId, int needed, int availableNQ, int availableHQ, string? detailsOverride = null)
        => SetPreparationFailure(
            CraftPreparationFailureReason.MissingIngredientsUnableToSelect,
            itemId,
            needed,
            availableNQ,
            availableHQ,
            detailsOverride);

    private static void SetPreparationFailure(
        CraftPreparationFailureReason reason,
        uint itemId,
        int needed,
        int availableNQ,
        int availableHQ,
        string? detailsOverride = null)
    {
        if (!_currentRecipeId.HasValue)
            return;

        var itemName = GetItemName(itemId);
        var details = detailsOverride ?? reason switch
        {
            CraftPreparationFailureReason.MissingMaterialsUnableToQuickSynth =>
                $"unable to quick synth '{itemName}' (item {itemId}): needed {needed}, available NQ={availableNQ}, HQ={availableHQ}",
            _ =>
                $"unable to select '{itemName}' (item {itemId}) in RecipeNote: needed {needed}, available NQ={availableNQ}, HQ={availableHQ}",
        };
        _lastPreparationFailure = new CraftPreparationFailure(
            _currentRecipeId.Value,
            reason,
            itemId,
            needed,
            availableNQ,
            availableHQ,
            details);
        GatherBuddy.Log.Warning($"[Crafting] Recipe {_currentRecipeId.Value} preparation failed ({reason}) due to missing materials: {details}");
    }
    private static CraftState TransitionFromPreparingCraft()
    {
        if (_lastPreparationFailure != null)
        {
            GatherBuddy.Log.Debug("[Crafting] PreparingCraft detected recorded preparation failure, forcing IdleNormal");
            _taskManagerIdleSince = DateTime.MinValue;
            return CraftState.IdleNormal;
        }
        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
        {
            _taskManagerIdleSince = DateTime.MinValue;
            return CraftState.WaitStart;
        }

        if (Dalamud.Conditions[ConditionFlag.Crafting])
        {
            _taskManagerIdleSince = DateTime.MinValue;
            return SynthesisReader.IsSynthesisWindowOpen()
                ? CraftState.WaitStart
                : CraftState.IdleBetween;
        }

        var tm = GatherBuddy.AutoGather?.TaskManager;
        if (tm != null && !tm.IsBusy)
        {
            if (_taskManagerIdleSince == DateTime.MinValue)
                _taskManagerIdleSince = DateTime.Now;

            if ((DateTime.Now - _taskManagerIdleSince).TotalSeconds > 1.0)
            {
                GatherBuddy.Log.Warning("[Crafting] PreparingCraft: craft tasks completed but no crafting conditions after 1s, resetting to IdleNormal");
                _taskManagerIdleSince = DateTime.MinValue;
                return CraftState.IdleNormal;
            }
        }
        else
        {
            _taskManagerIdleSince = DateTime.MinValue;
        }

        return CraftState.PreparingCraft;
    }

    private static unsafe CraftState TransitionFromQuickSynthesis()
    {
        var quickSynthAddon = Dalamud.GameGui.GetAddonByName("SynthesisSimple");
        
        if (quickSynthAddon != null && quickSynthAddon.Address != nint.Zero)
        {
            var atkUnit = (AtkUnitBase*)quickSynthAddon.Address;
            if (atkUnit != null && atkUnit->IsVisible && atkUnit->AtkValuesCount >= 5)
            {
                _quickSynthWindowSeen = true;
                
                var current = atkUnit->AtkValues[3].Int;
                var max = atkUnit->AtkValues[4].Int;
                
                if (_quickSynthCompleted != current)
                {
                    _quickSynthCompleted = current;
                    GatherBuddy.Log.Debug($"[Crafting] Quick synthesis progress: {current}/{max}");
                    QuickSynthProgress?.Invoke(current, max);
                }
                
                if (current >= max && max > 0)
                {
                    GatherBuddy.Log.Debug($"[Crafting] Quick synthesis complete ({current}/{max}), closing window");
                    Callback.Fire(atkUnit, true, -1);
                    return CraftState.QuickSynthesis;
                }
                
                return CraftState.QuickSynthesis;
            }
        }
        
        if (!_quickSynthWindowSeen)
        {
            return CraftState.QuickSynthesis;
        }
        
        if (Dalamud.Conditions[ConditionFlag.PreparingToCraft])
        {
            GatherBuddy.Log.Debug("[Crafting] Quick synthesis complete, back in crafting menu");
            var finishedRecipe = _currentRecipe;
            _quickSynthTarget = 0;
            _quickSynthCompleted = 0;
            _quickSynthWindowSeen = false;
            _currentQualityPolicy = null;
            _currentIngredientPreferences = null;
            _currentUseAllNQ = false;
            _currentSelectedMacroId = null;
            _currentDonatelloOptions = null;
            _currentRecipe = null;
            _currentRecipeId = null;
            CraftFinished?.Invoke(finishedRecipe, false);
            return CraftState.IdleBetween;
        }
        
        return CraftState.QuickSynthesis;
    }
    
    private static CraftState TransitionFromIdleBetween()
    {
        var preparingFlag = Dalamud.Conditions[ConditionFlag.PreparingToCraft];
        var craftingFlag = Dalamud.Conditions[ConditionFlag.Crafting];
        var executingFlag = Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction];
        
        GatherBuddy.Log.Verbose($"[Crafting] IdleBetween check: Preparing={preparingFlag}, Crafting={craftingFlag}, Executing={executingFlag}");
        
        var tm = GatherBuddy.AutoGather?.TaskManager;
        if (tm != null && tm.IsBusy)
            return CraftState.IdleBetween;

        if (_lastPreparationFailure != null)
        {
            GatherBuddy.Log.Debug("[Crafting] IdleBetween detected recorded preparation failure, closing RecipeNote");
            return TransitionFromIdleBetweenToExit();
        }
        
        if (preparingFlag)
            return CraftState.IdleBetween;

        if (executingFlag)
            return CraftState.WaitStart;

        return TransitionFromIdleBetweenToExit();
    }

    private static unsafe CraftState TransitionFromIdleBetweenToExit()
    {
        GatherBuddy.Log.Information($"[Crafting] Exiting crafting menu, closing windows");
        try
        {
            var recipeAddon = Dalamud.GameGui.GetAddonByName("RecipeNote");
            if (recipeAddon != null && recipeAddon.Address != nint.Zero)
            {
                var atkUnit = (AtkUnitBase*)recipeAddon.Address;
                if (atkUnit != null && atkUnit->IsVisible)
                {
                    GatherBuddy.Log.Information("[Crafting] Closing RecipeNote window on exit from IdleBetween");
                    atkUnit->Close(true);
                }
            }
            var cosmicAddon = Dalamud.GameGui.GetAddonByName("WKSRecipeNotebook");
            if (cosmicAddon != null && cosmicAddon.Address != nint.Zero)
            {
                var atkUnit = (AtkUnitBase*)cosmicAddon.Address;
                if (atkUnit != null && atkUnit->IsVisible)
                {
                    GatherBuddy.Log.Information("[Crafting] Closing WKSRecipeNotebook window on exit from IdleBetween");
                    Callback.Fire(atkUnit, true, -1);
                }
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[Crafting] Error closing recipe note on exit: {ex.Message}");
        }
        return CraftState.IdleNormal;
    }

    private static CraftState TransitionFromWaitStart()
    {
        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
            return CraftState.WaitStart;

        if (!Dalamud.Conditions[ConditionFlag.Crafting])
            return CraftState.IdleNormal;

        if (_currentRecipeId == null)
            return CraftState.WaitStart;

        if (!TryGetRecipe(_currentRecipeId.Value, out var recipe))
            return CraftState.InvalidState;

        if (recipe == null)
            return CraftState.InvalidState;

        var actualRecipe = recipe.Value;
        GatherBuddy.Log.Debug($"[Crafting] Building craft state for recipe {_currentRecipeId}");
        _vulcanCraftState = CraftingStateBuilder.BuildCraftState(actualRecipe);
        if (_vulcanCraftState == null)
        {
            GatherBuddy.Log.Debug("[Crafting] Current crafter stats are unavailable; waiting before starting solver");
            return CraftState.WaitStart;
        }
        _vulcanCraftState.DonatelloOptions = _currentDonatelloOptions;
        if (_currentSolverMode == VulcanSolverMode.Gabriel
         && GabrielPolicyCatalog.TryPrepare(_vulcanCraftState, out var preparedGabrielCraft, out _, out _))
            _vulcanCraftState = preparedGabrielCraft;
        var qualityPolicy = GetActiveQualityPolicy();
        if (!_currentCraftIsTrial && qualityPolicy != null)
        {
            var iq = qualityPolicy.CalculateGuaranteedInitialQuality(actualRecipe);
            if (iq > 0)
            {
                GatherBuddy.Log.Debug($"[Crafting] Setting guaranteed InitialQuality={iq} from quality policy for Raphael key");
                _vulcanCraftState = _vulcanCraftState with { InitialQuality = iq };
            }
        }
        if (_vulcanCraftState != null && CraftingProcessor.SolverDefinitions.Any(definition => definition is RaphaelSolverDefinition))
        {
            var liveRaphaelRequest = RaphaelSolveRequest.FromCraftState(
                _vulcanCraftState,
                CraftingContextResolver.ResolveSpecialistActionsAllowed(_vulcanCraftState));
            GatherBuddy.Log.Debug($@"[Crafting] Live Raphael request at craft start: {liveRaphaelRequest.GetKey()}");
        }
        var modeledInitialStep = CraftingStateBuilder.BuildInitialStepState(_vulcanCraftState!);
        var observedInitialStep = SynthesisReader.ReadCurrentStepState(_vulcanCraftState!, modeledInitialStep);
        if (observedInitialStep == null)
            return CraftState.WaitStart;

        _currentRecipe = recipe;
        _executedActions.Clear();
        CraftStarted?.Invoke(recipe, _currentRecipeId.Value);
        _vulcanStepState = observedInitialStep;
        _vulcanPredictionPendingObservation = false;
        ResetReconciliationTracking();
        _automationFaultReported = false;
        if (_vulcanCraftState != null && _vulcanStepState != null)
        {
            BeginActionDelay(_vulcanStepState);
            var raphaelRequest = RaphaelSolveRequest.FromCraftState(
                _vulcanCraftState,
                CraftingContextResolver.ResolveSpecialistActionsAllowed(_vulcanCraftState));
            var hasGuaranteedMaximumQualityRaphaelPlan =
                GatherBuddy.RaphaelSolveCoordinator.TryGetSolution(raphaelRequest, out var raphaelSolution)
                && DonatelloSolverDefinition.IsGuaranteedMaximumQualitySolution(
                    raphaelSolution,
                    _vulcanCraftState);
            _vulcanSolverStartDeferredForMaterialMiracle = ShouldDeferMaterialMiracleBootstrap(
                _currentSolverMode,
                CraftingProcessor.SolverDefinitions.Any(definition => definition is DonatelloSolverDefinition),
                hasGuaranteedMaximumQualityRaphaelPlan,
                _vulcanCraftState,
                _vulcanStepState);
            if (_vulcanSolverStartDeferredForMaterialMiracle)
            {
                GatherBuddy.Log.Information(CraftingProcessorSession.FormatCraftStartLogLine(
                    _vulcanCraftState,
                    _vulcanStepState,
                    _currentRecipeId.Value,
                    _currentCraftIsTrial,
                    "Donatello (Material Miracle bootstrap pending)"));
            }
            else
            {
                CraftingProcessor.OnCraftStarted(
                    _vulcanCraftState,
                    _vulcanStepState,
                    _currentRecipeId.Value,
                    _currentCraftIsTrial,
                    _currentSolverMode == VulcanSolverMode.Gabriel
                        ? typeof(GabrielSolverDefinition)
                        : null);
            }
            var recommendation = RecommendationForExecution(
                _vulcanCraftState,
                _vulcanStepState,
                _vulcanSolverStartDeferredForMaterialMiracle);
            GatherBuddy.Log.Debug($"[Crafting] OnCraftStarted recommendation: {recommendation.Action}");
            if (recommendation.Action != VulcanSkill.None && _nextActionAllowedAt == DateTime.MinValue)
            {
                ExecuteSolverRecommendation(_vulcanCraftState, _vulcanStepState, recommendation);
            }
        }

        return CraftState.InProgress;
    }

    private static void BeginActionDelay(StepState step, bool restart = false)
    {
        if (!restart && _actionDelayState == step)
            return;

        _actionDelayState = step with { };
        var delayMs = Math.Max(0, GatherBuddy.Config.VulcanExecutionDelayMs);
        _nextActionAllowedAt = delayMs > 0
            ? DateTime.Now.AddMilliseconds(delayMs)
            : DateTime.MinValue;
    }

    private static CraftState TransitionFromInProgress()
    {
        if (!Dalamud.Conditions[ConditionFlag.Crafting])
            return Finish(cancelled: true);

        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
            return CraftState.WaitAction;

        if (_nextActionAllowedAt != DateTime.MinValue)
        {
            if (DateTime.Now < _nextActionAllowedAt)
                return CraftState.InProgress;

            _nextActionAllowedAt = DateTime.MinValue;
            var delayedActual = _vulcanCraftState != null && _vulcanStepState != null
                ? SynthesisReader.ReadCurrentStepState(_vulcanCraftState, _vulcanStepState)
                : null;
            if (delayedActual == null
                || _vulcanStepState == null
                || StepStateReconciler.ObservableEquivalent(_vulcanStepState, delayedActual))
            {
                var cachedRecommendation = _vulcanCraftState != null && _vulcanStepState != null
                    ? RecommendationForExecution(
                        _vulcanCraftState,
                        _vulcanStepState,
                        _vulcanSolverStartDeferredForMaterialMiracle)
                    : CraftingProcessor.NextRecommendation;
                if (cachedRecommendation.Action != VulcanSkill.None && _vulcanCraftState != null && _vulcanStepState != null)
                    ExecuteSolverRecommendation(_vulcanCraftState, _vulcanStepState, cachedRecommendation);
                return CraftState.InProgress;
            }

            GatherBuddy.Log.Debug("[Crafting] Live state changed during execution delay; discarding stale recommendation");
        }

        if (_vulcanCraftState != null && _vulcanStepState != null)
        {
            var externalActionObserved = false;
            var inferredExternalAction = VulcanSkill.None;
            var actualState = SynthesisReader.ReadCurrentStepState(_vulcanCraftState, _vulcanStepState);
            if (actualState != null)
            {
                if (_vulcanPredictionPendingObservation
                    && _vulcanPendingAction == VulcanSkill.MaterialMiracle
                    && _vulcanPreActionStepState is { } materialMiraclePrevious
                    && !MaterialMiracleAcknowledged(materialMiraclePrevious, actualState))
                {
                    if (Environment.TickCount64 - _vulcanPendingActionIssuedTick >= 5_000
                        && !_automationFaultReported)
                    {
                        const string reason = "Material Miracle was issued but the game did not acknowledge its condition or charge change";
                        _automationFaultReported = true;
                        GatherBuddy.Log.Error($"[Crafting] {reason}. Previous={materialMiraclePrevious}; Observed={actualState}");
                        AutomationFaulted?.Invoke(reason);
                    }
                    return CraftState.InProgress;
                }

                var pendingAction = _vulcanPredictionPendingObservation
                    ? _vulcanPendingAction
                    : VulcanSkill.None;
                if (actualState.Durability <= 0)
                {
                    LogVerboseObservedAction(pendingAction, actualState, "durability-depleted");
                    RecordConfirmedAction(pendingAction);
                    GatherBuddy.Log.Debug($"[Crafting] Durability depleted, finishing craft");
                    return Finish(cancelled: false);
                }
                
                if (actualState.Progress >= _vulcanCraftState.CraftProgress)
                {
                    LogVerboseObservedAction(pendingAction, actualState, "complete");
                    RecordConfirmedAction(pendingAction);
                    GatherBuddy.Log.Debug($"[Crafting] Progress complete, transitioning to WaitFinish");
                    return CraftState.WaitFinish;
                }
                
                bool reconciled;
                if (_vulcanPredictionPendingObservation)
                {
                    reconciled = _vulcanPreActionStepState != null
                        && StepStateReconciler.TryReconcileAction(
                            _vulcanCraftState,
                            _vulcanPreActionStepState,
                            _vulcanPendingAction,
                            actualState,
                            out actualState);
                }
                else
                {
                    reconciled = StepStateReconciler.TryReconcileExternalAction(
                        _vulcanCraftState,
                        _vulcanStepState,
                        actualState,
                        out actualState,
                        out externalActionObserved,
                        out inferredExternalAction);
                }
                if (!reconciled)
                {
                    if (!UnreconciledObservationIsStable(actualState))
                        return CraftState.InProgress;

                    if (!_automationFaultReported)
                    {
                        const string reason = "Could not reconcile the live crafting state; automation stopped before issuing an unsafe action";
                        _automationFaultReported = true;
                        GatherBuddy.Log.Error($"[Crafting] {reason}. PendingAction={_vulcanPendingAction}; Previous={(_vulcanPreActionStepState ?? _vulcanStepState)}; Observed={actualState}");
                        AutomationFaulted?.Invoke(reason);
                    }
                    return CraftState.InProgress;
                }
                LogVerboseObservedAction(pendingAction, actualState, "advanced");
                RecordConfirmedAction(pendingAction != VulcanSkill.None
                    ? pendingAction
                    : inferredExternalAction);
                _vulcanPredictionPendingObservation = false;
                ResetReconciliationTracking();
                _vulcanStepState = actualState;
            }
            else
            {
                if (Dalamud.Conditions[ConditionFlag.PreparingToCraft])
                {
                    GatherBuddy.Log.Debug("[Crafting] Synthesis window closed and preparation menu returned; craft finished");
                    return Finish(cancelled: false);
                }

                GatherBuddy.Log.Debug("[Crafting] Could not read actual state from Synthesis window; waiting without issuing an action");
                return CraftState.InProgress;
            }
            
            if (_currentRecipe != null)
                CraftAdvanced?.Invoke(_currentRecipe);

            BeginActionDelay(_vulcanStepState);
            if (_vulcanSolverStartDeferredForMaterialMiracle)
            {
                _vulcanSolverStartDeferredForMaterialMiracle = false;
                GatherBuddy.Log.Information(
                    $"[Crafting] Material Miracle acknowledged; starting solver from observed state {_vulcanStepState}");
                if (!CraftingProcessor.TryAdoptLiveCraft(
                        _vulcanCraftState,
                        _vulcanStepState,
                        _currentSolverMode is VulcanSolverMode.Donatello or VulcanSolverMode.Gabriel
                            ? _currentSolverMode
                            : null,
                        out var liveSolverFailure))
                {
                    var reason = $"Could not start Donatello after Material Miracle: {liveSolverFailure}";
                    _automationFaultReported = true;
                    GatherBuddy.Log.Error($"[Crafting] {reason}");
                    AutomationFaulted?.Invoke(reason);
                    return CraftState.InProgress;
                }
            }
            else if (externalActionObserved)
            {
                GatherBuddy.Log.Information(
                    $"[Crafting] External crafting action {(inferredExternalAction == VulcanSkill.None ? "detected" : inferredExternalAction.ToString())}; replacing solver and replanning from observed state {_vulcanStepState}");
                if (!CraftingProcessor.TryAdoptLiveCraft(
                        _vulcanCraftState,
                        _vulcanStepState,
                        _currentSolverMode is VulcanSolverMode.Donatello or VulcanSolverMode.Gabriel
                            ? _currentSolverMode
                            : null,
                        out var liveSolverFailure))
                {
                    var reason = $"Could not replan after an external crafting action: {liveSolverFailure}";
                    _automationFaultReported = true;
                    GatherBuddy.Log.Error($"[Crafting] {reason}");
                    AutomationFaulted?.Invoke(reason);
                    return CraftState.InProgress;
                }
            }
            else
            {
                CraftingProcessor.OnCraftAdvanced(_vulcanCraftState, _vulcanStepState, _currentRecipeId);
            }
            var recommendation = RecommendationForExecution(
                _vulcanCraftState,
                _vulcanStepState,
                _vulcanSolverStartDeferredForMaterialMiracle);
            if (recommendation.Action != VulcanSkill.None && _nextActionAllowedAt == DateTime.MinValue)
                ExecuteSolverRecommendation(_vulcanCraftState, _vulcanStepState, recommendation);
        }

        return CraftState.InProgress;
    }

    private static void RecordConfirmedAction(VulcanSkill action)
    {
        if (action == VulcanSkill.None)
            return;
        _executedActions.Add(action);
        CraftActionExecuted?.Invoke(action);
    }

    internal static Solver.Recommendation ResolveExecutionRecommendation(
        Vulcan.CraftState craft,
        Vulcan.StepState step,
        Solver.Recommendation solverRecommendation,
        bool materialMiracleBootstrapPending)
        => materialMiracleBootstrapPending
            && Vulcan.Simulator.CanUseAction(craft, step, VulcanSkill.MaterialMiracle)
            ? new(VulcanSkill.MaterialMiracle, "ICE automatic Material Miracle")
            : solverRecommendation;

    private static Solver.Recommendation RecommendationForExecution(
        Vulcan.CraftState craft,
        Vulcan.StepState step,
        bool materialMiracleBootstrapPending)
    {
        CraftingProcessor.Update();
        var solverRecommendation = CraftingProcessor.NextRecommendation;
        if (CraftingProcessor.ActiveSolver is DonatelloSolver donatello
            && solverRecommendation.Action != VulcanSkill.None
            && donatello.TryApplyCompletedOpportunisticReplan(step, out var refreshed)
            && refreshed.Action != VulcanSkill.None)
            solverRecommendation = refreshed;
        return ResolveExecutionRecommendation(
            craft,
            step,
            solverRecommendation,
            materialMiracleBootstrapPending);
    }

    private static void LogVerboseObservedAction(
        VulcanSkill action,
        Vulcan.StepState observed,
        string outcome)
    {
        if (action == VulcanSkill.None || _vulcanCraftState == null)
            return;

        GatherBuddy.Log.Debug(
            $"[CraftingTrace] Observed recipe={_vulcanCraftState.RecipeId} action={action}({(uint)action}) outcome={outcome} "
            + $"status={Vulcan.SolverUtils.Status(_vulcanCraftState, observed)} step={observed.Index} condition={observed.Condition} "
            + $"progress={observed.Progress}/{_vulcanCraftState.CraftProgress} quality={observed.Quality}/{_vulcanCraftState.CraftQualityMax} "
            + $"durability={observed.Durability}/{_vulcanCraftState.CraftDurability} cp={observed.RemainingCP}/{_vulcanCraftState.StatCP} state={observed}");
    }

    internal static bool ShouldDeferMaterialMiracleBootstrap(
        VulcanSolverMode solverMode,
        bool donatelloRegistered,
        bool hasGuaranteedMaximumQualityRaphaelPlan,
        Vulcan.CraftState craft,
        Vulcan.StepState step)
        => solverMode == VulcanSolverMode.Donatello
            && donatelloRegistered
            && !hasGuaranteedMaximumQualityRaphaelPlan
            && Vulcan.Simulator.CanUseAction(craft, step, VulcanSkill.MaterialMiracle);

    internal static bool MaterialMiracleAcknowledged(StepState previous, StepState observed)
        => observed.MaterialMiracleCharges < previous.MaterialMiracleCharges
            || observed.Condition != previous.Condition;

    internal static uint ConservativeRecoveredStellarSteadyHandsUsed(DonatelloExecutionOptions? options)
        => options?.MaxStellarSteadyHandUses ?? 0;

    private static CraftState TransitionFromWaitAction()
    {
        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
            return CraftState.WaitAction;

        if (!Dalamud.Conditions[ConditionFlag.Crafting])
            return Finish(cancelled: true);

        return CraftState.InProgress;
    }

    private static CraftState TransitionFromWaitFinish()
    {
        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
            return CraftState.WaitFinish;

        if (!SynthesisReader.IsSynthesisWindowOpen())
        {
            GatherBuddy.Log.Debug($"[Crafting] Craft finished, closing windows");
            return Finish(cancelled: false);
        }

        return CraftState.WaitFinish;
    }

    private static CraftState TransitionFromInvalid()
    {
        if (!Dalamud.Conditions[ConditionFlag.Crafting] && !Dalamud.Conditions[ConditionFlag.PreparingToCraft] && !Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
            return CraftState.IdleNormal;

        if (Dalamud.Conditions[ConditionFlag.Crafting] && Dalamud.Conditions[ConditionFlag.PreparingToCraft])
            return CraftState.IdleBetween;

        return CraftState.InvalidState;
    }

    private static unsafe CraftState Finish(bool cancelled)
    {
        _nextActionAllowedAt = DateTime.MinValue;
        _actionDelayState = null;
        _lastPreparationFailure = null;
        ResetIngredientSelectionState();

        if (_currentRecipe != null)
            CraftFinished?.Invoke(_currentRecipe, cancelled);

        if (_vulcanCraftState != null && _vulcanStepState != null)
        {
            CraftingProcessor.OnCraftFinished(_vulcanCraftState, _vulcanStepState, _currentRecipeId, cancelled);
        }
        
        _currentSelectedMacroId = null;
        _currentDonatelloOptions = null;
        _currentQualityPolicy = null;
        _currentCraftIsTrial = false;
        ResetManualSynthesisTakeoverTracking();

        _currentRecipe = null;
        _currentRecipeId = null;
        _vulcanCraftState = null;
        _vulcanStepState = null;
        _vulcanPredictionPendingObservation = false;
        _vulcanSolverStartDeferredForMaterialMiracle = false;
        ResetReconciliationTracking();
        _automationFaultReported = false;
        
        GatherBuddy.Log.Debug($"[Crafting] Craft finished. Preparing={Dalamud.Conditions[ConditionFlag.PreparingToCraft]}, Crafting={Dalamud.Conditions[ConditionFlag.Crafting]}");
        
        if (Dalamud.Conditions[ConditionFlag.PreparingToCraft])
            return CraftState.IdleBetween;

        return CraftState.IdleNormal;
    }

    private static bool UnreconciledObservationIsStable(Vulcan.StepState observed)
    {
        if (_unreconciledObservedState == null
            || !StepStateReconciler.ObservableEquivalent(_unreconciledObservedState, observed))
        {
            _unreconciledObservedState = observed with { };
            _unreconciledObservedSince = DateTime.Now;
            return false;
        }

        return DateTime.Now - _unreconciledObservedSince >= ReconciliationGracePeriod;
    }

    private static void ResetUnreconciledObservation()
    {
        _unreconciledObservedState = null;
        _unreconciledObservedSince = DateTime.MinValue;
    }

    private static void ResetReconciliationTracking()
    {
        _vulcanPreActionStepState = null;
        _vulcanPendingAction = VulcanSkill.None;
        _vulcanPendingActionIssuedTick = 0;
        ResetUnreconciledObservation();
    }

    private static bool TryGetRecipe(uint recipeId, out Recipe? recipe)
    {
        recipe = null;
        var sheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (sheet != null && sheet.TryGetRow(recipeId, out var row))
        {
            recipe = row;
            return true;
        }

        return false;
    }
}
