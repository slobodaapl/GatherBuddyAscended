using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Capabilities;
using GatherBuddy.FcMesh.Chest;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Native;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Sessions;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.Vulcan.Tests;

internal static class FcMeshPhase8Tests
{
    public static void Run(Action<bool, string> require)
    {
        ProtocolRejectsMalformedCapabilityRegisters(require);
        AssessorUsesQualityPolicyAndDeterministicJobOrder(require);
        FingerprintsAreCanonicalAndCacheInvalidationIsObservable(require);
        PublicationReservesAndRetriesAbsoluteRegisters(require);
        ProductionAdmissionRechecksLocalCapabilityBeforeQueueEntry(require);
        CraftingQueueProcessorDefersInvalidatedNextEntry(require);
        ControllerDefersCapabilityReplanUntilCraftFinishes(require);
    }

    private static void ProtocolRejectsMalformedCapabilityRegisters(Action<bool, string> require)
    {
        const string author = "phase8-author";
        var clock = new FcManualClock(5_000_000);
        var request = Request(author, clock, FcItemQuality.Nq);
        var validator = new FcRecordValidator();
        require(validator.Validate(request, author).IsValid,
            "a compatible capability request with an explicit owner and exact policy validates");

        var response = new CapabilityResponseRecord(
            new FcRecordHeader(
                FcProtocolVersion.Current,
                FcProtocolVersion.CurrentSchema,
                FcRecordTypes.CapabilityResponse,
                request.RequestId,
                "phase8-responder",
                1),
            request.RequestId,
            new FcHlcTimestamp(clock.UnixMilliseconds + 60_000, 0, "phase8-responder"),
            request.WorldFingerprint,
            "game",
            FcCapabilityFingerprints.Planner(1, "game"),
            "gear",
            "solver",
            [new CraftCapabilityResult(
                100,
                true,
                8,
                false,
                FcRaphaelAssessmentOutcome.NoQualityRequired)])
        {
            RequestedRecipes = request.Recipes,
            ResponderAuthorId = "phase8-responder",
        };
        require(validator.Validate(response, "phase8-responder").IsValid,
            "a response carrying the exact request recipe set and selected job validates");
        require(!validator.Validate(
                response with
                {
                    Results = [new CraftCapabilityResult(
                        100,
                        true,
                        null,
                        false,
                        FcRaphaelAssessmentOutcome.NoQualityRequired)],
                },
                "phase8-responder").IsValid,
            "a CanCraft response without a selected job is rejected");

        var hqRequest = Request(author, clock, FcItemQuality.Hq);
        var partial = response with
        {
            Header = response.Header with { RecordId = hqRequest.RequestId },
            RequestId = hqRequest.RequestId,
            ExpiresAt = new FcHlcTimestamp(clock.UnixMilliseconds + 60_000, 0, "phase8-responder"),
            WorldFingerprint = hqRequest.WorldFingerprint,
            Results = [new CraftCapabilityResult(
                38_247,
                true,
                8,
                true,
                FcRaphaelAssessmentOutcome.PartialQuality)],
            RequestedRecipes = hqRequest.Recipes,
        };
        require(!validator.Validate(partial, "phase8-responder").IsValid,
            "a guaranteed capability response with partial Raphael quality is rejected");
    }

