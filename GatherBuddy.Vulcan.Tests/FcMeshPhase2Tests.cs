using System;
using System.Collections.Generic;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.Vulcan.Tests;

internal static class FcMeshPhase2Tests
{
    internal static void Run(Action<bool, string> require)
    {
        var represented = new FakeQuantitySource((100u, FcItemQuality.Nq, 3_000));
        var demand = MaterialDemandBuilder.Build(100u, FcItemQuality.Nq, 10_000, represented);
        require(demand.RequiredRepresentedTotal == 10_000
                && demand.CurrentRepresented == 3_000
                && demand.Remaining == 7_000,
            "FC material demand must remain an absolute represented total with a checked deficit");

        var deficits = CraftingQueueProcessor.ComputeCurrentMaterialDeficits(
            new Dictionary<uint, int> { [100u] = 100 },
            new Dictionary<uint, IngredientQualityDemand>(),
            _ => (30, 0));
        require(deficits.GetValueOrDefault(100u) == 70,
            "material deficit calculation must consume represented supply exactly once");

        require(CraftingGatherBridge.ComputeGatherTargetQuantityForSource(
                    quantity: 100,
                    quantityIsDeficit: false,
                    isApprovedItem: true,
                    isFcFulfillment: true,
                    currentCount: 90,
                    batchSize: 64) == 100
                && CraftingGatherBridge.ComputeGatherTargetQuantityForSource(
                    quantity: 100,
                    quantityIsDeficit: false,
                    isApprovedItem: true,
                    isFcFulfillment: false,
                    currentCount: 90,
                    batchSize: 64) == 64
                && CraftingGatherBridge.ComputeGatherTargetQuantityForSource(
                    quantity: 7_000,
                    quantityIsDeficit: true,
                    isApprovedItem: true,
                    isFcFulfillment: true,
                    currentCount: 3_000,
                    batchSize: 64) == 10_000,
            "FC approved gather targets must remain absolute represented totals while private batching remains unchanged");

        var gatherOverflowRejected = false;
        try
        {
            _ = CraftingGatherBridge.ComputeGatherTargetQuantityForSource(
                quantity: int.MaxValue,
                quantityIsDeficit: false,
                isApprovedItem: true,
                isFcFulfillment: false,
                currentCount: 0,
                batchSize: 64);
        }
        catch (Exception ex) when (ex is OverflowException or InvalidOperationException)
        {
            gatherOverflowRejected = true;
        }
        require(gatherOverflowRejected,
            "approved gather batch rounding must reject an unrepresentable target instead of wrapping");

        var session = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var list = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var context = new FcExecutionContext(
            session,
            new[] { list },
            "world-a",
            new FcWorldRevision(1, "revision-a"));
        var fcPlanning = CraftingPlanningContext.CreateFc(represented, new FakeQuantitySource(), context);
        var plan = CraftingExecutionPlan.Create(new CraftingListDefinition { ID = 42 }, fcPlanning);
        require(plan.ExecutionSource == ExecutionSource.FcFulfillment
                && plan.FcContext == context
                && plan.WorldRevision?.Number == 1,
            "FC execution plan must retain its validated planning context");

        require(!plan.MarkWorldRevisionChanged(fcPlanning),
            "same FC world revision must not dirty a plan");

        var older = new CraftingPlanningContext(
            represented,
            fcPlanning.LocalConsumableInventory,
            ExecutionSource.FcFulfillment,
            new FcExecutionContext(session, new[] { list }, "world-a", new FcWorldRevision(0, "revision-old")));
        require(!plan.MarkWorldRevisionChanged(older),
            "older FC world revision must not dirty a plan");

        var otherSession = new CraftingPlanningContext(
            represented,
            fcPlanning.LocalConsumableInventory,
            ExecutionSource.FcFulfillment,
            new FcExecutionContext(
                Guid.Parse("00000000-0000-0000-0000-000000000003"),
                new[] { list },
                "world-a",
                new FcWorldRevision(2, "revision-other-session")));
        require(!plan.MarkWorldRevisionChanged(otherSession),
            "a different FC session must not dirty a plan");

        var newerContext = new CraftingPlanningContext(
            represented,
            fcPlanning.LocalConsumableInventory,
            ExecutionSource.FcFulfillment,
            new FcExecutionContext(session, new[] { list }, "world-a", new FcWorldRevision(2, "revision-b")));
        require(plan.MarkWorldRevisionChanged(newerContext) && plan.IsWorldRevisionDirty,
            "a newer relevant FC world revision must request a deferred replan");
        require(plan.ApplyPendingWorldRevisionAtSafeBoundary(
                    newerContext,
                    isCrafting: false,
                    isGatheringInteraction: false)
                && !plan.IsWorldRevisionDirty
                && plan.WorldRevision?.Number == 2,
            "safe-boundary FC replan must apply the new context and clear the request");

        var revisionThree = new CraftingPlanningContext(
            represented,
            fcPlanning.LocalConsumableInventory,
            ExecutionSource.FcFulfillment,
            new FcExecutionContext(session, new[] { list }, "world-a", new FcWorldRevision(3, "revision-c")));
        var revisionFour = new CraftingPlanningContext(
            represented,
            fcPlanning.LocalConsumableInventory,
            ExecutionSource.FcFulfillment,
            new FcExecutionContext(session, new[] { list }, "world-a", new FcWorldRevision(4, "revision-d")));
        require(plan.MarkWorldRevisionChanged(revisionThree)
                && plan.MarkWorldRevisionChanged(revisionFour)
                && plan.PendingWorldRevisionContext == revisionFour,
            "multiple newer FC updates must coalesce to the greatest pending context");
        require(!plan.ApplyPendingWorldRevisionAtSafeBoundary(
                    isCrafting: true,
                    isGatheringInteraction: false)
                && plan.IsWorldRevisionDirty
                && plan.WorldRevision?.Number == 2,
            "an active craft must retain the pending FC replan");
        require(plan.ApplyPendingWorldRevisionAtSafeBoundary(
                    isCrafting: false,
                    isGatheringInteraction: false)
                && !plan.IsWorldRevisionDirty
                && plan.WorldRevision?.Number == 4,
            "the safe boundary must apply the coalesced newest FC revision");

        var revisionFive = new CraftingPlanningContext(
            represented,
            fcPlanning.LocalConsumableInventory,
            ExecutionSource.FcFulfillment,
            new FcExecutionContext(session, new[] { list }, "world-a", new FcWorldRevision(5, "revision-e")));
        require(plan.MarkWorldRevisionChanged(revisionFive)
                && !plan.ApplyPendingWorldRevisionAtSafeBoundary(
                    isCrafting: false,
                    isGatheringInteraction: true)
                && plan.IsWorldRevisionDirty
                && plan.WorldRevision?.Number == 4,
            "an active gathering interaction must retain the pending FC replan");
        require(plan.ApplyPendingWorldRevisionAtSafeBoundary(
                    isCrafting: false,
                    isGatheringInteraction: false)
                && !plan.IsWorldRevisionDirty
                && plan.WorldRevision?.Number == 5,
            "the safe boundary must apply a pending replan after gathering ends");

        var privateTracking = CompletionTrackingPolicy.Create(100u, 0, null, null);
        var privateTrackingAgain = CompletionTrackingPolicy.Create(100u, 0, null, "private");
        var representedPrivateTracking = CompletionTrackingPolicy.Create(100u, 0, null, "private", "represented");
        var fcTrackingA = CompletionTrackingPolicy.Create(100u, 0, FcItemQuality.Nq, "fc:scope-a", "represented");
        var fcTrackingB = CompletionTrackingPolicy.Create(100u, 0, FcItemQuality.Nq, "fc:scope-b", "represented");
        var hqTracking = CompletionTrackingPolicy.Create(100u, 0, FcItemQuality.Hq, "fc:scope-a", "represented");
        require(CompletionTrackingPolicy.CanAggregate(privateTracking, privateTrackingAgain)
                && !CompletionTrackingPolicy.CanAggregate(privateTracking, representedPrivateTracking)
                && !CompletionTrackingPolicy.CanAggregate(privateTracking, fcTrackingA)
                && !CompletionTrackingPolicy.CanAggregate(privateTracking, fcTrackingB)
                && !CompletionTrackingPolicy.CanAggregate(fcTrackingA, fcTrackingB)
                && !CompletionTrackingPolicy.CanAggregate(fcTrackingA, hqTracking),
            "completion tracking must aggregate equivalent private entries while separating FC scopes and quality");
        require(CompletionTrackingPolicy.IsCompleteFor(fcTrackingA, fcTrackingA, 10, 10)
                && !CompletionTrackingPolicy.IsCompleteFor(fcTrackingA, fcTrackingB, 10, 10)
                && !CompletionTrackingPolicy.IsCompleteFor(fcTrackingA, hqTracking, 10, 10)
                && !CompletionTrackingPolicy.IsCompleteFor(
                    privateTracking,
                    representedPrivateTracking,
                    10,
                    10),
            "completion removal must recheck matching scope and quality before accepting a provider count");

        var provider = new FcCompletionCountProvider(represented);
        require(provider.GetCompletionCount(100u) == 3_000
                && provider.GetCompletionCount(100u, FcItemQuality.Nq) == 3_000
                && provider.GetCompletionCount(100u, FcItemQuality.Hq) == 0,
            "FC completion provider must use represented NQ/HQ quantities without summing workers");

        var physical = CraftingPlanningContext.CreatePrivate();
        var privatePlan = CraftingExecutionPlan.Create(new CraftingListDefinition { ID = 43 });
        require(privatePlan.ExecutionSource == ExecutionSource.PrivateList
                && privatePlan.FcContext == null
                && physical.FcContext == null,
            "private planning defaults must remain physical and free of FC context");

        var directPlan = CraftingExecutionPlan.CreateDirect(new CraftingListDefinition { ID = 44 });
        var directInventoryBeforeRefresh = directPlan.PlanningContext.LocalConsumableInventory;
        directPlan.RefreshFromCurrentInventory();
        var directInventoryAfterFirstRefresh = directPlan.PlanningContext.LocalConsumableInventory;
        directPlan.RefreshFromCurrentInventory();
        require(directInventoryBeforeRefresh is CraftingPhysicalInventorySource
                && directInventoryAfterFirstRefresh is CraftingPhysicalInventorySource
                && directPlan.PlanningContext.LocalConsumableInventory is CraftingPhysicalInventorySource
                && !ReferenceEquals(
                    directInventoryBeforeRefresh,
                    directInventoryAfterFirstRefresh)
                && !ReferenceEquals(
                    directInventoryAfterFirstRefresh,
                    directPlan.PlanningContext.LocalConsumableInventory),
            "direct refresh must retain inventory-independent planning while replacing the physical local snapshot source");

        var invalidListsRejected = false;
        try
        {
            _ = new FcExecutionContext(
                session,
                new[] { list, list },
                "world-a",
                new FcWorldRevision(1, "revision-a"));
        }
        catch (ArgumentException)
        {
            invalidListsRejected = true;
        }

        require(invalidListsRejected,
            "FC execution context must reject duplicate list subscriptions");
    }

    private sealed class FakeQuantitySource : IItemQuantitySource
    {
        private readonly Dictionary<(uint ItemId, FcItemQuality Quality), int> _values = new();

        public FakeQuantitySource(params (uint ItemId, FcItemQuality Quality, int Quantity)[] entries)
        {
            foreach (var (itemId, quality, quantity) in entries)
                _values.Add((itemId, quality), quantity);
        }

        public int GetNq(uint itemId)
            => _values.GetValueOrDefault((itemId, FcItemQuality.Nq));

        public int GetHq(uint itemId)
            => _values.GetValueOrDefault((itemId, FcItemQuality.Hq));
    }

}
