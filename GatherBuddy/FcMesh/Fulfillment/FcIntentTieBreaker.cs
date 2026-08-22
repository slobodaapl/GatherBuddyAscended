using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.Fulfillment;

/// <summary>
/// Applies replicated worker intent only as a deterministic local ordering
/// hint.  It never changes demand, supply, capability eligibility, or queue
/// membership.  Callers must provide the current projection; this policy does
/// not treat an arrival or a relayed observation as fresh liveness evidence.
/// </summary>
public static class FcIntentTieBreaker
{
    public interface ICraftQueueFactsResolver
    {
        bool TryResolve(uint recipeId, out CraftQueueRecipeFacts facts);
    }

    public sealed record CraftQueueRecipeFacts(
        uint JobId,
        uint ResultItemId,
        IReadOnlyList<uint> IngredientItemIds);

    public sealed record CraftQueueCandidate(
        CraftingListItem Item,
        uint JobId,
        IReadOnlySet<uint> Dependencies,
        bool Ready);

    private sealed class RecipeManagerCraftQueueFactsResolver : ICraftQueueFactsResolver
    {
        public bool TryResolve(uint recipeId, out CraftQueueRecipeFacts facts)
        {
            if (recipeId == 0 || RecipeManager.GetRecipe(recipeId) is not { } recipe)
            {
                facts = null!;
                return false;
            }

            facts = new CraftQueueRecipeFacts(
                recipe.CraftType.RowId,
                recipe.ItemResult.RowId,
                RecipeManager.GetIngredients(recipe)
                    .Select(ingredient => ingredient.itemId)
                    .ToArray());
            return facts.JobId != 0
                && facts.ResultItemId != 0
                && facts.IngredientItemIds.All(itemId => itemId != 0);
        }
    }

    public static ICraftQueueFactsResolver ProductionCraftQueueFactsResolver { get; }
        = new RecipeManagerCraftQueueFactsResolver();

    /// <summary>
    /// Returns a transient item order only when a visible active worker is
    /// occupying a candidate and another candidate is free.  Empty means the
    /// existing route/node ordering remains authoritative (no intent,
    /// irrelevant intent, one candidate, or all candidates occupied).
    /// </summary>
    public static uint[] PreferGatherOrder(
        IReadOnlyList<uint> candidateOrder,
        IReadOnlyList<WorkerSessionRecord> activeWorkers,
        WorkerSessionRecord localWorker)
    {
        ArgumentNullException.ThrowIfNull(candidateOrder);
        ArgumentNullException.ThrowIfNull(activeWorkers);
        ArgumentNullException.ThrowIfNull(localWorker);

        var candidates = candidateOrder
            .Where(itemId => itemId != 0)
            .Distinct()
            .ToArray();
        if (candidates.Length < 2)
            return Array.Empty<uint>();

        var occupied = activeWorkers
            .Where(worker => IsUsableIntentWorker(worker, localWorker))
            .SelectMany(GetGatherIntentItems)
            .ToHashSet();
        var occupiedCandidates = candidates.Where(occupied.Contains).ToArray();
        if (occupiedCandidates.Length == 0 || occupiedCandidates.Length == candidates.Length)
            return Array.Empty<uint>();

        return candidates
            .Where(itemId => !occupied.Contains(itemId))
            .Concat(occupiedCandidates)
            .ToArray();
    }

    /// <summary>
    /// Reorders only the currently ready prefix of one existing craft-job
    /// group.  Dependency edges are supplied by the existing recipe planner;
    /// a candidate is never moved ahead of an uncompleted dependency.  The
    /// returned candidates are the same objects, preserving repeated recipe
    /// entries, quality options, list scope, and queue metadata.
    /// </summary>
    public static CraftQueueCandidate[] PreferCraftQueue(
        IReadOnlyList<CraftQueueCandidate> queue,
        IReadOnlyList<WorkerSessionRecord> activeWorkers,
        WorkerSessionRecord localWorker)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(activeWorkers);
        ArgumentNullException.ThrowIfNull(localWorker);
        if (queue.Count < 2)
            return queue.ToArray();

        var occupied = activeWorkers
            .Where(worker => IsUsableIntentWorker(worker, localWorker))
            .SelectMany(GetCraftIntentRecipes)
            .ToHashSet();
        var tailPreferred = IsWithoutCurrentCraftIntent(localWorker)
            ? activeWorkers
                .Where(worker => IsUsableIntentWorker(worker, localWorker))
                .SelectMany(GetCraftIntentTailRecipes)
                .Where(recipeId => !occupied.Contains(recipeId))
                .ToHashSet()
            : new HashSet<uint>();
        if (occupied.Count == 0 && tailPreferred.Count == 0)
            return queue.ToArray();