    private static void AssessorUsesQualityPolicyAndDeterministicJobOrder(Action<bool, string> require)
    {
        var clock = new FcManualClock(5_100_000);
        var hqRequest = Request("phase8-requester", clock, FcItemQuality.Hq, recipeId: 38_247);
        var legitimateJob = RecipeJob(38_247);
        var hqContext = new FcCapabilityAssessmentContext(
            hqRequest,
            hqRequest.Recipes[0],
            Worker("phase8-requester", hqRequest.WorldFingerprint, clock),
            "game",
            FcCapabilityFingerprints.Planner(1, "game"),
            hqRequest.WorldFingerprint);
        var hq = FcCapabilityAssessor.SelectBest(
            hqContext,
            [
                new FcCapabilityCandidate(
                    legitimateJob,
                    true,
                    true,
                    FcRaphaelAssessmentOutcome.FullQuality,
                    12,
                    500,
                    "gear-8-slower",
                    "solver-8-slower",
                    "slower"),
                new FcCapabilityCandidate(
                    legitimateJob,
                    true,
                    true,
                    FcRaphaelAssessmentOutcome.FullQuality,
                    10,
                    500,
                    "gear-8",
                    "solver-8",
                    "job tie-break"),
                new FcCapabilityCandidate(
                    legitimateJob + 1,
                    true,
                    true,
                    FcRaphaelAssessmentOutcome.PartialQuality,
                    1,
                    1,
                    "gear-9",
                    "solver-9",
                    "quality loss"),
            ],
            RecipeJob);
        require(hq.CanCraft
                && hq.SelectedJobId == legitimateJob
                && hq.IsFullQuality,
            "HQ capability admission requires FullQuality and deterministically chooses the better plan for the recipe's legitimate job");

        var precraftPolicy = new FcQualityPolicy([
            new FcQualityRule(200, FcItemQuality.Hq, 3),
        ]);
        var precraftRequest = hqRequest with
        {
            Recipes = [new RequiredCraftCapability(38_247, precraftPolicy)
            {
                PrecraftQualityPolicy = precraftPolicy,
                IsPrecraft = true,
            }],
        };
        var precraft = FcCapabilityAssessor.SelectBest(
            new FcCapabilityAssessmentContext(
                precraftRequest,
                precraftRequest.Recipes[0],
                Worker("phase8-requester", precraftRequest.WorldFingerprint, clock),
                "game",
                FcCapabilityFingerprints.Planner(1, "game"),
                precraftRequest.WorldFingerprint),
            [new FcCapabilityCandidate(
                legitimateJob,
                true,
                true,
                FcRaphaelAssessmentOutcome.FullQuality,
                1,
                1,
                "gear-precraft",
                "solver-precraft",
                "precraft quality")],
            RecipeJob);
        require(precraft.CanCraft && precraft.IsPrecraft && precraft.IsFullQuality,
            "precraft HQ policy is assessed separately from final HQ policy and still requires a FullQuality guarantee");

        var nqRequest = Request("phase8-requester", clock, FcItemQuality.Nq, recipeId: 38_247);
        var nq = FcCapabilityAssessor.SelectBest(
            new FcCapabilityAssessmentContext(
                nqRequest,
                nqRequest.Recipes[0],
                Worker("phase8-requester", nqRequest.WorldFingerprint, clock),
                "game",
                FcCapabilityFingerprints.Planner(1, "game"),
                nqRequest.WorldFingerprint),
            [new FcCapabilityCandidate(
                legitimateJob,
                true,
                false,
                FcRaphaelAssessmentOutcome.NoQualityRequired,
                1,
                1,
                "gear-8",
                "solver-8",
                "NQ craftable")],
            RecipeJob);
        require(nq.CanCraft && nq.SelectedJobId == legitimateJob && !nq.IsFullQuality,
            "NQ capability admission accepts CanCraft without an HQ guarantee");

        var unresolved = FcCapabilityAssessor.SelectBest(
            new FcCapabilityAssessmentContext(
                nqRequest,
                nqRequest.Recipes[0],
                Worker("phase8-requester", nqRequest.WorldFingerprint, clock),
                "game",
                FcCapabilityFingerprints.Planner(1, "game"),
                nqRequest.WorldFingerprint),
            [new FcCapabilityCandidate(
                legitimateJob,
                true,
                false,
                FcRaphaelAssessmentOutcome.NoQualityRequired,
                1,
                1,
                "gear-8",
                "solver-8",
                "unresolved recipe")],
            _ => 0u);
        require(!unresolved.CanCraft && unresolved.SelectedJobId is null,
            "capability admission fails closed when the recipe-job resolver cannot resolve the recipe");
    }

    private static void FingerprintsAreCanonicalAndCacheInvalidationIsObservable(Action<bool, string> require)
    {
        var firstPolicy = new FcQualityPolicy([
            new FcQualityRule(101, FcItemQuality.Hq, 1),
            new FcQualityRule(100, FcItemQuality.Nq, 2),
        ]);
        var reorderedPolicy = new FcQualityPolicy([
            new FcQualityRule(100, FcItemQuality.Nq, 2),
            new FcQualityRule(101, FcItemQuality.Hq, 1),
        ]);
        var changedPolicy = new FcQualityPolicy([
            new FcQualityRule(101, FcItemQuality.Hq, 2),
            new FcQualityRule(100, FcItemQuality.Nq, 2),
        ]);
        require(FcCapabilityFingerprints.Quality(firstPolicy)
                == FcCapabilityFingerprints.Quality(reorderedPolicy)
                && FcCapabilityFingerprints.Quality(firstPolicy)
                    != FcCapabilityFingerprints.Quality(changedPolicy),
            "quality fingerprints are stable under protocol ordering and change when a required quantity changes");

        var clock = new FcManualClock(5_200_000);
        var transport = new Phase8Transport();
        var state = new FcInMemoryCapabilityPublicationStateStore();
        var worker = Worker("phase8-cache-author", "world", clock);
        var game = "game";
        var plannerVersion = 1;
        var world = "world";
        var environment = "gear-a|solver-a";
        using var publication = new FcCapabilityPublicationService(
            state,
            transport,
            () => "phase8-cache-scope",
            () => transport.LocalAuthorId,
            () => new FcCompatibilityContext(plannerVersion, game),
            () => world,
            () => worker,
            RecipeJob,
            clock);
        var assessor = new CountingAssessor(() => environment);
        using var service = new FcCapabilityService(
            publication,
            () => worker,
            () => new FcCompatibilityContext(plannerVersion, game),
            () => world,
            RecipeJob,
            assessor,
            clock);
        var request = Request("phase8-requester", clock, FcItemQuality.Nq, world, game, plannerVersion);
        var first = service.AssessRequest(request, worker);
        var second = service.AssessRequest(request, worker);
        require(first.Count == 1 && second.Count == 1 && assessor.Calls == 1,
            "capability assessment cache reuses an assessment for the same recipe and environment fingerprints");
        environment = "gear-b|solver-a";
        service.AssessRequest(request, worker);
        require(assessor.Calls == 2,
            "gear/environment fingerprint changes invalidate the capability assessment cache");
        game = "game-next";
        var gameRequest = Request("phase8-requester", clock, FcItemQuality.Nq, world, game, plannerVersion);
        service.AssessRequest(gameRequest, worker);
        require(assessor.Calls == 3,
            "loaded game-version changes invalidate capability assessment cache entries");
        plannerVersion = 2;
        var plannerRequest = Request("phase8-requester", clock, FcItemQuality.Nq, world, game, plannerVersion);
        service.AssessRequest(plannerRequest, worker);
        require(assessor.Calls == 4,
            "planner-semantics changes invalidate capability assessment cache entries");
    }

