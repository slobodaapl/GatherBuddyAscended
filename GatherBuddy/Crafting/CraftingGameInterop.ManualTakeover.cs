using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using System;
using System.Linq;

namespace GatherBuddy.Crafting;

public static partial class CraftingGameInterop
{
    private static uint? _manualTakeoverRecipeId;
    private static uint? _manualTakeoverBlockedRecipeId;
    private static string _manualTakeoverStatus = string.Empty;

    private static bool TryAutoTakeOverManualSynthesis()
    {
        if (!CanClaimManualSynthesis(
                GatherBuddy.Config.VulcanAutoTakeOverManualSynthesis,
                CraftingGatherBridge.HasActiveQueue,
                _currentRecipeId.HasValue,
                _currentState))
        {
            ResetManualSynthesisTakeoverTracking();
            return false;
        }

        if (!Dalamud.Conditions[ConditionFlag.Crafting] || !SynthesisReader.IsSynthesisWindowOpen())
        {
            ResetManualSynthesisTakeoverTracking();
            return false;
        }
        if (Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction])
            return false;

        var activeRecipeId = RecipeNoteExt.GetActiveCraftRecipeId();
        if (!activeRecipeId.HasValue)
            return false;
        if (_manualTakeoverRecipeId != activeRecipeId.Value)
        {
            _manualTakeoverRecipeId = activeRecipeId.Value;
            _manualTakeoverBlockedRecipeId = null;
            _manualTakeoverStatus = string.Empty;
        }
        if (_manualTakeoverBlockedRecipeId == activeRecipeId.Value)
            return false;

        var recipe = RecipeManager.GetRecipe(activeRecipeId.Value);
        if (!recipe.HasValue)
        {
            BlockManualTakeover(activeRecipeId.Value, "the active recipe row is unavailable");
            return false;
        }

        var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
        var requiredJob = (uint)(recipe.Value.CraftType.RowId + 8);
        if (currentJob == 0)
            return false;
        if (!ManualTakeoverMatchesRecipeClass(requiredJob, currentJob))
        {
            BlockManualTakeover(
                activeRecipeId.Value,
                $"active crafter job {currentJob} does not match recipe job {requiredJob}");
            return false;
        }

        var settings = GatherBuddy.RecipeBrowserSettings.Get(activeRecipeId.Value)?.Clone();
        var item = BuildManualTakeoverItem(activeRecipeId.Value, settings);
        var configuredContext = CraftingContextResolver.ResolveExecutionContext(item, recipe.Value, null);
        var liveContext = configuredContext with
        {
            ConsumableSettings = null,
            UseQuickSynthesis = false,
        };
        if (!TryBuildLiveCraft(recipe.Value, liveContext, out var craft, out var buildFailure))
        {
            ReportManualTakeoverStatus(activeRecipeId.Value, buildFailure);
            return false;
        }

        var modeledInitial = GameStateBuilder.BuildInitialStepState(craft, craft.InitialQuality);
        var liveStep = SynthesisReader.ReadCurrentStepState(craft, modeledInitial);
        if (liveStep == null)
            return false;
        var initialRoot = IsInitialManualSynthesisRoot(craft, liveStep);
        if (!initialRoot)
        {
            liveStep = SynthesisReader.ReadCurrentStepState(
                craft,
                BuildConservativeRecoveryPrior(craft, liveContext.DonatelloOptions));
            if (liveStep == null)
                return false;
        }
        else
        {
            craft = craft with { InitialQuality = liveStep.Quality };
        }

        CachedRaphaelSolution? raphaelSolution = null;
        if (initialRoot && CraftingContextResolver.UsesRaphaelSolver(liveContext))
        {
            var request = RaphaelSolveRequest.FromCraftState(
                craft,
                CraftingContextResolver.ResolveSpecialistActionsAllowed(craft));
            if (GatherBuddy.RaphaelSolveCoordinator.HasFailedSolution(request, out var solveFailure))
            {
                BlockManualTakeover(
                    activeRecipeId.Value,
                    $"Raphael baseline failed: {solveFailure ?? "unknown failure"}");
                return false;
            }
            if (!GatherBuddy.RaphaelSolveCoordinator.TryGetSolution(request, out raphaelSolution)
                || raphaelSolution == null)
            {
                if (!GatherBuddy.Config.RaphaelSolverConfig.RaphaelEnabled)
                {
                    BlockManualTakeover(activeRecipeId.Value, "Raphael is disabled, so no safe baseline can be established");
                    return false;
                }
                if (!GatherBuddy.RaphaelSolveCoordinator.IsKnown(request))
                    GatherBuddy.RaphaelSolveCoordinator.EnqueueOrPromoteRequest(request, RaphaelSolvePriority.Urgent);
                ReportManualTakeoverStatus(
                    activeRecipeId.Value,
                    $"waiting for the initial Raphael baseline; solver={liveContext.EffectiveSolverMode}");
                return false;
            }
        }