        var remaining = queue.ToList();
        var result = new List<CraftQueueCandidate>(queue.Count);
        while (remaining.Count > 0)
        {
            var first = remaining[0];
            if (!first.Ready || first.JobId == 0 || HasRemainingDependency(first, remaining))
            {
                // The original planner's order is the safety fallback if a
                // candidate is not proven ready.  Do not manufacture a
                // topological order here.
                return queue.ToArray();
            }

            var prefixLength = 0;
            while (prefixLength < remaining.Count
                && remaining[prefixLength].JobId == first.JobId)
                prefixLength++;

            var readyPrefix = remaining
                .Take(prefixLength)
                .Where(candidate => candidate.Ready
                    && !HasRemainingDependency(candidate, remaining))
                .ToArray();
            if (readyPrefix.Length == 0)
                return queue.ToArray();

            // Keep the planner's order when all ready branches overlap.  A
            // free branch is preferred only within the same existing job
            // group and ready prefix.
            var hasFreeCandidate = readyPrefix.Any(candidate
                => !occupied.Contains(candidate.Item.RecipeId));
            var chosen = hasFreeCandidate
                ? readyPrefix
                    .OrderBy(candidate => occupied.Contains(candidate.Item.RecipeId)
                        ? 2
                        : tailPreferred.Contains(candidate.Item.RecipeId) ? 0 : 1)
                    .First()
                : readyPrefix[0];
            var chosenIndex = remaining.FindIndex(candidate
                => ReferenceEquals(candidate, chosen));
            if (chosenIndex < 0)
                return queue.ToArray();
            remaining.RemoveAt(chosenIndex);
            result.Add(chosen);
        }

        return result.ToArray();
    }

    /// <summary>
    /// Builds candidate facts from the authoritative recipe planner seam.
    /// Missing or ambiguous facts return an empty array so callers retain the
    /// planner's incumbent order instead of assuming independence.
    /// </summary>
    public static CraftQueueCandidate[] BuildCraftQueueCandidates(
        IReadOnlyList<CraftingListItem> queue,
        ICraftQueueFactsResolver factsResolver)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(factsResolver);
        if (queue.Any(item => item is null || item.RecipeId == 0))
            return Array.Empty<CraftQueueCandidate>();

        var factsByRecipe = new Dictionary<uint, CraftQueueRecipeFacts>();
        foreach (var recipeId in queue
                     .Select(item => item.RecipeId)
                     .Distinct())
        {
            if (!factsResolver.TryResolve(recipeId, out var facts)
                || facts is null
                || facts.JobId == 0
                || facts.ResultItemId == 0
                || facts.IngredientItemIds is null
                || facts.IngredientItemIds.Any(itemId => itemId == 0))
                return Array.Empty<CraftQueueCandidate>();

            factsByRecipe[recipeId] = facts;
        }

        var dependencies = new Dictionary<uint, IReadOnlySet<uint>>();
        foreach (var (recipeId, facts) in factsByRecipe)
        {
            var required = new HashSet<uint>();
            foreach (var ingredientItemId in facts.IngredientItemIds)
            {
                var matches = factsByRecipe
                    .Where(pair => pair.Value.ResultItemId == ingredientItemId)
                    .Select(pair => pair.Key)
                    .Where(candidateRecipeId => candidateRecipeId != recipeId)
                    .Distinct()
                    .ToArray();
                if (matches.Length > 1)
                    return Array.Empty<CraftQueueCandidate>();
                if (matches.Length == 1)
                    required.Add(matches[0]);
            }
            dependencies[recipeId] = required;
        }

        return queue
            .Select(item => new CraftQueueCandidate(
                item,
                factsByRecipe[item.RecipeId].JobId,
                dependencies[item.RecipeId],
                !item.Options.Skipping && item.Quantity > 0))
            .ToArray();
    }

    public static bool IsUsableIntentWorker(
        WorkerSessionRecord worker,
        WorkerSessionRecord localWorker)
        => worker is not null
            && localWorker is not null
            && worker.Header is not null
            && localWorker.Header is not null
            && !string.Equals(
                worker.Header.OwnerAuthorId,
                localWorker.Header.OwnerAuthorId,
                StringComparison.Ordinal)
            && worker.State == FcWorkerState.Active
            && worker.SessionId != Guid.Empty
            && worker.SessionGeneration != 0
            && !string.IsNullOrWhiteSpace(worker.WorldFingerprint)
            && string.Equals(
                worker.WorldFingerprint,
                localWorker.WorldFingerprint,
                StringComparison.Ordinal)
            && HasSelectionOverlap(worker.Selection, localWorker.Selection);

    private static IEnumerable<uint> GetGatherIntentItems(WorkerSessionRecord worker)
    {
        if (worker.CurrentTarget is { RecipeId: null, ItemId: > 0 } target)
            yield return target.ItemId.Value;
    }

    private static IEnumerable<uint> GetCraftIntentRecipes(WorkerSessionRecord worker)
    {
        var currentRecipeId = worker.CurrentTarget?.RecipeId;
        if (currentRecipeId is > 0)
            yield return currentRecipeId.Value;
    }

    private static IEnumerable<uint> GetCraftIntentTailRecipes(WorkerSessionRecord worker)
    {
        foreach (var item in worker.CraftQueue?.Skip(1)
                     ?? Array.Empty<FcLogicalQueueEntry>())
        {
            if (item is not null && item.RecipeId > 0 && item.Remaining > 0)
                yield return item.RecipeId;
        }
    }

    private static bool IsWithoutCurrentCraftIntent(WorkerSessionRecord worker)
        => worker.CurrentTarget?.RecipeId is not > 0;

    private static bool HasSelectionOverlap(
        FcFulfillmentSelection? left,
        FcFulfillmentSelection? right)
    {
        if (left is null || right is null)
            return false;
        if (left.AllPublishedLists || right.AllPublishedLists)
            return true;
        var rightIds = (right.ListIds ?? Array.Empty<Guid>()).ToHashSet();
        return (left.ListIds ?? Array.Empty<Guid>()).Any(rightIds.Contains);
    }

    private static bool HasRemainingDependency(
        CraftQueueCandidate candidate,
        IReadOnlyList<CraftQueueCandidate> remaining)
        => candidate.Dependencies.Any(dependency => remaining.Any(item
            => item.Item.RecipeId == dependency));
}