    private static void PublicationReservesAndRetriesAbsoluteRegisters(Action<bool, string> require)
    {
        var clock = new FcManualClock(5_300_000);
        var transport = new Phase8Transport { FailPuts = true };
        var state = new FcInMemoryCapabilityPublicationStateStore();
        var worker = Worker(transport.LocalAuthorId, "world", clock);
        using var publication = new FcCapabilityPublicationService(
            state,
            transport,
            () => "phase8-publication-scope",
            () => transport.LocalAuthorId,
            () => new FcCompatibilityContext(1, "game"),
            () => "world",
            () => worker,
            RecipeJob,
            clock);
        var reserved = publication.Request([
            new RequiredCraftCapability(
                100,
                new FcQualityPolicy([new FcQualityRule(100, FcItemQuality.Hq, 1)]))
            {
                FinalQualityPolicy = new FcQualityPolicy([
                    new FcQualityRule(100, FcItemQuality.Hq, 1),
                ]),
            }]);
        publication.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var failed = state.Load("phase8-publication-scope").State;
        require(reserved.Accepted
                && reserved.Revision > 0
                && failed.LastRequest?.RequestId == reserved.RequestId
                && failed.Pending.Any(entry => entry.RequestId == reserved.RequestId
                    && entry.Status == FcCapabilityPublicationStatus.Failed),
            "capability publication reserves and durably retains a failed absolute request before transport retry");

        transport.FailPuts = false;
        require(publication.RetryPending().Accepted,
            "failed capability publication exposes an explicit retry operation");
        publication.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var accepted = state.Load("phase8-publication-scope").State;
        require(accepted.Pending.All(entry => entry.RequestId != reserved.RequestId)
                && accepted.LastRequest?.RequestId == reserved.RequestId
                && accepted.ReservedRevision == reserved.Revision
                && transport.PutCalls >= 2,
            "successful retry clears the pending request while preserving its reserved revision");
    }