        SetQualityPolicy(liveContext.QualityPolicy);
        SetSelectedMacro(liveContext.SelectedMacroId);
        SetDonatelloOptions(liveContext.DonatelloOptions);
        ReloadSolversForCraft(
            liveContext.EffectiveSolverMode,
            !liveContext.ForceProgressOnlyUnlockCraft);

        var isTrial = TrialSynthesisUi.IsActive(activeRecipeId.Value);
        var materialMiracleBootstrap = initialRoot
            && ShouldDeferMaterialMiracleBootstrap(
                liveContext.EffectiveSolverMode,
                CraftingProcessor.SolverDefinitions.Any(definition => definition is DonatelloSolverDefinition),
                DonatelloSolverDefinition.IsGuaranteedMaximumQualitySolution(raphaelSolution, craft),
                craft,
                liveStep);
        if (materialMiracleBootstrap)
        {
            GatherBuddy.Log.Information(CraftingProcessorSession.FormatCraftStartLogLine(
                craft,
                liveStep,
                activeRecipeId.Value,
                isTrial,
                "Donatello (Material Miracle bootstrap pending)"));
        }
        else if (initialRoot)
        {
            CraftingProcessor.OnCraftStarted(
                craft,
                liveStep,
                activeRecipeId.Value,
                isTrial,
                liveContext.EffectiveSolverMode == VulcanSolverMode.Gabriel
                    ? typeof(GabrielSolverDefinition)
                    : null);
            if (!CraftingProcessor.IsActive)
            {
                var failure = string.IsNullOrWhiteSpace(CraftingProcessor.FaultReason)
                    ? "the configured solver could not start"
                    : CraftingProcessor.FaultReason;
                CraftingProcessor.OnCraftFinished(craft, liveStep, activeRecipeId.Value, cancelled: true);
                RestoreGlobalSolverSelectionAfterManualRefusal();
                BlockManualTakeover(activeRecipeId.Value, failure);
                return false;
            }
        }
        else if (!CraftingProcessor.TryAdoptLiveCraft(
                     craft,
                     liveStep,
                     liveContext.EffectiveSolverMode is VulcanSolverMode.Donatello or VulcanSolverMode.Gabriel
                         ? liveContext.EffectiveSolverMode
                         : null,
                     out var recoveryFailure))
        {
            RestoreGlobalSolverSelectionAfterManualRefusal();
            BlockManualTakeover(activeRecipeId.Value, $"mid-craft recovery is unsafe: {recoveryFailure}");
            return false;
        }
        else
        {
            GatherBuddy.Log.Information(CraftingProcessorSession.FormatCraftStartLogLine(
                craft,
                liveStep,
                activeRecipeId.Value,
                isTrial,
                $"{CraftingProcessor.ActiveSolverName} (manual mid-craft recovery)"));
        }

