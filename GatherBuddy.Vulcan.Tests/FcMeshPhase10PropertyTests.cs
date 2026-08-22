using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Native;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.Vulcan.Tests;

internal static class FcMeshPhase10PropertyTests
{
    private const long ClockMilliseconds = 150_000;
    private const long FirstHlcMilliseconds = 100_000;
    private const string NodeId = "11111111111111111111111111111111";

    public static void Run(Action<bool, string> require)
    {
        for (var seed = 1; seed <= 64; seed++)
        {
            try
            {
                RunSeed(seed, require);
            }
            catch (Exception exception)
            {
                require(false, $"Phase10 property seed {seed} failed: {exception.Message}");
            }
        }
    }

    private static void RunSeed(int seed, Action<bool, string> require)
    {
        var clock = new FcManualClock(ClockMilliseconds);
        var listOwner = Owner(seed, 1);
        var responderOwner = Owner(seed, 2);
        var transferOwner = Owner(seed, 3);
        var listId = Id(seed, 1);
        var requestId = Id(seed, 2);
        var transferId = Id(seed, 3);
        var sessionId = Id(seed, 4);
        var itemId = (uint)(100 + seed);
        var recipeId = (uint)(200 + seed);

        var published = MakeList(listOwner, listId, 1, true, itemId, recipeId);
        var tombstone = published with
        {
            Header = published.Header with { Revision = 2 },
            Published = false,
        };
        var responder = MakeWorker(
            responderOwner,
            Id(seed, 5),
            1,
            1,
            0,
            listId,
            itemId,
            "world");
        var transferWorker = MakeWorker(
            transferOwner,
            sessionId,
            1,
            1,
            0,
            listId,
            itemId,
            "world");
        var transferChest = MakeChest(transferOwner, Id(seed, 6), 1, itemId, 5);
        var quality = seed % 2 == 0 ? FcItemQuality.Hq : FcItemQuality.Nq;
        var policy = new FcQualityPolicy([new FcQualityRule(itemId, quality, 1)]);
        var capability = new RequiredCraftCapability(recipeId, policy)
        {
            FinalQualityPolicy = policy,
            PrecraftQualityPolicy = FcQualityPolicy.Empty,
            IsPrecraft = false,
        };
        var request = new CapabilityRequestRecord(
            Header(FcRecordTypes.CapabilityRequest, requestId, listOwner, 1),
            requestId,
            $"world-{seed}",
            new FcHlcTimestamp(250_000, 0, listOwner),
            [capability])
        {
            RequesterAuthorId = listOwner,
            GameVersion = "game-1",
            PlannerFingerprint = "planner-1",
        };
        var response = new CapabilityResponseRecord(
            Header(FcRecordTypes.CapabilityResponse, requestId, responderOwner, 1),
            requestId,
            new FcHlcTimestamp(250_000, 0, responderOwner),
            request.WorldFingerprint,
            "game-1",
            "planner-1",
            $"gear-{seed}",
            $"solver-{seed}",
            [new CraftCapabilityResult(
                recipeId,
                CanCraft: true,
                SelectedJobId: 2,
                GuaranteesRequiredQuality: quality == FcItemQuality.Hq,
                Assessment: quality == FcItemQuality.Hq
                    ? FcRaphaelAssessmentOutcome.FullQuality
                    : FcRaphaelAssessmentOutcome.NoQualityRequired)])
        {
            RequestedRecipes = [capability],
            SessionId = responder.SessionId,
            SessionGeneration = responder.SessionGeneration,
            ResponderAuthorId = responderOwner,
        };
        var workerAfter = transferWorker with
        {
            Header = transferWorker.Header with { Revision = 2 },
            HeldInventory = [new ItemQuantityEntry(itemId, FcItemQuality.Nq, 1)],
        };
        var chestAfter = transferChest with
        {
            Header = transferChest.Header with { Revision = 2 },
            Items = [new ItemQuantityEntry(itemId, FcItemQuality.Nq, 4)],
        };
        var transfer = new FcInventoryTransferRecord(
            Header(FcRecordTypes.InventoryTransfer, transferId, transferOwner, 1),
            transferId,
            sessionId,
            1,
            listId,
            FcInventoryTransferKind.Withdraw,
            FcInventoryTransferOutcome.Committed,
            [new ItemQuantityEntry(itemId, FcItemQuality.Nq, 1)],
            chestAfter,
            workerAfter);

        var baseFrames = new[]
        {
            BuildFrame(published, listOwner, FirstHlcMilliseconds + 1),
            BuildFrame(responder, responderOwner, FirstHlcMilliseconds + 2, responder.SessionGeneration),
            BuildFrame(transferWorker, transferOwner, FirstHlcMilliseconds + 3, transferWorker.SessionGeneration),
            BuildFrame(transferChest, transferOwner, FirstHlcMilliseconds + 4),
            BuildFrame(request, listOwner, FirstHlcMilliseconds + 5),
            BuildFrame(response, responderOwner, FirstHlcMilliseconds + 6),
        };
        var laterFrames = new[]
        {
            BuildFrame(tombstone, listOwner, FirstHlcMilliseconds + 10),
            BuildFrame(transfer, transferOwner, FirstHlcMilliseconds + 11, transfer.SessionGeneration),
        };

        var stores = new[]
        {
            new FcWorldStore(clock),
            new FcWorldStore(clock),
            new FcWorldStore(clock),
        };
        for (var storeIndex = 0; storeIndex < stores.Length; storeIndex++)
        {
            foreach (var frame in Permute(baseFrames, seed + storeIndex * 17))
                ApplyAccepted(stores[storeIndex], frame, require, seed);

            foreach (var frame in Permute(laterFrames, seed + storeIndex * 31))
                ApplyAccepted(stores[storeIndex], frame, require, seed);

            // Duplication is a transport event, never a new business event.
            foreach (var frame in Permute(baseFrames.Concat(laterFrames).ToArray(), seed + 101 + storeIndex))
            {
                var duplicate = ApplyFrame(stores[storeIndex], frame);
                var expectedSuperseded = frame.Payload is PublishedListRecord
                    or WorkerSessionRecord
                    or ChestSnapshotRecord;
                require(duplicate.Status is FcApplyStatus.Accepted
                        or FcApplyStatus.Duplicate
                        || expectedSuperseded && duplicate.Status == FcApplyStatus.Rejected,
                    $"property seed {seed}: duplicate delivery failed: {duplicate.Status} {duplicate.Message}");
            }
        }

        var fingerprints = stores.Select(store => store.Revision.Fingerprint).Distinct().ToArray();
        require(fingerprints.Length == 1,
            $"property seed {seed}: permutations and duplication must converge to one world fingerprint");
        require(stores.All(store => store.Lists.Values.Single().Published == false),
            $"property seed {seed}: absolute tombstone must win over its older list revision");
        require(stores.All(store => store.Transfers.Count == 1
                && store.Workers[transferOwner + "/worker"].HeldInventoryMap.Get(new FcQuantityKey(itemId, FcItemQuality.Nq)) == 1
                && store.Chests[transferOwner + "/chest"].ItemMap.Get(new FcQuantityKey(itemId, FcItemQuality.Nq)) == 4),
            $"property seed {seed}: atomic transfer must expose both after-states together");

        var projection = new FcWorldProjection(stores[0]).Build(
            clock,
            new FcCompatibilityContext(1, "game-1"));
        require(projection.ActiveLists.Count == 0
                && projection.ActiveWorkers.Count == 2
                && projection.Chest.IsFresh,
            $"property seed {seed}: projection must derive active workers and fresh chest from the converged store");
        var responderHlc = stores[0].GetWorkerHlc(responderOwner);
        require(responderHlc is not null
                && FcWorldProjection.IsCapabilityValid(
                    request,
                    response,
                    responder,
                    responderHlc.Value,
                    request.WorldFingerprint,
                    clock),
            $"property seed {seed}: exact capability response must remain eligible through projection");

        var oldRevision = ApplyFrame(stores[0], BuildFrame(published, listOwner, FirstHlcMilliseconds + 12));
        require(oldRevision.Status == FcApplyStatus.Rejected,
            $"property seed {seed}: older author revision must remain rejected after tombstone delivery");

        var forkA = published with
        {
            Header = published.Header with { Revision = 3 },
            DisplayName = $"fork-a-{seed}",
        };
        var forkB = forkA with { DisplayName = $"fork-b-{seed}" };
        var forkAResult = ApplyFrame(stores[0], BuildFrame(forkA, listOwner, FirstHlcMilliseconds + 20));
        var forkBResult = ApplyFrame(stores[0], BuildFrame(forkB, listOwner, FirstHlcMilliseconds + 21));
        require(forkAResult.Status == FcApplyStatus.Accepted
                && forkBResult.Status == FcApplyStatus.Fork
                && stores[0].IsForked(listOwner + "/list/" + listId.ToString("D"))
                && stores[0].ForkVariants.Values.Single().Count == 2
                && new FcWorldProjection(stores[0]).Build(clock, new FcCompatibilityContext(1, "game-1")).ActiveLists.Count == 0,
            $"property seed {seed}: equal-revision fork must retain sorted evidence and exclude action");
        var repair = forkA with
        {
            Header = forkA.Header with { Revision = 4 },
            DisplayName = $"repair-{seed}",
        };
        require(ApplyFrame(stores[0], BuildFrame(repair, listOwner, FirstHlcMilliseconds + 22)).Status
                    == FcApplyStatus.Accepted
                && !stores[0].IsForked(listOwner + "/list/" + listId.ToString("D")),
            $"property seed {seed}: strictly newer repair must clear the write block");

        var regressionStore = new FcWorldStore(clock);
        var high = BuildFrame(published with { Header = published.Header with { Revision = 1 } }, listOwner, 120_000);
        var low = BuildFrame(published with { Header = published.Header with { Revision = 2 } }, listOwner, 119_999);
        require(ApplyFrame(regressionStore, high).Status == FcApplyStatus.Accepted
                && ApplyFrame(regressionStore, low).Status == FcApplyStatus.Rejected,
            $"property seed {seed}: higher author revision cannot regress HLC");

        var badWorkerAfter = workerAfter with
        {
            Header = workerAfter.Header with { Revision = 3 },
            HeldInventory = [new ItemQuantityEntry(itemId, FcItemQuality.Nq, 2)],
        };
        var badChestAfter = chestAfter with
        {
            Header = chestAfter.Header with { Revision = 3 },
            Items = [new ItemQuantityEntry(itemId, FcItemQuality.Nq, 3)],
        };
        var badTransfer = transfer with
        {
            OperationId = Id(seed, 7),
            Header = transfer.Header with { RecordId = Id(seed, 7) },
            ActualTransferred = [new ItemQuantityEntry(itemId, FcItemQuality.Nq, 2)],
            WorkerAfter = badWorkerAfter,
            ChestAfter = badChestAfter,
        };
        var beforeBadTransfer = stores[1].Revision.Fingerprint;
        require(ApplyFrame(stores[1], BuildFrame(badTransfer, transferOwner, FirstHlcMilliseconds + 30, 1)).Status
                    == FcApplyStatus.Rejected
                && stores[1].Revision.Fingerprint == beforeBadTransfer
                && stores[1].Workers[transferOwner + "/worker"].HeldInventoryMap.Get(new FcQuantityKey(itemId, FcItemQuality.Nq)) == 1
                && stores[1].Chests[transferOwner + "/chest"].ItemMap.Get(new FcQuantityKey(itemId, FcItemQuality.Nq)) == 4,
            $"property seed {seed}: invalid transfer must mutate neither after-state");

        var matcher = FcFulfillmentMatcher.Match(
            [
                new FcListDemand(listId, itemId, FcItemQuality.Nq, 5),
                new FcListDemand(Id(seed, 8), itemId, FcItemQuality.Nq, 5),
                new FcListDemand(listId, itemId, FcItemQuality.Hq, 2),
            ],
            new FcItemQuantityMap([
                new ItemQuantityEntry(itemId, FcItemQuality.Nq, 3),
                new ItemQuantityEntry(itemId, FcItemQuality.Hq, 1),
            ]),
            [
                new FcWorkerSupply(
                    "worker-a",
                    new FcItemQuantityMap([new ItemQuantityEntry(itemId, FcItemQuality.Nq, 4)]),
                    new HashSet<Guid> { listId }),
                new FcWorkerSupply(
                    "worker-b",
                    new FcItemQuantityMap([new ItemQuantityEntry(itemId, FcItemQuality.Nq, 4)]),
                    new HashSet<Guid> { Id(seed, 8) }),
            ]);
        require(matcher.TotalMatched == 11
                && matcher.Contributions
                    .Where(contribution => contribution.Key.Quality == FcItemQuality.Nq)
                    .Sum(contribution => contribution.Quantity) == 10
                && matcher.Contributions
                    .Where(contribution => contribution.Key.Quality == FcItemQuality.Hq)
                    .Sum(contribution => contribution.Quantity) == 1,
            $"property seed {seed}: accounting must conserve unit capacity per quality and list scope");

        var tracker = new FcLivenessTracker();
        require(tracker.IsActive(responder, new FcHlcTimestamp(100_000, 900, NodeId), FirstHlcMilliseconds + 299_999)
                && !tracker.IsActive(responder, new FcHlcTimestamp(100_000, 900, NodeId), FirstHlcMilliseconds + 300_000),
            $"property seed {seed}: physical HLC time, not logical counter, defines liveness freshness");
    }

