using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Chest;
using GatherBuddy.FcMesh.Capabilities;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Sessions;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Fulfillment;

/// <summary>
/// FC-specific input projection for the existing crafting planner. It creates
/// no alternate solver: published targets, global chest supply, replicated
/// held supply, and the local protected-stock ledger become the inputs to
/// <see cref="CraftingExecutionPlan.CreateFc"/>.
/// </summary>
public static class FcFulfillmentPlanner
{
    public static FcFulfillmentPlanDecision Build(
        FcProjectedWorld world,
        FcWorkerSessionStatus session,
        FcCapabilityService? capabilities = null)
    {
        if (world is null)
            return FcFulfillmentPlanDecision.Invalid("FC world projection is unavailable.");
        if (session.Desired is not { } worker
            || !session.IsSubscribed
            || worker.SessionId == Guid.Empty
            || worker.SessionGeneration == 0)
            return FcFulfillmentPlanDecision.Invalid("A subscribed FC worker session is unavailable.");
        if (session.Ledger is null)
            return FcFulfillmentPlanDecision.Invalid("FC contribution ledger is unavailable.");

        var selected = SelectLists(world.ActiveLists, worker.Selection);
        if (selected.Length == 0)
            return FcFulfillmentPlanDecision.Invalid("No compatible published FC list is selected.");
        var selectedIds = selected.Select(list => list.ListId).OrderBy(id => id).ToArray();
        var target = selected
            .SelectMany(list => list.FinalTargets.Select(value => (ListId: list.ListId, Target: value)))
            .FirstOrDefault(value => value.Target is not null);
        if (target.Target is null)
            return FcFulfillmentPlanDecision.Invalid("Selected FC lists contain no final targets.");

        var targetQuality = selected
            .SelectMany(list => list.FinalTargets
                .Where(target => IsFinalHq(list, target)))
            .FirstOrDefault();
        var capabilityRequirements = BuildCapabilityRequirements(selected);
        if (capabilityRequirements.Any(requirement => requirement.RecipeId == 0))
            return FcFulfillmentPlanDecision.Invalid(
                "FC capability demand contains conflicting final/precraft quality policies for one recipe.");
        FcCapabilityExecutionProof? capabilityProof = null;
        if (capabilityRequirements.Length > 0)
        {
            var blockedTarget = targetQuality ?? target.Target;
            var capabilityRequest = capabilities?.Publication.Request(capabilityRequirements).Request
                ?? capabilities?.Publication.CurrentRequest;
            var capabilityReady = capabilities is not null
                && capabilityRequest is not null
                && capabilityRequirements.All(requirement =>
                {
                    var response = capabilities.Publication.Responses
                        .Where(value => value.RequestId == capabilityRequest.RequestId)
                        .OrderByDescending(value => value.Header.Revision)
                        .FirstOrDefault();
                    return response is not null
                        && capabilities.EvaluateEligibility(
                            capabilityRequest,
                            response,
                            requirement.RecipeId,
                            requirement.IsPrecraft,
                            requirement.EffectiveQualityPolicy)
                            == FcCapabilityEligibility.EligibleGuaranteed;
                });
            if (!capabilityReady)
            {
                var pending = new FcFulfillmentPlanDecision(
                    true,
                    "HQ quality requires a fresh local capability response before crafting.",
                    FcFulfillmentActionKind.Craft,
                    target.ListId,
                    new FcLogicalTarget(
                        target.ListId,
                        blockedTarget.ItemId,
                        blockedTarget.RecipeId,
                        targetQuality is null ? blockedTarget.Quality : FcItemQuality.Hq),
                    Array.Empty<FcLogicalQueueEntry>(),
                    Array.Empty<uint>(),
                    Array.Empty<ItemTransferRequest>(),
                    null,
                    true,
                    true,
                    false)
                {
                    CapabilityRequest = capabilityRequest,
                    CapabilityEligibility = capabilityRequest is null
                        ? FcCapabilityEligibility.CapabilityPending
                        : FcCapabilityEligibility.CapabilityPending,
                };
                return pending;
            }
            var proofReason = "Capability proof is unavailable.";
            if (capabilities is null
                || capabilityRequest is null
                || !capabilities.TryCreateExecutionProof(
                    capabilityRequest,
                    out capabilityProof,
                    out proofReason))
            {
                return new FcFulfillmentPlanDecision(
                    true,
                    proofReason,
                    FcFulfillmentActionKind.Craft,
                    target.ListId,
                    new FcLogicalTarget(
                        target.ListId,
                        blockedTarget.ItemId,
                        blockedTarget.RecipeId,
                        targetQuality is null ? blockedTarget.Quality : FcItemQuality.Hq),
                    Array.Empty<FcLogicalQueueEntry>(),
                    Array.Empty<uint>(),
                    Array.Empty<ItemTransferRequest>(),
                    null,
                    true,
                    true,
                    false)
                {
                    CapabilityRequest = capabilityRequest,
                    CapabilityEligibility = FcCapabilityEligibility.CapabilityPending,
                };
            }
        }
        else if (capabilities is not null
            && !capabilities.TryCreateNqExecutionProof(out capabilityProof, out var nqProofReason))
        {
            return FcFulfillmentPlanDecision.Invalid(nqProofReason);
        }

        CraftingListDefinition list;
        CraftingExecutionPlan plan;
        try
        {
            list = BuildCraftingList(selected);
            var represented = world.Chest.Items;
            foreach (var remote in world.ActiveWorkers)
            {
                if (string.Equals(remote.Header.OwnerAuthorId, worker.Header.OwnerAuthorId, StringComparison.Ordinal))
                    continue;
                if (IsEligibleForSelectedLists(remote.Selection, selectedIds))
                    represented = represented.Add(remote.HeldInventoryMap);
            }
            // The local desired register is the freshest local logical state;
            // do not wait for its mesh echo before planning against held supply.
            represented = represented.Add(worker.HeldInventoryMap);

            IItemQuantitySource local = worker.UseOwnStock
                ? new CraftingPhysicalInventorySource()
                : new FcLocalConsumableInventorySource(
                    session.Ledger.CurrentPhysical.SubtractClamped(session.Ledger.State.ProtectedInventory));
            var context = new FcExecutionContext(
                worker.SessionId,
                selectedIds,
                worker.WorldFingerprint,
                world.Revision,
                capabilityProof);
            plan = CraftingExecutionPlan.CreateFc(
                list,
                new FcRepresentedInventorySource(represented),
                local,
                context);
            plan.ApplyFcIntentQueueOrder(
                world.ActiveWorkers,
                worker,
                FcIntentTieBreaker.ProductionCraftQueueFactsResolver);
        }
        catch (Exception exception)
        {
            return FcFulfillmentPlanDecision.Invalid($"FC execution plan could not be built: {exception.Message}");
        }

        var remaining = AggregateRemaining(world.Fulfillment, selectedIds);
        var logicalQueue = plan.QueueView
            .Where(item => item is not null && item.Quantity > 0 && !item.Options.Skipping)
            .Select(item => new FcLogicalQueueEntry(item.RecipeId, item.Quantity, selectedIds[0]))
            .ToArray();
        var candidateGatherOrder = plan.MaterialsView
            .Where(pair => pair.Value > 0)
            .Select(pair => pair.Key)
            .OrderBy(itemId => itemId)
            .ToArray();
        plan.SetFcIntentGatherCandidates(candidateGatherOrder);
        var intentGatherOrder = FcIntentTieBreaker.PreferGatherOrder(
            candidateGatherOrder,
            world.ActiveWorkers,
            worker);
        var gatherOrder = intentGatherOrder.Length == 0
            ? candidateGatherOrder
            : intentGatherOrder;
        var currentTarget = BuildTarget(selectedIds[0], remaining, logicalQueue);

        var chestTransfer = remaining.Entries
            .Select(entry => (Entry: entry, Quantity: Math.Min(entry.Quantity, world.Chest.Items.Get(entry.Key))))
            .FirstOrDefault(value => value.Quantity > 0);
        if (chestTransfer.Entry is not null)
        {
            var purpose = ListForKey(world.Fulfillment, selectedIds, chestTransfer.Entry.Key) ?? selectedIds[0];
            return new FcFulfillmentPlanDecision(
                true,
                string.Empty,
                FcFulfillmentActionKind.Withdraw,
                purpose,
                new FcLogicalTarget(purpose, chestTransfer.Entry.ItemId, null, chestTransfer.Entry.Quality),
                logicalQueue,
                gatherOrder,
                [new ItemTransferRequest(
                    new FcChestItemKey(chestTransfer.Entry.ItemId, chestTransfer.Entry.Quality == FcItemQuality.Hq),
                    checked((uint)chestTransfer.Quantity))],
                plan,
                false,
                false,
                false)
            {
                CapabilityRequest = capabilityRequirements.Length == 0 ? null : capabilities?.Publication.CurrentRequest,
                CapabilityEligibility = capabilityRequirements.Length == 0
                    ? FcCapabilityEligibility.NqAllowed
                    : FcCapabilityEligibility.EligibleGuaranteed,
            };
        }

        if (logicalQueue.Length > 0)
        {
            var action = gatherOrder.Length > 0
                ? FcFulfillmentActionKind.Gather
                : FcFulfillmentActionKind.Craft;
            return new FcFulfillmentPlanDecision(
                true,
                string.Empty,
                action,
                selectedIds[0],
                currentTarget,
                logicalQueue,
                gatherOrder,
                Array.Empty<ItemTransferRequest>(),
                plan,
                false,
                false,
                false)
            {
                CapabilityRequest = capabilityRequirements.Length == 0 ? null : capabilities?.Publication.CurrentRequest,
                CapabilityEligibility = capabilityRequirements.Length == 0
                    ? FcCapabilityEligibility.NqAllowed
                    : FcCapabilityEligibility.EligibleGuaranteed,
            };
        }

        var localHeld = worker.HeldInventoryMap.Entries.FirstOrDefault(entry => entry.Quantity > 0);
        if (localHeld is not null)
        {
            return new FcFulfillmentPlanDecision(
                true,
                string.Empty,
                FcFulfillmentActionKind.Deposit,
                ListForKey(world.Fulfillment, selectedIds, localHeld.Key) ?? selectedIds[0],
                new FcLogicalTarget(selectedIds[0], localHeld.ItemId, null, localHeld.Quality),
                logicalQueue,
                gatherOrder,
                [new ItemTransferRequest(
                    new FcChestItemKey(localHeld.ItemId, localHeld.Quality == FcItemQuality.Hq),
                    checked((uint)localHeld.Quantity))],
                plan,
                false,
                false,
                false)
            {
                CapabilityRequest = capabilityRequirements.Length == 0 ? null : capabilities?.Publication.CurrentRequest,
                CapabilityEligibility = capabilityRequirements.Length == 0
                    ? FcCapabilityEligibility.NqAllowed
                    : FcCapabilityEligibility.EligibleGuaranteed,
            };
        }

        var remoteHeld = world.Fulfillment.Contributions.Any(contribution =>
            !contribution.IsChest
            && contribution.SourceId != worker.Header.OwnerAuthorId
            && selectedIds.Contains(contribution.ListId));
        if (remoteHeld)
        {
            return new FcFulfillmentPlanDecision(
                true,
                "A subscribed remote worker holds the remaining contribution.",
                FcFulfillmentActionKind.WaitRemote,
                selectedIds[0],
                currentTarget,
                logicalQueue,
                gatherOrder,
                Array.Empty<ItemTransferRequest>(),
                plan,
                false,
                false,
                false)
            {
                CapabilityRequest = capabilityRequirements.Length == 0 ? null : capabilities?.Publication.CurrentRequest,
                CapabilityEligibility = capabilityRequirements.Length == 0
                    ? FcCapabilityEligibility.NqAllowed
                    : FcCapabilityEligibility.EligibleGuaranteed,
            };
        }

        if (remaining.Entries.Length != 0)
            return FcFulfillmentPlanDecision.Invalid("FC demand remains without a chest, local, remote, or executable planner source.");

        return new FcFulfillmentPlanDecision(
            true,
            string.Empty,
            FcFulfillmentActionKind.Complete,
            null,
            null,
            logicalQueue,
            gatherOrder,
            Array.Empty<ItemTransferRequest>(),
            plan,
            false,
            false,
            true)
        {
            CapabilityRequest = capabilityRequirements.Length == 0 ? null : capabilities?.Publication.CurrentRequest,
            CapabilityEligibility = capabilityRequirements.Length == 0
                ? FcCapabilityEligibility.NqAllowed
                : FcCapabilityEligibility.EligibleGuaranteed,
        };
    }

