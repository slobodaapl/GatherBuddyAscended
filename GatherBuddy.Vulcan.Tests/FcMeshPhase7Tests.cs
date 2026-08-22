using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Chest;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Native;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Sessions;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.Vulcan.Tests;

internal static class FcMeshPhase7Tests
{
    public static void Run(Action<bool, string> require)
    {
        CatalogUsesApproximateAnchors(require);
        PublicResolverRejectsInvalidDestination(require);
        StableObjectResolutionFailsClosed(require);
        LocationCaptureRequiresLiveUniqueEvidence(require);
        LocationValidatorRejectsNonExecutableAddress(require);
        AtomicTransferCommitsOnlyAfterCompletePairedReread(require);
        InterActionDelayGatesProductionDispatch(require);
        FailedTransferPublishesReconciledOutcome(require);
        StopBeforeDispatchCancelsOnlyTheReservation(require);
        JournalRecoveryRequiresMatchingPhysicalFingerprints(require);
        AtomicRecoveryReconstructsPhysicalDelta(require);
        RecoveryUsesJournaledWorkerBaseline(require);
        WorkerAtomicObservationIsIdempotentAndRejectsWorkerAfterConflict(require);
        HqPhase7PlanStopsAtCapabilityBoundary(require);
        ControllerStartsSessionAndUnsubscribesOnCompletion(require);
        ControllerStopContainsActiveBoundaryWithoutDeposit(require);
        ControllerStopsAtEveryActiveBoundaryWithoutDeposit(require);
        ChestRouteFallbackFailsClosed(require);
        GatherYieldBoundaryReconcilesQualityAndStopsAmbiguity(require);
        GatherYieldBoundaryRequiresScopedSessionToken(require);
        GatherYieldPublicationUpdatesWorkerLedger(require);
    }

    private static void CatalogUsesApproximateAnchors(Action<bool, string> require)
    {
        var catalog = FcChestLocationCatalog.All;
        require(catalog.Count == 8,
            "Phase7 public Company Chest catalog contains exactly the eight requested destinations");
        var expected = new[]
        {
            ("The Black Shroud", "Old Gridania", "Leatherworkers' Guild & Shaded Bower", 14f, 9f),
            ("The Black Shroud", "New Gridania", "Gridania Aetheryte Plaza", 9f, 11f),
            ("La Noscea", "Limsa Lominsa Lower Decks", "Hawkers' Round", 7f, 12f),
            ("La Noscea", "Limsa Lominsa Upper Decks", "The Aftcastle", 13f, 12f),
            ("Thanalan", "Ul'dah - Steps of Nald", "Ul'dah Aetheryte Plaza", 8.3f, 9.4f),
            ("Thanalan", "Ul'dah - Steps of Thal", "The Sapphire Avenue Exchange", 13.8f, 10.4f),
            ("Gyr Abania", "Rhalgr's Reach", "Western Rhalgr's Reach", 10.1f, 12.9f),
            ("Hingashi", "Kugane", "Kogane Dori", 12.3f, 12.3f),
        };
        require(expected.All(item => catalog.Any(value => value.Region == item.Item1
                && value.Zone == item.Item2
                && value.AethernetShard == item.Item3
                && value.ApproximateMapX == item.Item4
                && value.ApproximateMapY == item.Item5)),
            "public destination catalog preserves every requested region, zone, shard, and approximate anchor");
        require(catalog.All(value => value.ApproximateMapX >= 0f && value.ApproximateMapY >= 0f),
            "public destination anchors are bounded map search inputs");
    }

    private static void StableObjectResolutionFailsClosed(Action<bool, string> require)
    {
        var objects = new List<FcChestLiveObject>
        {
            new(700, 0, "EventObj", "Company Chest", new Vector3(10, 0, 20), true),
        };
        var resolver = new FcChestObjectResolver(() => 42, () => objects);
        var search = new FcChestObjectSearch(700, 0, 42, new Vector3(10, 0, 20), 2, "EventObj");
        var resolved = resolver.Resolve(search);
        require(resolved.Succeeded && resolved.Object!.Value.Position == new Vector3(10, 0, 20),
            "resolver returns the current live object position after stable BaseId matching");

        objects.Add(new FcChestLiveObject(700, 0, "EventObj", "Company Chest", new Vector3(11, 0, 20), true));
        require(resolver.Resolve(search).Status == FcChestObjectResolutionStatus.Ambiguous,
            "multiple stable-identity live objects block interaction instead of guessing");
        require(new FcChestObjectResolver(() => 99, () => objects).Resolve(search).Status
                == FcChestObjectResolutionStatus.WrongTerritory,
            "wrong territory blocks object interaction");
        objects.RemoveAt(1);
        objects[0] = objects[0] with { IsTargetable = false };
        require(resolver.Resolve(search).Status == FcChestObjectResolutionStatus.NotTargetable,
            "non-targetable live object blocks interaction");
    }

    private static void PublicResolverRejectsInvalidDestination(Action<bool, string> require)
    {
        var resolver = new FcPublicChestDestinationResolver(new FcDalamudChestObjectResolver());
        var result = resolver.BuildSearch(new FcPublicChestDestination(
            "",
            "Old Gridania",
            "Leatherworkers' Guild & Shaded Bower",
            "Company Chest",
            float.NaN,
            9f));
        require(!result.Succeeded
                && !result.Search.IsValid,
            "public destination resolver blocks malformed route/map inputs before any live-object interaction");
    }

    private static void GatherYieldBoundaryReconcilesQualityAndStopsAmbiguity(Action<bool, string> require)
    {
        var counts = new Dictionary<uint, (int NQ, int HQ)>
        {
            [100] = (4, 1),
            [101] = (7, 0),
        };
        var reads = 0;
        var boundary = new FcGatherYieldBoundary(itemId =>
        {
            reads++;
            return counts[itemId];
        });
        require(boundary.Begin([100, 101], out var beginError) && string.IsNullOrEmpty(beginError),
            "gather yield boundary snapshots expected inventory once at interaction start");
        require(reads == 2,
            "gather yield boundary performs no inventory polling between start and completion");
        boundary.ObserveInventoryItem(100);
        counts[100] = (6, 2);
        var observed = boundary.Complete();
        require(observed.Succeeded
                && observed.Quantity.Get(100, FcItemQuality.Nq) == 2
                && observed.Quantity.Get(100, FcItemQuality.Hq) == 1
                && observed.Quantity.Get(101, FcItemQuality.Nq) == 0,
            "gather yield boundary emits checked NQ/HQ deltas at the completion boundary");
        require(reads == 4,
            "gather yield boundary reads expected inventory exactly once at completion");
        require(boundary.Complete().Outcome == FcGatherYieldBoundaryOutcome.Duplicate,
            "duplicate gather completion cannot publish a second yield");

        counts[100] = (6, 2);
        require(boundary.Begin([100], out _),
            "a new gather interaction starts a fresh boundary after the prior completion");
        counts[100] = (6, 2);
        require(boundary.Complete().Outcome == FcGatherYieldBoundaryOutcome.NoYield,
            "a completed gather with no inventory delta emits no attributed yield");

        require(boundary.Begin([100], out _),
            "unrelated-change scenario starts a fresh boundary");
        boundary.ObserveInventoryItem(999);
        counts[100] = (7, 2);
        require(boundary.Complete().Outcome == FcGatherYieldBoundaryOutcome.Blocked,
            "an unrelated inventory event blocks attribution instead of guessing a yield");

        require(boundary.Begin([100], out _),
            "negative-delta scenario starts a fresh boundary");
        counts[100] = (5, 2);
        require(boundary.Complete().Outcome == FcGatherYieldBoundaryOutcome.Blocked,
            "an inventory decrease during gather reconciliation fails closed");

        require(boundary.Begin([100], out _),
            "stop/replan scenario starts a fresh boundary");
        boundary.Abort();
        require(boundary.Complete().Outcome == FcGatherYieldBoundaryOutcome.Blocked,
            "stop or replan aborts the active gather boundary without publishing a guessed yield");
    }

