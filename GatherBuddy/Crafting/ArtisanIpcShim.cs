using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using GatherBuddy.Vulcan;

namespace GatherBuddy.Crafting;

/// <summary>
/// ICE-facing Artisan ABI compatibility provider. The provider exists only
/// while the real Artisan plugin is unloaded; all solver selections are
/// translated to Donatello execution options.
/// </summary>
public sealed class ArtisanIpcShim : IDisposable
{
    public const string ArtisanInternalName = "Artisan";
    private const string ProgressOnlySolverName = "Progress Only Solver";
    private const string StandardSolverName = "Standard Recipe Solver";
    private const string RaphaelSolverName = "Raphael Recipe Solver";
    private const string ExpertSolverName = "Expert Recipe Solver";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Dictionary<uint, RecipeOverrides> _recipeOverrides = new();
    private bool _registered;
    private bool _providerYieldedForEnable;
    private bool _stopRequested;
    private bool _disposed;
    private bool _recoveringActiveSynthesis;
    private ushort _recoveryRecipeId;
    private (ushort RecipeId, int Amount)? _deferredCraft;
    private ushort _restoredQueueRecipeId;
    private int _restoredQueueRecipeCount;

    private sealed class RecipeOverrides
    {
        public string? SolverName;
        public ConsumableSelection? Food;
        public ConsumableSelection? Potion;
        public uint? Manual;
        public uint? SquadronManual;
        public uint? ExpertProfileId;
        public uint? ExpertMaxSteadyUses;
    }

    private readonly record struct ConsumableSelection(uint ItemId, bool HighQuality);

    public ArtisanIpcShim(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        ReconcileProviderState();
    }

    public bool IsActive => _registered && !IsRealArtisanLoaded();
    public bool IsBusy => IsShimBusy();

    public void Update()
    {
        ReconcileProviderState();
        if (!_recoveringActiveSynthesis
            || SynthesisReader.IsSynthesisWindowOpen()
            || CraftingGatherBridge.HasActiveQueue)
            return;

        _recoveringActiveSynthesis = false;
        _restoredQueueRecipeId = 0;
        _restoredQueueRecipeCount = 0;
        if (_deferredCraft is not { } deferred)
            return;

        _deferredCraft = null;
        try
        {
            CraftItemCore(deferred.RecipeId, deferred.Amount);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error(
                $"[ArtisanIpcShim] Deferred ICE craft failed after live-synthesis recovery: recipe={deferred.RecipeId}, amount={deferred.Amount}: {ex}");
        }
    }