    private static void ApplyAccepted(
        FcWorldStore store,
        Frame frame,
        Action<bool, string> require,
        int seed)
    {
        var result = ApplyFrame(store, frame);
        require(result.Status is FcApplyStatus.Accepted or FcApplyStatus.Duplicate,
            $"property seed {seed}: absolute frame delivery failed: {result.Status} {result.Message}");
    }

    private static FcApplyResult ApplyFrame(FcWorldStore store, Frame frame)
        => frame.Payload switch
        {
            PublishedListRecord value => store.Apply(frame.Envelope, value, frame.Context),
            WorkerSessionRecord value => store.Apply(frame.Envelope, value, frame.Context),
            ChestSnapshotRecord value => store.Apply(frame.Envelope, value, frame.Context),
            CapabilityRequestRecord value => store.Apply(frame.Envelope, value, frame.Context),
            CapabilityResponseRecord value => store.Apply(frame.Envelope, value, frame.Context),
            FcInventoryTransferRecord value => store.Apply(frame.Envelope, value, frame.Context),
            _ => throw new InvalidOperationException($"Unsupported property frame {frame.Payload.GetType().Name}.")
        };

    private static IEnumerable<Frame> Permute(IReadOnlyList<Frame> frames, int seed)
    {
        var result = frames.ToArray();
        var state = unchecked((uint)seed * 0x9E3779B9u);
        for (var index = result.Length - 1; index > 0; index--)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            var swap = (int)(state % (uint)(index + 1));
            (result[index], result[swap]) = (result[swap], result[index]);
        }
        return result;
    }

    private static Frame BuildFrame(object payload, string owner, long physical, ulong? generation = null)
    {
        var header = HeaderOf(payload);
        var bytes = FcCanonical.SerializeUtf8(payload);
        var documentKey = FcMeshKey.ForRecord(header.RecordType, owner, header.RecordId.ToString("D"));
        var wire = new FcNativeEnvelopeWire
        {
            ProtocolVersion = FcProtocolVersion.Current,
            RecordId = header.RecordId.ToString("D"),
            ActualAuthorId = owner,
            Generation = generation,
            Revision = header.Revision,
            Hlc = new FcNativeHlcWire
            {
                PhysicalUnixMs = physical,
                Logical = (ulong)(physical - FirstHlcMilliseconds),
                NodeId = NodeId,
            },
            RecordType = header.RecordType,
            Payload = bytes,
            PayloadHash = FcMeshSignature.HashPayload(bytes),
            Signature = Convert.ToBase64String(new byte[64]),
            DocumentKeyOwnerId = owner,
            DocumentKey = documentKey,
            SignatureAlgorithm = FcNativeEnvelopeDecoder.SignatureAlgorithm,
        };
        var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(
            wire,
            FcJsonContext.Default.FcNativeEnvelopeWire);
        // Property generation targets world-store algebra, not envelope
        // cryptography. The real signed fixture and native insertion tests
        // cover that boundary; keep this generated context explicitly
        // synthetic while preserving exact envelope bytes for fork evidence.
        var record = new FcMeshRecord(
            wire.RecordId,
            wire.ActualAuthorId,
            wire.Generation,
            wire.Revision,
            new FcHlcTimestamp(wire.Hlc.PhysicalUnixMs, wire.Hlc.Logical, wire.Hlc.NodeId),
            wire.RecordType,
            wire.Payload,
            wire.PayloadHash)
        {
            ProtocolVersion = wire.ProtocolVersion,
            Signature = wire.Signature,
            DocumentKeyOwnerId = wire.DocumentKeyOwnerId,
            DocumentKey = wire.DocumentKey,
            SignatureAlgorithm = wire.SignatureAlgorithm,
            OriginalEnvelopeBytes = envelopeBytes,
        };
        var context = FcVerifiedMeshContext.FromEnvelope(record) with { SignatureValid = true };
        return new Frame(record, payload, context);
    }

    private static PublishedListRecord MakeList(
        string owner,
        Guid listId,
        ulong revision,
        bool published,
        uint itemId,
        uint recipeId)
        => new(
            Header(FcRecordTypes.PublishedList, listId, owner, revision),
            listId,
            $"property-list-{listId:N}",
            published,
            1,
            "game-1",
            [new PublishedRecipeTarget(recipeId, itemId, 5, FcItemQuality.Nq)],
            FcQualityPolicy.Empty,
            FcQualityPolicy.Empty);

    private static WorkerSessionRecord MakeWorker(
        string owner,
        Guid sessionId,
        ulong revision,
        ulong generation,
        int held,
        Guid listId,
        uint itemId,
        string world)
        => new(
            Header(FcRecordTypes.WorkerSession, sessionId, owner, revision),
            sessionId,
            generation,
            FcWorkerState.Active,
            new CharacterIdentity(owner, owner, "world"),
            FcFulfillmentSelection.Specific(listId),
            false,
            held == 0
                ? Array.Empty<ItemQuantityEntry>()
                : [new ItemQuantityEntry(itemId, FcItemQuality.Nq, held)],
            null,
            Array.Empty<FcLogicalQueueEntry>(),
            [itemId],
            world);

    private static ChestSnapshotRecord MakeChest(
        string owner,
        Guid recordId,
        ulong revision,
        uint itemId,
        int quantity)
        => new(
            Header(FcRecordTypes.ChestSnapshot, recordId, owner, revision),
            true,
            FcRecordValidator.CompleteChestPageMask,
            [new ItemQuantityEntry(itemId, FcItemQuality.Nq, quantity)],
            new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>()));

    private static FcRecordHeader Header(string type, Guid recordId, string owner, ulong revision)
        => new(FcProtocolVersion.Current, FcProtocolVersion.CurrentSchema, type, recordId, owner, revision);

    private static FcRecordHeader HeaderOf(object value)
        => value switch
        {
            PublishedListRecord record => record.Header,
            WorkerSessionRecord record => record.Header,
            ChestSnapshotRecord record => record.Header,
            CapabilityRequestRecord record => record.Header,
            CapabilityResponseRecord record => record.Header,
            FcInventoryTransferRecord record => record.Header,
            _ => throw new InvalidOperationException($"Unsupported property fixture {value.GetType().Name}.")
        };

    private static string Owner(int seed, int slot)
        => string.Concat(Enumerable.Repeat((((seed * 7 + slot) % 15) + 1).ToString("x"), 64));

    private static Guid Id(int seed, int slot)
        => Guid.Parse($"00000000-0000-0000-0000-{(seed * 32 + slot):x12}");

    private sealed record Frame(
        FcMeshRecord Envelope,
        object Payload,
        FcVerifiedMeshContext Context);
}