    private static void ProductionAdmissionRechecksLocalCapabilityBeforeQueueEntry(Action<bool, string> require)
    {
        var clock = new FcManualClock(5_400_000);
        var transport = new Phase8Transport();
        var state = new FcInMemoryCapabilityPublicationStateStore();
        var worker = Worker(transport.LocalAuthorId, "world", clock);
        using var publication = new FcCapabilityPublicationService(
            state,
            transport,
            () => "phase8-admission-scope",
            () => transport.LocalAuthorId,
            () => new FcCompatibilityContext(1, "game"),
            () => "world",
            () => worker,
            RecipeJob,
            clock);
        var environment = "gear-a|solver-a";
        var assessor = new CountingAssessor(() => environment);
        using var service = new FcCapabilityService(
            publication,
            () => worker,
            () => new FcCompatibilityContext(1, "game"),
            () => "world",
            RecipeJob,
            assessor,
            clock);

        var finalPolicy = new FcQualityPolicy([
            new FcQualityRule(52642, FcItemQuality.Hq, 1),
        ]);
        var precraftPolicy = new FcQualityPolicy([
            new FcQualityRule(100, FcItemQuality.Hq, 1),
        ]);
        var requested = publication.Request([
            new RequiredCraftCapability(38_247, finalPolicy)
            {
                FinalQualityPolicy = finalPolicy,
            },
            new RequiredCraftCapability(5_630, precraftPolicy)
            {
                PrecraftQualityPolicy = precraftPolicy,
                IsPrecraft = true,
            },
        ]);
        require(requested.Accepted && requested.Request is not null,
            "production capability admission starts from a durable final-plus-precraft HQ request");
        var request = requested.Request!;
        var assessments = service.AssessRequest(request, worker);
        require(assessments.Count == 2 && assessments.All(value => value.CanCraft && value.IsFullQuality),
            "the local assessor publishes independent FullQuality guarantees for final and precraft HQ policies");
        require(!CraftingGatherBridge.TryPreflightFcQueueAdmission(
                    ExecutionSource.FcFulfillment,
                    null,
                    service,
                    out _),
            "an FC queue cannot start before an exact capability response exists");
        var recoveryLists = new[] { Guid.NewGuid() };
        var recoveryIdentity = new FcCapabilityRecoveryIdentity
        {
            RequestId = request.RequestId,
            SessionId = worker.SessionId,
            SessionGeneration = worker.SessionGeneration,
            Lists = recoveryLists.ToList(),
        };
        require(!service.TryCreateRecoveryExecutionProof(
                    recoveryIdentity,
                    out _,
                    out _),
            "FC recovery with a missing response cannot create authority for the next craft");
        var missingHqIdentity = new FcCapabilityRecoveryIdentity
        {
            SessionId = recoveryIdentity.SessionId,
            SessionGeneration = recoveryIdentity.SessionGeneration,
            Lists = recoveryIdentity.Lists.ToList(),
        };
        require(!service.TryCreateRecoveryExecutionProof(
                    missingHqIdentity,
                    out _,
                    out _),
            "FC recovery cannot downgrade lost HQ request identity to an NQ-only proof");
        var missingRecoveryPlan = CraftingExecutionPlan.CreateRecovery(
            [new CraftingListItem(38_247, 1), new CraftingListItem(5_630, 1)],
            new FcExecutionContext(
                recoveryIdentity.SessionId,
                recoveryIdentity.Lists,
                "world",
                new FcWorldRevision(1, "world")),
            RecoveryPlan);
        require(missingRecoveryPlan.ExecutionSource == ExecutionSource.FcFulfillment
                && !CraftingGatherBridge.TryPreflightFcQueueAdmission(
                    missingRecoveryPlan.ExecutionSource,
                    missingRecoveryPlan.FcContext?.CapabilityProof,
                    service,
                    out _),
            "missing FC recovery authority remains blocked at the queue boundary and cannot fall back to private execution");
        var responseResult = publication.PublishResponse(request, assessments);
        publication.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(responseResult.Accepted && publication.Responses.Count == 1,
            "an exact local response is durably accepted before queue admission");
        require(service.TryPreflightRequest(request, out var initialReason),
            $"a fresh exact local response is eligible before queue entry: {initialReason}");
        require(service.TryCreateExecutionProof(request, out var proof, out var proofReason)
                && proof is not null,
            $"a fresh exact local response creates a typed FC execution proof: {proofReason}");
        require(service.TryCreateRecoveryExecutionProof(
                    recoveryIdentity,
                    out var recoveryProof,
                    out var recoveryReason)
                && recoveryProof is not null,
            $"fresh recovery reassessment rebuilds a capability proof: {recoveryReason}");
        var recoveryTicket = CraftingRecoveryTicket.Capture(
            CraftingAutomationOwner.FcFulfillment,
            [new CraftingListItem(38_247, 1), new CraftingListItem(5_630, 1)],
            null,
            ExecutionSource.FcFulfillment,
            recoveryIdentity);
        require(recoveryTicket.Source == ExecutionSource.FcFulfillment
                && recoveryTicket.IsFcOwned
                && recoveryTicket.FcCapability is { } persistedCapability
                && persistedCapability.RequestId == request.RequestId
                && persistedCapability.SessionId == worker.SessionId
                && persistedCapability.SessionGeneration == worker.SessionGeneration,
            "FC recovery persistence keeps source and request/session identity without serializing proof authority");
        var recoveredPlan = CraftingExecutionPlan.CreateRecovery(
            [new CraftingListItem(38_247, 1), new CraftingListItem(5_630, 1)],
            new FcExecutionContext(
                recoveryIdentity.SessionId,
                recoveryIdentity.Lists,
                "world",
                new FcWorldRevision(1, "world"),
                recoveryProof),
            RecoveryPlan);
        require(recoveredPlan.ExecutionSource == ExecutionSource.FcFulfillment
                && recoveredPlan.FcContext?.CapabilityProof is not null,
            "FC recovery preserves the FC execution source and fresh proof instead of creating a private plan");
        require(CraftingGatherBridge.TryPreflightFcQueueAdmission(
                    recoveredPlan.ExecutionSource,
                    recoveredPlan.FcContext?.CapabilityProof,
                    service,
                    out var recoveryAdmissionReason),
            $"a fresh FC recovery proof admits the next craft at the shared queue boundary: {recoveryAdmissionReason}");

        require(!CraftingGatherBridge.TryPreflightFcQueueAdmission(
                    ExecutionSource.PrivateList,
                    proof,
                    service,
                    out _),
            "the shared queue admission boundary rejects non-FC execution sources");
        var callsBeforeBoundary = assessor.Calls;
        require(CraftingGatherBridge.TryPreflightFcQueueAdmission(
                    ExecutionSource.FcFulfillment,
                    proof,
                    service,
                    out var boundaryReason)
                && assessor.Calls > callsBeforeBoundary,
            $"the real FC queue admission boundary invokes fresh capability preflight: {boundaryReason}");
        if (proof is null)
            throw new InvalidOperationException("Capability proof fixture unexpectedly disappeared before actual queue-entry coverage.");
        var mismatchedProof = proof with { GearsetFingerprint = "mismatched-gear" };
        require(!CraftingGatherBridge.TryPreflightFcQueueAdmission(
                    ExecutionSource.FcFulfillment,
                    mismatchedProof,
                    service,
                    out _),
            "a capability proof with a mismatched gear fingerprint cannot start the FC queue");
        var actualQueuePlan = CraftingExecutionPlan.CreateFc(
            new CraftingListDefinition
            {
                ID = 8_502,
                Name = "Phase8 queue boundary",
            },
            new FcRepresentedInventorySource(FcItemQuantityMap.Empty),
            new CraftingPhysicalInventorySource(),
            new FcExecutionContext(
                proof.SessionId,
                [Guid.NewGuid()],
                "world",
                new FcWorldRevision(1, "world"),
                mismatchedProof));
        var priorCapabilityService = global::GatherBuddy.GatherBuddy.FcCapabilities;
        using (global::GatherBuddy.GatherBuddy.PushFcCapabilityServiceForTesting(service))
        {
            require(!CraftingGatherBridge.TryStartFcQueue(actualQueuePlan),
                "the actual FC queue entry rejects a mismatched capability proof before queue side effects");
        }
        require(ReferenceEquals(global::GatherBuddy.GatherBuddy.FcCapabilities, priorCapabilityService),
            "the production FC capability provider test scope restores the previous character binding");

        environment = "gear-b|solver-b";
        require(!service.TryCreateRecoveryExecutionProof(
                    recoveryIdentity,
                    out _,
                    out _),
            "a changed gear or solver fingerprint in the current response blocks the next FC recovery craft");
        environment = "gear-a|solver-a";
        worker = worker with { State = FcWorkerState.Unsubscribed };
        require(!CraftingGatherBridge.TryPreflightFcQueueAdmission(
                    ExecutionSource.FcFulfillment,
                    proof,
                    service,
                    out _),
            "an inactive local worker session cannot start from a previously valid capability proof");
        worker = worker with { State = FcWorkerState.Active };
        assessor.AllowCraft = false;
        require(!service.TryPreflightRequest(request, out _),
            "a current Raphael capability loss blocks the next FC queue admission");
        require(!service.TryCreateRecoveryExecutionProof(
                    recoveryIdentity,
                    out _,
                    out _),
            "a changed Raphael response blocks the next FC recovery craft");
        assessor.AllowCraft = true;
        worker = worker with { SessionId = Guid.NewGuid(), SessionGeneration = 2 };
        require(!service.TryPreflightExecution(proof, out _),
            "a session identity change invalidates an FC execution proof before queue side effects");
        worker = worker with
        {
            SessionId = recoveryIdentity.SessionId,
            SessionGeneration = recoveryIdentity.SessionGeneration,
        };
        clock.Set(request.ExpiresAt.PhysicalUnixMs);
        require(!service.TryPreflightRequest(request, out _),
            "an expired capability request cannot start an FC queue");
        require(!service.TryCreateRecoveryExecutionProof(
                    recoveryIdentity,
                    out _,
                    out _),
            "an expired capability response blocks the next FC recovery craft");
    }