    public bool TryPrepareArtisanToggle(bool enable, out string? blockedReason)
    {
        blockedReason = null;
        if (enable)
        {
            if (IsShimBusy())
            {
                blockedReason = "Cannot enable Artisan while the compatibility shim is crafting.";
                return false;
            }

            _providerYieldedForEnable = true;
            if (_registered)
                UnregisterProviders();
            return true;
        }

        if (!IsRealArtisanLoaded())
            return true;

        try
        {
            if (_pluginInterface.GetIpcSubscriber<bool>("Artisan.IsBusy").InvokeFunc())
            {
                blockedReason = "Cannot disable Artisan while Artisan is crafting.";
                return false;
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[ArtisanIpcShim] Could not query real Artisan busy state: {ex}");
            blockedReason = "Cannot disable Artisan because its busy state could not be verified.";
            return false;
        }

        return true;
    }

    public void RestoreAfterFailedArtisanEnable()
    {
        _providerYieldedForEnable = false;
        ReconcileProviderState();
    }

    public void ReconcileProviderState()
    {
        if (_disposed)
            return;

        if (IsRealArtisanLoaded())
        {
            // Real Artisan registered after us and owns the shared delegates.
            // Unregistering here would remove its providers.
            _registered = false;
            _providerYieldedForEnable = false;
            return;
        }

        if (!_providerYieldedForEnable && !_registered)
            RegisterProviders();
    }

    internal static DonatelloSolveObjective ResolveObjective(string? solverName)
        => string.Equals(solverName, ProgressOnlySolverName, StringComparison.Ordinal)
            ? DonatelloSolveObjective.ProgressOnly
            : DonatelloSolveObjective.MaximizeQuality;

    internal static SolverOverrideMode ResolveSolverOverride(string? solverName)
        => solverName switch
        {
            null or "" => SolverOverrideMode.DonatelloSolver,
            ProgressOnlySolverName => SolverOverrideMode.ProgressOnlySolver,
            StandardSolverName => SolverOverrideMode.StandardSolver,
            RaphaelSolverName => SolverOverrideMode.RaphaelSolver,
            ExpertSolverName => SolverOverrideMode.DonatelloSolver,
            _ when solverName.StartsWith("Macro: ", StringComparison.Ordinal) => SolverOverrideMode.DonatelloSolver,
            _ => throw new NotSupportedException($"Artisan solver '{solverName}' is not supported by the GatherBuddy compatibility shim."),
        };

    internal static DonatelloExecutionOptions BuildDonatelloOptions(
        string? solverName,
        bool isExpert,
        uint? expertMaxSteadyUses,
        uint? expertMaxMaterialMiracleUses,
        uint? expertMinimumStepsBeforeMiracle,
        uint? standardMaxMaterialMiracleUses,
        uint? standardMinimumStepsBeforeMiracle)
    {
        _ = expertMaxMaterialMiracleUses;
        _ = expertMinimumStepsBeforeMiracle;
        _ = standardMaxMaterialMiracleUses;
        _ = standardMinimumStepsBeforeMiracle;
        return new(
            ResolveObjective(solverName),
            MinimizeSteps: false,
            MaxStellarSteadyHandUses: isExpert ? expertMaxSteadyUses ?? 0 : 0);
    }

    internal RecipeCraftSettings BuildCraftSettings(uint recipeId, bool isExpert)
    {
        var settings = GatherBuddy.RecipeBrowserSettings.Get(recipeId)?.Clone() ?? new RecipeCraftSettings();
        var overrides = _recipeOverrides.GetValueOrDefault(recipeId);
        if (overrides?.Food is { } food)
        {
            settings.FoodMode = food.ItemId == 0 ? ConsumableOverrideMode.None : ConsumableOverrideMode.Specific;
            settings.FoodItemId = food.ItemId == 0 ? null : food.ItemId;
            settings.FoodHQ = food.HighQuality;
        }
        if (overrides?.Potion is { } potion)
        {
            settings.MedicineMode = potion.ItemId == 0 ? ConsumableOverrideMode.None : ConsumableOverrideMode.Specific;
            settings.MedicineItemId = potion.ItemId == 0 ? null : potion.ItemId;
            settings.MedicineHQ = potion.HighQuality;
        }
        if (overrides?.Manual is { } manual)
        {
            settings.ManualMode = manual == 0 ? ConsumableOverrideMode.None : ConsumableOverrideMode.Specific;
            settings.ManualItemId = manual == 0 ? null : manual;
        }
        if (overrides?.SquadronManual is { } squadronManual)
        {
            settings.SquadronManualMode = squadronManual == 0 ? ConsumableOverrideMode.None : ConsumableOverrideMode.Specific;
            settings.SquadronManualItemId = squadronManual == 0 ? null : squadronManual;
        }

        var objective = ResolveObjective(overrides?.SolverName);
        settings.IngredientPreferences.Clear();
        if (objective == DonatelloSolveObjective.ProgressOnly)
        {
            settings.UseAllNQ = true;
        }
        else
        {
            settings.UseAllNQ = false;
        }
        var solverName = overrides?.SolverName;
        settings.SelectedMacroId = null;
        settings.MacroMode = MacroOverrideMode.Specific;
        settings.SolverOverride = ResolveSolverOverride(solverName);
        if (solverName?.StartsWith("Macro: ", StringComparison.Ordinal) == true)
        {
            var macroName = solverName["Macro: ".Length..];
            var macro = CraftingGameInterop.UserMacroLibrary.GetAllMacros()
                .FirstOrDefault(candidate => string.Equals(candidate.Name, macroName, StringComparison.Ordinal));
            if (macro == null)
                throw new NotSupportedException($"Artisan requested macro '{macroName}', but no GatherBuddy macro has that name.");
            settings.SelectedMacroId = macro.Id;
        }
        settings.DonatelloOptions = BuildDonatelloOptions(
            overrides?.SolverName,
            isExpert,
            overrides?.ExpertMaxSteadyUses,
            null,
            null,
            null,
            null);
        return settings;
    }

    private bool IsRealArtisanLoaded()
        => _pluginInterface.InstalledPlugins.Any(plugin =>
            plugin.IsLoaded
            && string.Equals(plugin.InternalName, ArtisanInternalName, StringComparison.Ordinal));

    private static bool IsShimBusy()
        => CraftingGatherBridge.HasActiveQueue
            || CraftingGameInterop.CurrentState is not (CraftingGameInterop.CraftState.IdleNormal
                or CraftingGameInterop.CraftState.IdleBetween);

    private void CraftItem(ushort recipeId, int amount)
    {
        GatherBuddy.Log.Information($"[ArtisanIpcShim] Received ICE craft request: recipe={recipeId}, amount={amount}");
        if (CraftingGatherBridge.TryGetPersistedArtisanRecovery(
                recipeId,
                out var activeRecipeId,
                out var alreadyQueued))
        {
            _recoveringActiveSynthesis = true;
            _recoveryRecipeId = activeRecipeId;
            _restoredQueueRecipeId = recipeId;
            _restoredQueueRecipeCount = alreadyQueued;
            DeferCraftAfterRecovery(recipeId, amount);
            GatherBuddy.Log.Information(
                $"[ArtisanIpcShim] Deduplicated ICE request against restored queue: recipe={recipeId}, requested={amount}, alreadyQueued={alreadyQueued}");
            return;
        }
        if (_recoveringActiveSynthesis)
        {
            DeferCraftAfterRecovery(recipeId, amount);
            return;
        }
        if (SynthesisReader.IsSynthesisWindowOpen()
            && !CraftingGatherBridge.HasActiveQueue
            && TryRecoverActiveSynthesis(recipeId, amount))
            return;

        try
        {
            CraftItemCore(recipeId, amount);
        }
        catch (Exception ex)
        {
            var reason = ex.Message.Replace('\n', ' ');
            GatherBuddy.Log.Error($"[ArtisanIpcShim] Rejected ICE craft: recipe={recipeId}, amount={amount}: {ex}");
            Dalamud.Chat.PrintError($"[GatherBuddy Ascended] ICE craft could not start: {reason}");
            throw;
        }
    }

    private void CraftItemCore(ushort recipeId, int amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Craft amount must be positive.");
        if (IsShimBusy())
            throw new InvalidOperationException("The Artisan compatibility shim is already crafting.");

        var recipe = RecipeManager.GetRecipe(recipeId)
            ?? throw new InvalidOperationException($"Recipe {recipeId} was not found.");
        var list = new CraftingListDefinition
        {
            ID = int.MinValue,
            Name = "Artisan IPC craft",
            SkipIfEnough = false,
            SkipFinalIfEnough = false,
            RetainerRestock = false,
            AutoPurchaseBlockedDependencies = false,
            ReturnToHomeWorldBeforeCrafting = false,
        };
        list.Recipes.Add(new CraftingListItem(recipeId, amount)
        {
            IsOriginalRecipe = true,
            CraftSettings = BuildCraftSettings(recipeId, recipe.IsExpert),
        });

        _stopRequested = false;
        var plan = CraftingExecutionPlan.CreateDirect(list);
        if (!CraftingQueuePreflight.TryValidate(plan, out var failure))
            throw new InvalidOperationException(failure.Replace('\n', ' '));
        CraftingGatherBridge.StartQueueCraftAndGather(
            plan,
            owner: CraftingAutomationOwner.ArtisanIpc);
        if (CraftingGatherBridge.TryGetActiveQueueFailure(out failure))
            throw new InvalidOperationException(failure.Replace('\n', ' '));
        GatherBuddy.Log.Information(
            $"[ArtisanIpcShim] Accepted ICE craft: recipe={recipeId}, amount={amount}, objective={list.Recipes[0].CraftSettings?.DonatelloOptions?.Objective}");
    }

    private bool TryRecoverActiveSynthesis(ushort requestedRecipeId, int requestedAmount)
    {
        var activeRecipeId = RecipeNoteExt.GetActiveCraftRecipeId();
        if (activeRecipeId is not { } selectedId || selectedId > ushort.MaxValue)
            return false;
        var recipe = RecipeManager.GetRecipe(selectedId);
        if (!recipe.HasValue)
            return false;

        var recoveryItem = new CraftingListItem(selectedId, 1)
        {
            IsOriginalRecipe = true,
            CraftSettings = BuildCraftSettings(selectedId, recipe.Value.IsExpert),
        };

        var recoveryList = new CraftingListDefinition
        {
            ID = int.MinValue,
            Name = "Recovered Artisan IPC craft",
            SkipIfEnough = false,
            SkipFinalIfEnough = false,
            RetainerRestock = false,
            AutoPurchaseBlockedDependencies = false,
            ReturnToHomeWorldBeforeCrafting = false,
        };
        recoveryList.Recipes.Add(recoveryItem);
        var recoveryPlan = CraftingExecutionPlan.CreateDirect(recoveryList);
        var failureReason = string.Empty;
        if (!CraftingQueuePreflight.TryValidate(recoveryPlan, out failureReason))
        {
            GatherBuddy.Log.Warning(
                $"[ArtisanIpcShim] Active synthesis recipe {selectedId} failed recovery preflight: {failureReason.Replace('\n', ' ')}");
            return false;
        }

        CraftingGatherBridge.StartQueueCraftAndGather(
            recoveryPlan,
            owner: CraftingAutomationOwner.ArtisanIpc,
            restoringPersistedCraft: true);
        if (!CraftingGatherBridge.HasActiveQueue
            || CraftingGatherBridge.TryGetActiveQueueFailure(out failureReason))
        {
            GatherBuddy.Log.Warning(
                $"[ArtisanIpcShim] Active synthesis recipe {selectedId} failed recovery startup: {failureReason.Replace('\n', ' ')}");
            return false;
        }

        _recoveringActiveSynthesis = true;
        _recoveryRecipeId = (ushort)selectedId;
        _restoredQueueRecipeId = 0;
        _restoredQueueRecipeCount = 0;
        DeferCraftAfterRecovery(requestedRecipeId, requestedAmount);
        GatherBuddy.Log.Information(
            $"[ArtisanIpcShim] Recovering active synthesis recipe {selectedId}; deferred ICE request recipe={requestedRecipeId}, amount={requestedAmount}");
        return true;
    }

    private void DeferCraftAfterRecovery(ushort recipeId, int amount)
    {
        var alreadyQueued = recipeId == _restoredQueueRecipeId ? _restoredQueueRecipeCount : 0;
        var remaining = RemainingRequestedAfterRecovery(
            _recoveryRecipeId,
            recipeId,
            amount,
            alreadyQueued);
        if (remaining <= 0)
            return;
        if (_deferredCraft is { } existing && existing.RecipeId != recipeId)
        {
            GatherBuddy.Log.Warning(
                $"[ArtisanIpcShim] Ignoring additional ICE request recipe={recipeId}, amount={amount} while recipe={existing.RecipeId}, amount={existing.Amount} is already deferred");
            return;
        }

        _deferredCraft = (recipeId, Math.Max(_deferredCraft?.Amount ?? 0, remaining));
    }

    internal static int RemainingRequestedAfterRecovery(
        ushort activeRecipeId,
        ushort requestedRecipeId,
        int requestedAmount,
        int alreadyQueued = 0)
        => Math.Max(
            0,
            requestedAmount - Math.Max(
                activeRecipeId == requestedRecipeId ? 1 : 0,
                alreadyQueued));

    private void AssignRecipe(ushort recipeId, uint _, uint __, uint ___, uint ____)
        => GatherBuddy.Log.Debug($"[ArtisanIpcShim] Ignoring legacy AssignRecipie for recipe {recipeId}; CraftItem owns exact ingredient assignment.");

    private bool GetEnduranceStatus()
        => CraftingGatherBridge.HasActiveQueue && !CraftingGatherBridge.IsQueuePaused;

    private void SetEnduranceStatus(bool enabled)
    {
        if (!enabled)
            CraftingGatherBridge.StopQueue();
        else if (CraftingGatherBridge.IsQueuePaused)
            CraftingGatherBridge.ResumeQueue();
    }

    private bool IsListRunning()
        => CraftingGatherBridge.HasActiveQueue;

    private bool IsListPaused()
        => CraftingGatherBridge.IsQueuePaused;

    private void SetListPause(bool paused)
    {
        if (paused)
            CraftingGatherBridge.PauseQueue("Paused through Artisan IPC.");
        else
            CraftingGatherBridge.ResumeQueue();
    }

    private bool GetStopRequest()
        => _stopRequested;

    private void SetStopRequest(bool stop)
    {
        _stopRequested = stop;
        if (stop)
            CraftingGatherBridge.StopQueue();
    }

    private RecipeOverrides Overrides(uint recipeId)
    {
        if (!_recipeOverrides.TryGetValue(recipeId, out var overrides))
        {
            overrides = new RecipeOverrides();
            _recipeOverrides[recipeId] = overrides;
        }
        return overrides;
    }

    private void ChangeSolver(uint recipeId, string solverName, bool temporary)
    {
        _ = temporary;
        _ = ResolveSolverOverride(solverName);
        Overrides(recipeId).SolverName = solverName;
    }

    private void ResetSolver(uint recipeId)
        => Overrides(recipeId).SolverName = null;

    private void ChangeFood(uint recipeId, uint itemId, bool highQuality, bool temporary)
    {
        _ = temporary;
        Overrides(recipeId).Food = new ConsumableSelection(itemId, highQuality);
    }

    private void ResetFood(uint recipeId)
        => Overrides(recipeId).Food = null;

    private void ChangePotion(uint recipeId, uint itemId, bool highQuality, bool temporary)
    {
        _ = temporary;
        Overrides(recipeId).Potion = new ConsumableSelection(itemId, highQuality);
    }

    private void ResetPotion(uint recipeId)
        => Overrides(recipeId).Potion = null;

    private void ChangeManual(uint recipeId, uint itemId, bool temporary)
    {
        _ = temporary;
        Overrides(recipeId).Manual = itemId;
    }

    private void ResetManual(uint recipeId)
        => Overrides(recipeId).Manual = null;

    private void ChangeSquadronManual(uint recipeId, uint itemId, bool temporary)
    {
        _ = temporary;
        Overrides(recipeId).SquadronManual = itemId;
    }

    private void ResetSquadronManual(uint recipeId)
        => Overrides(recipeId).SquadronManual = null;

    private void ChangeExpertProfileId(uint recipeId, uint profileId, bool temporary)
    {
        _ = temporary;
        if (profileId != 0)
            throw new NotSupportedException("Artisan expert profile IDs are not supported by the GatherBuddy compatibility shim.");
        Overrides(recipeId).ExpertProfileId = profileId;
    }

    private void ResetExpertProfileId(uint recipeId)
        => Overrides(recipeId).ExpertProfileId = null;

    private void ChangeExpertMaxSteadyUses(uint recipeId, uint uses, bool temporary)
    {
        _ = temporary;
        Overrides(recipeId).ExpertMaxSteadyUses = uses;
    }

    private void ResetExpertMaxSteadyUses(uint recipeId)
        => Overrides(recipeId).ExpertMaxSteadyUses = null;

    private void ChangeExpertMaxMaterialMiracleUses(uint recipeId, uint uses, bool temporary)
    {
        throw new NotSupportedException("Artisan expert Material Miracle limits are not supported by the GatherBuddy compatibility shim.");
    }

    private void ResetExpertMaxMaterialMiracleUses(uint recipeId)
        => _ = recipeId;

    private void ChangeExpertMinimumStepsBeforeMiracle(uint recipeId, uint steps, bool temporary)
    {
        throw new NotSupportedException("Artisan expert Material Miracle step thresholds are not supported by the GatherBuddy compatibility shim.");
    }

    private void ResetExpertMinimumStepsBeforeMiracle(uint recipeId)
        => _ = recipeId;

    private void ChangeStandardMaxMaterialMiracleUses(uint uses, bool temporary)
    {
        throw new NotSupportedException("Artisan standard Material Miracle limits are not supported by the GatherBuddy compatibility shim.");
    }

    private void ResetStandardMaxMaterialMiracleUses()
    {
    }

    private void ChangeStandardMinimumStepsBeforeMiracle(uint steps, bool temporary)
    {
        throw new NotSupportedException("Artisan standard Material Miracle step thresholds are not supported by the GatherBuddy compatibility shim.");
    }

    private void ResetStandardMinimumStepsBeforeMiracle()
    {
    }

    private static List<(string, int)> ReturnMacroInfo()
        => [];

    private static Dictionary<int, string> GetLists()
        => [];

    private static void StartListById(int listId)
        => throw new NotSupportedException($"Artisan list {listId} is unavailable through the GatherBuddy compatibility shim.");

    private void RegisterProviders()
    {
        _pluginInterface.GetIpcProvider<bool>("Artisan.GetEnduranceStatus").RegisterFunc(GetEnduranceStatus);
        _pluginInterface.GetIpcProvider<bool, object>("Artisan.SetEnduranceStatus").RegisterAction(SetEnduranceStatus);
        _pluginInterface.GetIpcProvider<bool>("Artisan.IsListRunning").RegisterFunc(IsListRunning);
        _pluginInterface.GetIpcProvider<bool>("Artisan.IsListPaused").RegisterFunc(IsListPaused);
        _pluginInterface.GetIpcProvider<bool, object>("Artisan.SetListPause").RegisterAction(SetListPause);
        _pluginInterface.GetIpcProvider<bool>("Artisan.GetStopRequest").RegisterFunc(GetStopRequest);
        _pluginInterface.GetIpcProvider<bool, object>("Artisan.SetStopRequest").RegisterAction(SetStopRequest);
        _pluginInterface.GetIpcProvider<ushort, int, object>("Artisan.CraftItem").RegisterAction(CraftItem);
        _pluginInterface.GetIpcProvider<bool>("Artisan.IsBusy").RegisterFunc(IsShimBusy);
        _pluginInterface.GetIpcProvider<ushort, uint, uint, uint, uint, object>("Artisan.AssignRecipie").RegisterAction(AssignRecipe);

        _pluginInterface.GetIpcProvider<uint, string, bool, object>("Artisan.ChangeSolver").RegisterAction(ChangeSolver);
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempSolverBackToNormal").RegisterAction(ResetSolver);
        _pluginInterface.GetIpcProvider<uint, uint, bool, bool, object>("Artisan.ChangeFood").RegisterAction(ChangeFood);
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempFoodBackToNormal").RegisterAction(ResetFood);
        _pluginInterface.GetIpcProvider<uint, uint, bool, bool, object>("Artisan.ChangePotion").RegisterAction(ChangePotion);
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempPotionBackToNormal").RegisterAction(ResetPotion);
        _pluginInterface.GetIpcProvider<uint, uint, bool, object>("Artisan.ChangeManual").RegisterAction(ChangeManual);
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempManualBackToNormal").RegisterAction(ResetManual);
        _pluginInterface.GetIpcProvider<uint, uint, bool, object>("Artisan.ChangeSquadronManual").RegisterAction(ChangeSquadronManual);
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempSquadronManualBackToNormal").RegisterAction(ResetSquadronManual);

        _pluginInterface.GetIpcProvider<uint, uint, bool, object>("Artisan.ChangeExpertProfileID").RegisterAction(ChangeExpertProfileId);
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempExpertProfileIDBackToNormal").RegisterAction(ResetExpertProfileId);
        _pluginInterface.GetIpcProvider<uint, uint, bool, object>("Artisan.ChangeExpertMaxSteadyUses").RegisterAction(ChangeExpertMaxSteadyUses);
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempExpertMaxSteadyUsesBackToNormal").RegisterAction(ResetExpertMaxSteadyUses);
        _pluginInterface.GetIpcProvider<uint, uint, bool, object>("Artisan.ChangeExpertMaxMaterialMiracleUses").RegisterAction(ChangeExpertMaxMaterialMiracleUses);
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempExpertMaxMaterialMiracleUsesBackToNormal").RegisterAction(ResetExpertMaxMaterialMiracleUses);
        _pluginInterface.GetIpcProvider<uint, uint, bool, object>("Artisan.ChangeExpertMinimumStepsBeforeMiracle").RegisterAction(ChangeExpertMinimumStepsBeforeMiracle);
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempExpertMinimumStepsBeforeMiracleBackToNormal").RegisterAction(ResetExpertMinimumStepsBeforeMiracle);
        _pluginInterface.GetIpcProvider<uint, bool, object>("Artisan.ChangeStandardMaxMaterialMiracleUses").RegisterAction(ChangeStandardMaxMaterialMiracleUses);
        _pluginInterface.GetIpcProvider<object>("Artisan.SetTempStandardMaxMaterialMiracleUsesBackToNormal").RegisterAction(ResetStandardMaxMaterialMiracleUses);
        _pluginInterface.GetIpcProvider<uint, bool, object>("Artisan.ChangeStandardMinimumStepsBeforeMiracle").RegisterAction(ChangeStandardMinimumStepsBeforeMiracle);
        _pluginInterface.GetIpcProvider<object>("Artisan.SetTempStandardMinimumStepsBeforeMiracleBackToNormal").RegisterAction(ResetStandardMinimumStepsBeforeMiracle);

        _pluginInterface.GetIpcProvider<List<(string, int)>>("Artisan.ReturnMacroInfo").RegisterFunc(ReturnMacroInfo);
        _pluginInterface.GetIpcProvider<Dictionary<int, string>>("Artisan.GetLists").RegisterFunc(GetLists);
        _pluginInterface.GetIpcProvider<int, object>("Artisan.StartListById").RegisterAction(StartListById);
        _registered = true;
        GatherBuddy.Log.Information("[ArtisanIpcShim] Registered Artisan compatibility IPC providers.");
    }

    private void UnregisterProviders()
    {
        _pluginInterface.GetIpcProvider<bool>("Artisan.GetEnduranceStatus").UnregisterFunc();
        _pluginInterface.GetIpcProvider<bool, object>("Artisan.SetEnduranceStatus").UnregisterAction();
        _pluginInterface.GetIpcProvider<bool>("Artisan.IsListRunning").UnregisterFunc();
        _pluginInterface.GetIpcProvider<bool>("Artisan.IsListPaused").UnregisterFunc();
        _pluginInterface.GetIpcProvider<bool, object>("Artisan.SetListPause").UnregisterAction();
        _pluginInterface.GetIpcProvider<bool>("Artisan.GetStopRequest").UnregisterFunc();
        _pluginInterface.GetIpcProvider<bool, object>("Artisan.SetStopRequest").UnregisterAction();
        _pluginInterface.GetIpcProvider<ushort, int, object>("Artisan.CraftItem").UnregisterAction();
        _pluginInterface.GetIpcProvider<bool>("Artisan.IsBusy").UnregisterFunc();
        _pluginInterface.GetIpcProvider<ushort, uint, uint, uint, uint, object>("Artisan.AssignRecipie").UnregisterAction();

        _pluginInterface.GetIpcProvider<uint, string, bool, object>("Artisan.ChangeSolver").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempSolverBackToNormal").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, uint, bool, bool, object>("Artisan.ChangeFood").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempFoodBackToNormal").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, uint, bool, bool, object>("Artisan.ChangePotion").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempPotionBackToNormal").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, uint, bool, object>("Artisan.ChangeManual").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempManualBackToNormal").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, uint, bool, object>("Artisan.ChangeSquadronManual").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempSquadronManualBackToNormal").UnregisterAction();