    private static void LocationCaptureRequiresLiveUniqueEvidence(Action<bool, string> require)
    {
        var evidence = new FcCurrentEstateChestEvidence(
            true,
            true,
            new FcHousingAddress("Cerberus", "Chaos", 5, 7, false, "Mist"),
            new FcChestLocationEnvironment(100, 200, "Old Gridania", 14, 9),
            new FcChestLiveObject(700, 900, "EventObj", "Company Chest", new Vector3(1, 2, 3), true),
            "game",
            true,
            true,
            true,
            339);
        var captured = FcChestLocationCapture.Capture(evidence);
        require(captured.Succeeded
                && captured.Observation!.Chest.BaseId == 700
                && captured.Observation.Chest.DataId == 0,
            "location registration captures stable BaseId and approximate environment only");
        require(!FcChestLocationCapture.Capture(evidence with { ObjectResolutionWasUnique = false }).Succeeded,
            "location registration rejects ambiguous live object evidence");
        require(!FcChestLocationCapture.Capture(evidence with { HousingAddressAvailable = false }).Succeeded,
            "location registration rejects missing current housing address");
    }

    private static void LocationValidatorRejectsNonExecutableAddress(Action<bool, string> require)
    {
        var evidence = new FcCurrentEstateChestEvidence(
            true,
            true,
            new FcHousingAddress("Cerberus", "Chaos", 5, 7, true, "Mist"),
            new FcChestLocationEnvironment(100, 200, "Mist", 14, 9),
            new FcChestLiveObject(700, 0, "EventObj", "Company Chest", new Vector3(1, 2, 3), true),
            "game",
            true,
            true,
            true,
            339);
        var captured = FcChestLocationCapture.Capture(evidence);
        require(captured.Succeeded && captured.Observation is not null,
            "validator fixture captures a native-proof subdivision address");
        var record = FcChestLocationRegister.Create(captured.Observation!, "location-author", 1);
        var validator = new FcRecordValidator();
        require(validator.Validate(record, "location-author").IsValid,
            "route-executable FC location passes record validation");
        require(!validator.Validate(
                record with { Housing = record.Housing with { HousingDistrict = string.Empty } },
                "location-author").IsValid,
            "empty housing district is rejected instead of becoming an unusable route");
        require(!validator.Validate(
                record with { Housing = record.Housing with { Ward = 31 } },
                "location-author").IsValid,
            "out-of-range housing ward is rejected before publication");
        require(!FcChestLocationCapture.Capture(evidence with { NativeOwnershipVerified = false }).Succeeded,
            "external evidence cannot bypass native FC ownership proof");
        require(!FcChestLocationCapture.Capture(evidence with { SubdivisionKnown = false }).Succeeded,
            "registration blocks when authoritative housing division is unavailable");
    }

    private static void AtomicTransferCommitsOnlyAfterCompletePairedReread(Action<bool, string> require)
    {
        var before = Snapshot(chestQuantity: 5, playerQuantity: 0);
        var after = Snapshot(chestQuantity: 3, playerQuantity: 2);
        var adapter = new FcFakeChestAdapter(before);
        adapter.IsChestOpen = true;
        adapter.LoadState = FcChestLoadState.Complete;
        adapter = new FcFakeChestAdapter(
            before,
            withdraw: items =>
            {
                adapter.Snapshot = after;
                return new(FcTransferResultStatus.Dispatched, "dispatched", items);
            });
        adapter.IsChestOpen = true;
        adapter.LoadState = FcChestLoadState.Complete;
        var commits = new List<FcAtomicTransferCommit>();
        var coordinator = new FcChestCoordinator(
            adapter,
            new FcUnavailableChestRouteAdapter(),
            new FcPendingTransferJournal(),
            _ => { },
            commit =>
            {
                commits.Add(commit);
                return new(FcAtomicTransferCommitStatus.Accepted, "accepted");
            },
            () => new FcChestLocationProjection(null, false, false),
            operationIdProvider: () => Guid.Parse("00000000-0000-0000-0000-000000000701"));
        require(coordinator.StartWithdrawal(
                    Guid.Parse("00000000-0000-0000-0000-000000000702"),
                    1,
                    null,
                    [new ItemTransferRequest(new FcChestItemKey(100, false), 2)]),
            "withdrawal reserves one immediate transfer operation");
        coordinator.Tick();
        require(commits.Count == 0,
            "dispatch acknowledgement alone does not publish a transfer world revision");
        coordinator.Tick();
        require(commits.Count == 1
                && commits[0].ActualTransferred.Get(100, FcItemQuality.Nq) == 2
                && commits[0].ChestAfter.ItemMap.Get(100, FcItemQuality.Nq) == 3
                && commits[0].PlayerAfter.Get(100, FcItemQuality.Nq) == 2
                && !coordinator.HasPendingTransfer,
            "complete reread proves equal chest/player deltas before one atomic commit");
    }

