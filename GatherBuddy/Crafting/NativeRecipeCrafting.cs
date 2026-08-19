using System;

namespace GatherBuddy.Crafting;

internal sealed class NativeCraftAmountState
{
    public uint? RecipeId { get; private set; }
    public int Maximum { get; private set; }
    public int Value { get; private set; }

    public void Refresh(uint recipeId, int maximum)
    {
        maximum = Math.Max(0, maximum);
        if (RecipeId != recipeId)
        {
            RecipeId = recipeId;
            Maximum = maximum;
            Value = maximum;
            return;
        }

        Maximum = maximum;
        Value = Math.Min(Value, maximum);
    }

    public bool ApplyText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            Value = 0;
            return true;
        }

        if (!int.TryParse(text, out var value))
            return false;

        Value = Math.Clamp(value, 0, Maximum);
        return true;
    }
}

internal static class NativeRecipeCraftingLauncher
{
    internal static void ConfigureDefinition(
        CraftingListDefinition list,
        uint recipeId,
        int amount,
        RecipeCraftSettings recipeSettings)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(recipeSettings);
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Craft amount must be positive.");

        list.Ephemeral = true;
        list.SkipIfEnough = false;
        list.SkipFinalIfEnough = false;
        list.QuickSynthAll = false;
        list.QuickSynthAllPreferNQ = false;
        list.QuickSynthAllPrecraftsOnly = false;
        list.RetainerRestock = false;
        list.AutoPurchaseBlockedDependencies = false;
        list.ReturnToHomeWorldBeforeCrafting = false;
        list.Recipes.Clear();
        list.Recipes.Add(new CraftingListItem(recipeId, amount)
        {
            IsOriginalRecipe = true,
            CraftSettings = recipeSettings.Clone(),
        });
    }

    internal static bool TryStart(uint recipeId, int amount, out string failure)
    {
        failure = string.Empty;
        if (amount <= 0)
        {
            failure = "Craft amount must be positive.";
            return false;
        }
        if (CraftingGatherBridge.HasActiveQueue)
        {
            failure = "A GatherBuddy crafting queue is already active.";
            return false;
        }
        if (RecipeManager.GetRecipe(recipeId) is null)
        {
            failure = $"Recipe {recipeId} was not found.";
            return false;
        }

        CraftingListDefinition? list = null;
        var bridgeOwnsList = false;
        try
        {
            list = GatherBuddy.CraftingListManager.CreateNewList(
                $"Craft {amount} x recipe {recipeId}",
                ephemeral: true);
            var settings = GatherBuddy.RecipeBrowserSettings.Get(recipeId) ?? new RecipeCraftSettings();
            ConfigureDefinition(list, recipeId, amount, settings);
            if (!GatherBuddy.CraftingListManager.SaveList(list))
                throw new InvalidOperationException($"Ephemeral crafting list {list.ID} could not be saved.");

            var plan = CraftingExecutionPlan.CreateDirect(list);
            if (!CraftingQueuePreflight.TryValidate(plan, out failure))
                return false;
            if (CraftingGatherBridge.HasActiveQueue)
            {
                failure = "A GatherBuddy crafting queue became active before this craft could start.";
                return false;
            }

            CraftingGatherBridge.StartQueueCraftAndGather(
                plan,
                ephemeralListId: list.ID,
                owner: CraftingAutomationOwner.GatherBuddy);
            bridgeOwnsList = CraftingGatherBridge.HasActiveQueue;
            if (CraftingGatherBridge.TryGetActiveQueueFailure(out failure))
                return false;
            if (!bridgeOwnsList)
            {
                failure = "GatherBuddy did not accept the crafting queue.";
                return false;
            }

            GatherBuddy.Log.Information(
                $"[NativeRecipeCrafting] Started ephemeral direct queue: recipe={recipeId}, amount={amount}, list={list.ID}, solver={list.Recipes[0].CraftSettings?.SolverOverride}, objective={list.Recipes[0].CraftSettings?.DonatelloOptions?.Objective}");
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message.Replace('\n', ' ');
            GatherBuddy.Log.Error(
                $"[NativeRecipeCrafting] Failed to start recipe={recipeId}, amount={amount}: {ex}");
            return false;
        }
        finally
        {
            if (list != null && !bridgeOwnsList)
                GatherBuddy.CraftingListManager.DeleteList(list.ID);
        }
    }
}