        _pluginInterface.GetIpcProvider<uint, uint, bool, object>("Artisan.ChangeExpertProfileID").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempExpertProfileIDBackToNormal").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, uint, bool, object>("Artisan.ChangeExpertMaxSteadyUses").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempExpertMaxSteadyUsesBackToNormal").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, uint, bool, object>("Artisan.ChangeExpertMaxMaterialMiracleUses").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempExpertMaxMaterialMiracleUsesBackToNormal").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, uint, bool, object>("Artisan.ChangeExpertMinimumStepsBeforeMiracle").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, object>("Artisan.SetTempExpertMinimumStepsBeforeMiracleBackToNormal").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, bool, object>("Artisan.ChangeStandardMaxMaterialMiracleUses").UnregisterAction();
        _pluginInterface.GetIpcProvider<object>("Artisan.SetTempStandardMaxMaterialMiracleUsesBackToNormal").UnregisterAction();
        _pluginInterface.GetIpcProvider<uint, bool, object>("Artisan.ChangeStandardMinimumStepsBeforeMiracle").UnregisterAction();
        _pluginInterface.GetIpcProvider<object>("Artisan.SetTempStandardMinimumStepsBeforeMiracleBackToNormal").UnregisterAction();

        _pluginInterface.GetIpcProvider<List<(string, int)>>("Artisan.ReturnMacroInfo").UnregisterFunc();
        _pluginInterface.GetIpcProvider<Dictionary<int, string>>("Artisan.GetLists").UnregisterFunc();
        _pluginInterface.GetIpcProvider<int, object>("Artisan.StartListById").UnregisterAction();
        _registered = false;
        GatherBuddy.Log.Information("[ArtisanIpcShim] Unregistered Artisan compatibility IPC providers.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_registered && !IsRealArtisanLoaded())
            UnregisterProviders();
        _registered = false;
    }
}