    private static PublishedListRecord[] SelectLists(
        IReadOnlyList<PublishedListRecord> lists,
        FcFulfillmentSelection selection)
    {
        if (selection is null)
            return Array.Empty<PublishedListRecord>();
        return (selection.AllPublishedLists
                ? lists
                : lists.Where(list => selection.ListIds.Contains(list.ListId)).ToArray())
            .Where(list => list is not null && list.Published)
            .OrderBy(list => list.ListId)
            .ToArray();
    }

    private static CraftingListDefinition BuildCraftingList(IReadOnlyList<PublishedListRecord> records)
    {
        var list = new CraftingListDefinition
        {
            ID = int.MinValue + 7,
            Name = "FC fulfillment",
            UseAllHQ = records.Any(record => record.FinalTargets
                .Any(target => IsFinalHq(record, target))),
        };
        foreach (var record in records)
        {
            foreach (var target in record.FinalTargets)
            {
                if (target.Quantity <= 0)
                    throw new InvalidOperationException("Published final target quantity must be positive.");
                var recipe = RecipeManager.GetRecipe(target.RecipeId);
                if (recipe is not { } resolved
                    || resolved.ItemResult.RowId != target.ItemId
                    || resolved.AmountResult == 0
                    || target.Quantity % (int)resolved.AmountResult != 0)
                    throw new InvalidOperationException($"Published recipe {target.RecipeId} is not an exact current NQ recipe.");
                list.Recipes.Add(new CraftingListItem(
                    target.RecipeId,
                    target.Quantity / (int)resolved.AmountResult)
                {
                    Options = new ListItemOptions { NQOnly = !IsFinalHq(record, target) },
                });
            }
        }
        return list;
    }