    private static void InterActionDelayGatesProductionDispatch(Action<bool, string> require)
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var before = Snapshot(5, 0);
        var afterWithdrawal = Snapshot(3, 2);
        var afterDeposit = Snapshot(5, 0);
        var withdrawalCalls = 0;
        var depositCalls = 0;
        var adapter = new FcFakeChestAdapter(before);
        adapter = new FcFakeChestAdapter(
            before,
            withdraw: items =>
            {
                withdrawalCalls++;
                adapter.Snapshot = afterWithdrawal;
                return new(FcTransferResultStatus.Dispatched, "withdrawal dispatched", items);
            },
            deposit: items =>
            {
                depositCalls++;
                adapter.Snapshot = afterDeposit;
                return new(FcTransferResultStatus.Dispatched, "deposit dispatched", items);
            })
        {
            IsChestOpen = true,
            LoadState = FcChestLoadState.Complete,
        };
        var commits = new List<FcAtomicTransferCommit>();
        var operationIds = new Queue<Guid>([
            Guid.Parse("00000000-0000-0000-0000-0000000007A1"),
            Guid.Parse("00000000-0000-0000-0000-0000000007A2"),
        ]);
        using var coordinator = new FcChestCoordinator(
            adapter,
            new FcUnavailableChestRouteAdapter(),
            new FcPendingTransferJournal(),
            _ => { },
            commit =>
            {
                commits.Add(commit);
                return new(FcAtomicTransferCommitStatus.Accepted, "accepted");
            },
            () => new FcChestLocationProjection(null, false, false),
            operationIdProvider: () => operationIds.Dequeue(),
            utcNow: () => now);

        require(coordinator.StartWithdrawal(
                    Guid.Parse("00000000-0000-0000-0000-0000000007A3"),
                    1,
                    null,
                    [new ItemTransferRequest(new FcChestItemKey(100, false), 2)]),
            "production coordinator starts the withdrawal boundary");
        coordinator.Tick();
        require(withdrawalCalls == 1 && commits.Count == 0,
            "production coordinator dispatches the first physical action immediately without committing early");
        coordinator.Tick();
        require(commits.Count == 1
                && commits[0].Kind == FcInventoryTransferKind.Withdraw
                && !coordinator.HasPendingTransfer,
            "withdrawal reconciliation atomically commits before the next operation");

        require(coordinator.StartDeposit(
                    Guid.Parse("00000000-0000-0000-0000-0000000007A4"),
                    1,
                    null,
                    [new ItemTransferRequest(new FcChestItemKey(100, false), 2)]),
            "production coordinator accepts the immediate deposit operation after withdrawal commit");
        coordinator.Tick();
        require(depositCalls == 0 && coordinator.HasPendingTransfer,
            "deposit remains pending at the immediate post-withdrawal tick");

        now = now.AddMilliseconds(499);
        coordinator.Tick();
        require(depositCalls == 0 && coordinator.HasPendingTransfer,
            "deposit remains pending through t0 plus 499 ms without a physical call");

