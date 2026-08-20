using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Sessions;

public sealed record FcWorkerDependencyClosureResult(
    bool Succeeded,
    string Error,
    IReadOnlySet<FcQuantityKey> Keys)
{
    public static FcWorkerDependencyClosureResult Invalid(string error)
        => new(false, error, new HashSet<FcQuantityKey>());

    public static FcWorkerDependencyClosureResult Valid(IEnumerable<FcQuantityKey> keys)
    {
        if (keys is null)
            throw new ArgumentNullException(nameof(keys));
        var values = keys.ToArray();
        if (values.Length > FcRecordValidator.MaxArrayEntries)
            return Invalid("Published list dependency closure is too large.");
        if (values.Any(key => key.ItemId == 0 || !Enum.IsDefined(key.Quality)))
            return Invalid("Dependency closure contains an invalid quantity key.");
        var normalized = values.ToHashSet();
        if (normalized.Count != values.Length)
            return Invalid("Dependency closure contains duplicate quantity keys.");
        return normalized.Count == 0
            ? Invalid("Published list dependency closure is empty.")
            : new(true, string.Empty, normalized);
    }
}

/// <summary>
/// Derives the physical inventory scope from published recipe identities. The
/// planner receives no represented inventory, so current stock cannot prune a
/// recursive precraft/material dependency from the protected scope.
/// </summary>
public static class FcWorkerDependencyClosure
{
    public static FcWorkerDependencyClosureResult Build(
        IEnumerable<PublishedListRecord> records)
    {
        if (records is null)
            return FcWorkerDependencyClosureResult.Invalid("Published list records are unavailable.");

        var keys = new HashSet<FcQuantityKey>();
        foreach (var record in records)
        {
            if (record is null || !record.Published)
                return FcWorkerDependencyClosureResult.Invalid("Dependency closure contains an unpublished list.");
            var result = BuildRecord(record, keys);
            if (!result.Succeeded)
                return result;
            if (keys.Count > FcRecordValidator.MaxArrayEntries)
                return FcWorkerDependencyClosureResult.Invalid(
                    "Published list dependency closure is too large.");
        }

        return FcWorkerDependencyClosureResult.Valid(keys);
    }

    public static FcWorkerDependencyClosureResult Build(
        IEnumerable<FcPublicListView> views,
        FcFulfillmentSelection selection)
    {
        if (views is null)
            return FcWorkerDependencyClosureResult.Invalid("Published list projection is unavailable.");
        if (selection is null)
            return FcWorkerDependencyClosureResult.Invalid("Worker selection is unavailable.");
        if (selection.ListIds is null
            || selection.ListIds.Length > FcRecordValidator.MaxArrayEntries
            || selection.ListIds.Any(id => id == Guid.Empty)
            || selection.ListIds.Distinct().Count() != selection.ListIds.Length
            || selection.AllPublishedLists && selection.ListIds.Length != 0)
            return FcWorkerDependencyClosureResult.Invalid("Worker selection is invalid.");

        var records = views
            .Where(view => view is not null
                && view.Record is not null
                && view.Record.Published
                && view.IsCompatible)
            .Where(view => selection.AllPublishedLists || selection.ListIds.Contains(view.Record.ListId))
            .Select(view => view.Record)
            .ToArray();
        if (records.Length == 0)
            return FcWorkerDependencyClosureResult.Invalid("No compatible published list is available for dependency closure.");
        return Build(records);
    }

