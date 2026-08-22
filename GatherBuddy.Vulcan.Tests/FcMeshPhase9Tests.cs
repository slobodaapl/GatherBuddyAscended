using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Sessions;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.Vulcan.Tests;

internal static class FcMeshPhase9Tests
{
    public static void Run(Action<bool, string> require)
    {
        GatherIntentPrefersAnUnoccupiedActionableTarget(require);
        GatherIntentFailsOpenWhenNoUsableAlternativeExists(require);
        CrossModeIntentFieldsDoNotCreateFalseOccupancy(require);
        CraftIntentAvoidsAVisibleFrontWithoutBreakingDependencies(require);
        CraftIntentPreservesRepeatedEntriesAndFallbackOrder(require);
        FcQueueBoundaryConsumesTheAdvisoryOrder(require);
        FreshIntentReordersOnlyTheUnstartedQueueSuffix(require);
        FreshIntentChangesTheNextGatherTargetOnly(require);
        RecipeFactsFailurePreservesTheIncumbentOrder(require);
        PrivatePlanDoesNotConsumeWorkerIntent(require);
        ProjectedIntentProviderRequiresExactSessionBinding(require);
        RuntimeIntentBoundariesUseFreshScopedProjection(require);
    }

    private static void GatherIntentPrefersAnUnoccupiedActionableTarget(
        Action<bool, string> require)
    {
        var local = Worker(
            "phase9-local",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-world");
        var remote = Worker(
            "phase9-remote",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-world",
            currentTarget: new FcLogicalTarget(null, 100u, null, FcItemQuality.Nq),
            gatherTargetOrder: [100]);

        var order = FcIntentTieBreaker.PreferGatherOrder(
            [100, 200],
            [remote],
            local);

        require(order.SequenceEqual([200u, 100u]),
            "fresh compatible remote gather intent prefers the currently free alternative");
        require(FcIntentTieBreaker.PreferGatherOrder([100, 200], [], local).Length == 0,
            "no visible intent preserves the existing local route order");
    }

    private static void GatherIntentFailsOpenWhenNoUsableAlternativeExists(
        Action<bool, string> require)
    {
        var local = Worker(
            "phase9-local-fallback",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-world");
        var occupied = Worker(
            "phase9-occupied",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-world",
            currentTarget: new FcLogicalTarget(null, 100u, null, FcItemQuality.Nq),
            gatherTargetOrder: [100]);
        var stale = Worker(
            "phase9-stale",
            FcWorkerState.Waiting,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-world",
            currentTarget: new FcLogicalTarget(null, 200u, null, FcItemQuality.Nq),
            gatherTargetOrder: [200]);
        var incompatible = Worker(
            "phase9-incompatible",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "other-world",
            currentTarget: new FcLogicalTarget(null, 200u, null, FcItemQuality.Nq),
            gatherTargetOrder: [200]);

        require(FcIntentTieBreaker.PreferGatherOrder([100], [occupied], local).Length == 0,
            "one remaining candidate cannot be excluded by intent");
        require(FcIntentTieBreaker.PreferGatherOrder([100, 200], [occupied, stale], local)
                .SequenceEqual([200u, 100u]),
            "waiting intent does not occupy a target");
        require(FcIntentTieBreaker.PreferGatherOrder([100, 200], [occupied, incompatible], local)
                .SequenceEqual([200u, 100u]),
            "incompatible intent does not occupy a target");
        require(FcIntentTieBreaker.PreferGatherOrder([100, 200], [occupied, Worker(
                "phase9-other-occupied",
                FcWorkerState.Active,
                FcFulfillmentSelection.All,
                worldFingerprint: "phase9-world",
                currentTarget: new FcLogicalTarget(null, 200u, null, FcItemQuality.Nq),
                gatherTargetOrder: [200])], local).Length == 0,
            "all visible actionable alternatives occupied keeps the normal route fallback");
    }

    private static void CrossModeIntentFieldsDoNotCreateFalseOccupancy(
        Action<bool, string> require)
    {
        var local = Worker(
            "phase9-cross-local",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-cross-world");
        var gatheringWorker = Worker(
            "phase9-cross-gathering",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-cross-world",
            currentTarget: new FcLogicalTarget(null, 100u, null, FcItemQuality.Nq),
            gatherTargetOrder: [100u],
            craftQueue:
            [
                new FcLogicalQueueEntry(10u, 1, null),
                new FcLogicalQueueEntry(30u, 1, null),
            ]);
        var gatherWorkerCraftOrder = FcIntentTieBreaker.PreferCraftQueue(
            [Candidate(10u), Candidate(30u), Candidate(20u)],
            [gatheringWorker],
            local);
        require(gatherWorkerCraftOrder[0].Item.RecipeId == 30u,
            "a gathering worker's planned craft queue contributes tail preference without occupying its front");

        var craftingWorker = Worker(
            "phase9-cross-crafting",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-cross-world",
            currentTarget: new FcLogicalTarget(null, null, 10u, FcItemQuality.Nq),
            gatherTargetOrder: [100u]);
        require(FcIntentTieBreaker.PreferGatherOrder(
                    [100u, 200u],
                    [craftingWorker],
                    local).Length == 0,
            "a crafting worker's planned gather order does not occupy a gather target");

        var plannedOnlyWorker = Worker(
            "phase9-cross-planned",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-cross-world",
            gatherTargetOrder: [100u],
            craftQueue: [new FcLogicalQueueEntry(10u, 1, null)]);
        require(FcIntentTieBreaker.PreferGatherOrder(
                    [100u, 200u],
                    [plannedOnlyWorker],
                    local).Length == 0
                && FcIntentTieBreaker.PreferCraftQueue(
                    [Candidate(10u), Candidate(20u)],
                    [plannedOnlyWorker],
                    local)
                    .Select(candidate => candidate.Item.RecipeId)
                    .SequenceEqual([10u, 20u]),
            "null current target leaves planned queues advisory without active occupancy");
    }

    private static void CraftIntentAvoidsAVisibleFrontWithoutBreakingDependencies(
        Action<bool, string> require)
    {
        var local = Worker(
            "phase9-craft-local",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-craft-world");
        var remote = Worker(
            "phase9-craft-remote",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-craft-world",
            currentTarget: new FcLogicalTarget(null, null, 10u, FcItemQuality.Nq),
            craftQueue: [new FcLogicalQueueEntry(10, 1, null)]);
        var independentFront = new FcIntentTieBreaker.CraftQueueCandidate(
            new CraftingListItem(10, 1),
            8,
            new HashSet<uint>(),
            true);
        var independentAlternative = new FcIntentTieBreaker.CraftQueueCandidate(
            new CraftingListItem(20, 1),
            8,
            new HashSet<uint>(),
            true);

        var reordered = FcIntentTieBreaker.PreferCraftQueue(
            [independentFront, independentAlternative],
            [remote],
            local);
        require(reordered.Select(candidate => candidate.Item.RecipeId).SequenceEqual([20u, 10u]),
            "fresh compatible craft intent prefers an independent non-front recipe");

        var dependency = new FcIntentTieBreaker.CraftQueueCandidate(
            new CraftingListItem(30, 1),
            8,
            new HashSet<uint> { 20 },
            true);
        var dependencyRemote = remote with
        {
            CraftQueue = [new FcLogicalQueueEntry(30, 1, null)],
        };
        var dependencyOrder = FcIntentTieBreaker.PreferCraftQueue(
            [independentAlternative, dependency],
            [dependencyRemote],
            local);
        require(dependencyOrder.Select(candidate => candidate.Item.RecipeId).SequenceEqual([20u, 30u]),
            "a dependent recipe remains behind its independent prerequisite");
    }

    private static void CraftIntentPreservesRepeatedEntriesAndFallbackOrder(
        Action<bool, string> require)
    {
        var local = Worker(
            "phase9-repeat-local",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-repeat-world");
        var remote = Worker(
            "phase9-repeat-remote",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-repeat-world",
            currentTarget: new FcLogicalTarget(null, null, 10u, FcItemQuality.Nq),
            craftQueue: [new FcLogicalQueueEntry(10, 1, null)]);
        var queue = new[]
        {
            Candidate(10),
            Candidate(20),
            Candidate(20),
        };
        var reordered = FcIntentTieBreaker.PreferCraftQueue(queue, [remote], local);
        require(reordered.Select(candidate => candidate.Item.RecipeId).SequenceEqual([20u, 20u, 10u]),
            "intent reordering preserves repeated queue quantities");

        var noIntent = FcIntentTieBreaker.PreferCraftQueue(queue, [], local);
        require(noIntent.Select(candidate => candidate.Item.RecipeId).SequenceEqual([10u, 20u, 20u]),
            "no visible intent preserves deterministic existing queue order");

        var tailRemote = remote with
        {
            CraftQueue =
            [
                new FcLogicalQueueEntry(10, 1, null),
                new FcLogicalQueueEntry(30, 1, null),
            ],
        };
        var tailQueue = new[]
        {
            Candidate(10),
            Candidate(20),
            Candidate(30),
        };
        var tailOrder = FcIntentTieBreaker.PreferCraftQueue(tailQueue, [tailRemote], local);
        require(tailOrder.Select(candidate => candidate.Item.RecipeId).SequenceEqual([30u, 20u, 10u]),
            "a newly joining worker may prefer a visible independent queue tail without creating a claim");
    }

    private static void FcQueueBoundaryConsumesTheAdvisoryOrder(
        Action<bool, string> require)
    {
        var local = Worker(
            "phase9-plan-local",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-plan-world");
        var remote = Worker(
            "phase9-plan-remote",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-plan-world",
            currentTarget: new FcLogicalTarget(null, null, 38_247u, FcItemQuality.Nq),
            craftQueue: [new FcLogicalQueueEntry(38_247, 1, null)]);
        var plan = CraftingExecutionPlan.CreateRecovery(
            [new CraftingListItem(38_247, 1), new CraftingListItem(5_630, 1)],
            new FcExecutionContext(
                local.SessionId,
                [Guid.Parse("00000000-0000-0000-0000-000000000901")],
                "phase9-plan-world",
                new FcWorldRevision(1, "phase9-plan-world"),
                null),
            RecoveryPlan);

        plan.ApplyFcIntentQueueOrder([remote], local, new FixtureCraftQueueFactsResolver(
            new Dictionary<uint, FcIntentTieBreaker.CraftQueueRecipeFacts>
            {
                [38_247u] = new(8u, 10_001u, Array.Empty<uint>()),
                [5_630u] = new(8u, 10_002u, Array.Empty<uint>()),
            }));
        require(plan.ExecutionSource == ExecutionSource.FcFulfillment
                && plan.QueueView.Count == 2
                && plan.QueueView.Select(item => item.RecipeId).SequenceEqual([5_630u, 38_247u]),
            "the FC execution-plan queue boundary consumes visible intent without changing queue membership");
        plan.RefreshFromCurrentInventory();
        require(plan.QueueView.Select(item => item.RecipeId).SequenceEqual([5_630u, 38_247u]),
            "a safe existing-plan refresh retains the advisory order until the next FC world replan");
    }

    private static void FreshIntentReordersOnlyTheUnstartedQueueSuffix(
        Action<bool, string> require)
    {
        var local = Worker(
            "phase9-fresh-craft-local",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-fresh-craft-world");
        var remote = Worker(
            "phase9-fresh-craft-remote",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-fresh-craft-world");
        var plan = CraftingExecutionPlan.CreateRecovery(
            [
                new CraftingListItem(10u, 1),
                new CraftingListItem(20u, 1),
                new CraftingListItem(30u, 1),
            ],
            new FcExecutionContext(
                local.SessionId,
                [Guid.Parse("00000000-0000-0000-0000-000000000903")],
                "phase9-fresh-craft-world",
                new FcWorldRevision(1, "phase9-fresh-craft-world"),
                null),
            RecoveryPlan);
        plan.ApplyFcIntentQueueOrder(
            [remote],
            local,
            new FixtureCraftQueueFactsResolver(
                new Dictionary<uint, FcIntentTieBreaker.CraftQueueRecipeFacts>
                {
                    [10u] = new(8u, 10_010u, Array.Empty<uint>()),
                    [20u] = new(8u, 10_020u, Array.Empty<uint>()),
                    [30u] = new(8u, 10_030u, Array.Empty<uint>()),
                }));
        var provider = new MutableIntentSnapshotProvider();
        plan.BindFcIntentSnapshotProvider(provider);

        provider.Current = Snapshot(
            local,
            remote with
            {
                CurrentTarget = new FcLogicalTarget(null, null, 20u, FcItemQuality.Nq),
            });
        require(plan.RefreshFcIntentOrderAtSafeBoundary(1)
                && plan.QueueView.Select(item => item.RecipeId).SequenceEqual([10u, 30u, 20u]),
            "fresh craft intent reorders the next suffix while preserving the completed prefix");

        provider.Current = Snapshot(
            local,
            remote with
            {
                CurrentTarget = new FcLogicalTarget(null, null, 30u, FcItemQuality.Nq),
            });
        require(plan.RefreshFcIntentOrderAtSafeBoundary(1)
                && plan.QueueView.Select(item => item.RecipeId).SequenceEqual([10u, 20u, 30u]),
            "an intent change at the next craft boundary is observed without interrupting the prefix");

        provider.Current = null;
        require(!plan.RefreshFcIntentOrderAtSafeBoundary(1)
                && plan.QueueView.Select(item => item.RecipeId).SequenceEqual([10u, 20u, 30u]),
            "missing current intent retains the incumbent suffix order");
    }

    private static void FreshIntentChangesTheNextGatherTargetOnly(
        Action<bool, string> require)
    {
        var local = Worker(
            "phase9-fresh-gather-local",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-fresh-gather-world");
        var remote = Worker(
            "phase9-fresh-gather-remote",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-fresh-gather-world",
            currentTarget: new FcLogicalTarget(null, 100u, null, FcItemQuality.Nq));
        var plan = CraftingExecutionPlan.CreateRecovery(
            [new CraftingListItem(10u, 1), new CraftingListItem(20u, 1)],
            new FcExecutionContext(
                local.SessionId,
                [Guid.Parse("00000000-0000-0000-0000-000000000904")],
                "phase9-fresh-gather-world",
                new FcWorldRevision(1, "phase9-fresh-gather-world"),
                null),
            RecoveryPlan);
        plan.SetFcIntentGatherCandidates([100u, 200u]);
        var provider = new MutableIntentSnapshotProvider
        {
            Current = Snapshot(local, remote),
        };
        plan.BindFcIntentSnapshotProvider(provider);

        var currentInteractionTarget = 100u;
        require(plan.GetCurrentFcGatherTargetOrder().SequenceEqual([200u, 100u])
                && currentInteractionTarget == 100u,
            "the current gather interaction remains unchanged while the next target prefers a free item");

        provider.Current = Snapshot(
            local,
            remote with
            {
                CurrentTarget = new FcLogicalTarget(null, 200u, null, FcItemQuality.Nq),
            });
        require(plan.GetCurrentFcGatherTargetOrder().SequenceEqual([100u, 200u])
                && currentInteractionTarget == 100u,
            "a changed remote gather intent affects only the next target selection");

        plan.ClearFcIntentSnapshotProvider();
        require(plan.GetCurrentFcGatherTargetOrder().Length == 0,
            "teardown clears the character-scoped gather intent provider");
    }

    private static void PrivatePlanDoesNotConsumeWorkerIntent(
        Action<bool, string> require)
    {
        var local = Worker(
            "phase9-private-local",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-private-world");
        var remote = Worker(
            "phase9-private-remote",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-private-world",
            craftQueue: [new FcLogicalQueueEntry(38_247, 1, null)]);
        var plan = CraftingExecutionPlan.CreateRecovery(
            [new CraftingListItem(38_247, 1), new CraftingListItem(5_630, 1)],
            null,
            RecoveryPlan);
        plan.ApplyFcIntentQueueOrder([remote], local);
            require(plan.ExecutionSource == ExecutionSource.PrivateList
                && plan.QueueView.Select(item => item.RecipeId).SequenceEqual([38_247u, 5_630u]),
            "private queue order remains unchanged when FC intent is visible");
    }

    private static void ProjectedIntentProviderRequiresExactSessionBinding(
        Action<bool, string> require)
    {
        var local = Worker(
            "phase9-provider-local",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-provider-world");
        var currentLocal = local;
        var projectionClock = new FcManualClock(1_000);
        var projectionStore = new FcWorldStore(projectionClock);
        var localEnvelope = FcMeshRecord.Create(
            local,
            local.Header.OwnerAuthorId,
            new FcHlcTimestamp(1_000, 0, local.Header.OwnerAuthorId),
            local.SessionGeneration,
            local.Header.Revision,
            local.Header.RecordType,
            local.Header.RecordId.ToString("D")) with
        {
            Signature = "phase9-fixture-signature",
            SignatureAlgorithm = "phase9-fixture-algorithm",
        };
        var localApply = projectionStore.Apply(
            localEnvelope,
            local,
            FcVerifiedMeshContext.FromEnvelope(localEnvelope) with
            {
                SignatureValid = true,
                TransportAuthorId = local.Header.OwnerAuthorId,
            });
        var currentWorld = new FcWorldProjection(projectionStore).Build(projectionClock);
        var currentDiagnostics = Diagnostics(local, "phase9-provider-scope");
        using var provider = new FcProjectedIntentSnapshotProvider(
            () => currentWorld,
            () => currentLocal,
            () => currentDiagnostics,
            "phase9-provider-scope",
            local.Header.OwnerAuthorId,
            local.WorldFingerprint);

        require(provider.TryGetCurrent(out var initial)
                && localApply.Accepted
                && initial.LocalWorker.SessionId == local.SessionId
                && initial.ActiveWorkers.Count == 1,
            "production intent provider accepts the exact current subscribed session");

        projectionClock.Set(301_000);
        currentWorld = new FcWorldProjection(projectionStore).Build(projectionClock);
        require(!provider.TryGetCurrent(out _),
            "the actual liveness projection excludes an expired local worker");
        projectionClock.Set(1_000);
        currentWorld = new FcWorldProjection(projectionStore).Build(projectionClock);

        currentLocal = local with { SessionGeneration = local.SessionGeneration + 1 };
        require(!provider.TryGetCurrent(out _),
            "a newer local session generation cannot reuse the prior intent provider");
        currentLocal = local;

        currentLocal = local with { SessionId = Guid.NewGuid() };
        require(!provider.TryGetCurrent(out _),
            "a different local session cannot reuse the prior intent provider");
        currentLocal = local;

        currentDiagnostics = currentDiagnostics with { SessionId = Guid.NewGuid() };
        require(!provider.TryGetCurrent(out _),
            "diagnostics from an older session cannot supply current intent");
        currentDiagnostics = Diagnostics(local, "phase9-provider-scope");

        currentDiagnostics = currentDiagnostics with { SessionGeneration = local.SessionGeneration + 1 };
        require(!provider.TryGetCurrent(out _),
            "a diagnostics generation mismatch fails closed");
        currentDiagnostics = Diagnostics(local, "phase9-provider-scope");

        currentDiagnostics = currentDiagnostics with { Revision = local.Header.Revision + 1 };
        require(!provider.TryGetCurrent(out _),
            "stale local revision evidence cannot provide intent");
        currentDiagnostics = Diagnostics(local, "phase9-provider-scope");

        currentDiagnostics = currentDiagnostics with { AuthorId = "phase9-other-author" };
        require(!provider.TryGetCurrent(out _),
            "an author mismatch cannot reuse local intent");
        currentDiagnostics = Diagnostics(local, "phase9-provider-scope");

        currentDiagnostics = currentDiagnostics with { AuthorScope = "phase9-other-scope" };
        require(!provider.TryGetCurrent(out _),
            "a character scope mismatch cannot reuse local intent");
        currentDiagnostics = Diagnostics(local, "phase9-provider-scope");

        currentLocal = local with { State = FcWorkerState.Unsubscribed };
        currentDiagnostics = Diagnostics(currentLocal, "phase9-provider-scope");
        require(!provider.TryGetCurrent(out _),
            "an unsubscribed local worker cannot provide intent");
        currentLocal = local;
        currentDiagnostics = Diagnostics(local, "phase9-provider-scope") with { RecoveryRequired = true };
        require(!provider.TryGetCurrent(out _),
            "a recovery-required local session cannot provide intent");
        currentDiagnostics = Diagnostics(local, "phase9-provider-scope") with { Forked = true };
        require(!provider.TryGetCurrent(out _),
            "a forked local session cannot provide intent");
        currentDiagnostics = Diagnostics(local, "phase9-provider-scope");

        currentLocal = local with { WorldFingerprint = "phase9-other-world" };
        currentDiagnostics = Diagnostics(currentLocal, "phase9-provider-scope");
        require(!provider.TryGetCurrent(out _),
            "a local worker world mismatch cannot reuse the provider");
        currentLocal = local;
        currentDiagnostics = Diagnostics(local, "phase9-provider-scope");
        currentLocal = local with
        {
            Header = local.Header with { Revision = local.Header.Revision + 1 },
        };
        currentDiagnostics = Diagnostics(currentLocal, "phase9-provider-scope");
        require(!provider.TryGetCurrent(out _),
            "a local worker revision mismatch cannot reuse the provider");
        currentLocal = local;
        currentDiagnostics = Diagnostics(local, "phase9-provider-scope");
        var forkedLocal = local with
        {
            CurrentTarget = new FcLogicalTarget(null, 100u, null, FcItemQuality.Nq),
        };
        var forkEnvelope = FcMeshRecord.Create(
            forkedLocal,
            forkedLocal.Header.OwnerAuthorId,
            new FcHlcTimestamp(1_001, 0, forkedLocal.Header.OwnerAuthorId),
            forkedLocal.SessionGeneration,
            forkedLocal.Header.Revision,
            forkedLocal.Header.RecordType,
            forkedLocal.Header.RecordId.ToString("D")) with
        {
            Signature = "phase9-fixture-fork-signature",
            SignatureAlgorithm = "phase9-fixture-algorithm",
        };
        var forkApply = projectionStore.Apply(
            forkEnvelope,
            forkedLocal,
            FcVerifiedMeshContext.FromEnvelope(forkEnvelope) with
            {
                SignatureValid = true,
                TransportAuthorId = forkedLocal.Header.OwnerAuthorId,
            });
        currentWorld = new FcWorldProjection(projectionStore).Build(projectionClock);
        require(!provider.TryGetCurrent(out _),
            "the actual forked local projection cannot supply current intent");
        require(forkApply.Status == FcApplyStatus.Fork,
            "the provider fork fixture records same-revision divergent evidence");
        currentWorld = ProjectedWorld(local, local);

        provider.Dispose();
        require(!provider.TryGetCurrent(out _),
            "a disposed character-scoped provider cannot provide intent");
    }

    private static void RuntimeIntentBoundariesUseFreshScopedProjection(
        Action<bool, string> require)
    {
        var local = Worker(
            "phase9-runtime-local",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-runtime-world");
        var remote = Worker(
            "phase9-runtime-remote",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-runtime-world",
            currentTarget: new FcLogicalTarget(null, null, 20u, FcItemQuality.Nq));
        var currentWorld = ProjectedWorld(local, local, remote);
        var craftReads = 0;
        FcProjectedWorld CurrentWorldProvider()
        {
            craftReads++;
            return currentWorld;
        }
        var currentDiagnostics = Diagnostics(local, "phase9-runtime-scope");
        using var provider = new FcProjectedIntentSnapshotProvider(
            CurrentWorldProvider,
            () => local,
            () => currentDiagnostics,
            "phase9-runtime-scope",
            local.Header.OwnerAuthorId,
            local.WorldFingerprint);
        var plan = CraftingExecutionPlan.CreateRecovery(
            [
                new CraftingListItem(10u, 1),
                new CraftingListItem(20u, 1),
                new CraftingListItem(30u, 1),
            ],
            new FcExecutionContext(
                local.SessionId,
                [Guid.Parse("00000000-0000-0000-0000-000000000905")],
                "phase9-runtime-world",
                new FcWorldRevision(1, "phase9-runtime-world"),
                null),
            RecoveryPlan);
        plan.ApplyFcIntentQueueOrder(
            [remote],
            local,
            new FixtureCraftQueueFactsResolver(
                new Dictionary<uint, FcIntentTieBreaker.CraftQueueRecipeFacts>
                {
                    [10u] = new(8u, 10_010u, Array.Empty<uint>()),
                    [20u] = new(8u, 10_020u, Array.Empty<uint>()),
                    [30u] = new(8u, 10_030u, Array.Empty<uint>()),
                }));
        plan.BindFcIntentSnapshotProvider(provider);

        using var processor = new CraftingQueueProcessor();
        processor.StartNextCraftAtBoundaryForTest(plan, 1);
        require(plan.QueueView.Select(item => item.RecipeId).SequenceEqual([10u, 30u, 20u])
                && processor.CurrentState == CraftingQueueProcessor.QueueState.Idle
                && craftReads == 1,
            "the production StartNextCraft boundary consumes fresh intent after its immutable prefix");

        currentWorld = ProjectedWorld(
            local,
            local,
            remote with
            {
                CurrentTarget = new FcLogicalTarget(null, null, 30u, FcItemQuality.Nq),
            });
        processor.StartNextCraftAtBoundaryForTest(plan, 1);
        require(plan.QueueView.Select(item => item.RecipeId).SequenceEqual([10u, 20u, 30u])
                && craftReads == 2,
            "the next production StartNextCraft boundary reads changed remote intent");

        currentDiagnostics = currentDiagnostics with { SessionGeneration = local.SessionGeneration + 1 };
        processor.StartNextCraftAtBoundaryForTest(plan, 1);
        require(plan.QueueView.Select(item => item.RecipeId).SequenceEqual([10u, 20u, 30u])
                && craftReads == 2,
            "a provider session mismatch leaves the production craft incumbent untouched");
        currentDiagnostics = Diagnostics(local, "phase9-runtime-scope");

        plan.SetFcIntentGatherCandidates([100u, 200u]);
        currentWorld = ProjectedWorld(
            local,
            local,
            remote with
            {
                CurrentTarget = new FcLogicalTarget(null, 100u, null, FcItemQuality.Nq),
            });
        var appliedOrder = Array.Empty<uint>();
        var gatherReads = 0;
        IReadOnlyList<uint>? GatherOrderProvider()
        {
            gatherReads++;
            return plan.GetCurrentFcGatherTargetOrder();
        }
        ActiveItemList.RefreshFcIntentOrderAtSelectionBoundary(
            GatherOrderProvider,
            hasCurrentGatherTarget: true,
            interactionActive: true,
            order => appliedOrder = order?.ToArray() ?? Array.Empty<uint>());
        require(appliedOrder.Length == 0 && gatherReads == 0,
            "the production gather-selection boundary retains the active interaction target");

        currentWorld = ProjectedWorld(
            local,
            local,
            remote with
            {
                CurrentTarget = new FcLogicalTarget(null, 200u, null, FcItemQuality.Nq),
            });
        ActiveItemList.RefreshFcIntentOrderAtSelectionBoundary(
            GatherOrderProvider,
            hasCurrentGatherTarget: false,
            interactionActive: false,
            order => appliedOrder = order?.ToArray() ?? Array.Empty<uint>());
        require(appliedOrder.SequenceEqual([100u, 200u]) && gatherReads == 1,
            "the next production gather-selection boundary consumes fresh intent");

        currentDiagnostics = currentDiagnostics with { AuthorScope = "phase9-other-scope" };
        var incumbent = appliedOrder;
        ActiveItemList.RefreshFcIntentOrderAtSelectionBoundary(
            GatherOrderProvider,
            hasCurrentGatherTarget: false,
            interactionActive: false,
            order => appliedOrder = order?.ToArray() ?? Array.Empty<uint>());
        require(appliedOrder.Length == 0
                && gatherReads == 2
                && incumbent.SequenceEqual([100u, 200u]),
            "provider scope teardown clears only the advisory hint and retains normal route selection");
    }

    private static void RecipeFactsFailurePreservesTheIncumbentOrder(
        Action<bool, string> require)
    {
        var local = Worker(
            "phase9-facts-local",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-facts-world");
        var remote = Worker(
            "phase9-facts-remote",
            FcWorkerState.Active,
            FcFulfillmentSelection.All,
            worldFingerprint: "phase9-facts-world",
            craftQueue: [new FcLogicalQueueEntry(38_247, 1, null)]);
        var plan = CraftingExecutionPlan.CreateRecovery(
            [new CraftingListItem(38_247, 1), new CraftingListItem(5_630, 1)],
            new FcExecutionContext(
                local.SessionId,
                [Guid.Parse("00000000-0000-0000-0000-000000000902")],
                "phase9-facts-world",
                new FcWorldRevision(1, "phase9-facts-world"),
                null),
            RecoveryPlan);

        plan.ApplyFcIntentQueueOrder([remote], local, new FixtureCraftQueueFactsResolver(
            new Dictionary<uint, FcIntentTieBreaker.CraftQueueRecipeFacts>
            {
                [38_247u] = new(8u, 10_001u, Array.Empty<uint>()),
            }));
        require(plan.QueueView.Select(item => item.RecipeId).SequenceEqual([38_247u, 5_630u]),
            "missing recipe facts preserve the incumbent FC queue order");

        var ambiguous = FcIntentTieBreaker.BuildCraftQueueCandidates(
            [
                new CraftingListItem(1, 1),
                new CraftingListItem(2, 1),
                new CraftingListItem(3, 1),
            ],
            new FixtureCraftQueueFactsResolver(
                new Dictionary<uint, FcIntentTieBreaker.CraftQueueRecipeFacts>
                {
                    [1u] = new(8u, 20u, new[] { 30u }),
                    [2u] = new(8u, 30u, Array.Empty<uint>()),
                    [3u] = new(8u, 30u, Array.Empty<uint>()),
                }));
        require(ambiguous.Length == 0,
            "ambiguous recipe output facts disable intent reordering instead of assuming independence");
    }

    private static CraftingListPlan RecoveryPlan(CraftingListDefinition list)
    {
        var plan = new CraftingListPlan();
        foreach (var item in list.Recipes)
        {
            plan.OriginalRecipes.Add(new CraftingListItem(item.RecipeId, item.Quantity)
            {
                IsOriginalRecipe = true,
            });
            plan.Recipes.Add(new CraftingListItem(item.RecipeId, item.Quantity)
            {
                IsOriginalRecipe = true,
            });
        }
        return plan;
    }

    private static FcProjectedWorld ProjectedWorld(
        WorkerSessionRecord local,
        params WorkerSessionRecord[] activeWorkers)
        => ProjectedWorld(local, local.WorldFingerprint, activeWorkers);

    private static FcProjectedWorld ProjectedWorld(
        WorkerSessionRecord local,
        string worldFingerprint,
        params WorkerSessionRecord[] activeWorkers)
        => new(
            new FcWorldRevision(1, worldFingerprint),
            Array.Empty<PublishedListRecord>(),
            Array.Empty<Guid>(),
            activeWorkers,
            new FcChestProjection(null, false),
            new FcChestLocationProjection(null, false, false),
            new FcFulfillmentMatchResult(
                Array.Empty<FcListDemand>(),
                Array.Empty<FcContribution>()));

    private static FcWorkerSessionDiagnostics Diagnostics(
        WorkerSessionRecord worker,
        string scope)
        => new(
            "Active",
            scope,
            worker.Header.OwnerAuthorId,
            true,
            false,
            false,
            worker.State,
            worker.SessionId,
            worker.SessionGeneration,
            worker.Header.Revision,
            1_000_000,
            1_000_500,
            0,
            string.Empty);

    private static FcIntentTieBreaker.CraftQueueCandidate Candidate(uint recipeId)
        => new(new CraftingListItem(recipeId, 1), 8, new HashSet<uint>(), true);

    private static FcIntentSnapshot Snapshot(
        WorkerSessionRecord local,
        params WorkerSessionRecord[] activeWorkers)
        => new(activeWorkers, local);

    private sealed class MutableIntentSnapshotProvider : IFcIntentSnapshotProvider
    {
        public FcIntentSnapshot? Current { get; set; }

        public bool TryGetCurrent(out FcIntentSnapshot snapshot)
        {
            if (Current is { } current)
            {
                snapshot = current;
                return true;
            }

            snapshot = null!;
            return false;
        }
    }

    private sealed class FixtureCraftQueueFactsResolver :
        FcIntentTieBreaker.ICraftQueueFactsResolver
    {
        private readonly IReadOnlyDictionary<uint, FcIntentTieBreaker.CraftQueueRecipeFacts> _facts;

        public FixtureCraftQueueFactsResolver(
            IReadOnlyDictionary<uint, FcIntentTieBreaker.CraftQueueRecipeFacts> facts)
            => _facts = facts;

        public bool TryResolve(
            uint recipeId,
            out FcIntentTieBreaker.CraftQueueRecipeFacts facts)
        {
            if (_facts.TryGetValue(recipeId, out var resolved) && resolved is not null)
            {
                facts = resolved;
                return true;
            }

            facts = null!;
            return false;
        }
    }

    private static WorkerSessionRecord Worker(
        string owner,
        FcWorkerState state,
        FcFulfillmentSelection selection,
        string worldFingerprint,
        FcLogicalTarget? currentTarget = null,
        uint[]? gatherTargetOrder = null,
        FcLogicalQueueEntry[]? craftQueue = null)
    {
        var sessionId = Guid.NewGuid();
        return new WorkerSessionRecord(
            new FcRecordHeader(
                FcProtocolVersion.Current,
                FcProtocolVersion.CurrentSchema,
                FcRecordTypes.WorkerSession,
                sessionId,
                owner,
                1),
            sessionId,
            1,
            state,
            new CharacterIdentity(owner, owner, "phase9-world"),
            selection,
            false,
            Array.Empty<ItemQuantityEntry>(),
            currentTarget,
            craftQueue ?? Array.Empty<FcLogicalQueueEntry>(),
            gatherTargetOrder ?? Array.Empty<uint>(),
            worldFingerprint);
    }
}