        now = now.AddMilliseconds(1);
        coordinator.Tick();
        require(depositCalls == 1 && coordinator.HasPendingTransfer,
            "exactly one deposit dispatch occurs at the 500 ms boundary before reconciliation");
        coordinator.Tick();
        require(depositCalls == 1
                && commits.Count == 2
                && commits[1].Kind == FcInventoryTransferKind.Deposit
                && !coordinator.HasPendingTransfer,
            "the next tick reconciles and atomically commits the deposit without duplicate dispatch");
        coordinator.Tick();
        require(depositCalls == 1 && commits.Count == 2,
            "completed deposit cannot dispatch or commit a second time");
    }

    private static void StopBeforeDispatchCancelsOnlyTheReservation(Action<bool, string> require)
    {
        var dispatches = 0;
        var adapter = new FcFakeChestAdapter(
            Snapshot(5, 0),
            withdraw: items =>
            {
                dispatches++;
                return new(FcTransferResultStatus.Dispatched, "unexpected", items);
            })
        { IsChestOpen = true, LoadState = FcChestLoadState.Complete };
        var coordinator = new FcChestCoordinator(
            adapter,
            new FcUnavailableChestRouteAdapter(),
            new FcPendingTransferJournal(),
            _ => { },
            _ => new(FcAtomicTransferCommitStatus.Accepted, "unexpected"),
            () => new FcChestLocationProjection(null, false, false));
        require(coordinator.StartWithdrawal(Guid.NewGuid(), 1, null,
                    [new ItemTransferRequest(new FcChestItemKey(100, false), 1)]),
            "stop test starts a reserved but not yet dispatched transfer");
        coordinator.RequestStop();
        coordinator.Tick();
        require(dispatches == 0 && !coordinator.HasPendingTransfer
                && coordinator.State == FcChestCoordinatorState.Idle,
            "stop before dispatch cancels only the reservation and performs no transfer or travel");
    }

    private static void FailedTransferPublishesReconciledOutcome(Action<bool, string> require)
    {
        var adapter = new FcFakeChestAdapter(
            Snapshot(5, 0),
            withdraw: items => new(FcTransferResultStatus.Failed, "permission denied", items))
        {
            IsChestOpen = true,
            LoadState = FcChestLoadState.Complete,
        };
        var commits = new List<FcAtomicTransferCommit>();
        using var coordinator = new FcChestCoordinator(
            adapter,
            new FcUnavailableChestRouteAdapter(),
            new FcPendingTransferJournal(),
            _ => { },
            commit =>
            {
                commits.Add(commit);
                return new(FcAtomicTransferCommitStatus.Accepted, "accepted");
            },
            () => new FcChestLocationProjection(null, false, false));

        require(coordinator.StartWithdrawal(
                    Guid.Parse("00000000-0000-0000-0000-000000000705"),
                    1,
                    null,
                    [new ItemTransferRequest(new FcChestItemKey(100, false), 2)]),
            "failed transfer still reserves an indivisible operation before dispatch");
        coordinator.Tick();
        coordinator.Tick();
        require(commits.Count == 1
                && commits[0].Outcome == FcInventoryTransferOutcome.ReconciledFailure
                && commits[0].ActualTransferred.Entries.Length == 0
                && coordinator.State == FcChestCoordinatorState.Ready
                && !coordinator.HasPendingTransfer,
            "complete unchanged reread publishes one reconciled failure instead of dropping the failed physical boundary");
    }

    private static void JournalRecoveryRequiresMatchingPhysicalFingerprints(Action<bool, string> require)
    {
        var store = new FcInMemoryPendingTransferJournalStore();
        var journal = new FcPendingTransferJournal();
        var operation = new FcTransferPreOperation(
            Guid.Parse("00000000-0000-0000-0000-000000000703"),
            Guid.Parse("00000000-0000-0000-0000-000000000704"),
            1,
            FcInventoryTransferKind.Withdraw,
            null,
            "chest-before",
            "worker-before",
            new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>()),
            DateTimeOffset.UtcNow);
        journal.Begin(operation);
        store.Save(journal.ExportState());
        require(FcPendingTransferJournal.TryRestore(store.Load(), out var restored)
                && restored.HasPending,
            "pending transfer journal survives a durable state round trip");
        var mismatch = restored.Recover(operation.OperationId, "chest-after", "worker-after");
            require(mismatch.Status == FcPendingTransferRecoveryStatus.FingerprintMismatch
                && restored.HasPending,
            "crash recovery retains the operation and blocks guessed compensation on fingerprint mismatch");
    }

    private static void AtomicRecoveryReconstructsPhysicalDelta(Action<bool, string> require)
    {
        var before = Snapshot(5, 0);
        var after = Snapshot(3, 2);
        var operationId = Guid.Parse("00000000-0000-0000-0000-000000000706");
        var sessionId = Guid.Parse("00000000-0000-0000-0000-000000000707");
        var request = new ItemTransferRequest(new FcChestItemKey(100, false), 2);
        var journal = new FcPendingTransferJournal();
        journal.Begin(new FcTransferPreOperation(
            operationId,
            sessionId,
            1,
            FcInventoryTransferKind.Withdraw,
            null,
            "pre-chest",
            "pre-worker",
            new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>()),
            DateTimeOffset.UtcNow,
            FcTransferPhysicalSnapshot.Capture(before),
            [request],
            FcItemQuantityMap.Empty));
        var adapter = new FcFakeChestAdapter(after)
        {
            IsChestOpen = true,
            LoadState = FcChestLoadState.Complete,
        };
        var commits = new List<FcAtomicTransferCommit>();
        using var coordinator = new FcChestCoordinator(
            adapter,
            new FcUnavailableChestRouteAdapter(),
            journal,
            _ => { },
            commit =>
            {
                commits.Add(commit);
                return new(FcAtomicTransferCommitStatus.Accepted, "accepted");
            },
            () => new FcChestLocationProjection(null, false, false));
        require(coordinator.RecoverPending()
                && commits.Count == 1
                && commits[0].ActualTransferred.Get(100, FcItemQuality.Nq) == 2
                && !journal.HasPending,
            "post-dispatch crash recovery derives one exact physical withdrawal and clears its journal");

        var unchangedJournal = new FcPendingTransferJournal();
        unchangedJournal.Begin(new FcTransferPreOperation(
            Guid.Parse("00000000-0000-0000-0000-000000000708"),
            sessionId,
            1,
            FcInventoryTransferKind.Withdraw,
            null,
            "pre-chest",
            "pre-worker",
            new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>()),
            DateTimeOffset.UtcNow,
            FcTransferPhysicalSnapshot.Capture(before),
            [request],
            FcItemQuantityMap.Empty));
        var unchangedAdapter = new FcFakeChestAdapter(before)
        {
            IsChestOpen = true,
            LoadState = FcChestLoadState.Complete,
        };
        var unchangedCommits = new List<FcAtomicTransferCommit>();
        using var unchangedCoordinator = new FcChestCoordinator(
            unchangedAdapter,
            new FcUnavailableChestRouteAdapter(),
            unchangedJournal,
            _ => { },
            commit =>
            {
                unchangedCommits.Add(commit);
                return new(FcAtomicTransferCommitStatus.Accepted, "accepted");
            },
            () => new FcChestLocationProjection(null, false, false));
        require(unchangedCoordinator.RecoverPending()
                && unchangedCommits.Count == 0
                && !unchangedJournal.HasPending,
            "crash before dispatch with an unchanged physical snapshot clears only the reservation");

        var unavailableJournal = new FcPendingTransferJournal();
        unavailableJournal.Begin(new FcTransferPreOperation(
            Guid.Parse("00000000-0000-0000-0000-00000000070C"),
            sessionId,
            1,
            FcInventoryTransferKind.Withdraw,
            null,
            "pre-chest",
            "pre-worker",
            new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>()),
            DateTimeOffset.UtcNow,
            FcTransferPhysicalSnapshot.Capture(before),
            [request],
            FcItemQuantityMap.Empty));
        var unavailableAdapter = new FcFakeChestAdapter(before)
        {
            IsChestOpen = false,
            LoadState = FcChestLoadState.Closed,
        };
        using var unavailableCoordinator = new FcChestCoordinator(
            unavailableAdapter,
            new FcUnavailableChestRouteAdapter(),
            unavailableJournal,
            _ => { },
            _ => new(FcAtomicTransferCommitStatus.Accepted, "accepted"),
            () => new FcChestLocationProjection(null, false, false));
        require(!unavailableCoordinator.RecoverPending()
                && unavailableCoordinator.State == FcChestCoordinatorState.CleanupPending
                && unavailableJournal.HasPending,
            "unavailable crash-recovery reread retains the journal and enters CleanupPending");
    }

    private static void RecoveryUsesJournaledWorkerBaseline(Action<bool, string> require)
    {
        var transport = new ControllerTransport();
        using var worker = new FcWorkerSessionService(
            new FcInMemoryWorkerSessionStateStore(),
            transport,
            () => "journal-worker-scope",
            () => transport.LocalAuthorId,
            () => new FcCompatibilityContext(1, "game"),
            ControllerPublishedLists,
            () => new CharacterIdentity("journal-worker-scope", "Alice", "World"),
            () => "game",
            new FcManualClock(4_250_000));
        var start = worker.StartAll(
            FcItemQuantityMap.Empty,
            useOwnStock: true,
            dependencyClosure: [new FcQuantityKey(100, FcItemQuality.Nq)]);
        worker.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var baseline = worker.Status.Desired!;
        var operationId = Guid.Parse("00000000-0000-0000-0000-00000000070D");
        var operation = new FcTransferPreOperation(
            operationId,
            baseline.SessionId,
            baseline.SessionGeneration,
            FcInventoryTransferKind.Withdraw,
            null,
            "journal-chest-before",
            "journal-worker-before",
            new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>()),
            DateTimeOffset.UtcNow,
            FcTransferPhysicalSnapshot.Capture(Snapshot(5, 0)),
            [new ItemTransferRequest(new FcChestItemKey(100, false), 2)],
            baseline.HeldInventoryMap,
            baseline);
        var journal = new FcPendingTransferJournal();
        journal.Begin(operation);
        var restoredJournal = FcPendingTransferJournal.Restore(journal.ExportState());
        var adapter = new FcFakeChestAdapter(Snapshot(3, 2))
        {
            IsChestOpen = true,
            LoadState = FcChestLoadState.Complete,
        };
        FcInventoryTransferRecord? recovered = null;
        using var coordinator = new FcChestCoordinator(
            adapter,
            new FcUnavailableChestRouteAdapter(),
            restoredJournal,
            _ => { },
            commit =>
            {
                var built = worker.BuildAtomicTransferRecord(commit, 1, out var transfer);
                if (!built.Accepted)
                    return new(FcAtomicTransferCommitStatus.Blocked, built.Message);
                recovered = transfer;
                var observed = worker.ObserveAtomicTransfer(transfer);
                return observed.Accepted
                    ? new(FcAtomicTransferCommitStatus.Accepted, observed.Message)
                    : new(FcAtomicTransferCommitStatus.Blocked, observed.Message);
            },
            () => new FcChestLocationProjection(null, false, false));
        var recoveredSuccessfully = coordinator.RecoverPending();
        var expectedHeld = baseline.HeldInventoryMap.Add(new FcItemQuantityMap([
            new ItemQuantityEntry(100, FcItemQuality.Nq, 2)]));
        var duplicate = recovered is null
            ? FcWorkerSessionResult.Blocked("Recovery did not produce a transfer record.")
            : worker.ObserveAtomicTransfer(recovered);
        var current = worker.Status.Desired!;
        var depositCommit = new FcAtomicTransferCommit(
            Guid.Parse("00000000-0000-0000-0000-00000000070E"),
            current.SessionId,
            current.SessionGeneration,
            null,
            FcInventoryTransferKind.Deposit,
            FcInventoryTransferOutcome.Committed,
            new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 1)]),
            ChestAfterRecord(Guid.Parse("00000000-0000-0000-0000-00000000070E"), transport.LocalAuthorId),
            FcItemQuantityMap.Empty,
            current.HeldInventoryMap,
            current);
        var partialDeposit = worker.BuildAtomicTransferRecord(
            depositCommit,
            2,
            out var depositTransfer);
        var conflictBaseline = worker.Status.Desired!;
        var conflictJournal = new FcPendingTransferJournal();
        conflictJournal.Begin(new FcTransferPreOperation(
            Guid.Parse("00000000-0000-0000-0000-00000000070F"),
            conflictBaseline.SessionId,
            conflictBaseline.SessionGeneration,
            FcInventoryTransferKind.Withdraw,
            null,
            "conflict-chest-before",
            "conflict-worker-before",
            new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>()),
            DateTimeOffset.UtcNow,
            FcTransferPhysicalSnapshot.Capture(Snapshot(5, 0)),
            [new ItemTransferRequest(new FcChestItemKey(100, false), 2)],
            conflictBaseline.HeldInventoryMap,
            conflictBaseline));
        var conflictAdapter = new FcFakeChestAdapter(Snapshot(3, 2))
        {
            IsChestOpen = true,
            LoadState = FcChestLoadState.Complete,
        };
        var conflictRecords = 0;
        using var conflictCoordinator = new FcChestCoordinator(
            conflictAdapter,
            new FcUnavailableChestRouteAdapter(),
            conflictJournal,
            _ => { },
            commit =>
            {
                var built = worker.BuildAtomicTransferRecord(commit, 3, out _);
                if (!built.Accepted)
                    return new(FcAtomicTransferCommitStatus.Blocked, built.Message);
                conflictRecords++;
                return new(FcAtomicTransferCommitStatus.Accepted, string.Empty);
            },
            () => new FcChestLocationProjection(null, false, false));
        var mutation = worker.SetCurrentTarget(new FcLogicalTarget(null, 101, null, FcItemQuality.Nq));
        var conflictRecovered = conflictCoordinator.RecoverPending();
        require(start.Accepted
                && recoveredSuccessfully
                && recovered is not null
                && recovered.WorkerAfter.HeldInventoryMap.Equals(expectedHeld)
                && !restoredJournal.HasPending
                && duplicate.Accepted
                && worker.State.ObservedAtomicTransfers.Length == 1
                && partialDeposit.Accepted
                && depositTransfer.WorkerAfter.HeldInventoryMap.Get(100, FcItemQuality.Nq) == 1
                && mutation.Accepted
                && !conflictRecovered
                && conflictRecords == 0
                && conflictJournal.HasPending,
            "recovery derives exact withdrawal/deposit WorkerAfter from the journaled worker baseline, rejects a mutated baseline, and keeps duplicate observation idempotent");
    }

    private static void WorkerAtomicObservationIsIdempotentAndRejectsWorkerAfterConflict(Action<bool, string> require)
    {
        var transport = new ControllerTransport();
        using var worker = new FcWorkerSessionService(
            new FcInMemoryWorkerSessionStateStore(),
            transport,
            () => "atomic-observation-scope",
            () => transport.LocalAuthorId,
            () => new FcCompatibilityContext(1, "game"),
            ControllerPublishedLists,
            () => new CharacterIdentity("atomic-observation-scope", "Alice", "World"),
            () => "game",
            new FcManualClock(4_200_000));
        var start = worker.StartAll(
            FcItemQuantityMap.Empty,
            useOwnStock: true,
            dependencyClosure: [new FcQuantityKey(100, FcItemQuality.Nq)]);
        worker.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var operationId = Guid.Parse("00000000-0000-0000-0000-000000000709");
        var actual = new FcItemQuantityMap([
            new ItemQuantityEntry(100, FcItemQuality.Nq, 2)]);
        var desired = worker.Status.Desired!;
        var commit = new FcAtomicTransferCommit(
            operationId,
            desired.SessionId,
            desired.SessionGeneration,
            null,
            FcInventoryTransferKind.Withdraw,
            FcInventoryTransferOutcome.Committed,
            actual,
            ChestAfterRecord(operationId, transport.LocalAuthorId),
            actual,
            desired.HeldInventoryMap,
            desired);
        var built = worker.BuildAtomicTransferRecord(commit, 1, out var transfer);
        var first = built.Accepted
            ? worker.ObserveAtomicTransfer(transfer)
            : FcWorkerSessionResult.Blocked(built.Message);
        var second = worker.ObserveAtomicTransfer(transfer);
        var conflict = transfer with
        {
            WorkerAfter = transfer.WorkerAfter with
            {
                HeldInventory = Array.Empty<ItemQuantityEntry>(),
            },
        };
        var changed = worker.SetCurrentTarget(new FcLogicalTarget(null, 101, null, FcItemQuality.Nq));
        var staleBuild = worker.BuildAtomicTransferRecord(commit, 2, out _);
        var conflicting = worker.ObserveAtomicTransfer(conflict);
        require(start.Accepted && built.Accepted && first.Accepted && second.Accepted
                && worker.Status.Ledger?.State.AttributedInventory.Get(100, FcItemQuality.Nq) == 2
                && worker.State.ObservedAtomicTransfers.Length == 1
                && changed.Accepted
                && !staleBuild.Accepted
                && !conflicting.Accepted,
            "atomic worker observation repairs once, rejects stale journal baselines, and rejects WorkerAfter conflict");
    }

    private static void HqPhase7PlanStopsAtCapabilityBoundary(Action<bool, string> require)
    {
        var listId = Guid.Parse("00000000-0000-0000-0000-000000000721");
        var sessionId = Guid.Parse("00000000-0000-0000-0000-000000000722");
        var list = new PublishedListRecord(
            new FcRecordHeader(1, 1, FcRecordTypes.PublishedList, listId, "hq-owner", 1),
            listId,
            "HQ list",
            true,
            1,
            "game",
            [new PublishedRecipeTarget(1, 100, 1, FcItemQuality.Hq)],
            new FcQualityPolicy([new FcQualityRule(100, FcItemQuality.Hq, 1)]),
            FcQualityPolicy.Empty);
        var worker = new WorkerSessionRecord(
            new FcRecordHeader(1, 1, FcRecordTypes.WorkerSession, Guid.Parse("00000000-0000-0000-0000-000000000723"), "local", 1),
            sessionId,
            1,
            FcWorkerState.Active,
            new CharacterIdentity("local", "Alice", "World"),
            FcFulfillmentSelection.Specific(listId),
            false,
            Array.Empty<ItemQuantityEntry>(),
            null,
            Array.Empty<FcLogicalQueueEntry>(),
            Array.Empty<uint>(),
            "fp");
        var ledger = new FcContributionLedger(new FcContributionLedgerState(
            sessionId,
            FcItemQuantityMap.Empty,
            FcItemQuantityMap.Empty,
            FcItemQuantityMap.Empty,
            false));
        var status = new FcWorkerSessionStatus(true, false, worker, worker, null, ledger, string.Empty);
        var world = new FcProjectedWorld(
            new FcWorldRevision(1, "fp"),
            [list],
            [listId],
            [worker],
            new FcChestProjection(null, false),
            new FcChestLocationProjection(null, false, false),
            new FcFulfillmentMatchResult(
                [new FcListDemand(listId, 100, FcItemQuality.Hq, 1)],
                Array.Empty<FcContribution>()));
        var decision = FcFulfillmentPlanner.Build(world, status);
        require(decision.IsValid
                && decision.RequiresCapability
                && decision.HqRequired
                && decision.CraftPlan is null,
            "Phase 7 must stop HQ-required final work at AwaitingCapability without starting a craft plan");
    }

    private static void ControllerStartsSessionAndUnsubscribesOnCompletion(Action<bool, string> require)
    {
        var transport = new ControllerTransport();
        using var worker = new FcWorkerSessionService(
            new FcInMemoryWorkerSessionStateStore(),
            transport,
            () => "controller-start-scope",
            () => transport.LocalAuthorId,
            () => new FcCompatibilityContext(1, "game"),
            ControllerPublishedLists,
            () => new CharacterIdentity("controller-start-scope", "Alice", "World"),
            () => "game",
            new FcManualClock(4_300_000));
        var adapter = new FcFakeChestAdapter(Snapshot(3, 0))
        {
            IsChestOpen = true,
            LoadState = FcChestLoadState.Complete,
        };
        using var chest = new FcChestCoordinator(
            adapter,
            new FcUnavailableChestRouteAdapter(),
            new FcPendingTransferJournal(),
            _ => { },
            _ => new(FcAtomicTransferCommitStatus.Accepted, string.Empty),
            () => new FcChestLocationProjection(null, false, false));
        var runtime = new ControllerRuntime();
        var starterCalls = 0;
        var completionCalls = 0;
        var decision = new FcFulfillmentPlanDecision(
            true,
            string.Empty,
            FcFulfillmentActionKind.Complete,
            null,
            null,
            Array.Empty<FcLogicalQueueEntry>(),
            Array.Empty<uint>(),
            Array.Empty<ItemTransferRequest>(),
            null,
            false,
            false,
            true);
        using var controller = new FcFulfillmentController(
            worker,
            chest,
            runtime,
            FreshControllerWorld,
            _ => decision,
            onCompleted: () => completionCalls++,
            sessionStarter: () =>
            {
                starterCalls++;
                return worker.StartAll(
                    new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 0)]),
                    useOwnStock: true,
                    dependencyClosure: [new FcQuantityKey(100, FcItemQuality.Nq)]);
            });
        require(controller.Start() && !worker.Status.IsSubscribed,
            "controller start accepts an unsubscribed worker and owns session startup");
        controller.Tick();
        require(starterCalls == 1 && worker.Status.IsSubscribed,
            "controller invokes the authoritative session starter before planning");
        for (var index = 0; index < 8 && controller.State != FcFulfillmentControllerState.Completed; index++)
            controller.Tick();
        worker.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(controller.State == FcFulfillmentControllerState.Completed
                && !worker.Status.IsSubscribed
                && completionCalls == 1,
            "true completion unsubscribes the worker and invokes the completion callback exactly once");
    }

    private static void ControllerStopContainsActiveBoundaryWithoutDeposit(Action<bool, string> require)
    {
        var liveRuntime = new FcLiveFulfillmentRuntime();
        require(!liveRuntime.TryStartGather(Array.Empty<uint>()),
            "production FC runtime rejects a raw gather target list that could bypass the existing execution plan");
        var clock = new FcManualClock(4_000_000);
        var transport = new ControllerTransport();
        using var worker = new FcWorkerSessionService(
            new FcInMemoryWorkerSessionStateStore(),
            transport,
            () => "controller-scope",
            () => transport.LocalAuthorId,
            () => new FcCompatibilityContext(1, "game"),
            ControllerPublishedLists,
            () => new CharacterIdentity("controller-scope", "Alice", "World"),
            () => "game",
            clock);
        var closure = new[] { new FcQuantityKey(100, FcItemQuality.Nq) };
        var start = worker.StartAll(
            new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 0)]),
            useOwnStock: true,
            dependencyClosure: closure);
        worker.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var adapter = new FcFakeChestAdapter(Snapshot(3, 0))
        {
            IsChestOpen = true,
            LoadState = FcChestLoadState.Complete,
        };
        using var chest = new FcChestCoordinator(
            adapter,
            new FcUnavailableChestRouteAdapter(),
            new FcPendingTransferJournal(),
            _ => { },
            _ => new(FcAtomicTransferCommitStatus.Accepted, string.Empty),
            () => new FcChestLocationProjection(null, false, false));
        var runtime = new ControllerRuntime { CraftingActive = true };
        using var controller = new FcFulfillmentController(
            worker,
            chest,
            runtime,
            () => null,
            _ => FcFulfillmentPlanDecision.Invalid("test"));

        require(start.Accepted && worker.Status.IsSubscribed && controller.Start(),
            "FC controller enters a started state with a subscribed worker before stop containment");
        runtime.ConnectivityAvailable = false;
        controller.Tick();
        controller.Tick();
        require(controller.State == FcFulfillmentControllerState.WaitingConnectivity,
            "FC controller waits for mesh connectivity before deriving a physical action");

        runtime.ConnectivityAvailable = true;
        runtime.CraftingActive = false;
        controller.Tick();
        require(controller.State == FcFulfillmentControllerState.DeriveWorld,
            "FC controller resumes replanning after connectivity returns at a safe boundary");

        runtime.CraftingActive = true;
        controller.RequestStop();
        controller.Tick();
        require(controller.State == FcFulfillmentControllerState.Stopping
                && !runtime.NavigationStopped
                && !chest.HasPendingTransfer,
            "stop waits at the safe boundary while an indivisible craft is active and creates no transfer reservation");

        runtime.CraftingActive = false;
        controller.Tick();
        worker.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(controller.State == FcFulfillmentControllerState.Idle
                && runtime.NavigationStopped
                && !chest.HasPendingTransfer
                && !worker.Status.IsSubscribed,
            "stop after the active boundary only stops navigation and leaves physical inventory/chest untouched");
    }

    private static void ControllerStopsAtEveryActiveBoundaryWithoutDeposit(Action<bool, string> require)
    {
        var actions = new[]
        {
            FcFulfillmentActionKind.Gather,
            FcFulfillmentActionKind.Craft,
            FcFulfillmentActionKind.Withdraw,
            FcFulfillmentActionKind.Deposit,
        };
        foreach (var action in actions)
        {
            var transport = new ControllerTransport();
            var clock = new FcManualClock(4_100_000 + (long)action * 10_000);
            using var worker = new FcWorkerSessionService(
                new FcInMemoryWorkerSessionStateStore(),
                transport,
                () => "controller-boundary-" + action,
                () => transport.LocalAuthorId,
                () => new FcCompatibilityContext(1, "game"),
                ControllerPublishedLists,
                () => new CharacterIdentity("controller-boundary-" + action, "Alice", "World"),
                () => "game",
                clock);
            var start = worker.StartAll(
                FcItemQuantityMap.Empty,
                useOwnStock: true,
                dependencyClosure: [new FcQuantityKey(100, FcItemQuality.Nq)]);
            worker.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            var adapter = new FcFakeChestAdapter(
                Snapshot(action == FcFulfillmentActionKind.Deposit ? 3u : 3u,
                    action == FcFulfillmentActionKind.Deposit ? 1u : 0u))
            {
                IsChestOpen = true,
                LoadState = FcChestLoadState.Complete,
            };
            using var chest = new FcChestCoordinator(
                adapter,
                new FcUnavailableChestRouteAdapter(),
                new FcPendingTransferJournal(),
                _ => { },
                _ => new(FcAtomicTransferCommitStatus.Accepted, string.Empty),
                () => new FcChestLocationProjection(null, false, false));
            var runtime = new ControllerRuntime
            {
                AcceptCraftStart = action == FcFulfillmentActionKind.Craft,
                AcceptGatherStart = action == FcFulfillmentActionKind.Gather,
            };
            var craftPlan = action is FcFulfillmentActionKind.Gather or FcFulfillmentActionKind.Craft
                ? CraftingExecutionPlan.CreateDirect(new CraftingListDefinition
                {
                    ID = 701,
                    Name = "Phase7 boundary",
                })
                : null;
            var decision = new FcFulfillmentPlanDecision(
                true,
                string.Empty,
                action,
                null,
                null,
                Array.Empty<FcLogicalQueueEntry>(),
                action == FcFulfillmentActionKind.Gather ? [100u] : Array.Empty<uint>(),
                action is FcFulfillmentActionKind.Withdraw or FcFulfillmentActionKind.Deposit
                    ? [new ItemTransferRequest(new FcChestItemKey(100, false), 1)]
                    : Array.Empty<ItemTransferRequest>(),
                craftPlan,
                false,
                false,
                false);
            using var controller = new FcFulfillmentController(
                worker,
                chest,
                runtime,
                FreshControllerWorld,
                _ => decision);

            require(start.Accepted && worker.Status.IsSubscribed && controller.Start(),
                $"{action} boundary starts from a subscribed worker session");
            controller.Tick();
            controller.Tick();
            controller.Tick();
            controller.Tick();
            controller.Tick();
            require(controller.State == action switch
            {
                FcFulfillmentActionKind.Gather => FcFulfillmentControllerState.Gathering,
                FcFulfillmentActionKind.Craft => FcFulfillmentControllerState.Crafting,
                FcFulfillmentActionKind.Withdraw => FcFulfillmentControllerState.Withdrawing,
                FcFulfillmentActionKind.Deposit => FcFulfillmentControllerState.Depositing,
                _ => FcFulfillmentControllerState.Blocked,
            },
                $"{action} reaches its active execution boundary through the controller");

            controller.RequestStop();
            controller.Tick();
            if (action is FcFulfillmentActionKind.Withdraw or FcFulfillmentActionKind.Deposit)
            {
                require(controller.State == FcFulfillmentControllerState.Idle
                        && runtime.NavigationStopped
                        && !chest.HasPendingTransfer,
                    $"stop before {action} dispatch cancels only the reservation without deposit");
            }
            else
            {
                require(controller.State == FcFulfillmentControllerState.Stopping
                        && !runtime.NavigationStopped
                        && !chest.HasPendingTransfer,
                    $"stop at {action} boundary waits only for the current indivisible action without deposit");
            }

            runtime.CraftingActive = false;
            runtime.GatheringInteractionActive = false;
            controller.Tick();
            worker.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            require(controller.State == FcFulfillmentControllerState.Idle
                    && runtime.NavigationStopped
                    && !chest.HasPendingTransfer
                    && !worker.Status.IsSubscribed,
                $"stop after {action} boundary unsubscribes without scheduling physical deposit");
        }
    }

    private static void ChestRouteFallbackFailsClosed(Action<bool, string> require)
    {
        var adapter = new FcFakeChestAdapter(Snapshot(2, 0))
        {
            IsChestOpen = false,
            LoadState = FcChestLoadState.Closed,
        };
        using var coordinator = new FcChestCoordinator(
            adapter,
            new FcUnavailableChestRouteAdapter(),
            new FcPendingTransferJournal(),
            _ => { },
            _ => new(FcAtomicTransferCommitStatus.Accepted, "unexpected"),
            () => new FcChestLocationProjection(null, false, false));
        require(coordinator.SelectPublicChestDestination(FcChestLocationCatalog.All[0]),
            "public fallback selection records a destination without assigning work in the mesh");
        coordinator.EnsureChestKnown(false);
        coordinator.Tick();
        require(coordinator.State == FcChestCoordinatorState.Blocked
                && coordinator.LastError.Contains("route adapter", StringComparison.OrdinalIgnoreCase),
            "public fallback blocks explicitly when no typed route adapter is available");
    }

    private static void GatherYieldBoundaryRequiresScopedSessionToken(Action<bool, string> require)
    {
        var counts = new Dictionary<uint, (int NQ, int HQ)> { [100] = (1, 2) };
        var token = new FcGatherInteractionToken(
            Guid.Parse("00000000-0000-0000-0000-00000000070A"),
            Guid.Parse("00000000-0000-0000-0000-00000000070B"),
            7,
            [100],
            FcItemQuality.Hq);
        var boundary = new FcGatherYieldBoundary(itemId => counts[itemId]);
        require(!boundary.Begin([100], token with { ItemIds = [101] }, out _),
            "gather boundary rejects a token whose item identity changed");
        require(boundary.Begin([100], token, out _),
            "gather boundary accepts a valid worker session/generation token");
        counts[100] = (2, 4);
        require(boundary.Complete().Outcome == FcGatherYieldBoundaryOutcome.Blocked,
            "positive inventory delta without an in-token expected-item event is blocked");

        require(boundary.Begin([100], token, out _),
            "a new gather interaction gets a new physical boundary after rejection");
        boundary.ObserveInventoryItem(100);
        counts[100] = (3, 6);
        var observed = boundary.Complete();
        require(observed.Succeeded
                && observed.Quantity.Get(100, FcItemQuality.Nq) == 0
                && observed.Quantity.Get(100, FcItemQuality.Hq) == 2,
            "scoped gather completion emits only the token's expected quality delta");
    }

    private static void GatherYieldPublicationUpdatesWorkerLedger(Action<bool, string> require)
    {
        var transport = new ControllerTransport();
        using var worker = new FcWorkerSessionService(
            new FcInMemoryWorkerSessionStateStore(),
            transport,
            () => "gather-binding-scope",
            () => transport.LocalAuthorId,
            () => new FcCompatibilityContext(1, "game"),
            ControllerPublishedLists,
            () => new CharacterIdentity("gather-binding-scope", "Alice", "World"),
            () => "game",
            new FcManualClock(4_400_000));
        var start = worker.StartAll(
            FcItemQuantityMap.Empty,
            useOwnStock: true,
            dependencyClosure: [new FcQuantityKey(100, FcItemQuality.Nq)]);
        worker.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var counts = 0;
        var boundary = new FcGatherYieldBoundary(_ => (counts, 0));
        var desired = worker.Status.Desired!;
        var token = new FcGatherInteractionToken(
            Guid.Parse("00000000-0000-0000-0000-00000000070D"),
            desired.SessionId,
            desired.SessionGeneration,
            [100],
            FcItemQuality.Nq);
        var began = boundary.Begin([100], token, out _);
        counts = 2;
        boundary.ObserveInventoryItem(100);
        var observed = boundary.Complete();
        var binding = new FcGatherYieldPublicationBinding(worker);
        var published = observed.Succeeded
            ? binding.Publish(new GatherYieldObserved(observed.Quantity))
            : FcWorkerSessionResult.Blocked(observed.Error);
        var held = worker.Status.Ledger?.State.AttributedInventory.Get(100, FcItemQuality.Nq);
        worker.Stop();
        var late = binding.Publish(new GatherYieldObserved(
            new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 1)])));
        require(start.Accepted && began && observed.Succeeded && published.Accepted
                && held == 2
                && !late.Accepted,
            "production gather boundary event binding records one absolute yield and rejects it after unsubscribe/session change");
    }

    private static FcProjectedWorld FreshControllerWorld()
        => new(
            new FcWorldRevision(1, "controller-world"),
            Array.Empty<PublishedListRecord>(),
            Array.Empty<Guid>(),
            Array.Empty<WorkerSessionRecord>(),
            new FcChestProjection(null, true),
            new FcChestLocationProjection(null, false, false),
            new FcFulfillmentMatchResult(
                Array.Empty<FcListDemand>(),
                Array.Empty<FcContribution>()));

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

    private static FcChestSnapshot Snapshot(uint chestQuantity, uint playerQuantity)
    {
        var states = Enum.GetValues<FcChestContainer>()
            .Select(container => new FcChestContainerState(container, true, 1))
            .ToArray();
        var chestSlots = Enum.GetValues<FcChestContainer>()
            .Where(container => container is >= FcChestContainer.FreeCompanyPage1 and <= FcChestContainer.FreeCompanyCrystals)
            .Select(container => new FcChestSlotAddress(container, 0))
            .ToArray();
        var playerSlots = Enum.GetValues<FcChestContainer>()
            .Where(container => container is >= FcChestContainer.Inventory1 and <= FcChestContainer.Inventory4)
            .Select(container => new FcChestSlotAddress(container, 0))
            .ToArray();
        var chestItems = chestQuantity == 0
            ? Array.Empty<FcChestSlotItem>()
            : [new FcChestSlotItem(chestSlots[0], new FcChestItemKey(100, false), chestQuantity)];
        var playerItems = playerQuantity == 0
            ? Array.Empty<FcChestSlotItem>()
            : [new FcChestSlotItem(playerSlots[0], new FcChestItemKey(100, false), playerQuantity)];
        return new FcChestSnapshot(
            true,
            true,
            states,
            chestSlots,
            playerSlots,
            chestItems,
            playerItems);
    }

    private static ChestSnapshotRecord ChestAfterRecord(Guid operationId, string author)
        => new(
            new FcRecordHeader(
                FcProtocolVersion.Current,
                FcProtocolVersion.CurrentSchema,
                FcRecordTypes.ChestSnapshot,
                operationId,
                author,
                1),
            true,
            FcRecordValidator.CompleteChestPageMask,
            Array.Empty<ItemQuantityEntry>(),
            new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>()));

    private sealed class ControllerRuntime : IFcFulfillmentRuntime
    {
        public bool ConnectivityAvailable { get; set; } = true;
        public bool CraftingActive { get; set; }
        public bool GatheringInteractionActive { get; set; }
        public bool AcceptCraftStart { get; set; }
        public bool AcceptGatherStart { get; set; }
        public bool NavigationStopped { get; private set; }
        public bool TryStartCraft(CraftingExecutionPlan plan)
        {
            if (!AcceptCraftStart)
                return false;
            CraftingActive = true;
            return true;
        }
        public bool TryStartGather(IReadOnlyList<uint> targetOrder)
        {
            if (!AcceptGatherStart)
                return false;
            GatheringInteractionActive = true;
            return true;
        }
        public void StopNavigation() => NavigationStopped = true;
    }

    private sealed class ControllerTransport : IFcPublicationTransport
    {
        public bool IsReady => true;
        public string LocalAuthorId { get; } = "controller-author";
        public FcWorldStore WorldStore { get; } = new(new FcManualClock(4_000_000));

        public FcNativeCallResult Put(
            ReadOnlySpan<byte> recordId,
            ReadOnlySpan<byte> key,
            ReadOnlySpan<byte> recordType,
            bool hasGeneration,
            ulong generation,
            ulong revision,
            ReadOnlySpan<byte> payload)
            => new((uint)FcNativeErrorCode.Ok, 0, 0);
    }
}
