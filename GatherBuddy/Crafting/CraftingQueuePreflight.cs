using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

internal static unsafe class CraftingQueuePreflight
{
    internal static bool TryValidate(
        CraftingExecutionPlan plan,
        out string failure,
        bool validatePrecrafts = false)
    {
        failure = string.Empty;
        var validationItems = validatePrecrafts
            ? plan.QueueView
            : plan.OriginalRecipesView;
        if (validationItems.Count == 0)
            return true;

        var missingRecipeItems = validationItems
            .Where(item => !item.Options.Skipping
                && item.Quantity > 0)
            .Where(item => !RecipeManager.GetRecipe(item.RecipeId).HasValue)
            .ToList();
        if (missingRecipeItems.Count > 0)
        {
            failure = "Cannot start crafting:\n"
                + string.Join("\n", missingRecipeItems.Select(item => $"Recipe {item.RecipeId} could not be resolved."));
            return false;
        }

        var recipes = validationItems
            .Select(item => (Item: item, Recipe: RecipeManager.GetRecipe(item.RecipeId)))
            .Where(entry => !entry.Item.Options.Skipping
                && entry.Item.Quantity > 0
                && entry.Recipe.HasValue)
            .Select(entry => (entry.Item, Recipe: entry.Recipe!.Value))
            .ToList();
        var requiredJobs = recipes.Select(entry => entry.Recipe.CraftType.RowId + 8).ToList();
        var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0u;
        if (currentJob == 0)
        {
            failure = "Cannot start crafting: the current crafting class could not be determined.";
            return false;
        }

        var gearsetModule = RaptureGearsetModule.Instance();
        var needsJobSwitch = requiredJobs.Aggregate(
            (ActiveJob: currentJob, NeedsSwitch: false),
            (state, requiredJob) => (requiredJob, state.NeedsSwitch || requiredJob != state.ActiveJob)).NeedsSwitch;
        if (needsJobSwitch && gearsetModule == null)
        {
            failure = "Cannot start crafting: saved gearset data is unavailable, so required class switches cannot be verified.";
            return false;
        }

        var missingGearsets = CraftingJobTransitionValidator.FindMissingGearsets(
            requiredJobs,
            currentJob,
            jobId => gearsetModule != null && GearsetStatsReader.TryResolveExistingGearsetIndex(gearsetModule, jobId, out _));

        var issues = new List<string>();
        foreach (var jobId in missingGearsets)
        {
            var dependency = recipes.First(entry => entry.Recipe.CraftType.RowId + 8 == jobId);
            issues.Add($"No saved gearset for {GetJobName(jobId)}, required by {DescribeRecipe(dependency.Item, dependency.Recipe)}.");
        }

        if (!requiredJobs.All(jobId => CraftingJobLevelReader.TryRead(jobId, out _)))
        {
            failure = "Cannot start crafting: player job levels are unavailable; retry when the game state is ready.";
            return false;
        }

        foreach (var entry in recipes)
        {
            var jobId = entry.Recipe.CraftType.RowId + 8;
            var requiredLevel = entry.Recipe.MinimumJobLevel();
            CraftingJobLevelReader.TryRead(jobId, out var actualLevel);
            if (actualLevel < requiredLevel)
            {
                var issue = $"{GetJobName(jobId)} is level {actualLevel}, but {DescribeRecipe(entry.Item, entry.Recipe)} requires level {requiredLevel}.";
                if (!issues.Contains(issue))
                    issues.Add(issue);
            }

            // Cosmic mission recipes are supplied by the active mission and do not
            // appear in the player's persistent recipe-unlock book.
            if (entry.Recipe.Number != 0 && !Dalamud.UnlockState.IsRecipeUnlocked(entry.Recipe))
                issues.Add($"The recipe for {DescribeRecipe(entry.Item, entry.Recipe)} is not unlocked.");
        }

        if (issues.Count == 0)
            return true;

        failure = "Cannot start crafting:\n" + string.Join("\n", issues);
        return false;
    }

    internal static bool TryValidateMaterials(CraftingExecutionPlan plan, out string failure)
    {
        failure = string.Empty;
        if (plan.MaterialsView.Count == 0)
            return true;

        var issues = new List<string>();
        foreach (var (itemId, requiredQuantity) in plan.MaterialsView.OrderBy(pair => pair.Key))
        {
            if (requiredQuantity <= 0)
                continue;
            var (nq, hq) = CraftingInventoryCounter.GetInventorySplitCounts(itemId);
            var total = nq + hq;
            if (total < requiredQuantity)
            {
                issues.Add($"{DescribeItem(itemId)} requires {requiredQuantity:N0}, but only {total:N0} are available.");
                continue;
            }

            var requiredHq = plan.IngredientDemandsView.GetValueOrDefault(itemId).RequiredHQ;
            if (requiredHq > hq)
                issues.Add($"{DescribeItem(itemId)} requires {requiredHq:N0} HQ, but only {hq:N0} HQ are available.");

            var requiredNq = plan.IngredientDemandsView.GetValueOrDefault(itemId).RequiredNQ;
            if (requiredNq > nq)
                issues.Add($"{DescribeItem(itemId)} requires {requiredNq:N0} NQ, but only {nq:N0} NQ are available.");
        }

        if (issues.Count == 0)
            return true;

        failure = "Cannot start crafting:\n" + string.Join("\n", issues);
        return false;
    }

    private static string DescribeRecipe(CraftingListItem item, Recipe recipe)
    {
        var name = recipe.ItemResult.Value.Name.ExtractText();
        return item.IsOriginalRecipe ? $"final craft '{name}'" : $"precraft '{name}'";
    }

    private static string GetJobName(uint jobId)
    {
        var classJobs = Dalamud.GameData.GetExcelSheet<ClassJob>();
        return classJobs?.TryGetRow(jobId, out var job) == true
            ? job.Name.ExtractText()
            : $"crafting job {jobId}";
    }

    private static string DescribeItem(uint itemId)
    {
        var items = Dalamud.GameData.GetExcelSheet<Item>();
        return items?.TryGetRow(itemId, out var item) == true
            ? $"'{item.Name.ExtractText()}'"
            : $"item {itemId}";
    }
}