    private static void CraftingQueueProcessorDefersInvalidatedNextEntry(Action<bool, string> require)
    {
        var clock = new FcManualClock(5_450_000);
        var transport = new Phase8Transport();
        var worker = Worker(transport.LocalAuthorId, "world", clock);
        using var publication = new FcCapabilityPublicationService(
            new FcInMemoryCapabilityPublicationStateStore(),
            transport,
            () => "phase8-queue-boundary-scope",
            () => transport.LocalAuthorId,
            () => new FcCompatibilityContext(1, "game"),
            () => "world",
            () => worker,
            RecipeJob,
            clock);
        var environment = "gear-a|solver-a";
        var assessor = new CountingAssessor(() => environment);
        using var service = new FcCapabilityService(
            publication,
            () => worker,
            () => new FcCompatibilityContext(1, "game"),
            () => "world",
            RecipeJob,
            assessor,
            clock);

        var finalPolicy = new FcQualityPolicy([
            new FcQualityRule(52642, FcItemQuality.Hq, 1),
        ]);
        var precraftPolicy = new FcQualityPolicy([
            new FcQualityRule(100, FcItemQuality.Hq, 1),
        ]);
        var requested = publication.Request([
            new RequiredCraftCapability(38_247, finalPolicy)
            {
                FinalQualityPolicy = finalPolicy,
            },
            new RequiredCraftCapability(5_630, precraftPolicy)
            {
                PrecraftQualityPolicy = precraftPolicy,
                IsPrecraft = true,
            },
        ]);
        require(requested.Accepted && requested.Request is not null,
            "queue-boundary fixture publishes a durable final-plus-precraft HQ request");
        if (requested.Request is null)
            throw new InvalidOperationException("Queue-boundary capability request fixture was not created.");
        var request = requested.Request;
        var assessments = service.AssessRequest(request, worker);
        var response = publication.PublishResponse(request, assessments);
        publication.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(response.Accepted && publication.Responses.Count == 1,
            "queue-boundary fixture publishes an exact local HQ response");
        require(service.TryCreateExecutionProof(request, out var proof, out var proofReason)
                && proof is not null,
            $"queue-boundary fixture creates a fresh HQ proof: {proofReason}");
        if (proof is null)
            throw new InvalidOperationException("Queue-boundary capability proof fixture was not created.");

        var lists = new[] { Guid.Parse("00000000-0000-0000-0000-000000000741") };
        var queueItems = new[]
        {
            new CraftingListItem(38_247, 1),
            new CraftingListItem(5_630, 1),
        };
        var plan = CraftingExecutionPlan.CreateRecovery(
            queueItems,
            new FcExecutionContext(
                proof.SessionId,
                lists,
                "world",
                new FcWorldRevision(1, "world"),
                proof),
            RecoveryPlan);

        var consumableApplications = 0;
        var gameQueueStarts = 0;
        var actionExecutions = 0;
        var preflightCalls = 0;
        (bool Accepted, string Reason) Preflight(FcCapabilityExecutionProof? candidate)
        {
            preflightCalls++;
            return service.TryPreflightExecution(candidate, out var reason)
                ? (true, string.Empty)
                : (false, reason);
        }

        var firstDecision = CraftingQueueProcessor.EvaluateFcCraftQueueAdmission(
            plan,
            0,
            Preflight,
            out var firstReason);
        if (firstDecision == CraftingQueueProcessor.FcCraftQueueAdmissionDecision.Admitted)
        {
            consumableApplications++;
            gameQueueStarts++;
            actionExecutions++;
        }

        // CraftFinished boundary: the next queue entry must repeat the same
        // admission check against the now-invalidated local capability.
        environment = "gear-b|solver-b";
        service.InvalidateAll();
        var secondDecision = CraftingQueueProcessor.EvaluateFcCraftQueueAdmission(
            plan,
            1,
            Preflight,
            out var secondReason);
        require(plan.QueueView.Count == 2
                && plan.QueueView[0].RecipeId == 38_247
                && plan.QueueView[1].RecipeId == 5_630
                && plan.ExecutionSource == ExecutionSource.FcFulfillment
                && plan.FcContext?.CapabilityProof?.Eligibility == FcCapabilityEligibility.EligibleGuaranteed
                && firstDecision == CraftingQueueProcessor.FcCraftQueueAdmissionDecision.Admitted
                && string.IsNullOrEmpty(firstReason)
                && secondDecision == CraftingQueueProcessor.FcCraftQueueAdmissionDecision.AwaitingCapability
                && !string.IsNullOrWhiteSpace(secondReason)
                && consumableApplications == 1
                && gameQueueStarts == 1
                && actionExecutions == 1
                && preflightCalls == 2,
            "CraftingQueueProcessor admits the first HQ entry, then pauses the next entry before consumables, game queue, or action execution when current capability changes");

        environment = "gear-a|solver-a";
        var refreshedAssessments = service.AssessRequest(request, worker, useCache: false);
        var refreshedResponse = publication.PublishResponse(request, refreshedAssessments);
        publication.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(refreshedResponse.Accepted,
            "queue-boundary fixture republishes a fresh response after capability invalidation");
        using var rebuiltService = new FcCapabilityService(
            publication,
            () => worker,
            () => new FcCompatibilityContext(1, "game"),
            () => "world",
            RecipeJob,
            assessor,
            clock);
        require(rebuiltService.TryCreateExecutionProof(request, out var rebuiltProof, out var rebuiltReason)
                && rebuiltProof is not null,
            $"fresh response rebuilds the HQ proof for the later queue entry: {rebuiltReason}");
        if (rebuiltProof is null)
            throw new InvalidOperationException("Queue-boundary rebuilt capability proof fixture was not created.");
        var rebuiltPlan = CraftingExecutionPlan.CreateRecovery(
            queueItems,
            new FcExecutionContext(
                rebuiltProof.SessionId,
                lists,
                "world",
                new FcWorldRevision(1, "world"),
                rebuiltProof),
            RecoveryPlan);
        var rebuiltDecision = CraftingQueueProcessor.EvaluateFcCraftQueueAdmission(
            rebuiltPlan,
            1,
            candidate => rebuiltService.TryPreflightExecution(candidate, out var reason)
                ? (true, string.Empty)
                : (false, reason),
            out var rebuiltAdmissionReason);
        if (rebuiltDecision == CraftingQueueProcessor.FcCraftQueueAdmissionDecision.Admitted)
        {
            consumableApplications++;
            gameQueueStarts++;
            actionExecutions++;
        }
        require(rebuiltDecision == CraftingQueueProcessor.FcCraftQueueAdmissionDecision.Admitted
                && string.IsNullOrEmpty(rebuiltAdmissionReason)
                && rebuiltPlan.ExecutionSource == ExecutionSource.FcFulfillment
                && rebuiltPlan.FcContext?.CapabilityProof?.Eligibility == FcCapabilityEligibility.EligibleGuaranteed
                && consumableApplications == 2
                && gameQueueStarts == 2
                && actionExecutions == 2,
            "a fresh rebuilt HQ proof admits the later FC queue entry without NQ or private-list fallback");
    }

