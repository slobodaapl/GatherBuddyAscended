using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.Vulcan.Tests;

internal static class FcMeshPhase0Tests
{
    private static readonly Guid ListA = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ListB = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid ListC = Guid.Parse("00000000-0000-0000-0000-00000000000e");
    private static readonly Guid SessionA = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly FcCompatibilityContext LocalCompatibility =
        new(FcPublishedListMapper.CurrentPlannerSemanticsVersion, "game");

    public static void Run(Action<bool, string> require)
    {
        QuantityValidation(require);
        EnvelopeAndConvergence(require);
        HlcAndLiveness(require);
        ChestAndAccounting(require);
        Capabilities(require);
        Ledger(require);
        AtomicTransfers(require);
        CanonicalAndTransport(require);
    }

    private static void QuantityValidation(Action<bool, string> require)
    {
        var duplicateRejected = Throws<ArgumentException>(() => new FcItemQuantityMap([
            new ItemQuantityEntry(100, FcItemQuality.Nq, 1),
            new ItemQuantityEntry(100, FcItemQuality.Nq, 1),
        ]));
        var negativeRejected = Throws<ArgumentOutOfRangeException>(()
            => new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, -1)]));
        var overflowRejected = Throws<OverflowException>(()
            => new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, int.MaxValue)])
                .Add(new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 1)])));
        var qualityRejected = Throws<ArgumentOutOfRangeException>(()
            => new FcItemQuantityMap([new ItemQuantityEntry(100, (FcItemQuality)99, 1)]));
        var crystalDuplicateRejected = Throws<ArgumentException>(()
            => new CrystalQuantityMap([new CrystalQuantityEntry(19, 1), new CrystalQuantityEntry(19, 2)]));
        var crystalNegativeRejected = Throws<ArgumentOutOfRangeException>(()
            => new CrystalQuantityMap([new CrystalQuantityEntry(19, -1)]));
        require(duplicateRejected && negativeRejected && overflowRejected && qualityRejected
                && crystalDuplicateRejected && crystalNegativeRejected,
            "quantity maps reject duplicate, negative, overflow, invalid quality, and duplicate-crystal input");
    }

    private static void EnvelopeAndConvergence(Action<bool, string> require)
    {
        var clock = new FcManualClock(1_000);
        var list = MakeList(ListA, "alice", 1, true, "Ore");
        var worker = MakeWorker("alice", SessionA, 1, 1, 60, ListA, 1_000);
        var chest = MakeChest("bob", 1, 40, 1_000);
        var envelopes = new[]
        {
            Envelope(list, "alice", 1_000),
            Envelope(worker, "alice", 1_001, worker.SessionGeneration),
            Envelope(chest, "bob", 1_002),
        };
        var left = new FcWorldStore(clock);
        var right = new FcWorldStore(clock);
        foreach (var envelope in envelopes.Reverse())
        {
            var payload = Decode(envelope);
            require(Apply(left, envelope, payload).Accepted, "reversed absolute envelope accepted by first peer");
            require(Apply(right, FakeMeshTransport.RoundTrip(envelope), payload).Accepted,
                "serialized absolute envelope accepted by second peer");
            require(Apply(left, envelope, payload).Status == FcApplyStatus.Duplicate,
                "duplicate absolute delivery is idempotent");
        }
        var leftWorld = new FcWorldProjection(left).Build(clock, LocalCompatibility);
        var rightWorld = new FcWorldProjection(right).Build(clock, LocalCompatibility);
        require(left.Revision == right.Revision
                && leftWorld.ActiveListIds.SequenceEqual([ListA])
                && rightWorld.Fulfillment.TotalMatched == 100,
            "arbitrary delivery order converges and global chest capacity is counted once");

        var orderFirst = MakeList(ListB, "alice", 1, true, "first");
        var orderSecond = MakeList(ListB, "alice", 2, true, "second");
        var orderLeft = new FcWorldStore(clock);
        var orderRight = new FcWorldStore(clock);
        require(ApplyRecord(orderLeft, orderFirst, 1_000).Accepted
                && ApplyRecord(orderLeft, orderSecond, 1_001).Accepted
                && ApplyRecord(orderRight, orderSecond, 1_001).Accepted
                && ApplyRecord(orderRight, orderFirst, 1_000).Status == FcApplyStatus.Rejected
                && orderLeft.Revision.Fingerprint == orderRight.Revision.Fingerprint
                && orderLeft.Lists.Values.Single().DisplayName == "second"
                && orderRight.Lists.Values.Single().DisplayName == "second",
            "superseded absolute revisions converge to the same projection independent of delivery order");

        var tampered = envelopes[0] with { PayloadHash = "00" };
        require(Apply(left, tampered, list).Status == FcApplyStatus.Rejected,
            "tampered payload hash is rejected before storage");
        var wrongKey = envelopes[0] with { DocumentKeyOwnerId = "bob" };
        require(Apply(left, wrongKey, list).Status == FcApplyStatus.Rejected,
            "document-key owner mismatch is rejected");
        var wrongSignature = envelopes[0] with { Signature = string.Empty };
        require(Apply(left, wrongSignature, list).Status == FcApplyStatus.Rejected,
            "signature tampering is rejected");
        require(left.Apply(envelopes[0], list).Status == FcApplyStatus.Rejected,
            "an absent native verification context is rejected");
        var falseContext = VerifiedContext(envelopes[0]) with { SignatureValid = false };
        require(left.Apply(envelopes[0], list, falseContext).Status == FcApplyStatus.Rejected,
            "a native verification context that reports an invalid signature is rejected");
        var wrongTransport = VerifiedContext(envelopes[0]) with { TransportAuthorId = "bob" };
        require(left.Apply(envelopes[0], list, wrongTransport).Status == FcApplyStatus.Rejected,
            "transport-authenticated author mismatch is rejected");
        var wrongContextAuthor = VerifiedContext(envelopes[0]) with
        {
            ActualAuthorId = "bob",
            KeyOwnerId = "bob",
            TransportAuthorId = "bob",
        };
        require(left.Apply(envelopes[0], list, wrongContextAuthor).Status == FcApplyStatus.Rejected,
            "verified context author/key-owner mismatch is rejected");
        var wrongContextKey = VerifiedContext(envelopes[0]) with { DocumentKey = "v1/lists/bob/not-the-key" };
        require(left.Apply(envelopes[0], list, wrongContextKey).Status == FcApplyStatus.Rejected,
            "verified context document-key mismatch is rejected");
        var wrongContextHash = VerifiedContext(envelopes[0]) with { ContentHash = "00" };
        require(left.Apply(envelopes[0], list, wrongContextHash).Status == FcApplyStatus.Rejected,
            "verified context content hash mismatch is rejected");
        var headerMismatch = list with
        {
            Header = list.Header with { OwnerAuthorId = "bob" },
        };
        var headerMismatchEnvelope = Envelope(headerMismatch, "alice", 1_000);
        require(Apply(left, headerMismatchEnvelope, headerMismatch).Status == FcApplyStatus.Rejected,
            "typed header owner mismatch is rejected at the envelope boundary");
        var wrongGeneration = envelopes[1] with { Generation = 2 };
        require(Apply(left, wrongGeneration, worker).Status == FcApplyStatus.Rejected,
            "worker generation mismatch between native envelope and inner state is rejected");

        var forkBase = MakeList(ListA, "alice", 1, true, "base");
        var forkA = MakeList(ListA, "alice", 2, true, "fork-a");
        var forkB = MakeList(ListA, "alice", 2, true, "fork-b");
        var forkBaseEnvelope = Envelope(forkBase, "alice", 1_000);
        var forkAEnvelope = Envelope(forkA, "alice", 1_001);
        var forkBEnvelope = Envelope(forkB, "alice", 1_002);
        var forkLeft = new FcWorldStore(clock);
        var forkRight = new FcWorldStore(clock);
        require(Apply(forkLeft, forkBaseEnvelope, forkBase).Accepted
                && Apply(forkRight, forkBaseEnvelope, forkBase).Accepted,
            "fork peers establish a common predecessor");
        require(Apply(forkLeft, forkAEnvelope, forkA).Accepted
                && Apply(forkRight, forkBEnvelope, forkB).Accepted,
            "fork peers accept divergent same-author revisions locally");
        require(Apply(forkLeft, forkBEnvelope, forkB).Status == FcApplyStatus.Fork
                && Apply(forkRight, forkAEnvelope, forkA).Status == FcApplyStatus.Fork
                && forkLeft.ForkVariants.Values.Single().Count == 2
                && forkLeft.ForkVariants.Values.Single().All(variant => variant.Payload.SequenceEqual(variant.Envelope.Payload))
                && forkLeft.Diagnostics.SequenceEqual(forkRight.Diagnostics)
                && forkLeft.Revision.Fingerprint == forkRight.Revision.Fingerprint,
            "same-revision variants are retained, sorted, and blocked independent of arrival order");
        var forkRevision = forkLeft.Revision;
        require(Apply(forkLeft, forkBEnvelope, forkB).Status == FcApplyStatus.Fork
                && forkLeft.Revision == forkRevision,
            "duplicate fork evidence does not create another semantic world revision");
        require(new FcWorldProjection(forkLeft).Build(clock, LocalCompatibility).ActiveLists.Count == 0,
            "forked public list is excluded from actionable projection");
        var repair = MakeList(ListA, "alice", 3, true, "repair");
        require(ApplyRecord(forkLeft, repair, 1_003).Accepted
                && !forkLeft.IsForked("alice/list/" + ListA.ToString("D")),
            "strictly greater authoritative revision repairs and clears a fork");

        var tombstoneStore = new FcWorldStore(clock);
        require(ApplyRecord(tombstoneStore, MakeList(ListB, "bob", 1, true, "published"), 1_000).Accepted
                && ApplyRecord(tombstoneStore, MakeList(ListB, "bob", 2, false, "published"), 1_001).Accepted
                && tombstoneStore.Lists.Values.Single().Published == false
                && ApplyRecord(tombstoneStore, MakeList(ListB, "bob", 1, true, "published"), 1_002).Status == FcApplyStatus.Rejected
                && new FcWorldProjection(tombstoneStore).Build(clock, LocalCompatibility).ActiveLists.Count == 0,
            "newer unpublished tombstone prevents an older public-list resurrection");
    }

    private static void HlcAndLiveness(Action<bool, string> require)
    {
        var clock = new FcManualClock(1_000);
        var hlc = new FcHlcClock("alice", clock);
        var first = hlc.Next(clock);
        hlc.Observe(new FcHlcTimestamp(first.PhysicalUnixMs, first.Logical + 9, "bob"), clock);
        var causal = hlc.Next(clock);
        require(causal > first && causal > new FcHlcTimestamp(first.PhysicalUnixMs, first.Logical + 9, "bob"),
            "accepted remote HLC advances subsequent local timestamps");
        var persistedHlc = hlc.ExportState();
        var restartedHlc = new FcHlcClock("alice", clock, persistedHlc);
        require(restartedHlc.Next(clock) > persistedHlc.Last,
            "persisted local HLC state prevents timestamp regression after restart");
        require(new FcHlcTimestamp(2_000, 0, "alice") > new FcHlcTimestamp(1_999, ulong.MaxValue, "bob"),
            "HLC orders physical before logical and node identity");
        require(new FcHlcTimestamp(2_000, 7, "alice") < new FcHlcTimestamp(2_000, 7, "bob"),
            "concurrent equal-physical HLC values use logical and node identity tie-breakers");

        var regressionStore = new FcWorldStore(clock);
        require(ApplyRecord(regressionStore, MakeList(ListA, "alice", 1, true, "clocked"), 1_000).Accepted
                && ApplyRecord(regressionStore, MakeList(ListA, "alice", 2, true, "regressed"), 999).Status == FcApplyStatus.Rejected,
            "same-author newer revision cannot regress its HLC");

        var worker = MakeWorker("alice", SessionA, 1, 1, 0, ListA, 1_000);
        var tracker = new FcLivenessTracker();
        require(tracker.IsActive(worker, new FcHlcTimestamp(1_000, 0, "alice"), 300_999),
            "worker stays active before the 300-second boundary");
        require(tracker.IsActive(worker, new FcHlcTimestamp(1_000, 0, "22222222222222222222222222222222"), 300_999),
            "worker liveness uses validated authorship and physical freshness, not HLC node identity");
        require(!tracker.IsActive(worker, new FcHlcTimestamp(1_000, 0, "alice"), 301_000),
            "worker expires at exactly 300 seconds");
        require(!tracker.IsActive(worker, new FcHlcTimestamp(1_000, 0, "22222222222222222222222222222222"), 301_000),
            "a distinct HLC node does not prevent physical liveness expiry");
        require(!tracker.IsActive(worker, new FcHlcTimestamp(301_001, 0, "alice"), 1_000),
            "future-drifted worker stamp cannot create liveness");
        var independentNodeStore = new FcWorldStore(clock);
        var independentNodeEnvelope = Envelope(worker, "alice", 1_000, worker.SessionGeneration) with
        {
            Hlc = new FcHlcTimestamp(1_000, 0, "22222222222222222222222222222222"),
        };
        require(Apply(independentNodeStore, independentNodeEnvelope, worker).Accepted
                && new FcWorldProjection(independentNodeStore).Build(clock, LocalCompatibility).ActiveWorkers.Count == 1,
            "projected subscribed worker remains active when validated HLC node is independent from author");

        var delayedStore = new FcWorldStore(clock);
        var delayedWorker = MakeWorker("alice", SessionA, 1, 1, 25, ListA, 1_000);
        require(ApplyRecord(delayedStore, delayedWorker, 1_000).Accepted,
            "delayed-frame fixture accepts the original authored worker state");
        clock.Set(301_000);
        require(new FcWorldProjection(delayedStore).Build(clock, LocalCompatibility).ActiveWorkers.Count == 0
                && ApplyRecord(delayedStore, delayedWorker, 1_000).Status == FcApplyStatus.Duplicate
                && new FcWorldProjection(delayedStore).Build(clock, LocalCompatibility).ActiveWorkers.Count == 0,
            "delayed old worker delivery cannot revive an HLC-expired worker");
        var unseenDelayedStore = new FcWorldStore(clock);
        require(ApplyRecord(unseenDelayedStore, delayedWorker, 1_000).Accepted
                && new FcWorldProjection(unseenDelayedStore).Build(clock, LocalCompatibility).ActiveWorkers.Count == 0,
            "a stale worker frame received for the first time cannot be revived by arrival time");
        clock.Set(1_000);

        var schedulerClock = new FcManualClock(1_000);
        var scheduler = new FcWorkerRefreshScheduler(worker, new FcHlcClock("alice", schedulerClock), schedulerClock);
        schedulerClock.Advance(TimeSpan.FromSeconds(60));
        var refresh1 = scheduler.TryCreateIdleRefresh(schedulerClock);
        schedulerClock.Advance(TimeSpan.FromSeconds(60));
        var refresh2 = scheduler.TryCreateIdleRefresh(schedulerClock);
        require(refresh1 is not null && refresh2 is not null
                && refresh1.Revision == 2 && refresh2.Revision == 3
                && refresh2.Hlc > refresh1.Hlc,
            "idle worker refreshes increment absolute revision every 60 seconds indefinitely");
        var meaningful = worker with { Header = worker.Header with { Revision = 4 } };
        scheduler.ObserveMeaningfulUpdate(meaningful, schedulerClock);
        schedulerClock.Advance(TimeSpan.FromSeconds(59));
        require(scheduler.TryCreateIdleRefresh(schedulerClock) is null,
            "meaningful worker interaction resets idle-refresh necessity");
        schedulerClock.Advance(TimeSpan.FromSeconds(1));
        require(scheduler.TryCreateIdleRefresh(schedulerClock) is not null,
            "idle refresh resumes one interval after the meaningful update");
        var unsubscribed = worker with
        {
            Header = worker.Header with { Revision = 2 },
            State = FcWorkerState.Unsubscribed,
        };
        var unsubscribeScheduler = new FcWorkerRefreshScheduler(unsubscribed, new FcHlcClock("alice", schedulerClock), schedulerClock);
        schedulerClock.Advance(TimeSpan.FromMinutes(1));
        require(unsubscribeScheduler.TryCreateIdleRefresh(schedulerClock) is null,
            "unsubscribed worker does not emit idle heartbeats");

        var stopStore = new FcWorldStore(clock);
        var subscribed = MakeWorker("alice", SessionA, 1, 1, 30, ListA, 1_000);
        var stopped = subscribed with
        {
            Header = subscribed.Header with { Revision = 2 },
            State = FcWorkerState.Unsubscribed,
        };
        require(ApplyRecord(stopStore, subscribed, 1_000).Accepted
                && ApplyRecord(stopStore, stopped, 1_001).Accepted
                && stopStore.Workers.Values.Single().HeldInventory.Single().Quantity == 30
                && new FcWorldProjection(stopStore).Build(clock, LocalCompatibility).ActiveWorkers.Count == 0,
            "unsubscribe leaves physical held inventory unchanged and removes it from active FC supply");
        var revived = stopped with
        {
            Header = stopped.Header with { Revision = 3 },
            State = FcWorkerState.Active,
        };
        require(ApplyRecord(stopStore, revived, 1_002).Accepted
                && new FcWorldProjection(stopStore).Build(clock, LocalCompatibility).ActiveWorkers.Count == 1,
            "a current-generation normal frame revives an explicitly unsubscribed worker");

        var persisted = new FcWorldStore(clock);
        require(ApplyRecord(persisted, MakeList(ListB, "bob", 1, true, "persisted"), 1_000).Accepted,
            "persisted high-water fixture accepts first revision");
        var state = persisted.ExportPersistenceState();
        var restarted = new FcWorldStore(clock, persisted: state);
        require(ApplyRecord(restarted, MakeList(ListB, "bob", 1, true, "persisted"), 1_000).Status == FcApplyStatus.Rejected,
            "restart high-water state prevents revision reuse");
        var stateJson = JsonSerializer.Serialize(state, FcJsonContext.Default.FcWorldPersistenceState);
        var stateRoundTrip = JsonSerializer.Deserialize(stateJson, FcJsonContext.Default.FcWorldPersistenceState)!;
        require(state.RevisionHighWaters.Length == 1
                && state.RegisterHlcHighWaters.Length == 1
                && stateRoundTrip.RevisionHighWaters.Single().Revision == 1
                && stateRoundTrip.RegisterHlcHighWaters.Single().Hlc == state.RegisterHlcHighWaters.Single().Hlc,
            "revision and register-HLC high-water state round-trips as explicit persistence entries");

        var reservationStore = new FcWorldStore(clock);
        var reservedKey = "alice/list/" + ListC.ToString("D");
        require(reservationStore.ReserveRevision(reservedKey, 3).Accepted,
            "revision reservation is durable before the record put");
        var reservationState = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(reservationStore.ExportPersistenceState(), FcJsonContext.Default.FcWorldPersistenceState),
            FcJsonContext.Default.FcWorldPersistenceState)!;
        var reservationRestarted = new FcWorldStore(clock, persisted: reservationState);
        require(ApplyRecord(reservationRestarted, MakeList(ListC, "alice", 3, true, "reserved"), 1_000).Status
                    == FcApplyStatus.Rejected,
            "crash after reservation and before put cannot reuse the reserved revision");
        var corruptState = new FcWorldPersistenceState(
            [new FcRevisionHighWaterEntry(string.Empty, 1)],
            Array.Empty<FcRegisterHlcHighWaterEntry>(),
            Array.Empty<FcWorkerHighWaterEntry>());
        require(Throws<ArgumentException>(() => new FcWorldStore(clock, persisted: corruptState)),
            "corrupt persisted high-water state fails closed before mutation");

        var generationStore = new FcWorldStore(clock);
        require(ApplyRecord(generationStore, MakeWorker("alice", SessionA, 1, 2, 0, ListA, 1_000), 1_000).Accepted,
            "worker generation predecessor establishes persisted generation high-water");
        var generationRestarted = new FcWorldStore(clock, persisted: generationStore.ExportPersistenceState());
        require(ApplyRecord(generationRestarted, MakeWorker("alice", SessionA, 2, 1, 0, ListA, 1_001), 1_001).Status == FcApplyStatus.Rejected,
            "restart high-water state prevents worker generation regression");

        var driftStore = new FcWorldStore(clock, options: new FcWorldStoreOptions(TimeSpan.FromSeconds(5)));
        var drifted = Envelope(MakeList(ListA, "alice", 1, true, "future"), "alice", 1_000 + 6_000);
        require(Apply(driftStore, drifted, Decode(drifted)).Status == FcApplyStatus.Rejected,
            "future HLC delta is quarantined before actionable storage");

        var remoteClock = new FcHlcClock("alice", clock);
        var reseededStore = new FcWorldStore(clock, hlcClock: remoteClock);
        var remoteList = Envelope(MakeList(ListA, "bob", 1, true, "remote"), "bob", 2_000);
        require(Apply(reseededStore, remoteList, Decode(remoteList)).Accepted
                && remoteClock.Next(clock) > remoteList.Hlc,
            "accepted remote envelope reseeds the local HLC before the next authored record");
    }

    private static void ChestAndAccounting(Action<bool, string> require)
    {
        var clock = new FcManualClock(1_000);
        var store = new FcWorldStore(clock);
        var listA = MakeList(ListA, "alice", 1, true, "A");
        var listB = MakeList(ListB, "bob", 1, true, "B");
        var alice = MakeWorker("alice", SessionA, 1, 1, 30, ListA, 1_000);
        var bob = MakeWorker("bob", Guid.Parse("00000000-0000-0000-0000-000000000004"), 1, 1, 30, ListB, 1_000);
        var greg = MakeWorker("greg", Guid.Parse("00000000-0000-0000-0000-000000000005"), 1, 1, 40, ListA, 1_000);
        var chestA = MakeChest("alice", 1, 40, 1_000);
        var chestSource = new FakeChestSource(chestA);
        require(ApplyRecord(store, listA, 1_000).Accepted
                && ApplyRecord(store, listB, 1_000).Accepted
                && ApplyRecord(store, alice, 1_000).Accepted
                && ApplyRecord(store, bob, 1_000).Accepted
                && ApplyRecord(store, greg, 1_000).Accepted
                && ApplyRecord(store, chestA, 1_000).Accepted,
            "accounting fixture accepts lists, workers, and complete chest");
        require(chestSource.ReadComplete().Complete && chestSource.ReadComplete().ItemMap.Get(100) == 40,
            "fake chest source exposes only a complete snapshot to Phase-0 projection tests");
        var projection = new FcWorldProjection(store).Build(clock, LocalCompatibility);
        require(projection.ActiveListIds.SequenceEqual([ListA, ListB])
                && projection.Fulfillment.TotalDemand == 200
                && projection.Fulfillment.TotalMatched == 140,
            "Alice/Greg selection of A and Bob selection of B create global 200 demand with no held double count");
        var allStore = new FcWorldStore(clock);
        var allWorker = MakeWorker("all", Guid.Parse("00000000-0000-0000-0000-00000000000c"), 1, 1, 0, ListA, 1_000)
            with { Selection = FcFulfillmentSelection.All };
        require(ApplyRecord(allStore, listA, 1_000).Accepted
                && ApplyRecord(allStore, listB, 1_000).Accepted
                && ApplyRecord(allStore, allWorker, 1_000).Accepted
                && new FcWorldProjection(allStore).Build(clock, LocalCompatibility).ActiveListIds.SequenceEqual([ListA, ListB]),
            "AllPublishedLists expands all compatible public lists exactly once");
        var incompatibleStore = new FcWorldStore(clock);
        var incompatible = listB with
        {
            Header = listB.Header with { OwnerAuthorId = "charlie" },
            GameVersion = "other-game",
        };
        require(ApplyRecord(incompatibleStore, listA, 1_000).Accepted
                && ApplyRecord(incompatibleStore, incompatible, 1_000).Accepted
                && ApplyRecord(incompatibleStore, allWorker, 1_000).Accepted
                && new FcWorldProjection(incompatibleStore).Build(clock, LocalCompatibility).ActiveListIds.SequenceEqual([ListA]),
            "AllPublishedLists excludes incompatible public list snapshots");
        var reversedCompatibilityStore = new FcWorldStore(clock);
        require(ApplyRecord(reversedCompatibilityStore, incompatible, 1_000).Accepted
                && ApplyRecord(reversedCompatibilityStore, listA, 1_001).Accepted
                && ApplyRecord(reversedCompatibilityStore, allWorker, 1_002).Accepted
                && new FcWorldProjection(reversedCompatibilityStore)
                    .Build(clock, LocalCompatibility)
                    .ActiveListIds.SequenceEqual([ListA]),
            "local compatibility filtering is independent of remote list arrival order");
        var source = new FcRepresentedInventorySource(projection.Chest.Items);
        var local = new FcLocalConsumableInventorySource(alice.HeldInventoryMap);
        require(source.GetNq(100) == 40 && source.GetHq(100) == 0
                && local.GetNq(100) == 30 && local.GetHq(100) == 0,
            "represented chest source and local consumable source remain separate");

        var hq = FcFulfillmentMatcher.Match(
            [new FcListDemand(ListA, 100, FcItemQuality.Nq, 3), new FcListDemand(ListA, 100, FcItemQuality.Hq, 2)],
            new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 3)]),
            [new FcWorkerSupply("alice", new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Hq, 2)]), new HashSet<Guid> { ListA })]);
        require(hq.TotalMatched == 5 && hq.RemainingDemand.Entries.Length == 0,
            "NQ/HQ physical capacities match independently");
        var excluded = FcFulfillmentMatcher.Match(
            [new FcListDemand(ListB, 100, FcItemQuality.Nq, 30)],
            FcItemQuantityMap.Empty,
            [new FcWorkerSupply("alice", new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 30)]), new HashSet<Guid> { ListA })]);
        require(excluded.TotalMatched == 0,
            "held capacity outside worker selection cannot satisfy another list");

        var newerChest = MakeChest("bob", 1, 99, 1_001);
        require(ApplyRecord(store, newerChest, 1_001).Accepted,
            "independent chest observer can publish a complete observation");
        var selected = new FcWorldProjection(store)
            .Build(clock, LocalCompatibility, TimeSpan.FromSeconds(10));
        require(selected.Chest.Snapshot?.Header.OwnerAuthorId == "bob"
                && selected.Chest.Items.Get(100) == 99,
            "projected chest selects greatest fresh HLC across observer registers");
        clock.Set(11_001);
        require(!new FcWorldProjection(store)
                    .Build(clock, LocalCompatibility, TimeSpan.FromSeconds(10))
                    .Chest.IsFresh,
            "chest TTL uses authored HLC and injected clock");
    }

    private static void Capabilities(Action<bool, string> require)
    {
        var clock = new FcManualClock(1_000);
        var requestId = Guid.Parse("00000000-0000-0000-0000-000000000006");
        var request = new CapabilityRequestRecord(
            Header(FcRecordTypes.CapabilityRequest, requestId, "alice", 1),
            requestId,
            "world",
            new FcHlcTimestamp(2_000, 0, "22222222222222222222222222222222"),
            [new RequiredCraftCapability(1, new FcQualityPolicy([new FcQualityRule(100, FcItemQuality.Hq, 1)]))]);
        var response = new CapabilityResponseRecord(
            Header(FcRecordTypes.CapabilityResponse, requestId, "bob", 1),
            requestId,
            new FcHlcTimestamp(2_000, 0, "33333333333333333333333333333333"),
            "world",
            "game",
            "planner",
            "gearset",
            "solver",
            [new CraftCapabilityResult(1, true, 8, true, FcRaphaelAssessmentOutcome.FullQuality)]);
        var responder = MakeWorker("bob", Guid.Parse("00000000-0000-0000-0000-000000000007"), 1, 1, 0, ListA, 1_000);
        var capabilityStore = new FcWorldStore(clock);
        require(ApplyRecord(capabilityStore, request, 1_000).Accepted
                && ApplyRecord(capabilityStore, response, 1_000).Accepted,
            "capability request and response are absolute registers carried through the store");
        var capabilityMesh = new FakeMeshTransport();
        capabilityMesh.Connect("alice", "bob");
        capabilityMesh.Publish("alice", Envelope(request, "alice", 1_000), duplicate: true);
        capabilityMesh.Publish("bob", Envelope(response, "bob", 1_000), duplicate: true);
        var capabilityAlice = new FcWorldStore(clock);
        var capabilityBob = new FcWorldStore(clock);
        require(ApplyFrames(capabilityAlice, capabilityMesh.Deliver("alice", permutationSeed: 5))
                && ApplyFrames(capabilityBob, capabilityMesh.Deliver("bob", reversed: true))
                && capabilityAlice.CapabilityResponses.Count == 1
                && capabilityBob.CapabilityRequests.Count == 1,
            "fake serialized transport delivers request/response records to their connected stores");
        require(FcWorldProjection.IsCapabilityValid(request, response, responder,
                new FcHlcTimestamp(1_000, 0, "44444444444444444444444444444444"), "world", clock),
            "capability response uses validated responder authorship and fresh physical time, not HLC node identity");
        require(!FcWorldProjection.IsCapabilityValid(request, response, "other", clock),
            "world fingerprint mismatch invalidates capability response");
        clock.Set(2_000);
        require(!FcWorldProjection.IsCapabilityValid(request, response, "world", clock),
            "HLC capability expiry is enforced");
    }

    private static void Ledger(Action<bool, string> require)
    {
        var starting = new FcItemQuantityMap([
            new ItemQuantityEntry(100, FcItemQuality.Nq, 10),
            new ItemQuantityEntry(100, FcItemQuality.Hq, 2),
        ]);
        var ledger = new FcContributionLedger(new FcContributionLedgerState(
            SessionA, starting, starting, FcItemQuantityMap.Empty, false)
        {
            SessionGeneration = 2,
            Selection = FcFulfillmentSelection.Specific(ListA),
            QueueReferences = [new FcLogicalQueueEntry(1, 2, ListA)],
        });
        ledger.RecordGatherYield(new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 5)]));
        ledger.RecordCraftOutput(new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Hq, 1)]));
        require(ledger.GetPublishableHeld().Get(100, FcItemQuality.Nq) == 5
                && ledger.GetPublishableHeld().Get(100, FcItemQuality.Hq) == 1
                && !ledger.CanConsumeLocally(new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 11)])),
            "ledger attributes gather/craft output, protects mixed NQ/HQ starting stock, and gates local consumption");
        ledger.RecordChestWithdrawal(new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 2)]));
        ledger.RecordCraftMaterialConsumption(new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 3)]));
        ledger.RecordChestDeposit(new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 1)]));
        ledger.RecordDiscardOrTransfer(new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 1)]));
        ledger.Recover(new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 1)]));
        require(ledger.GetPublishableHeld().Get(100, FcItemQuality.Nq) == 0
                && ledger.GetPublishableHeld().Get(100, FcItemQuality.Hq) == 0,
            "ledger recovery clamps mixed-quality attributed state to actual physical state after transfer/discard");
        var recovery = ledger.ExportRecovery(FcFulfillmentSelection.Specific(ListA));
        var recoveryJson = JsonSerializer.Serialize(recovery, FcJsonContext.Default.FcContributionLedgerRecovery);
        var recoveryRoundTrip = JsonSerializer.Deserialize(
            recoveryJson, FcJsonContext.Default.FcContributionLedgerRecovery)!;
        var recovered = FcContributionLedger.Recover(recoveryRoundTrip, ledger.CurrentPhysical);
        require(recovered.State.SessionGeneration == 2
                && recovered.State.QueueReferences.Length == 1
                && recovered.State.Selection?.ListIds.SequenceEqual([ListA]) == true,
            "recovery serializes session generation, selection, and logical queue references");
        require(Throws<ArgumentNullException>(()
                    => FcContributionLedger.Recover(null!, ledger.CurrentPhysical))
                && Throws<ArgumentNullException>(()
                    => FcContributionLedger.Recover(recovery, null!))
                && Throws<ArgumentException>(()
                    => FcContributionLedger.Recover(recovery with { SessionId = Guid.Empty }, ledger.CurrentPhysical))
                && Throws<ArgumentException>(()
                    => FcContributionLedger.Recover(recovery with { StartingInventory = null! }, ledger.CurrentPhysical))
                && Throws<ArgumentException>(()
                    => FcContributionLedger.Recover(recovery with { Selection = null! }, ledger.CurrentPhysical))
                && Throws<ArgumentException>(()
                    => FcContributionLedger.Recover(recovery with { QueueReferences = null! }, ledger.CurrentPhysical)),
            "missing or corrupted ledger recovery state fails closed");
        var own = new FcContributionLedger(new FcContributionLedgerState(
            SessionA, starting, starting, FcItemQuantityMap.Empty, true));
        own.ReconcilePhysical(new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 4)]));
        require(own.GetPublishableHeld([new FcQuantityKey(100, FcItemQuality.Nq)]).Get(100) == 4,
            "own-stock mode publishes only physical stock in the supplied dependency closure");
    }

    private static void AtomicTransfers(Action<bool, string> require)
    {
        var clock = new FcManualClock(1_000);
        var store = new FcWorldStore(clock);
        var worker1 = MakeWorker("alice", SessionA, 1, 1, 0, ListA, 1_000);
        var chest1 = MakeChest("alice", 1, 100, 1_000);
        require(ApplyRecord(store, worker1, 1_000).Accepted && ApplyRecord(store, chest1, 1_000).Accepted,
            "transfer fixture establishes current worker/chest states");
        var worker2 = MakeWorker("alice", SessionA, 2, 1, 100, ListA, 1_001);
        var chest2 = MakeChest("alice", 2, 0, 1_001);
        var operation = Guid.Parse("00000000-0000-0000-0000-000000000008");
        var withdraw = MakeTransfer(operation, 1, FcInventoryTransferKind.Withdraw,
            FcInventoryTransferOutcome.Committed, 100, worker2, chest2);
        var before = store.Revision;
        require(ApplyRecord(store, withdraw, 1_001).Accepted
                && store.Revision.Number == before.Number + 1
                && store.Workers.Values.Single().HeldInventory.Single().Quantity == 100
                && store.Chests.Values.Single().ItemMap.Get(100) == 0,
            "withdraw applies complete worker/chest after-states atomically in one world revision");
        require(ApplyRecord(store, withdraw, 1_001).Status == FcApplyStatus.Duplicate,
            "operation-id replay is idempotent");
        var conflicting = withdraw with
        {
            ActualTransferred = [new ItemQuantityEntry(100, FcItemQuality.Nq, 99)],
        };
        require(ApplyRecord(store, conflicting, 1_001).Status == FcApplyStatus.Fork
                && store.IsForked("alice/transfer/" + operation.ToString("D"))
                && store.Workers.Values.Single().HeldInventory.Single().Quantity == 100
                && store.Chests.Values.Single().ItemMap.Get(100) == 0,
            "conflicting duplicate operation ID is retained as a fork without half-applying state");
        var depositStore = new FcWorldStore(clock);
        var depositBeforeWorker = MakeWorker("alice", SessionA, 1, 1, 100, ListA, 1_000);
        var depositBeforeChest = MakeChest("alice", 1, 0, 1_000);
        var depositAfterWorker = MakeWorker("alice", SessionA, 2, 1, 0, ListA, 1_001);
        var depositAfterChest = MakeChest("alice", 2, 100, 1_001);
        var committedDeposit = MakeTransfer(Guid.Parse("00000000-0000-0000-0000-000000000010"), 1,
            FcInventoryTransferKind.Deposit, FcInventoryTransferOutcome.Committed, 100,
            depositAfterWorker, depositAfterChest);
        require(ApplyRecord(depositStore, depositBeforeWorker, 1_000).Accepted
                && ApplyRecord(depositStore, depositBeforeChest, 1_000).Accepted
                && ApplyRecord(depositStore, committedDeposit, 1_001).Accepted
                && depositStore.Workers.Values.Single().HeldInventory.Length == 0
                && depositStore.Chests.Values.Single().ItemMap.Get(100) == 100,
            "committed deposit applies worker and chest after-states atomically in one operation");
        var invalid = withdraw with { ChestAfter = chest2 with { LoadedPageMask = 0 } };
        var afterDuplicate = store.Revision;
        require(ApplyRecord(store, invalid, 1_001).Status == FcApplyStatus.Rejected
                && store.Revision == afterDuplicate
                && store.Workers.Values.Single().HeldInventory.Single().Quantity == 100,
            "invalid nested after-state leaves both registers untouched");

        var worker3 = MakeWorker("alice", SessionA, 3, 1, 50, ListA, 1_002);
        var chest3 = MakeChest("alice", 3, 50, 1_002);
        var deposit = MakeTransfer(Guid.Parse("00000000-0000-0000-0000-000000000009"), 2,
            FcInventoryTransferKind.Deposit, FcInventoryTransferOutcome.ReconciledFailure, 50, worker3, chest3);
        require(ApplyRecord(store, deposit, 1_002).Accepted
                && store.Workers.Values.Single().HeldInventory.Single().Quantity == 50
                && store.Chests.Values.Single().ItemMap.Get(100) == 50,
            "partial physical deposit is represented by one reconciled-failure transfer and both after-states");
        var failedWorker = worker3;
        var failedChest = chest3;
        var failedTransfer = MakeTransfer(Guid.Parse("00000000-0000-0000-0000-00000000000c"), 3,
            FcInventoryTransferKind.Deposit, FcInventoryTransferOutcome.ReconciledFailure, 0,
            failedWorker, failedChest);
        require(ApplyRecord(store, failedTransfer, 1_003).Accepted
                && store.Workers.Values.Single().HeldInventory.Single().Quantity == 50
                && store.Chests.Values.Single().ItemMap.Get(100) == 50,
            "a fully failed physical action can reconcile as one zero-quantity transfer without half mutation");

        var crystalChangedChest = failedChest with
        {
            Crystals = new CrystalQuantityMap([new CrystalQuantityEntry(19, 11)]),
        };
        var crystalTransfer = MakeTransfer(Guid.Parse("00000000-0000-0000-0000-00000000000d"), 5,
            FcInventoryTransferKind.Deposit, FcInventoryTransferOutcome.ReconciledFailure, 0,
            failedWorker, crystalChangedChest);
        require(ApplyRecord(store, crystalTransfer, 1_004).Status == FcApplyStatus.Rejected
                && store.Workers.Values.Single().HeldInventory.Single().Quantity == 50
                && store.Chests.Values.Single().ItemMap.Get(100) == 50,
            "crystal-only transfer changes use the separate reconciliation path and cannot half-apply item state");

        var journal = new FcPendingTransferJournal();
        var pending = new FcTransferPreOperation(
            Guid.Parse("00000000-0000-0000-0000-00000000000a"), SessionA, 1,
            FcInventoryTransferKind.Withdraw, ListA, "chest-before", "worker-before",
            new CrystalQuantityMap([new CrystalQuantityEntry(19, 10)]), DateTimeOffset.UnixEpoch);
        journal.Begin(pending);
        require(journal.HasPending && !journal.AllowsWorkerPublication
                && journal.TryGet(pending.OperationId, out _)
                && journal.Complete(pending.OperationId) && !journal.HasPending
                && journal.AllowsWorkerPublication,
            "pre-operation fingerprint journal remains local until reconciliation completes");

        journal.Begin(pending);
        var journalJson = JsonSerializer.Serialize(journal.ExportState(), FcJsonContext.Default.FcPendingTransferJournalState);
        var restoredJournal = FcPendingTransferJournal.Restore(
            JsonSerializer.Deserialize(journalJson, FcJsonContext.Default.FcPendingTransferJournalState)!);
        require(restoredJournal.HasPending
                && restoredJournal.Recover(pending.OperationId, null, null).Status
                    == FcPendingTransferRecoveryStatus.RequiresReconciliation
                && restoredJournal.Recover(pending.OperationId, "wrong-chest", "worker-before").Status
                    == FcPendingTransferRecoveryStatus.FingerprintMismatch
                && restoredJournal.TryComplete(pending.OperationId, "chest-before", "worker-before")
                && restoredJournal.Recover(pending.OperationId, "chest-before", "worker-before").Status
                    == FcPendingTransferRecoveryStatus.Missing,
            "pending physical recovery requires matching fingerprints and is idempotent after reconciliation");
        require(!FcPendingTransferJournal.TryRestore(null, out _)
                && !FcPendingTransferJournal.TryRestore(
                    new FcPendingTransferJournalState([null!]), out _),
            "missing or corrupt pending journal state fails closed");
    }

    private static void CanonicalAndTransport(Action<bool, string> require)
    {
        var clock = new FcManualClock(1_000);
        var list = MakeList(ListA, "alice", 1, true, "canonical");
        var worker = MakeWorker("alice", SessionA, 1, 1, 2, ListA, 1_000);
        var chest = MakeChest("alice", 1, 2, 3);
        var request = new CapabilityRequestRecord(
            Header(FcRecordTypes.CapabilityRequest, ListA, "alice", 1), ListA, "world",
            new FcHlcTimestamp(2_000, 0, "alice"), [new RequiredCraftCapability(1, FcQualityPolicy.Empty)]);
        var response = new CapabilityResponseRecord(
            Header(FcRecordTypes.CapabilityResponse, ListA, "bob", 1), ListA,
            new FcHlcTimestamp(2_000, 0, "bob"), "world", "game", "planner", "gearset", "solver",
            [new CraftCapabilityResult(1, true, 8, true, FcRaphaelAssessmentOutcome.NoQualityRequired)]);
        var transfer = MakeTransfer(Guid.Parse("00000000-0000-0000-0000-00000000000b"), 1,
            FcInventoryTransferKind.Withdraw, FcInventoryTransferOutcome.Committed, 1,
            worker with { Header = worker.Header with { Revision = 2 }, HeldInventory = [new ItemQuantityEntry(100, FcItemQuality.Nq, 3)] },
            chest with { Header = chest.Header with { Revision = 2 }, Items = [new ItemQuantityEntry(100, FcItemQuality.Nq, 2)] });
        foreach (var record in new object[] { list, worker, chest, request, response, transfer })
        {
            var json = FcCanonical.Serialize(record);
            var roundTrip = DeserializeSameType(record, json);
            require(FcCanonical.Hash(record) == FcCanonical.Hash(roundTrip),
                "source-generated canonical JSON round-trips every record without semantic hash drift");
        }
        var itemMap = new FcItemQuantityMap([
            new ItemQuantityEntry(100, FcItemQuality.Hq, 2),
            new ItemQuantityEntry(100, FcItemQuality.Nq, 3),
        ]);
        var mapRoundTrip = JsonSerializer.Deserialize<FcItemQuantityMap>(
            FcCanonical.Serialize(itemMap), FcJsonContext.Default.Options)!;
        var crystalMap = new CrystalQuantityMap([new CrystalQuantityEntry(19, 4)]);
        var crystalRoundTrip = JsonSerializer.Deserialize<CrystalQuantityMap>(
            FcCanonical.Serialize(crystalMap), FcJsonContext.Default.Options)!;
        var hlc = new FcHlcTimestamp(1_234, 7, "alice");
        var hlcRoundTrip = JsonSerializer.Deserialize<FcHlcTimestamp>(
            FcCanonical.Serialize(hlc), FcJsonContext.Default.Options);
        require(FcCanonical.Hash(itemMap) == FcCanonical.Hash(mapRoundTrip)
                && FcCanonical.Hash(crystalMap) == FcCanonical.Hash(crystalRoundTrip)
                && hlc == hlcRoundTrip,
            "source-generated JSON round-trips item maps, crystal maps, and HLC values");
        var envelope = Envelope(list, "alice", 1_000);
        var relayed = FakeMeshTransport.RoundTrip(envelope);
        require(relayed.ActualAuthorId == envelope.ActualAuthorId
                && relayed.Hlc == envelope.Hlc
                && relayed.Signature == envelope.Signature
                && relayed.PayloadHash == envelope.PayloadHash,
            "A-B-G relay preserves original author, HLC, signature, and opaque payload hash");
        var network = new FakeMeshTransport();
        network.Connect("alice", "bob");
        network.Connect("bob", "greg");
        network.Publish("alice", envelope);
        require(network.Relay("alice", "greg") is null,
            "A-G disconnected topology cannot bypass the B relay");
        var relay = network.Relay("bob", "greg");
        require(relay is not null
                && relay.ActualAuthorId == envelope.ActualAuthorId
                && relay.Hlc == envelope.Hlc
                && relay.PayloadHash == envelope.PayloadHash
                && relay.Signature == envelope.Signature
                && network.Deliver("greg").Count == 1,
            "in-memory fake mesh supports transitive relay with unchanged envelope");

        var aliceStore = new FcWorldStore(clock);
        var bobStore = new FcWorldStore(clock);
        var gregStore = new FcWorldStore(clock);
        var topology = new FakeMeshTransport();
        topology.Connect("alice", "bob");
        topology.Connect("bob", "greg");
        var topologyListB = MakeList(ListB, "alice", 1, true, "topology-b");
        var topologyWorker = MakeWorker("alice", SessionA, 1, 1, 1, ListA, 1_000) with
        {
            Selection = FcFulfillmentSelection.Specific(ListA, ListB),
        };
        var topologyRecords = new object[] { list, topologyListB, topologyWorker, chest };
        foreach (var topologyRecord in topologyRecords)
        {
            var topologyEnvelope = Envelope(topologyRecord, "alice", 1_000,
                topologyRecord is WorkerSessionRecord workerRecord ? workerRecord.SessionGeneration : null);
            require(Apply(aliceStore, topologyEnvelope, topologyRecord).Status
                        is FcApplyStatus.Accepted or FcApplyStatus.Duplicate,
                "Alice applies native-authored topology record");
            topology.Publish("alice", topologyEnvelope, duplicate: true);
            var relayedFrames = topology.RelayAll("bob", "greg");
            require(relayedFrames.All(frame => SameEnvelope(frame, topologyEnvelope)),
                "B relay preserves every native envelope unchanged");
            require(ApplyFrames(bobStore, topology.Deliver("bob", permutationSeed: 31)),
                "Bob applies permuted and duplicate topology deliveries");
            require(ApplyFrames(gregStore, topology.Deliver("greg", reversed: true)),
                "Greg applies transitively relayed topology deliveries");
        }
        var beforeRematchFingerprint = aliceStore.Revision.Fingerprint;
        var aliceTopology = new FcWorldProjection(aliceStore).Build(clock, LocalCompatibility);
        var bobTopology = new FcWorldProjection(bobStore).Build(clock, LocalCompatibility);
        var gregTopology = new FcWorldProjection(gregStore).Build(clock, LocalCompatibility);
        require(aliceStore.Revision.Fingerprint == bobStore.Revision.Fingerprint
                && bobStore.Revision.Fingerprint == gregStore.Revision.Fingerprint
                && aliceTopology.ActiveListIds.SequenceEqual([ListA, ListB])
                && bobTopology.ActiveListIds.SequenceEqual(aliceTopology.ActiveListIds)
                && gregTopology.ActiveListIds.SequenceEqual(aliceTopology.ActiveListIds)
                && aliceTopology.Fulfillment.Contributions
                    .Where(contribution => !contribution.IsChest && contribution.SourceId == "alice")
                    .Sum(contribution => contribution.Quantity) == 1,
            "A/B/G stores converge through the disconnected A-G topology and count one held unit once across two selected lists");

        var topologyListC = MakeList(ListC, "alice", 1, true, "topology-c");
        var topologyWorkerUpdate = topologyWorker with
        {
            Header = topologyWorker.Header with { Revision = 2 },
            Selection = FcFulfillmentSelection.Specific(ListA, ListB, ListC),
        };
        foreach (var topologyRecord in new object[] { topologyListC, topologyWorkerUpdate })
        {
            var topologyEnvelope = Envelope(topologyRecord, "alice", 1_001,
                topologyRecord is WorkerSessionRecord workerRecord ? workerRecord.SessionGeneration : null);
            require(Apply(aliceStore, topologyEnvelope, topologyRecord).Status == FcApplyStatus.Accepted,
                "Alice applies a newer list/selection world revision");
            topology.Publish("alice", topologyEnvelope, duplicate: true);
            _ = topology.RelayAll("bob", "greg");
            require(ApplyFrames(bobStore, topology.Deliver("bob", permutationSeed: 47))
                    && ApplyFrames(gregStore, topology.Deliver("greg", reversed: true)),
                "B/G apply rematching revisions after reconnection");
        }
        var finalAlice = new FcWorldProjection(aliceStore).Build(clock, LocalCompatibility);
        var finalBob = new FcWorldProjection(bobStore).Build(clock, LocalCompatibility);
        var finalGreg = new FcWorldProjection(gregStore).Build(clock, LocalCompatibility);
        require(beforeRematchFingerprint != aliceStore.Revision.Fingerprint
                && aliceStore.Revision.Fingerprint == bobStore.Revision.Fingerprint
                && bobStore.Revision.Fingerprint == gregStore.Revision.Fingerprint
                && finalAlice.ActiveListIds.SequenceEqual([ListA, ListB, ListC])
                && finalBob.Fulfillment.Contributions
                    .Where(contribution => !contribution.IsChest && contribution.SourceId == "alice")
                    .Sum(contribution => contribution.Quantity) == 1
                && finalGreg.Fulfillment.RemainingDemand.Equals(finalAlice.Fulfillment.RemainingDemand),
            "newer world revision adds a third list and deterministically rematches all replicas without duplicating held supply");
        var partitioned = new FakeMeshTransport();
        partitioned.Connect("alice", "bob");
        partitioned.Connect("bob", "greg");
        partitioned.Disconnect("alice", "bob");
        partitioned.Publish("alice", envelope, duplicate: true);
        partitioned.Reconnect("alice", "bob");
        partitioned.Publish("alice", envelope, duplicate: true);
        var reconnected = partitioned.Deliver("bob", permutationSeed: 17);
        require(reconnected.Count == 2
                && reconnected.All(frame => frame.ActualAuthorId == envelope.ActualAuthorId
                    && frame.Hlc == envelope.Hlc
                    && frame.Signature == envelope.Signature),
            "fake mesh partitions drop disconnected traffic and reconnect queues duplicate unchanged envelopes");

        var partitionList = MakeList(
            Guid.Parse("00000000-0000-0000-0000-000000000011"), "greg", 1, true, "partition-greg");
        var partitionListEnvelope = Envelope(partitionList, "greg", 1_002);
        var partitionUpdate = MakeList(ListA, "alice", 3, false, "partition-tombstone");
        var partitionUpdateEnvelope = Envelope(partitionUpdate, "alice", 1_002);
        partitioned.Disconnect("alice", "bob");
        require(Apply(aliceStore, partitionUpdateEnvelope, partitionUpdate).Accepted,
            "Alice mutates a list during the A-B partition");
        partitioned.Publish("alice", partitionUpdateEnvelope, duplicate: true);
        require(partitioned.Deliver("bob", permutationSeed: 23).Count == 0,
            "no disconnected Alice delivery reaches Bob during the partition");
        require(Apply(gregStore, partitionListEnvelope, partitionList).Accepted,
            "Greg mutates a separate domain register on the reachable side of the partition");
        partitioned.Publish("greg", partitionListEnvelope, duplicate: true);
        require(ApplyFrames(bobStore, partitioned.Deliver("bob", permutationSeed: 29)),
            "Bob receives and applies the reachable B-G domain delivery during the partition");
        var partitionAlice = new FcWorldProjection(aliceStore).Build(clock, LocalCompatibility);
        var partitionBob = new FcWorldProjection(bobStore).Build(clock, LocalCompatibility);
        var partitionGreg = new FcWorldProjection(gregStore).Build(clock, LocalCompatibility);
        require(partitionAlice.ActiveListIds.SequenceEqual([ListB, ListC])
                && partitionBob.ActiveListIds.SequenceEqual([ListA, ListB, ListC])
                && partitionGreg.ActiveListIds.SequenceEqual(partitionBob.ActiveListIds)
                && aliceStore.Revision.Fingerprint != bobStore.Revision.Fingerprint
                && bobStore.Revision.Fingerprint == gregStore.Revision.Fingerprint,
            "partitioned stores expose Alice divergence while reachable Bob/Greg replicas converge");

        partitioned.Reconnect("alice", "bob");
        partitioned.Publish("alice", partitionUpdateEnvelope, duplicate: true);
        partitioned.Publish("greg", partitionListEnvelope, duplicate: true);
        var relayedToGreg = partitioned.RelayAll("bob", "greg");
        var relayedToAlice = partitioned.RelayAll("bob", "alice");
        require(relayedToGreg.All(frame => SameEnvelope(frame, partitionUpdateEnvelope)
                    || SameEnvelope(frame, partitionListEnvelope))
                && relayedToAlice.All(frame => SameEnvelope(frame, partitionUpdateEnvelope)
                    || SameEnvelope(frame, partitionListEnvelope)),
            "reconnection relays the original signed envelopes without re-authoring or re-timing them");
        require(ApplyFrames(aliceStore, partitioned.Deliver("alice", permutationSeed: 37))
                && ApplyFrames(bobStore, partitioned.Deliver("bob", permutationSeed: 41))
                && ApplyFrames(gregStore, partitioned.Deliver("greg", reversed: true)),
            "all replicas apply duplicate post-reconnect envelopes in independent permutations");
        var convergedAlice = new FcWorldProjection(aliceStore).Build(clock, LocalCompatibility);
        var convergedBob = new FcWorldProjection(bobStore).Build(clock, LocalCompatibility);
        var convergedGreg = new FcWorldProjection(gregStore).Build(clock, LocalCompatibility);
        require(aliceStore.Revision.Fingerprint == bobStore.Revision.Fingerprint
                && bobStore.Revision.Fingerprint == gregStore.Revision.Fingerprint
                && convergedAlice.ActiveListIds.SequenceEqual(convergedBob.ActiveListIds)
                && convergedBob.ActiveListIds.SequenceEqual(convergedGreg.ActiveListIds)
                && convergedAlice.Fulfillment.RemainingDemand.Equals(convergedBob.Fulfillment.RemainingDemand)
                && convergedBob.Fulfillment.RemainingDemand.Equals(convergedGreg.Fulfillment.RemainingDemand)
                && aliceStore.Lists.Keys.Any(key => key.Contains("00000000-0000-0000-0000-000000000011", StringComparison.Ordinal)),
            "post-reconnect relay restores canonical world projection and fingerprint convergence");
        _ = transfer;
        _ = clock;
    }

    private static PublishedListRecord MakeList(Guid id, string owner, ulong revision, bool published, string name)
        => new(
            Header(FcRecordTypes.PublishedList, id, owner, revision),
            id,
            name,
            published,
            1,
            "game",
            [new PublishedRecipeTarget(1, 100, 100, FcItemQuality.Nq)],
            FcQualityPolicy.Empty,
            FcQualityPolicy.Empty);

    private static WorkerSessionRecord MakeWorker(
        string owner,
        Guid session,
        ulong revision,
        ulong generation,
        int held,
        Guid listId,
        long physical,
        FcWorkerState state = FcWorkerState.Active)
        => new(
            Header(FcRecordTypes.WorkerSession, GuidFrom(owner, session), owner, revision),
            session,
            generation,
            state,
            new CharacterIdentity(owner, owner, "world"),
            FcFulfillmentSelection.Specific(listId),
            false,
            held == 0 ? Array.Empty<ItemQuantityEntry>() : [new ItemQuantityEntry(100, FcItemQuality.Nq, held)],
            null,
            [],
            [100],
            "world");

    private static ChestSnapshotRecord MakeChest(string owner, ulong revision, int quantity, long physical)
        => new(
            Header(FcRecordTypes.ChestSnapshot, GuidFrom(owner, ListA), owner, revision),
            true,
            FcRecordValidator.CompleteChestPageMask,
            quantity == 0 ? Array.Empty<ItemQuantityEntry>() : [new ItemQuantityEntry(100, FcItemQuality.Nq, quantity)],
            new CrystalQuantityMap([new CrystalQuantityEntry(19, 10)]));

    private static FcInventoryTransferRecord MakeTransfer(
        Guid operation,
        ulong revision,
        FcInventoryTransferKind kind,
        FcInventoryTransferOutcome outcome,
        int actual,
        WorkerSessionRecord worker,
        ChestSnapshotRecord chest)
        => new(
            Header(FcRecordTypes.InventoryTransfer, operation, "alice", revision),
            operation,
            worker.SessionId,
            worker.SessionGeneration,
            ListA,
            kind,
            outcome,
            [new ItemQuantityEntry(100, FcItemQuality.Nq, actual)],
            chest,
            worker);

    private static FcRecordHeader Header(string type, Guid recordId, string owner, ulong revision)
        => new(FcProtocolVersion.Current, FcProtocolVersion.CurrentSchema, type, recordId, owner, revision);

    private static FcMeshRecord Envelope(object record, string owner, long physical, ulong? generation = null)
    {
        var header = HeaderOf(record);
        return FcMeshRecord.Create(
            record,
            owner,
            new FcHlcTimestamp(physical, 0, owner),
            generation,
            header.Revision,
            header.RecordType,
            header.RecordId.ToString("D")) with
        {
            Signature = "fake-native-signature",
            SignatureAlgorithm = "fake-native-context",
        };
    }

    private static FcApplyResult ApplyRecord(FcWorldStore store, object record, long physical)
    {
        var envelope = Envelope(record, HeaderOf(record).OwnerAuthorId, physical,
            record switch
            {
                WorkerSessionRecord worker => worker.SessionGeneration,
                FcInventoryTransferRecord transfer => transfer.SessionGeneration,
                _ => null,
            });
        return Apply(store, envelope, record);
    }

    private static FcApplyResult Apply<T>(FcWorldStore store, FcMeshRecord envelope, T payload)
        => store.Apply(envelope, payload, VerifiedContext(envelope));

    private static FcVerifiedMeshContext VerifiedContext(FcMeshRecord envelope)
        => FcVerifiedMeshContext.FromEnvelope(envelope) with
        {
            SignatureValid = true,
            TransportAuthorId = envelope.ActualAuthorId,
        };

    private static FcRecordHeader HeaderOf(object record)
        => record switch
        {
            PublishedListRecord value => value.Header,
            WorkerSessionRecord value => value.Header,
            ChestSnapshotRecord value => value.Header,
            CapabilityRequestRecord value => value.Header,
            CapabilityResponseRecord value => value.Header,
            FcInventoryTransferRecord value => value.Header,
            _ => throw new InvalidOperationException("Unsupported FC record in test fixture."),
        };

    private static object Decode(FcMeshRecord envelope)
        => envelope.RecordType switch
        {
            FcRecordTypes.PublishedList => JsonSerializer.Deserialize<PublishedListRecord>(envelope.Payload, FcJsonContext.Default.Options)!,
            FcRecordTypes.WorkerSession => JsonSerializer.Deserialize<WorkerSessionRecord>(envelope.Payload, FcJsonContext.Default.Options)!,
            FcRecordTypes.ChestSnapshot => JsonSerializer.Deserialize<ChestSnapshotRecord>(envelope.Payload, FcJsonContext.Default.Options)!,
            FcRecordTypes.CapabilityRequest => JsonSerializer.Deserialize<CapabilityRequestRecord>(envelope.Payload, FcJsonContext.Default.Options)!,
            FcRecordTypes.CapabilityResponse => JsonSerializer.Deserialize<CapabilityResponseRecord>(envelope.Payload, FcJsonContext.Default.Options)!,
            _ => throw new InvalidOperationException("Unsupported fake envelope type."),
        };

    private static bool ApplyFrames(FcWorldStore store, IReadOnlyList<FcMeshRecord> frames)
    {
        foreach (var frame in frames)
        {
            var payload = Decode(frame);
            var result = Apply(store, frame, payload);
            if (result.Status is not (FcApplyStatus.Accepted or FcApplyStatus.Duplicate))
                return false;
        }

        return true;
    }

    private static bool SameEnvelope(FcMeshRecord left, FcMeshRecord right)
        => FcCanonical.Serialize(left) == FcCanonical.Serialize(right);

    private static object DeserializeSameType(object record, string json)
        => record switch
        {
            PublishedListRecord => JsonSerializer.Deserialize<PublishedListRecord>(json, FcJsonContext.Default.Options)!,
            WorkerSessionRecord => JsonSerializer.Deserialize<WorkerSessionRecord>(json, FcJsonContext.Default.Options)!,
            ChestSnapshotRecord => JsonSerializer.Deserialize<ChestSnapshotRecord>(json, FcJsonContext.Default.Options)!,
            CapabilityRequestRecord => JsonSerializer.Deserialize<CapabilityRequestRecord>(json, FcJsonContext.Default.Options)!,
            CapabilityResponseRecord => JsonSerializer.Deserialize<CapabilityResponseRecord>(json, FcJsonContext.Default.Options)!,
            FcInventoryTransferRecord => JsonSerializer.Deserialize<FcInventoryTransferRecord>(json, FcJsonContext.Default.Options)!,
            _ => throw new InvalidOperationException("Unsupported canonical record type."),
        };

    private static Guid GuidFrom(string owner, Guid suffix)
        => Guid.Parse(owner switch
        {
            "alice" => "10000000-0000-0000-0000-000000000001",
            "bob" => "20000000-0000-0000-0000-000000000001",
            "greg" => "30000000-0000-0000-0000-000000000001",
            _ => suffix.ToString("D"),
        });

    private static bool Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private sealed class FakeMeshTransport
    {
        private readonly Dictionary<string, HashSet<string>> _links = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<FcMeshRecord>> _queues = new(StringComparer.Ordinal);

        public void Connect(string left, string right)
        {
            GetLinks(left).Add(right);
            GetLinks(right).Add(left);
            _ = GetQueue(left);
            _ = GetQueue(right);
        }

        public void Disconnect(string left, string right)
        {
            GetLinks(left).Remove(right);
            GetLinks(right).Remove(left);
        }

        public void Reconnect(string left, string right)
            => Connect(left, right);

        public void Publish(string peer, FcMeshRecord envelope, bool duplicate = false)
        {
            foreach (var link in GetLinks(peer))
            {
                GetQueue(link).Add(RoundTrip(envelope));
                if (duplicate)
                    GetQueue(link).Add(RoundTrip(envelope));
            }
        }

        public FcMeshRecord? Relay(string from, string to)
        {
            var queue = GetQueue(from);
            var envelope = queue.FirstOrDefault();
            if (envelope is null || !GetLinks(from).Contains(to))
                return null;
            GetQueue(to).Add(RoundTrip(envelope));
            return envelope;
        }

        public IReadOnlyList<FcMeshRecord> RelayAll(string from, string to)
        {
            if (!GetLinks(from).Contains(to))
                return Array.Empty<FcMeshRecord>();

            var frames = GetQueue(from).Select(RoundTrip).ToArray();
            GetQueue(to).AddRange(frames.Select(RoundTrip));
            return frames;
        }

        public IReadOnlyList<FcMeshRecord> Deliver(string peer, bool reversed = true, int? permutationSeed = null)
        {
            var queue = GetQueue(peer);
            FcMeshRecord[] result;
            if (permutationSeed is { } seed)
            {
                result = queue.ToArray();
                var random = new Random(seed);
                for (var index = result.Length - 1; index > 0; index--)
                {
                    var swap = random.Next(index + 1);
                    (result[index], result[swap]) = (result[swap], result[index]);
                }
            }
            else
            {
                result = (reversed
                        ? queue.OrderByDescending(value => value.RecordId, StringComparer.Ordinal)
                        : queue.OrderBy(value => value.RecordId, StringComparer.Ordinal))
                    .ToArray();
            }
            queue.Clear();
            return result;
        }

        public static FcMeshRecord RoundTrip(FcMeshRecord envelope)
            => JsonSerializer.Deserialize<FcMeshRecord>(FcCanonical.Serialize(envelope), FcJsonContext.Default.Options)!;

        private HashSet<string> GetLinks(string peer)
        {
            if (!_links.TryGetValue(peer, out var links))
                _links[peer] = links = new HashSet<string>(StringComparer.Ordinal);
            return links;
        }

        private List<FcMeshRecord> GetQueue(string peer)
        {
            if (!_queues.TryGetValue(peer, out var queue))
                _queues[peer] = queue = new List<FcMeshRecord>();
            return queue;
        }
    }

    private sealed class FakeChestSource
    {
        private ChestSnapshotRecord _snapshot;

        public FakeChestSource(ChestSnapshotRecord snapshot)
        {
            _snapshot = snapshot;
        }

        public ChestSnapshotRecord ReadComplete() => _snapshot;

        public void Replace(ChestSnapshotRecord snapshot)
        {
            if (!snapshot.Complete)
                throw new InvalidOperationException("Fake chest source cannot expose a partial snapshot.");
            _snapshot = snapshot;
        }
    }
}
