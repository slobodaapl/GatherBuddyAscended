using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GatherBuddy.FcMesh.Native;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.FcMesh.Sessions;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.Vulcan.Tests;

internal static class FcMeshPhase6Tests
{
    private const string Scope = "0000000000000001";
    private const string Author = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherAuthor = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly Guid ListA = Guid.Parse("00000000-0000-0000-0000-000000006001");
    private static readonly Guid ListB = Guid.Parse("00000000-0000-0000-0000-000000006002");

    public static void Run(Action<bool, string> require)
    {
        StartSelectedAndAll(require);
        RefreshFailureRetryAndStop(require);
        AtomicTransferObservation(require);
        AtomicTransferSupersedesPending(require);
        AtomicTransferCannotInterleaveWithNativePut(require);
        AuthorSwitchBlocksPendingPublication(require);
        HeldContributionAndRecovery(require);
        PendingStopCompletesDuringRecovery(require);
        PhysicalSnapshotFailsClosed(require);
        DependencyClosureFailsClosed(require);
        WorkerStatePersistenceFailsClosed(require);
    }

    private static void StartSelectedAndAll(Action<bool, string> require)
    {
        var clock = new FcManualClock(1_000_000);
        var store = new FcInMemoryWorkerSessionStateStore();
        var transport = new FakeTransport();
        using var service = NewService(store, transport, clock);
        var result = service.StartSelected(
            [ListA, ListA],
            new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 2)]),
            dependencyClosure: [new FcQuantityKey(100, FcItemQuality.Nq)]);
        require(result.Accepted && result.Status == FcWorkerSessionCommandStatus.Pending,
            "Start Selected must reserve one absolute worker command");
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(transport.Puts.Count == 1
                && transport.Puts[0].HasGeneration
                && transport.Puts[0].Generation == result.SessionGeneration
                && transport.Puts[0].Revision == result.Revision
                && transport.Puts[0].RecordId == service.State.WorkerRegisterId.ToString("D"),
            "worker Put must use the stable worker register identity, generation, and reserved revision");
        var first = Decode(transport.Puts[0]);
        require(first.Selection.ListIds.SequenceEqual([ListA])
                && first.Selection.ListIds.Length == 1
                && first.State == FcWorkerState.Active,
            "duplicate selected list IDs must be normalized and start only subscription intent");
        var firstSession = first.SessionId;
        var firstGeneration = first.SessionGeneration;
        require(service.ObserveAccepted(first, new FcHlcTimestamp(clock.UnixMilliseconds, 1, "node-a")).Accepted,
            "accepted worker event must reconcile the local register without synthetic world mutation");

        var all = service.Stop();
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(all.Accepted && Decode(transport.Puts[^1]).State == FcWorkerState.Unsubscribed,
            "Start/Stop protocol must publish explicit Unsubscribed state");
        var restarted = service.StartAll(
            FcItemQuantityMap.Empty,
            useOwnStock: true,
            dependencyClosure: [new FcQuantityKey(100, FcItemQuality.Nq)]);
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var second = Decode(transport.Puts[^1]);
        require(restarted.Accepted
                && second.Selection.AllPublishedLists
                && second.Selection.ListIds.Length == 0
                && second.SessionId != firstSession
                && second.SessionGeneration > firstGeneration
                && second.Header.Revision > first.Header.Revision,
            "a user restart creates a new session and generation while revisions remain monotonic");
    }

    private static void RefreshFailureRetryAndStop(Action<bool, string> require)
    {
        var clock = new FcManualClock(2_000_000);
        var store = new FcInMemoryWorkerSessionStateStore();
        var transport = new FakeTransport();
        using var service = NewService(store, transport, clock);
        var start = service.StartSelected(
            [ListA],
            FcItemQuantityMap.Empty,
            dependencyClosure: [new FcQuantityKey(100, FcItemQuality.Nq)]);
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var initial = Decode(transport.Puts[^1]);
        service.ObserveAccepted(initial, new FcHlcTimestamp(clock.UnixMilliseconds, 1, "node-a"));
        clock.Advance(TimeSpan.FromSeconds(59));
        service.Tick();
        require(transport.Puts.Count == 1,
            "meaningful communication must postpone the idle refresh until the full 60-second interval");
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Tick();
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var refresh = Decode(transport.Puts[^1]);
        require(transport.Puts.Count == 2
                && refresh.Header.Revision == initial.Header.Revision + 1
                && refresh.SessionId == initial.SessionId
                && refresh.Selection.AllPublishedLists == initial.Selection.AllPublishedLists
                && refresh.Selection.ListIds.SequenceEqual(initial.Selection.ListIds),
            "idle refresh must be a newer identical WorkerState forever within the same session");

        transport.FailPuts = true;
        var waiting = service.SetWaiting();
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var failedRevision = service.State.LastReservedRevision;
        require(waiting.Accepted
                && service.State.PendingWorker is { } pending
                && pending.Header.Revision == failedRevision
                && transport.Puts.Count == 3,
            "failed native Put must retain the same reserved absolute command and revision");
        transport.FailPuts = false;
        service.RetryPending();
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(transport.Puts.Count == 4
                && transport.Puts[^1].Revision == failedRevision
                && service.State.PendingWorker is null,
            "retry after queue failure must reuse the reserved revision and clear only after native acceptance");
        var waitingAccepted = Decode(transport.Puts[^1]);
        service.ObserveAccepted(waitingAccepted, new FcHlcTimestamp(clock.UnixMilliseconds, 4, "node-a"));
        var stop = service.Stop();
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(stop.Accepted
                && Decode(transport.Puts[^1]).State == FcWorkerState.Unsubscribed
                && transport.PhysicalEffects == 0,
            "stop must publish only Unsubscribed and must not travel, open chest, deposit, or mutate inventory");
        clock.Advance(TimeSpan.FromMinutes(10));
        service.Tick();
        require(transport.Puts.Count == 5,
            "explicit unsubscribe must end all future idle refreshes");
    }

    private static void HeldContributionAndRecovery(Action<bool, string> require)
    {
        var clock = new FcManualClock(3_000_000);
        var store = new FcInMemoryWorkerSessionStateStore();
        var transport = new FakeTransport();
        var physical = new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 5)]);
        var service = NewService(store, transport, clock);
        service.StartSelected(
            [ListA],
            physical,
            dependencyClosure: [new FcQuantityKey(100, FcItemQuality.Nq)]);
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var start = Decode(transport.Puts[^1]);
        require(start.HeldInventory.Length == 0,
            "UseOwnStock=false must protect starting inventory from FC held contribution");
        service.ObserveAccepted(start, new FcHlcTimestamp(clock.UnixMilliseconds, 1, "node-a"));
        service.RecordGatherYield(new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 3)]));
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var yielded = Decode(transport.Puts[^1]);
        require(yielded.HeldInventoryMap.Get(100, FcItemQuality.Nq) == 3,
            "gather yield must update the attributed quality-keyed held contribution");
        service.Dispose();
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

        using var recovered = NewService(store, transport, clock);
        require(recovered.Diagnostics.RecoveryRequired,
            "a subscribed session that shuts down without explicit unsubscribe must require physical recovery");
        recovered.ObserveAccepted(yielded, new FcHlcTimestamp(clock.UnixMilliseconds, 3, "node-a"));
        require(recovered.Diagnostics.RecoveryRequired,
            "an authenticated replay of the last active worker frame must not bypass required physical recovery");
        recovered.ReconcileAuthoritativeState();
        require(recovered.Diagnostics.RecoveryRequired,
            "authoritative replay of the last active worker frame must not bypass required physical recovery");
        var recovery = recovered.Recover(new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 6)]));
        recovered.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var recoveredWorker = Decode(transport.Puts[^1]);
        require(recovery.Accepted
                && recoveredWorker.SessionId == yielded.SessionId
                && recoveredWorker.SessionGeneration == yielded.SessionGeneration
                && recoveredWorker.Header.Revision > yielded.Header.Revision
                && recoveredWorker.HeldInventoryMap.Get(100) == 1,
            "recovery must reuse session identity, clamp attributed stock to current physical inventory, and publish newer revision");
    }

    private static void AtomicTransferSupersedesPending(Action<bool, string> require)
    {
        var clock = new FcManualClock(2_700_000);
        var store = new FcInMemoryWorkerSessionStateStore();
        var transport = new FakeTransport { FailPuts = true };
        using var service = NewService(store, transport, clock);
        var start = service.StartSelected(
            [ListA],
            FcItemQuantityMap.Empty,
            dependencyClosure: [new FcQuantityKey(100, FcItemQuality.Nq)]);
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var pending = service.State.PendingWorker;
        require(start.Accepted && pending is not null && transport.Puts.Count == 1,
            "transfer supersession setup must retain the failed absolute worker command");
        if (pending is null)
            return;

        var after = pending with
        {
            Header = pending.Header with { Revision = pending.Header.Revision + 5 },
            HeldInventory = [new ItemQuantityEntry(100, FcItemQuality.Nq, 2)],
        };
        var operationId = Guid.Parse("00000000-0000-0000-0000-000000006020");
        var chestAfter = new ChestSnapshotRecord(
            new FcRecordHeader(1, 1, FcRecordTypes.ChestSnapshot,
                Guid.Parse("00000000-0000-0000-0000-000000006021"), Author, 2),
            true,
            FcRecordValidator.CompleteChestPageMask,
            Array.Empty<ItemQuantityEntry>(),
            new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>()));
        var transfer = new FcInventoryTransferRecord(
            new FcRecordHeader(1, 1, FcRecordTypes.InventoryTransfer, operationId, Author, 1),
            operationId,
            after.SessionId,
            after.SessionGeneration,
            ListA,
            FcInventoryTransferKind.Withdraw,
            FcInventoryTransferOutcome.Committed,
            [new ItemQuantityEntry(100, FcItemQuality.Nq, 2)],
            chestAfter,
            after);
        var observed = service.ObserveAtomicTransfer(transfer);
        transport.FailPuts = false;
        var retry = service.RetryPending();
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(observed.Accepted
                && !retry.Accepted
                && transport.Puts.Count == 1
                && service.AcceptedWorker?.Header.Revision == after.Header.Revision,
            "a transfer WorkerAfter revision leap must durably cancel the stale pending Put without retrying it");

        var next = service.SetCurrentTarget(new FcLogicalTarget(null, 100, 1, FcItemQuality.Nq));
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(next.Accepted
                && transport.Puts.Count == 2
                && transport.Puts[^1].Revision == after.Header.Revision + 1,
            "the first post-transfer worker update must reserve strictly after WorkerAfter");
    }

    private static void PendingStopCompletesDuringRecovery(Action<bool, string> require)
    {
        var clock = new FcManualClock(3_100_000);
        var store = new FcInMemoryWorkerSessionStateStore();
        var transport = new FakeTransport();
        var service = NewService(store, transport, clock);
        service.StartSelected(
            [ListA],
            FcItemQuantityMap.Empty,
            dependencyClosure: [new FcQuantityKey(100, FcItemQuality.Nq)]);
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        service.Dispose();
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

        using var recovered = NewService(store, transport, clock);
        require(recovered.Diagnostics.RecoveryRequired,
            "a crash-recovered active worker must require physical recovery before resuming");
        transport.FailPuts = true;
        var stop = recovered.Stop();
        recovered.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(stop.Accepted
                && recovered.State.PendingWorker is { State: FcWorkerState.Unsubscribed }
                && transport.Puts.Count == 2,
            "explicit Stop must reserve Unsubscribed even while physical recovery is required");

        transport.FailPuts = false;
        var retry = recovered.RetryPending();
        recovered.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(retry.Accepted
                && recovered.State.PendingWorker is null
                && recovered.AcceptedWorker?.State == FcWorkerState.Unsubscribed
                && !recovered.Diagnostics.RecoveryRequired
                && transport.Puts.Count == 3,
            "pending Stop must finish Unsubscribed before any recovery resume and reuse its revision");
    }

    private static void AtomicTransferObservation(Action<bool, string> require)
    {
        var clock = new FcManualClock(2_500_000);
        var store = new FcInMemoryWorkerSessionStateStore();
        var transport = new FakeTransport();
        using var service = NewService(store, transport, clock);
        var start = service.StartSelected(
            [ListA],
            FcItemQuantityMap.Empty,
            dependencyClosure: [new FcQuantityKey(100, FcItemQuality.Nq)]);
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var before = Decode(transport.Puts[^1]);
        service.ObserveAccepted(before, new FcHlcTimestamp(clock.UnixMilliseconds, 1, "node-a"));

        var after = before with
        {
            Header = before.Header with { Revision = before.Header.Revision + 1 },
            HeldInventory = [new ItemQuantityEntry(100, FcItemQuality.Nq, 2)],
        };
        var operationId = Guid.Parse("00000000-0000-0000-0000-000000006010");
        var chestAfter = new ChestSnapshotRecord(
            new FcRecordHeader(1, 1, FcRecordTypes.ChestSnapshot,
                Guid.Parse("00000000-0000-0000-0000-000000006011"), Author, 2),
            true,
            FcRecordValidator.CompleteChestPageMask,
            Array.Empty<ItemQuantityEntry>(),
            new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>()));
        var transfer = new FcInventoryTransferRecord(
            new FcRecordHeader(1, 1, FcRecordTypes.InventoryTransfer, operationId, Author, 1),
            operationId,
            after.SessionId,
            after.SessionGeneration,
            ListA,
            FcInventoryTransferKind.Withdraw,
            FcInventoryTransferOutcome.Committed,
            [new ItemQuantityEntry(100, FcItemQuality.Nq, 2)],
            chestAfter,
            after);

        var observed = service.ObserveAtomicTransfer(transfer);
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(observed.Accepted
                && transport.Puts.Count == 1
                && service.AcceptedWorker?.Header.Revision == after.Header.Revision
                && service.ContributionLedger?.GetPublishableHeld().Get(100) == 2,
            "one accepted atomic transfer must update chest/worker-derived contribution state without a second worker Put");
        require(service.NextRefreshUnixMilliseconds == clock.UnixMilliseconds + 60_000,
            "accepted atomic transfer must reset the worker refresh deadline");
    }

    private static void AtomicTransferCannotInterleaveWithNativePut(Action<bool, string> require)
    {
        var clock = new FcManualClock(2_600_000);
        var store = new FcInMemoryWorkerSessionStateStore();
        var transport = new FakeTransport();
        using var service = NewService(store, transport, clock);
        var start = service.StartSelected(
            [ListA],
            FcItemQuantityMap.Empty,
            dependencyClosure: [new FcQuantityKey(100, FcItemQuality.Nq)]);
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var baseline = service.AcceptedWorker;
        require(start.Accepted && service.State.PendingWorker is null && baseline is not null,
            "native Put setup must leave an accepted worker frame before the transfer race");
        if (baseline is null)
            return;

        var workerAfter = baseline with
        {
            Header = baseline.Header with { Revision = baseline.Header.Revision + 5 },
            HeldInventory = [new ItemQuantityEntry(100, FcItemQuality.Nq, 2)],
        };
        var operationId = Guid.Parse("00000000-0000-0000-0000-000000006030");
        var transfer = new FcInventoryTransferRecord(
            new FcRecordHeader(1, 1, FcRecordTypes.InventoryTransfer, operationId, Author, 1),
            operationId,
            workerAfter.SessionId,
            workerAfter.SessionGeneration,
            ListA,
            FcInventoryTransferKind.Withdraw,
            FcInventoryTransferOutcome.Committed,
            [new ItemQuantityEntry(100, FcItemQuality.Nq, 2)],
            new ChestSnapshotRecord(
                new FcRecordHeader(1, 1, FcRecordTypes.ChestSnapshot,
                    Guid.Parse("00000000-0000-0000-0000-000000006031"), Author, 2),
                true,
                FcRecordValidator.CompleteChestPageMask,
                Array.Empty<ItemQuantityEntry>(),
                new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>())),
            workerAfter);

        using var putEntered = new ManualResetEventSlim(false);
        using var transferEntered = new ManualResetEventSlim(false);
        using var releasePut = new ManualResetEventSlim(false);
        Task<FcWorkerSessionResult>? transferTask = null;
        transport.PutEntered = putEntered;
        transport.ReleasePut = releasePut;
        transport.BeforePut = () =>
        {
            transferTask = Task.Run(() =>
            {
                transferEntered.Set();
                return service.ObserveAtomicTransfer(transfer);
            });
        };

        var update = service.SetCurrentTarget(new FcLogicalTarget(null, 100, 1, FcItemQuality.Nq));
        require(update.Accepted
                && update.Status == FcWorkerSessionCommandStatus.Pending
                && update.Revision < workerAfter.Header.Revision,
            "the race fixture must reserve one meaningful absolute worker update before the blocked Put");

        require(putEntered.Wait(TimeSpan.FromSeconds(1)),
            "race fixture must reach the native Put boundary");
        require(transferEntered.Wait(TimeSpan.FromSeconds(1)),
            "race fixture must attempt transfer observation while Put is blocked");
        require(transferTask is not null && !transferTask.IsCompleted,
            "accepted atomic transfer must wait for the in-flight Put critical section");
        releasePut.Set();
        var observed = transferTask!.GetAwaiter().GetResult();
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(observed.Accepted
                && transport.Puts.Count == 2
                && service.AcceptedWorker?.Header.Revision == workerAfter.Header.Revision,
            "a transfer racing a pending Put must not enqueue a second stale worker frame after transfer acceptance");
    }

    private static void AuthorSwitchBlocksPendingPublication(Action<bool, string> require)
    {
        var clock = new FcManualClock(2_800_000);
        var store = new FcInMemoryWorkerSessionStateStore();
        var transport = new FakeTransport { FailPuts = true };
        using var service = NewService(store, transport, clock);
        var start = service.StartSelected(
            [ListA],
            FcItemQuantityMap.Empty,
            dependencyClosure: [new FcQuantityKey(100, FcItemQuality.Nq)]);
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(start.Accepted && service.State.PendingWorker is not null && transport.Puts.Count == 1,
            "author-switch setup must retain a durably reserved pending worker command");

        transport.LocalAuthorId = OtherAuthor;
        var retry = service.RetryPending();
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(!retry.Accepted && transport.Puts.Count == 1,
            "logout or character change must block a pending old-author Put instead of publishing under the new author");
    }

    private static void WorkerStatePersistenceFailsClosed(Action<bool, string> require)
    {
        var store = new FcInMemoryWorkerSessionStateStore();
        store.MarkInitializationMarkerOnly(Scope);
        var threw = false;
        try
        {
            store.Save(Scope, FcWorkerSessionState.Create(Scope, Author));
        }
        catch (InvalidDataException)
        {
            threw = true;
        }
        require(threw && store.Load(Scope).Status == FcWorkerSessionLoadStatus.Corrupt,
            "marker-only worker persistence must fail closed instead of healing a possible revision rollback");
    }

    private static void PhysicalSnapshotFailsClosed(Action<bool, string> require)
    {
        var keys = new[]
        {
            new FcQuantityKey(100, FcItemQuality.Nq),
            new FcQuantityKey(100, FcItemQuality.Hq),
        };
        var unavailable = FcWorkerPhysicalInventorySnapshot.Capture(
            keys,
            () => false,
            _ => (4L, 2L));
        require(!unavailable.Succeeded && !unavailable.Complete && unavailable.Snapshot is null,
            "physical snapshot must fail closed when player inventory is unavailable");

        var partial = FcWorkerPhysicalInventorySnapshot.Capture(
            keys,
            () => true,
            _ => throw new InvalidOperationException("partial inventory read"));
        require(!partial.Succeeded && !partial.Complete && partial.Snapshot is null,
            "physical snapshot must fail closed on a partial/exceptional inventory read");

        var overflow = FcWorkerPhysicalInventorySnapshot.Capture(
            keys,
            () => true,
            _ => (long.MaxValue, 0L));
        require(!overflow.Succeeded && !overflow.Complete && overflow.Snapshot is null,
            "physical snapshot must fail closed on checked quantity overflow");

        var complete = FcWorkerPhysicalInventorySnapshot.Capture(
            keys,
            () => true,
            _ => (4L, 2L));
        require(complete.Succeeded
                && complete.Complete
                && complete.Snapshot is { } completeSnapshot
                && completeSnapshot.Get(100, FcItemQuality.Nq) == 4
                && completeSnapshot.Get(100, FcItemQuality.Hq) == 2,
            "complete NQ/HQ physical inventory reads must preserve both quality-keyed counts");

        var serviceTransport = new FakeTransport();
        using var service = NewService(
            new FcInMemoryWorkerSessionStateStore(),
            serviceTransport,
            new FcManualClock(2_900_000),
            _ => FcWorkerPhysicalInventorySnapshotResult.Valid(FcItemQuantityMap.Empty));
        var blockedStart = service.StartSelected(
            [ListA],
            unavailable,
            dependencyClosure: keys);
        require(!blockedStart.Accepted
                && serviceTransport.Puts.Count == 0
                && service.Diagnostics.LastError.Contains("unavailable", StringComparison.OrdinalIgnoreCase),
            "worker start must expose the physical snapshot failure and publish nothing");
        var incompleteStart = service.StartSelected(
            [ListA],
            FcWorkerPhysicalInventorySnapshotResult.Valid(FcItemQuantityMap.Empty),
            dependencyClosure: keys);
        require(!incompleteStart.Accepted
                && serviceTransport.Puts.Count == 0
                && service.Diagnostics.LastError.Contains("every requested dependency", StringComparison.OrdinalIgnoreCase),
            "a successful-looking but incomplete physical snapshot must still block worker start");
    }

    private static void DependencyClosureFailsClosed(Action<bool, string> require)
    {
        var invalid = MakeList(ListA) with
        {
            FinalTargets = [new PublishedRecipeTarget(uint.MaxValue, 100, 1, FcItemQuality.Nq)],
        };
        var closure = FcWorkerDependencyClosure.Build([invalid]);
        require(!closure.Succeeded && closure.Keys.Count == 0,
            "dependency closure must fail closed when authoritative recipe data cannot resolve a published target");
    }

    private static FcWorkerSessionService NewService(
        IFcWorkerSessionStateStore store,
        FakeTransport transport,
        FcManualClock clock,
        Func<IEnumerable<FcQuantityKey>, FcWorkerPhysicalInventorySnapshotResult>? physicalSnapshotProvider = null)
    {
        var views = new[]
        {
            new FcPublicListView(MakeList(ListA), true, string.Empty, false),
            new FcPublicListView(MakeList(ListB), true, string.Empty, false),
        };
        return new FcWorkerSessionService(
            store,
            transport,
            () => Scope,
            () => transport.LocalAuthorId,
            () => new FcCompatibilityContext(1, "game-1"),
            () => views,
            () => new CharacterIdentity(Scope, "Alice", "World"),
            () => "world-1",
            clock,
            physicalSnapshotProvider: physicalSnapshotProvider);
    }

    private static PublishedListRecord MakeList(Guid id)
        => new(
            new FcRecordHeader(1, 1, FcRecordTypes.PublishedList, id, "list-owner-" + id.ToString("N"), 1),
            id,
            "List " + id.ToString("N"),
            true,
            1,
            "game-1",
            [new PublishedRecipeTarget(1, 100, 10, FcItemQuality.Nq)],
            new FcQualityPolicy([new FcQualityRule(100, FcItemQuality.Nq, 10)]),
            FcQualityPolicy.Empty);

    private static WorkerSessionRecord Decode(FakeTransport.PutCall call)
        => JsonSerializer.Deserialize(call.Payload, FcJsonContext.Default.WorkerSessionRecord)
            ?? throw new InvalidOperationException("worker Put payload did not decode");

    private sealed class FakeTransport : IFcPublicationTransport
    {
        public bool IsReady { get; set; } = true;
        public string? LocalAuthorId { get; set; } = Author;
        public FcWorldStore WorldStore { get; } = new(new FcManualClock(1_000_000));
        public bool FailPuts { get; set; }
        public int PhysicalEffects { get; private set; }
        public List<PutCall> Puts { get; } = new();
        public Action? BeforePut { get; set; }
        public ManualResetEventSlim? PutEntered { get; set; }
        public ManualResetEventSlim? ReleasePut { get; set; }
        private int _putInvocations;

        public FcNativeCallResult Put(
            ReadOnlySpan<byte> recordId,
            ReadOnlySpan<byte> key,
            ReadOnlySpan<byte> recordType,
            bool hasGeneration,
            ulong generation,
            ulong revision,
            ReadOnlySpan<byte> payload)
        {
            var invocation = Interlocked.Increment(ref _putInvocations);
            if (invocation == 2)
            {
                BeforePut?.Invoke();
                PutEntered?.Set();
                ReleasePut?.Wait(TimeSpan.FromSeconds(1));
            }
            Puts.Add(new(
                System.Text.Encoding.UTF8.GetString(recordId),
                System.Text.Encoding.UTF8.GetString(key),
                System.Text.Encoding.UTF8.GetString(recordType),
                hasGeneration,
                generation,
                revision,
                payload.ToArray()));
            return FailPuts
                ? new FcNativeCallResult((uint)FcNativeErrorCode.QueueFull, 0, 0)
                : new FcNativeCallResult((uint)FcNativeErrorCode.Ok, 0, 0);
        }

        public sealed record PutCall(
            string RecordId,
            string Key,
            string RecordType,
            bool HasGeneration,
            ulong Generation,
            ulong Revision,
            byte[] Payload);
    }
}