    private static void ControllerDefersCapabilityReplanUntilCraftFinishes(Action<bool, string> require)
    {
        var clock = new FcManualClock(5_500_000);
        var transport = new Phase8Transport();
        using var worker = new FcWorkerSessionService(
            new FcInMemoryWorkerSessionStateStore(),
            transport,
            () => "phase8-controller-scope",
            () => transport.LocalAuthorId,
            () => new FcCompatibilityContext(1, "game"),
            ControllerPublishedLists,
            () => new CharacterIdentity("phase8-controller-scope", "Phase8", "World"),
            () => "game",
            clock);
        var started = worker.StartAll(
            new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 0)]),
            useOwnStock: true,
            dependencyClosure: [new FcQuantityKey(100, FcItemQuality.Nq)]);
        worker.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

        using var publication = new FcCapabilityPublicationService(
            new FcInMemoryCapabilityPublicationStateStore(),
            transport,
            () => "phase8-controller-capability-scope",
            () => transport.LocalAuthorId,
            () => new FcCompatibilityContext(1, "game"),
            () => "world",
            () => worker.Status.Desired,
            RecipeJob,
            clock);
        using var capabilities = new FcCapabilityService(
            publication,
            () => worker.Status.Desired,
            () => new FcCompatibilityContext(1, "game"),
            () => "world",
            RecipeJob,
            new CountingAssessor(() => "gear-a|solver-a"),
            clock);
        using var chest = new FcChestCoordinator(
            new FcFakeChestAdapter(),
            new FcUnavailableChestRouteAdapter(),
            new FcPendingTransferJournal(),
            _ => { },
            _ => new(FcAtomicTransferCommitStatus.Accepted, string.Empty),
            () => new FcChestLocationProjection(null, false, false));
        var runtime = new Phase8ControllerRuntime { AcceptCraftStart = true };
        var plan = CraftingExecutionPlan.CreateDirect(new CraftingListDefinition
        {
            ID = 8_501,
            Name = "Phase8 controller craft",
        });
        var activeDecision = new FcFulfillmentPlanDecision(
            true,
            string.Empty,
            FcFulfillmentActionKind.Craft,
            null,
            null,
            Array.Empty<FcLogicalQueueEntry>(),
            Array.Empty<uint>(),
            Array.Empty<ItemTransferRequest>(),
            plan,
            false,
            false,
            false);
        var blockedDecision = activeDecision with
        {
            Reason = "Capability reassessment is required.",
            RequiresCapability = true,
            HqRequired = true,
            CapabilityRequest = null,
            CapabilityEligibility = FcCapabilityEligibility.CapabilityPending,
        };
        var returnBlockedDecision = false;
        using var controller = new FcFulfillmentController(
            worker,
            chest,
            runtime,
            () => new FcProjectedWorld(
                new FcWorldRevision(1, "controller-world"),
                Array.Empty<PublishedListRecord>(),
                Array.Empty<Guid>(),
                Array.Empty<WorkerSessionRecord>(),
                new FcChestProjection(null, true),
                new FcChestLocationProjection(null, false, false),
                new FcFulfillmentMatchResult(
                    Array.Empty<FcListDemand>(),
                    Array.Empty<FcContribution>())),
            _ => returnBlockedDecision ? blockedDecision : activeDecision,
            capabilities: capabilities);

        require(started.Accepted && controller.Start(),
            "controller invalidation fixture starts from an active subscribed worker");
        for (var index = 0; index < 5; index++)
            controller.Tick();
        require(controller.State == FcFulfillmentControllerState.Crafting
                && runtime.CraftingActive
                && runtime.CraftStarts == 1,
            "controller reaches an active craft before capability invalidation");

        capabilities.InvalidateAll();
        require(controller.State == FcFulfillmentControllerState.Crafting
                && (controller.PendingReplanReasons & FcFulfillmentReplanReason.Capability) != 0,
            "capability invalidation marks an active craft dirty without interrupting the craft");

        returnBlockedDecision = true;
        runtime.CraftingActive = false;
        controller.Tick();
        require(controller.State == FcFulfillmentControllerState.DeriveWorld,
            "controller consumes capability invalidation only after the active craft reaches its finish boundary");
        controller.Tick();
        require(controller.State == FcFulfillmentControllerState.AwaitingCapability
                && runtime.CraftStarts == 1,
            "controller blocks the next action on reassessment instead of starting a second craft");
    }

    private static uint RecipeJob(uint recipeId)
        => recipeId switch
        {
            100u => 9u,
            5_630u => 9u,
            38_247u => 9u,
            _ => 0u,
        };

    private static IReadOnlyList<FcPublicListView> ControllerPublishedLists()
    {
        var listId = Guid.Parse("00000000-0000-0000-0000-000000000731");
        var list = new PublishedListRecord(
            new FcRecordHeader(1, 1, FcRecordTypes.PublishedList, listId, "controller-list-owner", 1),
            listId,
            "Controller list",
            true,
            1,
            "game",
            [new PublishedRecipeTarget(1, 100, 1, FcItemQuality.Nq)],
            new FcQualityPolicy([new FcQualityRule(100, FcItemQuality.Nq, 1)]),
            FcQualityPolicy.Empty);
        return [new FcPublicListView(list, true, string.Empty, false)];
    }

    private static CraftingListPlan RecoveryPlan(CraftingListDefinition list)
    {
        var plan = new CraftingListPlan();
        foreach (var item in list.Recipes)
        {
            var resolved = new CraftingListItem(item.RecipeId, item.Quantity)
            {
                IsOriginalRecipe = true,
            };
            plan.OriginalRecipes.Add(resolved);
            plan.Recipes.Add(new CraftingListItem(item.RecipeId, item.Quantity)
            {
                IsOriginalRecipe = true,
            });
        }
        return plan;
    }

    private static CapabilityRequestRecord Request(
        string owner,
        FcManualClock clock,
        FcItemQuality quality,
        string world = "world",
        string game = "game",
        int plannerVersion = 1,
        uint recipeId = 100)
    {
        var requestId = Guid.NewGuid();
        var policy = new FcQualityPolicy([
            new FcQualityRule(100, quality, 1),
        ]);
        var capability = new RequiredCraftCapability(recipeId, policy)
        {
            FinalQualityPolicy = policy,
            IsPrecraft = false,
        };
        return new CapabilityRequestRecord(
            new FcRecordHeader(
                FcProtocolVersion.Current,
                FcProtocolVersion.CurrentSchema,
                FcRecordTypes.CapabilityRequest,
                requestId,
                owner,
                1),
            requestId,
            world,
            new FcHlcTimestamp(clock.UnixMilliseconds + 60_000, 0, owner),
            [capability])
        {
            RequesterAuthorId = owner,
            GameVersion = game,
            PlannerFingerprint = FcCapabilityFingerprints.Planner(plannerVersion, game),
        };
    }

    private static WorkerSessionRecord Worker(
        string owner,
        string world,
        FcManualClock clock)
        => new(
            new FcRecordHeader(
                FcProtocolVersion.Current,
                FcProtocolVersion.CurrentSchema,
                FcRecordTypes.WorkerSession,
                Guid.NewGuid(),
                owner,
                1),
            Guid.NewGuid(),
            1,
            FcWorkerState.Active,
            new CharacterIdentity(owner, "Phase8", "World"),
            FcFulfillmentSelection.All,
            true,
            Array.Empty<ItemQuantityEntry>(),
            null,
            Array.Empty<FcLogicalQueueEntry>(),
            Array.Empty<uint>(),
            world);

    private sealed class CountingAssessor : IFcCapabilityAssessmentProvider
    {
        private readonly Func<string> _environment;

        public CountingAssessor(Func<string> environment)
            => _environment = environment;

        public int Calls { get; private set; }

        public bool AllowCraft { get; set; } = true;

        public string EnvironmentFingerprint(FcCapabilityAssessmentContext context)
            => _environment();

        public IReadOnlyList<FcCapabilityCandidate> Assess(FcCapabilityAssessmentContext context)
        {
            Calls++;
            var requiresHq = context.Capability.EffectiveQualityPolicy.Rules
                .Any(rule => rule.Quality == FcItemQuality.Hq);
            var selectedJob = RecipeJob(context.Capability.RecipeId);
            return [new FcCapabilityCandidate(
                selectedJob,
                AllowCraft,
                AllowCraft && requiresHq,
                AllowCraft && requiresHq
                    ? FcRaphaelAssessmentOutcome.FullQuality
                    : AllowCraft
                        ? FcRaphaelAssessmentOutcome.NoQualityRequired
                        : FcRaphaelAssessmentOutcome.SimulationFailed,
                1,
                1,
                _environment(),
                _environment(),
                AllowCraft ? "test assessment" : "Raphael assessment failed")];
        }
    }

    private sealed class Phase8Transport : IFcPublicationTransport
    {
        public bool IsReady => true;
        public string LocalAuthorId { get; } = "phase8-publication-author";
        public FcWorldStore WorldStore { get; } = new(new FcManualClock(5_300_000));
        public bool FailPuts { get; set; }
        public int PutCalls { get; private set; }

        public FcNativeCallResult Put(
            ReadOnlySpan<byte> recordId,
            ReadOnlySpan<byte> key,
            ReadOnlySpan<byte> recordType,
            bool hasGeneration,
            ulong generation,
            ulong revision,
            ReadOnlySpan<byte> payload)
        {
            PutCalls++;
            return FailPuts
                ? new((uint)FcNativeErrorCode.Storage, 0, 0)
                : new((uint)FcNativeErrorCode.Ok, 0, 0);
        }
    }

    private sealed class Phase8ControllerRuntime : IFcFulfillmentRuntime
    {
        public bool ConnectivityAvailable { get; set; } = true;
        public bool CraftingActive { get; set; }
        public bool GatheringInteractionActive { get; set; }
        public bool AcceptCraftStart { get; set; }
        public int CraftStarts { get; private set; }

        public bool TryStartCraft(CraftingExecutionPlan plan)
        {
            if (!AcceptCraftStart)
                return false;
            CraftStarts++;
            CraftingActive = true;
            return true;
        }

        public bool TryStartGather(IReadOnlyList<uint> targetOrder)
            => false;

        public void StopNavigation()
        {
        }
    }
}
