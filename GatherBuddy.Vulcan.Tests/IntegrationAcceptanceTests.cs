using System;
using System.Collections.Generic;
using System.Threading;
using GatherBuddy.Crafting;
using GatherBuddy.Crafting.Acquisition;

namespace GatherBuddy.Vulcan.Tests;

/// <summary>
/// End-to-end boundary fixtures for the queue's acquisition contract. These
/// stay game-independent: the live queue supplies the snapshot, and these
/// assertions verify the externally visible planner outcomes it must honor.
/// </summary>
internal static class IntegrationAcceptanceTests
{
    public static void Run(Action<bool, string> require)
    {
        var disabled = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[]
                {
                    new AcquisitionDependency
                    {
                        ItemId = 700,
                        ItemName = "Blocked dependency",
                        RequiredQuantity = 1,
                        SelectedPath = new AcquisitionPath
                        {
                            Kind = AcquisitionPathKind.Craft,
                            Capability = AcquisitionCapability.UnusablePath(
                                AcquisitionPathKind.Craft,
                                "No gearset is available."),
                        },
                    },
                },
            },
            new AcquisitionPlanningSettings());
        require(disabled.IsSuccess && disabled.SelectedPlan?.Transactions.Count == 0,
            "disabling automatic acquisition must preserve the existing gather/craft flow without purchases");

        var unresolved = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[]
                {
                    new AcquisitionDependency
                    {
                        ItemId = 701,
                        ItemName = "Unresolved dependency",
                        RequiredQuantity = 1,
                        SelectedPath = new AcquisitionPath
                        {
                            Kind = AcquisitionPathKind.Gather,
                            Capability = AcquisitionCapability.UnusablePath(
                                AcquisitionPathKind.Gather,
                                "No usable gathering path is available."),
                        },
                    },
                },
                GilBalance = 0,
            },
            new AcquisitionPlanningSettings { AutoPurchaseBlockedDependencies = true });
        require(!unresolved.IsSuccess && unresolved.SelectedPlan == null,
            "an unresolved dependency must stop before any queue craft can start");

        var noPurchaseRoute = AcquisitionRoutePlanner.Plan(
            new AcquisitionPlan(),
            new AcquisitionRouteInput { CurrentWorldId = 10, CurrentWorldName = "World A" });
        require(noPurchaseRoute.IsReady && noPurchaseRoute.Routes.Count == 0,
            "a complete no-purchase plan must not introduce market travel");

        var precraftCaps = new Dictionary<uint, int>
        {
            [800] = 4,
        };
        var finalOutputs = new HashSet<uint> { 801u };
        var acquired = new Dictionary<uint, AcquiredDependencyAvailability>
        {
            [800] = new(3, 0),
            [801] = new(3, 0),
            [802] = new(9, 0),
        };
        var overlay = CraftingExecutionPlan.FilterAcquiredAvailability(
            precraftCaps,
            finalOutputs,
            acquired);
        require(overlay.TryGetValue(800, out var purchasedPrecraft)
                && purchasedPrecraft.Total == 3,
            "verified purchased precraft availability must suppress the purchased quantity");
        var purchasedPrecraftRemaining = purchasedPrecraft
            .Consume(IngredientQualityDemand.FromPreferHQ(3), out _);
        require(purchasedPrecraftRemaining.Total == 0,
            "a fully acquired precraft demand must disappear from the generated precraft queue");
        require(!overlay.ContainsKey(801) && !overlay.ContainsKey(802),
            "final outputs and unrelated precrafts must not be suppressed by the acquisition overlay");

        var mixedDemandOverlay = CraftingExecutionPlan.FilterAcquiredAvailability(
            new Dictionary<uint, int> { [801] = 3 },
            finalOutputs,
            new Dictionary<uint, AcquiredDependencyAvailability>
            {
                [801] = new(2, 0),
            });
        require(mixedDemandOverlay.TryGetValue(801, out var mixedDemand)
                && mixedDemand.Total == 2,
            "an item also used as a final output must retain verified availability when it has an intermediate demand");

        var partialRemaining = new AcquiredDependencyAvailability(2, 0)
            .Consume(IngredientQualityDemand.FromPreferHQ(4), out var partialAvailability);
        require(partialRemaining.Total == 2 && partialAvailability.Total == 0,
            "partial acquired quantity must reduce, but not erase, the remaining precraft demand");

        var normalDemandRemaining = new AcquiredDependencyAvailability(0, 2)
            .Consume(IngredientQualityDemand.FromPreferHQ(2), out _);
        require(normalDemandRemaining.Total == 0,
            "HQ acquired quantity must satisfy ordinary quantity demand");

        var hardHqRemaining = new AcquiredDependencyAvailability(2, 0)
            .Consume(IngredientQualityDemand.FromRequiredHQ(2, 2), out _);
        require(hardHqRemaining.RequiredHQ == 2,
            "NQ acquired quantity must not satisfy a hard HQ demand");

        var hardHqSatisfied = new AcquiredDependencyAvailability(0, 2)
            .Consume(IngredientQualityDemand.FromRequiredHQ(2, 2), out _);
        require(hardHqSatisfied.Total == 0,
            "HQ acquired quantity must satisfy a hard HQ demand");

        var hardNqRemaining = new AcquiredDependencyAvailability(0, 2)
            .Consume(IngredientQualityDemand.FromRequireNQOnly(2), out _);
        require(hardNqRemaining.RequiredNQ == 2,
            "HQ acquired quantity must not satisfy a hard NQ demand");

        var hardNqSatisfied = new AcquiredDependencyAvailability(2, 0)
            .Consume(IngredientQualityDemand.FromRequireNQOnly(2), out _);
        require(hardNqSatisfied.Total == 0,
            "NQ acquired quantity must satisfy a hard NQ demand");

        var hqDeficit = AcquisitionPlanningInputBuilder.ComputeMissingQuantities(
            requiredQuantity: 5,
            requiredHqQuantity: 3,
            requiredNqQuantity: 0,
            inventoryNq: 5,
            inventoryHq: 0);
        require(hqDeficit.Total == 3 && hqDeficit.HQ == 3,
            "an HQ deficit must remain purchasable even when total inventory meets quantity");

        var nqDeficit = AcquisitionPlanningInputBuilder.ComputeMissingQuantities(
            requiredQuantity: 1,
            requiredHqQuantity: 0,
            requiredNqQuantity: 1,
            inventoryNq: 0,
            inventoryHq: 1);
        require(nqDeficit.Total == 1 && nqDeficit.NQ == 1,
            "an HQ item must not satisfy a hard NQ requirement when total inventory meets quantity");

        var nqDependency = new AcquisitionDependency
        {
            ItemId = 903,
            ItemName = "Required NQ material",
            RequiredQuantity = 1,
            RequiredNqQuantity = 1,
            SelectedPath = new AcquisitionPath
            {
                Kind = AcquisitionPathKind.Craft,
                Capability = AcquisitionCapability.UnusablePath(
                    AcquisitionPathKind.Craft,
                    "The selected class or route is unavailable."),
            },
        };
        var hqOnlyNqPlan = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { nqDependency },
                MarketListings = new[]
                {
                    new AcquisitionMarketListing
                    {
                        ItemId = 903,
                        ListingId = 1,
                        WorldId = 10,
                        Quantity = 1,
                        PricePerUnit = 1,
                        IsHq = true,
                    },
                },
                GilBalance = 1,
                CurrentWorldId = 10,
            },
            new AcquisitionPlanningSettings { AutoPurchaseBlockedDependencies = true });
        require(!hqOnlyNqPlan.IsSuccess,
            "an HQ-only source must block a required-NQ acquisition");

        var nqOnlyPlan = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { nqDependency },
                MarketListings = new[]
                {
                    new AcquisitionMarketListing
                    {
                        ItemId = 903,
                        ListingId = 2,
                        WorldId = 10,
                        Quantity = 1,
                        PricePerUnit = 1,
                        IsHq = false,
                    },
                },
                GilBalance = 1,
                CurrentWorldId = 10,
            },
            new AcquisitionPlanningSettings { AutoPurchaseBlockedDependencies = true });
        var hasRequiredNqPlanDemand = nqOnlyPlan.SelectedPlan != null
            && nqOnlyPlan.SelectedPlan.RequiredNqQuantities.TryGetValue(903, out var plannedRequiredNq)
            && plannedRequiredNq == 1;
        require(nqOnlyPlan.IsSuccess && hasRequiredNqPlanDemand,
            "an NQ source must satisfy and preserve the required-NQ acquisition demand");

        var requiredNqDemand = IngredientQualityDemand.FromRequireNQOnly(1);
        var nqGatherDeficit = CraftingQueueProcessor.ComputeCurrentMaterialDeficits(
            new Dictionary<uint, int> { [903] = 1 },
            new Dictionary<uint, IngredientQualityDemand> { [903] = requiredNqDemand },
            _ => (NQ: 0, HQ: 1));
        var hasNqGatherDeficit = nqGatherDeficit.TryGetValue(903, out var gatheredNqDeficit)
            && gatheredNqDeficit == 1;
        require(hasNqGatherDeficit,
            "gathering must retain a deficit when only HQ is available for a hard NQ demand");
        require(!CraftingGatherBridge.IsGatheringItemComplete(1, requiredNqDemand, 0, 1)
                && CraftingGatherBridge.IsGatheringItemComplete(1, requiredNqDemand, 1, 0),
            "gather completion must reject HQ-only stock for required NQ and accept NQ stock");

        var generations = new AcquisitionRunGenerationGate();
        require(generations.TryBeginRun(out var firstGeneration),
            "the first acquisition generation must start");
        require(generations.TryBeginDrain(firstGeneration, out var firstCompletion)
                && firstCompletion != null
                && !generations.IsReadyToBegin()
                && !generations.TryBeginRun(out _),
            "a replacement acquisition must wait for the previous drain gate");
        require(generations.TryReleaseActive(firstGeneration)
                && generations.TryCompleteDrain(firstGeneration, firstCompletion!),
            "the captured generation must release before its drain gate completes");
        require(generations.TryBeginRun(out var secondGeneration),
            "a replacement acquisition may start only after drain completion");
        require(!generations.TryReleaseActive(firstGeneration)
                && generations.IsCurrent(secondGeneration)
                && !generations.TryCompleteDrain(firstGeneration, firstCompletion!),
            "late cleanup from an old generation must not clear or complete the new run");

        var canceledDispatch = new FrameworkDispatchGate<int>();
        require(canceledDispatch.TryCancel()
                && !canceledDispatch.TryClaim()
                && !canceledDispatch.TryComplete(42),
            "a canceled framework dispatch must reject a late callback before it can mutate or complete stale state");
        var supersededDispatch = new FrameworkDispatchGate<int>();
        var claimedDispatch = new FrameworkDispatchGate<int>();
        require(!supersededDispatch.TryClaim()
                && claimedDispatch.TryClaim()
                && claimedDispatch.TryCancel()
                && !claimedDispatch.TryComplete(42)
                && claimedDispatch.Completion.IsCanceled,
            "a stale framework generation must reject its callback and cancellation must prevent a claimed callback from publishing");
        var claimedStaleDispatch = new FrameworkDispatchGate<int>();
        require(claimedStaleDispatch.TryClaim(),
            "the framework dispatch must claim before evaluating its state");
        var completedDispatch = new FrameworkDispatchGate<int>();
        require(!claimedStaleDispatch.TryComplete(42)
                && claimedStaleDispatch.Completion.IsCanceled
                && completedDispatch.TryClaim()
                && completedDispatch.TryComplete(42)
                && completedDispatch.Completion.GetAwaiter().GetResult() == 42,
            "a superseded claimed generation must not publish while the current generation completes exactly once");

        using var callbackCancellation = new CancellationTokenSource();
        var canceledCallbackDispatch = new FrameworkDispatchGate<int>();
        require(canceledCallbackDispatch.TryClaim(),
            "a framework callback must claim before its cancellation gate is evaluated");
        callbackCancellation.Cancel();
        require(!canceledCallbackDispatch.TryComplete(42, callbackCancellation.Token)
                && canceledCallbackDispatch.TryCancel(callbackCancellation.Token)
                && canceledCallbackDispatch.Completion.IsCanceled,
            "a claimed callback observing cancellation must not publish a refresh or replan result");

        var postRetainerTargets = new Dictionary<uint, int>
        {
            [900] = 10,
            [901] = 5,
        };
        var postRetainerInventory = new Dictionary<uint, (int NQ, int HQ)>
        {
            [900] = (6, 1),
            [901] = (5, 0),
            [902] = (5, 0),
        };
        var postRetainerDemands = new Dictionary<uint, IngredientQualityDemand>
        {
            [902] = IngredientQualityDemand.FromRequiredHQ(3, 5),
        };
        var postRetainerDeficits = CraftingQueueProcessor.ComputeCurrentMaterialDeficits(
            postRetainerTargets,
            new Dictionary<uint, IngredientQualityDemand>(),
            itemId => postRetainerInventory.TryGetValue(itemId, out var counts)
                ? counts
                : (NQ: 0, HQ: 0));
        var hasExpectedRawDeficit = postRetainerDeficits.TryGetValue(900, out var rawDeficit)
            && rawDeficit == 3;
        require(hasExpectedRawDeficit
                && !postRetainerDeficits.ContainsKey(901),
            "post-retainer raw-material gathering must use current NQ+HQ deficits");

        var postRetainerHqDeficits = CraftingQueueProcessor.ComputeCurrentMaterialDeficits(
            new Dictionary<uint, int> { [902] = 5 },
            postRetainerDemands,
            itemId => postRetainerInventory.TryGetValue(itemId, out var counts)
                ? counts
                : (NQ: 0, HQ: 0));
        var hasExpectedHqDeficit = postRetainerHqDeficits.TryGetValue(902, out var remainingHqDeficit)
            && remainingHqDeficit == 3;
        require(hasExpectedHqDeficit,
            "NQ retainer stock must not hide a remaining hard HQ deficit");
    }
}