    private static RequiredCraftCapability[] BuildCapabilityRequirements(
        IReadOnlyList<PublishedListRecord> records)
    {
        var requirements = new List<RequiredCraftCapability>();
        foreach (var record in records)
        {
            foreach (var target in record.FinalTargets.Where(target => IsFinalHq(record, target)))
            {
                var policy = FinalPolicyFor(record, target);
                requirements.Add(new RequiredCraftCapability(
                    target.RecipeId,
                    policy)
                {
                    FinalQualityPolicy = policy,
                    IsPrecraft = false,
                });
            }

            Dictionary<uint, uint>? bestClassRecipes = null;
            foreach (var rule in record.PrecraftQualityPolicy?.Rules
                         .Where(rule => rule.Quality == FcItemQuality.Hq)
                     ?? Array.Empty<FcQualityRule>())
            {
                bestClassRecipes ??= RecipeManager.GetBestClassRecipes();
                var precraft = bestClassRecipes.TryGetValue(rule.ItemId, out var bestRecipeId)
                    ? RecipeManager.GetRecipe(bestRecipeId)
                    // When the authoritative class resolver has no result,
                    // retain the exact recipe selected by the planner rather
                    // than inventing a cross-job capability candidate.
                    : RecipeManager.GetRecipeForItem(rule.ItemId);
                if (precraft is not { } recipe)
                    continue;
                requirements.Add(new RequiredCraftCapability(
                    recipe.RowId,
                    new FcQualityPolicy([rule]))
                {
                    PrecraftQualityPolicy = new FcQualityPolicy([rule]),
                    IsPrecraft = true,
                });
            }
        }

        var unique = new Dictionary<uint, RequiredCraftCapability>();
        foreach (var requirement in requirements.OrderBy(value => value.RecipeId))
        {
            if (!unique.TryGetValue(requirement.RecipeId, out var existing))
            {
                unique[requirement.RecipeId] = requirement;
                continue;
            }
            if (existing.IsPrecraft == requirement.IsPrecraft
                && FcCapabilityFingerprints.Quality(existing.EffectiveQualityPolicy)
                    == FcCapabilityFingerprints.Quality(requirement.EffectiveQualityPolicy))
                continue;
            // Conflicting policies for one recipe cannot be represented by one
            // exact request; leave it fail-closed at the planner boundary.
            return [new RequiredCraftCapability(0, FcQualityPolicy.Empty)];
        }
        return unique.Values.ToArray();
    }