        CommitManualSynthesisTakeover(
            recipe.Value,
            craft,
            liveStep,
            isTrial,
            materialMiracleBootstrap,
            currentJob,
            settings != null && settings.HasAnySettings());
        return true;
    }

    private static void CommitManualSynthesisTakeover(
        Recipe recipe,
        Vulcan.CraftState craft,
        StepState liveStep,
        bool isTrial,
        bool materialMiracleBootstrap,
        uint currentJob,
        bool hasRecipeOverrides)
    {
        _currentRecipe = recipe;
        _currentRecipeId = recipe.RowId;
        _currentCraftIsTrial = isTrial;
        _vulcanCraftState = craft;
        _vulcanStepState = liveStep;
        _vulcanPredictionPendingObservation = false;
        _vulcanSolverStartDeferredForMaterialMiracle = materialMiracleBootstrap;
        _executedActions.Clear();
        ResetReconciliationTracking();
        _automationFaultReported = false;
        BeginActionDelay(liveStep, restart: true);
        _currentState = CraftState.InProgress;
        CraftStarted?.Invoke(recipe, recipe.RowId);
        StateChanged?.Invoke(_currentState);
        GatherBuddy.Log.Information(
            $"[Crafting] Automatically took over manually started synthesis: recipe={recipe.RowId}, "
            + $"classJob={currentJob}, trial={isTrial}, solver={_currentSolverMode}, "
            + $"recipeOverrides={hasRecipeOverrides}, initialRoot={IsInitialManualSynthesisRoot(craft, liveStep)}, state={liveStep}");
        _manualTakeoverStatus = string.Empty;
    }

    internal static CraftingListItem BuildManualTakeoverItem(uint recipeId, RecipeCraftSettings? settings)
        => new(recipeId, 1)
        {
            IsOriginalRecipe = true,
            CraftSettings = settings?.Clone(),
        };

    internal static bool CanClaimManualSynthesis(
        bool enabled,
        bool hasActiveQueue,
        bool hasOwnedRecipe,
        CraftState state)
        => enabled
            && !hasActiveQueue
            && !hasOwnedRecipe
            && state is CraftState.IdleNormal or CraftState.WaitStart;

    internal static bool ManualTakeoverMatchesRecipeClass(uint requiredJob, uint currentJob)
        => requiredJob is >= 8 and <= 15 && currentJob == requiredJob;

    internal static bool IsInitialManualSynthesisRoot(Vulcan.CraftState craft, StepState step)
        => step.Index == 1
            && step.Progress == 0
            && step.Durability == craft.CraftDurability
            && step.RemainingCP == craft.StatCP
            && step.Condition == Vulcan.Condition.Normal
            && step.IQStacks == 0
            && step.WasteNotLeft == 0
            && step.ManipulationLeft == 0
            && step.GreatStridesLeft == 0
            && step.InnovationLeft == 0
            && step.VenerationLeft == 0
            && step.MuscleMemoryLeft == 0
            && step.FinalAppraisalLeft == 0
            && !step.HeartAndSoulActive
            && !step.TrainedPerfectionActive
            && step.StellarSteadyHandLeft == 0
            && step.ComboAction == VulcanSkill.None
            && step.PrevComboAction == VulcanSkill.None
            && !step.PrevActionFailed;

    private static StepState BuildConservativeRecoveryPrior(
        Vulcan.CraftState craft,
        DonatelloExecutionOptions? options)
        => GameStateBuilder.BuildInitialStepState(craft) with
        {
            CarefulObservationLeft = 0,
            HeartAndSoulAvailable = false,
            QuickInnoLeft = 0,
            QuickInnoAvailable = false,
            TrainedPerfectionAvailable = false,
            PrevComboAction = VulcanSkill.None,
            PrevActionFailed = false,
            StellarSteadyHandsUsed = ConservativeRecoveredStellarSteadyHandsUsed(options),
        };

    private static void RestoreGlobalSolverSelectionAfterManualRefusal()
    {
        SetQualityPolicy(null);
        SetSelectedMacro(null);
        SetDonatelloOptions(null);
        ReloadSolvers();
    }

    private static void ReportManualTakeoverStatus(uint recipeId, string status)
    {
        var key = $"{recipeId}:{status}";
        if (_manualTakeoverStatus == key)
            return;
        _manualTakeoverStatus = key;
        GatherBuddy.Log.Information($"[Crafting] Manual synthesis takeover pending: recipe={recipeId}, {status}");
    }

    private static void BlockManualTakeover(uint recipeId, string reason)
    {
        _manualTakeoverBlockedRecipeId = recipeId;
        _manualTakeoverStatus = string.Empty;
        GatherBuddy.Log.Warning($"[Crafting] Manual synthesis takeover refused: recipe={recipeId}, {reason}");
    }

    private static void ResetManualSynthesisTakeoverTracking()
    {
        _manualTakeoverRecipeId = null;
        _manualTakeoverBlockedRecipeId = null;
        _manualTakeoverStatus = string.Empty;
    }
}