    private static FcWorkerDependencyClosureResult BuildRecord(
        PublishedListRecord record,
        ISet<FcQuantityKey> destination)
    {
        if (record.FinalTargets is null
            || record.FinalTargets.Length == 0
            || record.FinalTargets.Length > FcRecordValidator.MaxArrayEntries)
            return FcWorkerDependencyClosureResult.Invalid("Published list has no final recipe targets.");
        if (record.FinalTargets.Any(target => target is null))
            return FcWorkerDependencyClosureResult.Invalid("Published list contains an incomplete final recipe target.");
        if (record.FinalQualityPolicy is null
            || record.PrecraftQualityPolicy is null
            || record.FinalQualityPolicy.Rules is null
            || record.PrecraftQualityPolicy.Rules is null
            || record.FinalQualityPolicy.Rules.Length > FcRecordValidator.MaxArrayEntries
            || record.PrecraftQualityPolicy.Rules.Length > FcRecordValidator.MaxArrayEntries)
            return FcWorkerDependencyClosureResult.Invalid("Published list quality policy is incomplete.");

        var allHq = record.FinalTargets.All(target => target.Quality == FcItemQuality.Hq);
        var allNq = record.FinalTargets.All(target => target.Quality == FcItemQuality.Nq);
        if (!allHq && !allNq)
            return FcWorkerDependencyClosureResult.Invalid("Published list mixes final quality policies that cannot be reconstructed safely.");

        try
        {
            if (record.FinalQualityPolicy.Rules.Any(rule => rule is null
                    || rule.ItemId == 0
                    || rule.Quantity <= 0
                    || !Enum.IsDefined(rule.Quality))
                || record.PrecraftQualityPolicy.Rules.Any(rule => rule is null
                    || rule.ItemId == 0
                    || rule.Quantity <= 0
                    || !Enum.IsDefined(rule.Quality)))
                return FcWorkerDependencyClosureResult.Invalid("Published list quality policy contains an invalid rule.");

            var list = new CraftingListDefinition { UseAllHQ = allHq };
            var targetKeys = new HashSet<(uint ItemId, FcItemQuality Quality)>();
            var recipeIds = new HashSet<uint>();
            foreach (var target in record.FinalTargets)
            {
                if (target.RecipeId == 0 || target.ItemId == 0 || target.Quantity <= 0
                    || !Enum.IsDefined(target.Quality))
                    return FcWorkerDependencyClosureResult.Invalid("Published list contains an invalid final recipe target.");
                if (!targetKeys.Add((target.ItemId, target.Quality)) || !recipeIds.Add(target.RecipeId))
                    return FcWorkerDependencyClosureResult.Invalid(
                        "Published list contains duplicate final recipe targets.");
                var finalRule = record.FinalQualityPolicy.Rules.SingleOrDefault(rule =>
                    rule is not null
                    && rule.ItemId == target.ItemId
                    && rule.Quality == target.Quality);
                if (finalRule is null || finalRule.Quantity != target.Quantity)
                    return FcWorkerDependencyClosureResult.Invalid(
                        $"Published final quality policy does not match recipe {target.RecipeId}.");

                var recipe = RecipeManager.GetRecipe(target.RecipeId);
                if (!recipe.HasValue
                    || recipe.Value.ItemResult.RowId != target.ItemId
                    || recipe.Value.AmountResult == 0)
                    return FcWorkerDependencyClosureResult.Invalid(
                        $"Published recipe {target.RecipeId} could not be resolved from authoritative recipe data.");

                var amountResult = (int)recipe.Value.AmountResult;
                if (target.Quantity % amountResult != 0)
                    return FcWorkerDependencyClosureResult.Invalid(
                        $"Published recipe {target.RecipeId} output quantity is not an exact recipe quantity.");

                list.Recipes.Add(new CraftingListItem(
                    target.RecipeId,
                    target.Quantity / amountResult)
                {
                    Options = new ListItemOptions { NQOnly = target.Quality == FcItemQuality.Nq },
                });
                destination.Add(new FcQuantityKey(target.ItemId, target.Quality));
            }

            var empty = FcItemQuantityMap.Empty;
            var zeroInventory = new FcMapQuantitySource(empty);
            var planningContext = new CraftingPlanningContext(
                zeroInventory,
                zeroInventory,
                ExecutionSource.PrivateList,
                null);
            var plan = CraftingListPlanner.Build(
                list,
                new CraftingListPlannerOptions(
                    ConsumeIntermediateAvailability: false,
                    ConsumeFinalAvailability: false,
                    PlanningContext: planningContext));

            var expectedRecipes = list.Recipes.Select(item => item.RecipeId).ToHashSet();
            if (!expectedRecipes.IsSubsetOf(plan.OriginalRecipes.Select(item => item.RecipeId)))
                return FcWorkerDependencyClosureResult.Invalid(
                    "Inventory-neutral planner omitted a published final recipe.");

            var precraftQuality = (record.PrecraftQualityPolicy?.Rules ?? Array.Empty<FcQualityRule>())
                .GroupBy(rule => rule.ItemId)
                .ToDictionary(group => group.Key, group => group
                    .Select(rule => rule.Quality)
                    .Distinct()
                    .ToArray());
            foreach (var (itemId, demand) in plan.IngredientDemands)
            {
                if (plan.Precrafts.ContainsKey(itemId)
                    && precraftQuality.TryGetValue(itemId, out var qualities))
                {
                    foreach (var quality in qualities)
                        destination.Add(new FcQuantityKey(itemId, quality));
                }
                else
                    AddDemandKeys(destination, itemId, demand);
            }

            return new(true, string.Empty, destination.ToHashSet());
        }
        catch (OverflowException exception)
        {
            return FcWorkerDependencyClosureResult.Invalid(
                $"Published list dependency quantities overflow: {exception.Message}");
        }
        catch (Exception exception)
        {
            return FcWorkerDependencyClosureResult.Invalid(
                $"Published list dependency planning failed: {exception.Message}");
        }
    }

    private static void AddDemandKeys(
        ISet<FcQuantityKey> destination,
        uint itemId,
        IngredientQualityDemand demand)
    {
        if (itemId == 0 || demand.Total <= 0)
            return;

        if (demand.RequiredNQ > 0)
            destination.Add(new FcQuantityKey(itemId, FcItemQuality.Nq));
        if (demand.RequiredHQ > 0)
            destination.Add(new FcQuantityKey(itemId, FcItemQuality.Hq));

        // PreferNQ/PreferHQ both permit the fallback quality. Both keys must
        // therefore remain protected until the worker's attribution ledger
        // proves that one quality has left the physical inventory.
        if (demand.PreferNQ > 0 || demand.PreferHQ > 0)
        {
            destination.Add(new FcQuantityKey(itemId, FcItemQuality.Nq));
            destination.Add(new FcQuantityKey(itemId, FcItemQuality.Hq));
        }
    }
}