    private static bool IsFinalHq(
        PublishedListRecord record,
        PublishedRecipeTarget target)
        => target.Quality == FcItemQuality.Hq
            || (record.FinalQualityPolicy?.Rules ?? Array.Empty<FcQualityRule>())
                .Any(rule => rule.ItemId == target.ItemId && rule.Quality == FcItemQuality.Hq);

    private static FcQualityPolicy FinalPolicyFor(
        PublishedListRecord record,
        PublishedRecipeTarget target)
    {
        var rules = (record.FinalQualityPolicy?.Rules ?? Array.Empty<FcQualityRule>())
            .Where(rule => rule.ItemId == target.ItemId && rule.Quality == FcItemQuality.Hq)
            .ToArray();
        return rules.Length == 0
            ? new FcQualityPolicy([new FcQualityRule(target.ItemId, FcItemQuality.Hq, target.Quantity)])
            : new FcQualityPolicy(rules);
    }

    private static FcItemQuantityMap AggregateRemaining(
        FcFulfillmentMatchResult fulfillment,
        IReadOnlyCollection<Guid> selectedIds)
    {
        var values = new Dictionary<FcQuantityKey, int>();
        foreach (var listId in selectedIds)
        {
            if (!fulfillment.RemainingByList.TryGetValue(listId, out var remaining))
                continue;
            foreach (var entry in remaining.Entries)
                values[(FcQuantityKey)entry.Key] = checked(values.GetValueOrDefault((FcQuantityKey)entry.Key) + entry.Quantity);
        }
        return FcItemQuantityMap.FromDictionary(values);
    }

