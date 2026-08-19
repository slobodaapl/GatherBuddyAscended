using System;
using System.Collections.Generic;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private static unsafe void SwitchJobIfNeeded(uint requiredJobId)
    {
        var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
        if (currentJob == requiredJobId)
            return;

        try
        {
            var gearsetModule = RaptureGearsetModule.Instance();
            if (gearsetModule == null)
            {
                GatherBuddy.Log.Error("[VulcanWindow] Failed to get gearset module");
                return;
            }

            if (GearsetStatsReader.TryResolveExistingGearsetIndex(gearsetModule, requiredJobId, out var gearsetIndex))
            {
                gearsetModule->EquipGearset(gearsetIndex);
                GatherBuddy.Log.Information($"[VulcanWindow] Switched to gearset {gearsetIndex} for job {requiredJobId}");
                return;
            }

            GatherBuddy.Log.Warning($"[VulcanWindow] No gearset found for job {requiredJobId}");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[VulcanWindow] Failed to switch job: {ex.Message}");
        }
    }

    private static void StartCraftWithRaphael(Recipe recipe, bool trialSynthesis = false)
    {
        var requiredJob = (uint)(recipe.CraftType.RowId + 8);
        var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
        
        if (currentJob != requiredJob)
        {
            GatherBuddy.Log.Information($"[VulcanWindow] Job switch needed: {currentJob} -> {requiredJob}");
            SwitchJobIfNeeded(requiredJob);
            
            var tm = GatherBuddy.AutoGather?.TaskManager;
            if (tm != null)
            {
                tm.DelayNext(3000);
                tm.Enqueue(() =>
                {
                    StartCraftWithRaphaelAfterJobSwitch(recipe, trialSynthesis);
                    return true;
                }, trialSynthesis ? "StartTrialSynthesisAfterJobSwitch" : "StartCraftAfterJobSwitch");
            }
            return;
        }
        
        StartCraftWithRaphaelAfterJobSwitch(recipe, trialSynthesis);
    }
    
    private static void StartCraftWithRaphaelAfterJobSwitch(Recipe recipe, bool trialSynthesis)
    {
        if (trialSynthesis && CraftingGameInterop.GetTrialSynthesisStartBlockReason() is { } blockReason)
        {
            GatherBuddy.Log.Warning($"[VulcanWindow] Trial Synthesis not started: {blockReason}");
            return;
        }

        var settings = GatherBuddy.RecipeBrowserSettings.Get(recipe.RowId);
        var item = new CraftingListItem(recipe.RowId, 1)
        {
            IsOriginalRecipe = true,
            CraftSettings = settings?.Clone(),
        };
        var executionContext = CraftingContextResolver.ResolveExecutionContext(item, recipe, null);
        if (settings != null && settings.HasAnySettings())
        {
            GatherBuddy.Log.Debug($"[VulcanWindow] Applying recipe browser settings for {recipe.RowId}");
            if (settings.FoodItemId.HasValue)
                GatherBuddy.Log.Debug($"  Food: {settings.FoodItemId.Value}");
            if (settings.MedicineItemId.HasValue)
                GatherBuddy.Log.Debug($"  Medicine: {settings.MedicineItemId.Value}");
            if (settings.ManualItemId.HasValue)
                GatherBuddy.Log.Debug($"  Manual: {settings.ManualItemId.Value}");
            if (settings.SquadronManualItemId.HasValue)
                GatherBuddy.Log.Debug($"  Squadron Manual: {settings.SquadronManualItemId.Value}");
            GatherBuddy.Log.Debug($"  Ingredient prefs: {settings.IngredientPreferences.Count} items, UseAllNQ={settings.UseAllNQ}");
        }

        if (executionContext.ConsumableSettings is { } consumableSettings)
        {
            var allApplied = ConsumableChecker.ApplyConsumables(consumableSettings);
            if (!allApplied)
            {
                GatherBuddy.Log.Debug($"[VulcanWindow] Consumables applied, waiting 3 seconds before starting synthesis");
                var taskMgr = GatherBuddy.AutoGather?.TaskManager;
                if (taskMgr != null)
                {
                    taskMgr.DelayNext(3000);
                }
            }
        }

        var selectedMacroId = executionContext.SelectedMacroId;
        if (!string.IsNullOrEmpty(selectedMacroId))
            GatherBuddy.Log.Information($"[VulcanWindow] Using macro: {selectedMacroId}");

        if (!CraftingContextResolver.UsesRaphaelSolver(executionContext))
        {
            StartPreparedSynthesis(recipe, executionContext, trialSynthesis);
            return;
        }
        var requiredJob = (uint)(recipe.CraftType.RowId + 8);

        var simulationIntent = trialSynthesis
            ? CraftingSimulationIntent.TrialExecution
            : CraftingSimulationIntent.Execution;
        if (!CraftingContextResolver.TryBuildSimulationContext(
                recipe,
                executionContext,
                CraftingStatsSource.AlwaysGearsetStats,
                simulationIntent,
                out var simulationContext))
        {
            GatherBuddy.Log.Warning($"[VulcanWindow] Could not read gearset stats for job {requiredJob}, starting without Raphael");
            StartPreparedSynthesis(recipe, executionContext, trialSynthesis);
            return;
        }
        var request = simulationContext.RaphaelRequest;

        if (GatherBuddy.RaphaelSolveCoordinator.TryGetSolution(request, out var solution) && solution != null && !solution.IsFailed)
        {
            GatherBuddy.Log.Debug($"[VulcanWindow] Raphael solution already cached for recipe {recipe.RowId}, starting synthesis");
            StartPreparedSynthesis(recipe, executionContext, trialSynthesis);
            return;
        }

        GatherBuddy.Log.Information($"[VulcanWindow] Enqueuing Raphael solve for recipe {recipe.RowId}");
        GatherBuddy.RaphaelSolveCoordinator.EnqueueOrPromoteRequest(request, RaphaelSolvePriority.Urgent);

        var tm = GatherBuddy.AutoGather?.TaskManager;
        if (tm == null)
        {
            GatherBuddy.Log.Error($"[VulcanWindow] TaskManager unavailable, cannot wait for Raphael solve");
            return;
        }

        tm.Enqueue(() => WaitForRaphaelSolution(request), 60000,
            trialSynthesis ? "WaitForTrialRaphaelSolution" : "WaitForRaphaelSolution");
        tm.Enqueue(() =>
        {
            GatherBuddy.Log.Information($"[VulcanWindow] Raphael solution ready, starting synthesis for recipe {recipe.RowId}");
            StartPreparedSynthesis(recipe, executionContext, trialSynthesis);
            return true;
        }, trialSynthesis ? "StartTrialSynthesisAfterRaphael" : "StartCraftAfterRaphael");
    }

    private static void StartPreparedSynthesis(
        Recipe recipe,
        CraftingExecutionContext executionContext,
        bool trialSynthesis)
    {
        if (trialSynthesis && CraftingGameInterop.GetTrialSynthesisStartBlockReason() is { } blockReason)
        {
            GatherBuddy.Log.Warning($"[VulcanWindow] Trial Synthesis not started: {blockReason}");
            return;
        }

        CraftingGameInterop.SetQualityPolicy(executionContext.QualityPolicy);
        CraftingGameInterop.SetSelectedMacro(executionContext.SelectedMacroId);
        CraftingGameInterop.SetDonatelloOptions(executionContext.DonatelloOptions);
        CraftingGameInterop.ReloadSolversForCraft(
            executionContext.EffectiveSolverMode,
            !executionContext.ForceProgressOnlyUnlockCraft);
        if (trialSynthesis)
            CraftingGameInterop.StartTrialSynthesis(recipe);
        else
            CraftingGameInterop.StartCraft(recipe, 1);
    }

    private static bool WaitForRaphaelSolution(RaphaelSolveRequest request)
    {
        if (GatherBuddy.RaphaelSolveCoordinator.TryGetSolution(request, out var solution))
        {
            if (solution != null && !solution.IsFailed)
            {
                GatherBuddy.Log.Debug($"[VulcanWindow] Raphael solution ready for recipe {request.RecipeId}");
                return true;
            }
            else if (solution != null && solution.IsFailed)
            {
                GatherBuddy.Log.Warning($"[VulcanWindow] Raphael solution failed for recipe {request.RecipeId}: {solution.FailureReason}");
                return true;
            }
        }

        return false;
    }

    private static void StartBrowserQuickSynth(Recipe recipe, int quantity)
    {
        var settings = GatherBuddy.RecipeBrowserSettings.Get(recipe.RowId);
        var retainerRestock = _browserRetainerRestock && AllaganTools.Enabled;
        var executionPlan = CreateBrowserExecutionPlan(recipe, quantity, settings, true, retainerRestock);
        GatherBuddy.Log.Information($"[VulcanWindow] Browser quick synth: {recipe.ItemResult.Value.Name.ExtractText()} x{quantity}");
        CraftingGatherBridge.StartQueueCraftAndGather(executionPlan);
    }

    private static void StartBrowserTrialSynthesis(Recipe recipe)
        => StartCraftWithRaphael(recipe, trialSynthesis: true);

    private static void StartBrowserCraft(Recipe recipe, int quantity)
    {
        var settings = GatherBuddy.RecipeBrowserSettings.Get(recipe.RowId);
        RecipeCraftSettings? craftSettings = null;
        if (settings != null && settings.HasAnySettings())
        {
            craftSettings = new RecipeCraftSettings
            {
                FoodMode = settings.FoodMode,
                FoodItemId = settings.FoodItemId,
                FoodHQ = settings.FoodHQ,
                MedicineMode = settings.MedicineMode,
                MedicineItemId = settings.MedicineItemId,
                MedicineHQ = settings.MedicineHQ,
                ManualMode = settings.ManualMode,
                ManualItemId = settings.ManualItemId,
                SquadronManualMode = settings.SquadronManualMode,
                SquadronManualItemId = settings.SquadronManualItemId,
                UseAllNQ = settings.UseAllNQ,
                SelectedMacroId = settings.SelectedMacroId,
                MacroMode = settings.MacroMode,
                SolverOverride = settings.SolverOverride,
                MaximizeQualityAtCostOfTime = settings.MaximizeQualityAtCostOfTime,
                DonatelloImprovementQuietSecondsOverride = settings.DonatelloImprovementQuietSecondsOverride,
                SpecialistActionOverride = settings.SpecialistActionOverride,
                IngredientPreferences = new Dictionary<uint, int>(settings.IngredientPreferences),
            };
        }

        var retainerRestock = _browserRetainerRestock && AllaganTools.Enabled;
        var executionPlan = CreateBrowserExecutionPlan(recipe, quantity, craftSettings, false, retainerRestock);

        GatherBuddy.Log.Information($"[VulcanWindow] Browser craft: {recipe.ItemResult.Value.Name.ExtractText()} x{quantity}");
        CraftingGatherBridge.StartQueueCraftAndGather(executionPlan);
    }

    private static CraftingExecutionPlan CreateBrowserExecutionPlan(Recipe recipe, int quantity, RecipeCraftSettings? craftSettings, bool nqOnly, bool retainerRestock)
    {
        var list = new CraftingListDefinition
        {
            ID = -1,
            Name = recipe.ItemResult.Value.Name.ExtractText(),
            SkipIfEnough = true,
            SkipFinalIfEnough = _browserSkipFinalIfEnough,
            QuickSynthAll = _browserQuickSynthAll,
            QuickSynthAllPreferNQ = _browserQuickSynthAllPreferNQ,
            QuickSynthAllPrecraftsOnly = _browserQuickSynthAllPrecraftsOnly,
            RetainerRestock = retainerRestock,
        };

        list.Recipes.Add(new CraftingListItem(recipe.RowId, quantity)
        {
            IsOriginalRecipe = true,
            CraftSettings = craftSettings?.Clone(),
            Options = new ListItemOptions
            {
                NQOnly = nqOnly,
            },
        });

        var executionPlan = CraftingExecutionPlan.Create(list);
        GatherBuddy.Log.Debug(
            $"[VulcanWindow] Browser execution plan for recipe {recipe.RowId}: quantity={quantity}, nqOnly={nqOnly}, queue={executionPlan.QueueView.Count}, materials={executionPlan.MaterialsView.Count}, precrafts={executionPlan.PrecraftsView.Count}, skipIfEnough={executionPlan.SkipIfEnough}, skipFinalIfEnough={executionPlan.SkipFinalIfEnough}, quickSynthAll={_browserQuickSynthAll}, quickSynthPrecraftsOnly={_browserQuickSynthAllPrecraftsOnly}, quickSynthPreferNQ={_browserQuickSynthAllPreferNQ}");
        return executionPlan;
    }

}