    private static Guid? ListForKey(
        FcFulfillmentMatchResult fulfillment,
        IReadOnlyCollection<Guid> selectedIds,
        FcQuantityKey key)
        => fulfillment.RemainingByList
            .Where(pair => selectedIds.Contains(pair.Key) && pair.Value.Get(key) > 0)
            .Select(pair => (Guid?)pair.Key)
            .FirstOrDefault();

    private static bool IsEligibleForSelectedLists(
        FcFulfillmentSelection selection,
        IReadOnlyCollection<Guid> selectedIds)
        => selection.AllPublishedLists
            || selection.ListIds.Any(selectedIds.Contains);

    private static FcLogicalTarget BuildTarget(
        Guid listId,
        FcItemQuantityMap remaining,
        IReadOnlyList<FcLogicalQueueEntry> queue)
    {
        if (remaining.Entries.FirstOrDefault() is { } demand)
            return new FcLogicalTarget(listId, demand.ItemId, null, demand.Quality);
        if (queue.FirstOrDefault() is { } recipe)
        {
            var result = RecipeManager.GetRecipe(recipe.RecipeId)?.ItemResult.RowId;
            return new FcLogicalTarget(listId, result, recipe.RecipeId, FcItemQuality.Nq);
        }
        return new FcLogicalTarget(null, null, null, null);
    }
}
