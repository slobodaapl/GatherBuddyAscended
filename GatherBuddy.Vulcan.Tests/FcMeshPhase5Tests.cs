using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Chest;
using GatherBuddy.FcMesh.Native;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.Vulcan.Tests;

internal static class FcMeshPhase5Tests
{
    private static readonly string Scope = "0000000000000001";
    private static readonly string Author = FcMeshNativeTests.DeterministicAuthor;
    private static readonly string RemoteAuthor = FcMeshNativeTests.DeterministicRemoteAuthor;

    public static void Run(Action<bool, string> require)
    {
        GameVersionParser(require);
        MapperWhitelistAndOrdering(require);
        PublishUpdateUnpublishAndPersistence(require);
        InitializationMarkerCrashWindow(require);
        CompatibilityIsReadOnly(require);
        CompleteChestOnly(require);
        InitialSyncRequiresManagedSnapshot(require);
        TicketNeverEntersDiagnostics(require);
    }

    private static void GameVersionParser(Action<bool, string> require)
    {
        var metadata = Encoding.UTF8.GetBytes(
            "{\"AssemblyVersion\":\"15.0.3.2\",\"SupportedGameVer\":\"2026.08.11.0000.0000\",\"RuntimeVersion\":\"10.0.0\"}");
        require(FcGameVersionProvider.ParseSupportedGameVersion(metadata)
                == "2026.08.11.0000.0000",
            "game compatibility must come from Dalamud SupportedGameVer metadata");

        var pluginOnly = Encoding.UTF8.GetBytes("{\"AssemblyVersion\":\"2.3.0.0\"}");
        require(FcGameVersionProvider.ParseSupportedGameVersion(pluginOnly) is null,
            "plugin or assembly version must not substitute for game compatibility");
        require(FcGameVersionProvider.ParseSupportedGameVersion(
                    Encoding.UTF8.GetBytes("{\"SupportedGameVer\":null}")) is null,
            "missing or malformed SupportedGameVer must keep publication blocked");
        require(FcGameVersionProvider.ParseSupportedGameVersion(
                    Encoding.UTF8.GetBytes("{\"SupportedGameVer\":\"plugin-build\"}")) is null,
            "non-FFXIV SupportedGameVer text must keep publication blocked");
    }

    private static void MapperWhitelistAndOrdering(Action<bool, string> require)
    {
        var list = new CraftingListDefinition
        {
            ID = 41,
            CreatedAt = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc),
            Name = "Public final goals",
            Description = "must not cross the public boundary",
            UseAllHQ = true,
            Materia = true,
            Recipes =
            [
                new CraftingListItem(20, 2),
                new CraftingListItem(10, 3),
            ],
        };
        var mapped = FcPublishedListMapper.TryMap(
            list,
            Author,
            "game-1",
            1,
            Guid.Parse("00000000-0000-0000-0000-000000000501"),
            recipeId => recipeId == 10
                ? new FcRecipeOutput(100, 2)
                : new FcRecipeOutput(200, 1));
        require(mapped.IsValid && mapped.Record is not null,
            "Phase5 mapper must capture a valid reduced final-target snapshot");
        if (mapped.Record is { } record)
        {
            require(record.FinalTargets.Select(target => target.ItemId).SequenceEqual([100u, 200u])
                    && record.FinalTargets[0].Quantity == 6
                    && record.FinalTargets[0].Quality == FcItemQuality.Hq
                    && record.FinalQualityPolicy.Rules.Length == 2
                    && record.PrecraftQualityPolicy.Rules.Length == 0,
                "published targets must be checked, quality-keyed, and deterministically ordered");
            var payload = FcCanonical.Serialize(record);
            require(!payload.Contains("Description", StringComparison.Ordinal)
                    && !payload.Contains("Materia", StringComparison.Ordinal)
                    && !payload.Contains("Consumable", StringComparison.Ordinal),
                "published payload must not carry private list settings");
        }
        var ingredientPolicyOff = new CraftingListDefinition
        {
            ID = 43,
            CreatedAt = list.CreatedAt,
            Name = "Ingredient policy off",
            UseAllHQ = false,
            Recipes = [new CraftingListItem(10, 1)],
        };
        var ingredientPolicyOn = new CraftingListDefinition
        {
            ID = 44,
            CreatedAt = list.CreatedAt,
            Name = "Ingredient policy on",
            UseAllHQ = true,
            Recipes = [new CraftingListItem(10, 1)],
        };
        var policyOffMapped = FcPublishedListMapper.TryMap(
            ingredientPolicyOff,
            Author,
            "game-1",
            1,
            Guid.Parse("00000000-0000-0000-0000-000000000503"),
            _ => new FcRecipeOutput(100, 1));
        var policyOnMapped = FcPublishedListMapper.TryMap(
            ingredientPolicyOn,
            Author,
            "game-1",
            1,
            Guid.Parse("00000000-0000-0000-0000-000000000505"),
            _ => new FcRecipeOutput(100, 1));
        require(policyOffMapped.IsValid
                && policyOnMapped.IsValid
                && policyOffMapped.Record?.FinalTargets.Single().Quality == FcItemQuality.Hq
                && policyOnMapped.Record?.FinalTargets.Single().Quality == FcItemQuality.Hq,
            "UseAllHQ ingredient policy must not change an ordinary HQ-capable final target");

        var nqOnlyOn = new CraftingListDefinition
        {
            ID = 45,
            CreatedAt = list.CreatedAt,
            Name = "NQ-only policy on",
            UseAllHQ = true,
            Recipes = [new CraftingListItem(10, 1)
            {
                Options = new ListItemOptions { NQOnly = true },
            }],
        };
        var nqOnlyOff = new CraftingListDefinition
        {
            ID = 46,
            CreatedAt = list.CreatedAt,
            Name = "NQ-only policy off",
            UseAllHQ = false,
            Recipes = [new CraftingListItem(10, 1)
            {
                Options = new ListItemOptions { NQOnly = true },
            }],
        };
        var nqOnlyOnMapped = FcPublishedListMapper.TryMap(
            nqOnlyOn,
            Author,
            "game-1",
            1,
            Guid.Parse("00000000-0000-0000-0000-000000000507"),
            _ => new FcRecipeOutput(100, 1));
        var nqOnlyOffMapped = FcPublishedListMapper.TryMap(
            nqOnlyOff,
            Author,
            "game-1",
            1,
            Guid.Parse("00000000-0000-0000-0000-000000000509"),
            _ => new FcRecipeOutput(100, 1));
        require(nqOnlyOnMapped.IsValid
                && nqOnlyOffMapped.IsValid
                && nqOnlyOnMapped.Record?.FinalTargets.Single().Quality == FcItemQuality.Nq
                && nqOnlyOffMapped.Record?.FinalTargets.Single().Quality == FcItemQuality.Nq,
            "NQOnly must select NQ output regardless of UseAllHQ ingredient policy");

        var nonHqOutput = new CraftingListDefinition
        {
            ID = 47,
            CreatedAt = list.CreatedAt,
            Name = "Non-HQ output",
            UseAllHQ = true,
            Recipes = [new CraftingListItem(10, 1)],
        };
        var nonHqMapped = FcPublishedListMapper.TryMap(
            nonHqOutput,
            Author,
            "game-1",
            1,
            Guid.Parse("00000000-0000-0000-0000-000000000511"),
            _ => new FcRecipeOutput(100, 1, CanBeHq: false));
        require(nonHqMapped.IsValid
                && nonHqMapped.Record?.FinalTargets.Single().Quality == FcItemQuality.Nq,
            "final outputs that cannot be HQ must map to NQ");
    }

    private static void PublishUpdateUnpublishAndPersistence(Action<bool, string> require)
    {
        var store = new FcInMemoryPublicationStateStore();
        var transport = new FakePublicationTransport();
        using var service = NewListService(store, transport);
        require(service.State.BackendKind == "iroh-docs"
                && service.State.BackendStorageVersion == 1
                && service.State.ChestRecordId != Guid.Empty,
            "publication state must persist the backend schema and stable observer register identity");
        var list = new CraftingListDefinition
        {
            ID = 42,
            CreatedAt = new DateTime(2026, 8, 20, 11, 0, 0, DateTimeKind.Utc),
            Name = "First name",
            Recipes = [new CraftingListItem(10, 2)
            {
                Options = new ListItemOptions { NQOnly = true },
            }],
        };
        var published = service.Publish(list);
        require(service.PublicLists.Count == 0,
            "public list projection must remain empty until an accepted world record arrives");
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(published.Accepted && transport.Puts.Count == 1,
            "explicit publish reserves and enqueues one native record");
        var publishedMapping = service.State.Lists.Single();
        if (publishedMapping.LastPublishedSnapshot is not { } firstSnapshot)
        {
            require(false, "accepted publication must retain its immutable snapshot");
            return;
        }
        var firstId = publishedMapping.PublicListId;
        var firstRevision = firstSnapshot.Header.Revision;
        require(transport.Puts[0].RecordId == firstId.ToString("D")
                && transport.Puts[0].Key == FcMeshKey.ForRecord(FcRecordTypes.PublishedList, Author, firstId.ToString("D"))
                && transport.Puts[0].HasGeneration == false
                && transport.Puts[0].Revision == firstRevision,
            "native Put must use stable Guid-D identity, exact published-list key, null generation, and reserved revision");

        list.Name = "Edited locally";
        var beforeUpdateSnapshot = service.State.Lists.Single().LastPublishedSnapshot;
        if (beforeUpdateSnapshot is null)
        {
            require(false, "local edits must retain the prior public snapshot");
            return;
        }
        require(beforeUpdateSnapshot.DisplayName == "First name",
            "local edits must not mutate the prior public snapshot");
        var updated = service.Update(list);
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var updatedMapping = service.State.Lists.Single();
        if (updatedMapping.LastPublishedSnapshot is not { } updatedSnapshot)
        {
            require(false, "accepted update must retain its immutable snapshot");
            return;
        }
        require(updated.Accepted
                && updatedMapping.PublicListId == firstId
                && updatedSnapshot.DisplayName == "Edited locally"
                && updatedSnapshot.Header.Revision > firstRevision,
            "explicit update must reuse the public Guid and publish a newer immutable copy");

        var tombstone = service.Unpublish(list);
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var tombstoneMapping = service.State.Lists.Single();
        if (tombstoneMapping.LastPublishedSnapshot is not { } tombstoneSnapshot)
        {
            require(false, "accepted unpublish must retain its tombstone snapshot");
            return;
        }
        require(tombstone.Accepted
                && !tombstoneSnapshot.Published
                && tombstoneSnapshot.Header.Revision > updated.Revision,
            "unpublish must be a newer tombstone, never key removal");

        var reopened = NewListService(store, transport);
        using (reopened)
        {
            require(reopened.State.Lists.Single().PublicListId == firstId
                    && reopened.State.Lists.Single().ReservedRevision >= tombstone.Revision,
                "restart must retain the stable local mapping and high-water revision");
        }

        store.MarkCorrupt(Scope);
        using var corrupt = NewListService(store, transport);
        require(!corrupt.Publish(list).Accepted,
            "corrupt local reservation state must fail closed instead of reusing a revision");
        transport.FailPuts = true;
        var queueFailure = service.Publish(list);
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(queueFailure.Accepted
                && service.State.Lists.Single().Pending?.Status == FcPublicationCommandStatus.Failed
                && service.State.Lists.Single().ReservedRevision > tombstone.Revision,
            "native queue failure must retain a failed pending command and consume the reserved revision");
    }

    private static void CompatibilityIsReadOnly(Action<bool, string> require)
    {
        var list = new PublishedListRecord(
            new FcRecordHeader(1, 1, FcRecordTypes.PublishedList, Guid.Parse("00000000-0000-0000-0000-000000000504"), RemoteAuthor, 1),
            Guid.Parse("00000000-0000-0000-0000-000000000504"),
            "Remote",
            true,
            FcPublishedListMapper.CurrentPlannerSemanticsVersion,
            "old-game",
            [new PublishedRecipeTarget(10, 100, 1, FcItemQuality.Nq)],
            FcQualityPolicy.Empty,
            FcQualityPolicy.Empty);
        var transport = new FakePublicationTransport();
        var envelope = FcMeshRecord.Create(
            list,
            RemoteAuthor,
            new FcHlcTimestamp(1_000_000, 0, RemoteAuthor),
            null,
            list.Header.Revision,
            FcRecordTypes.PublishedList,
            list.ListId.ToString("D")) with
        {
            Signature = "fake-native-signature",
            SignatureAlgorithm = "fake-native-context",
        };
        var applied = transport.WorldStore.Apply(
            envelope,
            list,
            FcVerifiedMeshContext.FromEnvelope(envelope) with
            {
                SignatureValid = true,
                TransportAuthorId = RemoteAuthor,
            });
        require(applied.Status == FcApplyStatus.Accepted,
            $"structurally valid incompatible public list must enter the world store; status={applied.Status}; message={applied.Message}");
        if (applied.Status != FcApplyStatus.Accepted)
            return;

        using var service = NewListService(new FcInMemoryPublicationStateStore(), transport);
        var views = service.PublicLists;
        var projection = new FcWorldProjection(transport.WorldStore)
            .Build(
                new FcManualClock(1_000_000),
                new FcCompatibilityContext(FcPublishedListMapper.CurrentPlannerSemanticsVersion, "game-1"));
        require(views.Count == 1
                && views[0].Published
                && !views[0].IsCompatible
                && views[0].CompatibilityReason.Contains("does not match", StringComparison.Ordinal)
                && projection.ActiveLists.Count == 0,
            "incompatible public records remain explainable read-only values and stay out of execution selection");
    }

    private static void CompleteChestOnly(Action<bool, string> require)
    {
        var store = new FcInMemoryPublicationStateStore();
        var transport = new FakePublicationTransport();
        var complete = CompleteSnapshot();
        var reader = new FakeChestReader(new FcChestReadResult(complete, string.Empty));
        using var service = new FcChestPublicationService(store, transport, reader, () => Scope, () => Author);
        var published = service.PublishCurrentCompleteObservation();
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        require(published.Accepted && published.Snapshot is null && reader.Reads == 0 && transport.Puts.Count == 0,
            "chest publication reserves before any framework-thread observation or native Put");
        reader.Result = new FcChestReadResult(CompleteSnapshot(7), string.Empty);
        service.ProcessFrameworkCommands();
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        var encoded = transport.Puts.Count == 1
            ? JsonSerializer.Deserialize(
                transport.Puts.Single().Payload,
                FcJsonContext.Default.ChestSnapshotRecord)
            : null;
        require(transport.Puts.Count == 1
                && transport.Puts.Single().RecordType == FcRecordTypes.ChestSnapshot
                && encoded is { } encodedSnapshot
                && encodedSnapshot.ItemMap.Get(100) == 7,
            "the final framework-thread chest read, not an earlier UI snapshot, becomes the published payload");
        reader.Result = FcChestReadResult.Incomplete("addon not ready");
        var rejected = service.PublishCurrentCompleteObservation();
        service.DrainAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        service.ProcessFrameworkCommands();
        require(rejected.Accepted && transport.Puts.Count == 1
                && rejected.Revision > published.Revision
                && service.LastError.Contains("addon not ready", StringComparison.Ordinal),
            "incomplete chest/addon observations must not publish or invoke movement");
    }

    private static void InitializationMarkerCrashWindow(Action<bool, string> require)
    {
        var store = new FcInMemoryPublicationStateStore();
        require(store.Load("marker-scope").Status == FcPublicationStateLoadStatus.Missing,
            "an absent marker and absent state are a clean first run");
        store.MarkInitializationMarkerOnly("marker-scope");
        var blocked = store.Load("marker-scope");
        require(blocked.Status == FcPublicationStateLoadStatus.Corrupt
                && !blocked.CanWrite
                && blocked.Error.Contains("marker", StringComparison.OrdinalIgnoreCase),
            "a committed initialization marker without state blocks writes after a crash");
    }

    private static void InitialSyncRequiresManagedSnapshot(Action<bool, string> require)
    {
        var fake = new FcMeshNativeTests.FakeNativeApi();
        using var coordinator = new FcMeshNativeCoordinator(
            fake,
            new FcWorldStore(),
            () => new FcWorldStore(),
            maxEventsPerTick: 1,
            statusInterval: TimeSpan.Zero);
        require(coordinator.Start(new FcNativeConfiguration("phase5-test"), FcMeshNativeTests.TestCharacterKey).Succeeded,
            "initial-sync coordinator test must start through the managed native boundary");
        require(coordinator.JoinGroup([1]).Succeeded,
            "initial-sync coordinator test must enter the contacted-peer readiness path");
        fake.EnqueueJoined();
        fake.EnqueueInitialSync(1);
        fake.EnqueueCompatibleGroupMetadataSnapshot();
        coordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(!coordinator.AutomationDecisionsAllowed
                && coordinator.Readiness.State != FcMeshReadinessState.Ready,
            "InitialSyncCompleted must not enable writes before the managed snapshot swaps");
        for (var index = 0; index < 8 && !coordinator.AutomationDecisionsAllowed; index++)
            coordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(coordinator.AutomationDecisionsAllowed
                && coordinator.Readiness.State == FcMeshReadinessState.Ready
                && coordinator.Readiness.ContactedPeer is not null,
            "matching managed snapshot base evidence must transition the coordinator to Ready");

        var metadataOnlyFake = new FcMeshNativeTests.FakeNativeApi();
        using var metadataOnlyCoordinator = new FcMeshNativeCoordinator(
            metadataOnlyFake,
            new FcWorldStore(),
            () => new FcWorldStore(),
            maxEventsPerTick: 1,
            statusInterval: TimeSpan.Zero);
        require(metadataOnlyCoordinator.Start(new FcNativeConfiguration("phase5-metadata-evidence-test"), FcMeshNativeTests.TestCharacterKey).Succeeded
                && metadataOnlyCoordinator.JoinGroup([1]).Succeeded,
            "metadata evidence test must enter the contacted-peer readiness path");
        metadataOnlyFake.EnqueueJoined();
        metadataOnlyFake.EnqueueCompatibleGroupMetadataEvent();
        metadataOnlyFake.EnqueueInitialSync();
        for (var index = 0; index < 32 && metadataOnlyFake.SnapshotRequests < 2; index++)
            metadataOnlyCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(!metadataOnlyCoordinator.AutomationDecisionsAllowed
                && metadataOnlyCoordinator.Readiness.State == FcMeshReadinessState.ReconcilingSnapshot
                && metadataOnlyCoordinator.Diagnostics.GroupMetadataCompatible,
            "live metadata before InitialSyncCompleted must not seed readiness when the managed snapshot omits it");

        var countFake = new FcMeshNativeTests.FakeNativeApi();
        using var countCoordinator = new FcMeshNativeCoordinator(
            countFake,
            new FcWorldStore(),
            () => new FcWorldStore(),
            maxEventsPerTick: 1,
            statusInterval: TimeSpan.Zero);
        require(countCoordinator.Start(new FcNativeConfiguration("phase5-count-lower-bound-test"), FcMeshNativeTests.TestCharacterKey).Succeeded
                && countCoordinator.JoinGroup([1]).Succeeded,
            "snapshot count test must enter the contacted-peer readiness path");
        countFake.EnqueueJoined();
        countFake.EnqueueInitialSync(1);
        countFake.EnqueueCompatibleGroupMetadataSnapshot();
        var snapshotList = MakeSnapshotList(Guid.Parse("00000000-0000-0000-0000-000000000121"), "snapshot-list");
        countFake.EnqueueSnapshotRecord(
            FcMeshNativeTests.BuildNativeRecord(snapshotList, 1_001, 0).Snapshot);
        var replayedList = MakeSnapshotList(Guid.Parse("00000000-0000-0000-0000-000000000122"), "replayed-list");
        var replayedSequence = countFake.AllocateSequence();
        countFake.EnqueueRecord(
            FcMeshNativeTests.BuildNativeRecord(
                replayedList,
                1_002,
                replayedSequence).Event);
        countFake.SnapshotBaseSequenceOverride = replayedSequence - 1;
        for (var index = 0; index < 32 && !countCoordinator.AutomationDecisionsAllowed; index++)
            countCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        var countReadiness = countCoordinator.Readiness;
        var countDiagnostics = countCoordinator.Diagnostics;
        var countListCount = countCoordinator.WorldStore.PublishedListsFor(Author).Count();
        require(countCoordinator.AutomationDecisionsAllowed
                && countListCount == 2,
            $"snapshot count lower-bound replay must be physically consistent; syncCount={countReadiness.RecordCount}; "
            + $"snapshotCount={countFake.SnapshotRecordsPolled}; base={countFake.LastSnapshotBaseEventSequence}; "
            + $"laterSequence={replayedSequence}; lastEvent={countDiagnostics.LastEventSequence}; "
            + $"observedLists={countListCount}; state={countReadiness.State}; allowed={countReadiness.IsReady}; "
            + $"error={countReadiness.Error}");

        var fewerFake = new FcMeshNativeTests.FakeNativeApi();
        using var fewerCoordinator = new FcMeshNativeCoordinator(
            fewerFake,
            new FcWorldStore(),
            () => new FcWorldStore(),
            maxEventsPerTick: 1,
            statusInterval: TimeSpan.Zero);
        require(fewerCoordinator.Start(new FcNativeConfiguration("phase5-count-reject-test"), FcMeshNativeTests.TestCharacterKey).Succeeded
                && fewerCoordinator.JoinGroup([1]).Succeeded,
            "fewer-than-count test must enter the contacted-peer readiness path");
        fewerFake.EnqueueJoined();
        fewerFake.EnqueueInitialSync(2);
        fewerFake.EnqueueCompatibleGroupMetadataSnapshot();
        for (var index = 0; index < 32 && fewerFake.SnapshotRequests < 2; index++)
            fewerCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(!fewerCoordinator.AutomationDecisionsAllowed
                && fewerFake.SnapshotRequests >= 2
                && fewerCoordinator.Diagnostics.LastError.Contains("below", StringComparison.OrdinalIgnoreCase),
            "a managed snapshot below InitialSyncCompleted record count must retry and remain not ready");

        var createFake = new FcMeshNativeTests.FakeNativeApi();
        using var createCoordinator = new FcMeshNativeCoordinator(
            createFake,
            new FcWorldStore(),
            () => new FcWorldStore(),
            maxEventsPerTick: 1,
            statusInterval: TimeSpan.Zero);
        require(createCoordinator.Start(new FcNativeConfiguration("phase5-create-metadata-test"), FcMeshNativeTests.TestCharacterKey).Succeeded
                && createCoordinator.CreateGroup().Succeeded,
            "create-group metadata test must enter the managed snapshot path");
        createFake.EnqueueJoined(created: true);
        createFake.EnqueueCompatibleGroupMetadataSnapshot();
        for (var index = 0; index < 32 && !createCoordinator.AutomationDecisionsAllowed; index++)
            createCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(createCoordinator.AutomationDecisionsAllowed,
            "CreateGroup must become ready only after a managed snapshot proves compatible metadata");

        var createOmittedFake = new FcMeshNativeTests.FakeNativeApi();
        using var createOmittedCoordinator = new FcMeshNativeCoordinator(
            createOmittedFake,
            new FcWorldStore(),
            () => new FcWorldStore(),
            maxEventsPerTick: 1,
            statusInterval: TimeSpan.Zero);
        require(createOmittedCoordinator.Start(new FcNativeConfiguration("phase5-create-metadata-omitted-test"), FcMeshNativeTests.TestCharacterKey).Succeeded
                && createOmittedCoordinator.CreateGroup().Succeeded,
            "CreateGroup omission test must enter the managed snapshot path");
        createOmittedFake.EnqueueCompatibleGroupMetadataEvent();
        createOmittedFake.EnqueueJoined(created: true);
        for (var index = 0; index < 32 && createOmittedFake.SnapshotRequests < 2; index++)
            createOmittedCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(!createOmittedCoordinator.AutomationDecisionsAllowed
                && createOmittedCoordinator.Diagnostics.LastError.Contains("managed snapshot", StringComparison.OrdinalIgnoreCase),
            "CreateGroup live metadata must not substitute for metadata in the managed snapshot");

        var preSyncFake = new FcMeshNativeTests.FakeNativeApi();
        using var preSyncCoordinator = new FcMeshNativeCoordinator(
            preSyncFake,
            new FcWorldStore(),
            () => new FcWorldStore(),
            statusInterval: TimeSpan.Zero);
        require(preSyncCoordinator.Start(new FcNativeConfiguration("phase5-pre-sync-test"), FcMeshNativeTests.TestCharacterKey).Succeeded
                && preSyncCoordinator.JoinGroup([1]).Succeeded,
            "pre-sync invalidation test must enter the joining path");
        preSyncFake.EnqueueJoined();
        preSyncFake.EnqueueInvalidation(1);
        for (var index = 0; index < 8; index++)
            preSyncCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(!preSyncCoordinator.AutomationDecisionsAllowed
                && preSyncCoordinator.Readiness.State == FcMeshReadinessState.Joining,
            "a snapshot recovered before InitialSyncCompleted must not enable publication writes");
    }

    private static void TicketNeverEntersDiagnostics(Action<bool, string> require)
    {
        var fake = new FcMeshNativeTests.FakeNativeApi();
        using var coordinator = new FcMeshNativeCoordinator(fake, statusInterval: TimeSpan.Zero);
        var ticket = "phase5-secret-ticket";
        require(coordinator.Start(new FcNativeConfiguration("phase5-ticket-test"), FcMeshNativeTests.TestCharacterKey).Succeeded
                && coordinator.JoinGroup(Encoding.UTF8.GetBytes(ticket)).Succeeded,
            "ticket redaction test must enter the native join command boundary");
        fake.EnqueueWarning(ticket);
        coordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(!coordinator.Diagnostics.LastError.Contains(ticket, StringComparison.Ordinal)
                && coordinator.Diagnostics.LastError.Contains("redacted", StringComparison.OrdinalIgnoreCase),
            "ticket text must not appear in managed diagnostics or errors");
    }

    private static FcPublishedListService NewListService(
        IFcPublicationStateStore store,
        FakePublicationTransport transport)
        => new(
            store,
            transport,
            () => Scope,
            () => Author,
            () => new FcCompatibilityContext(FcPublishedListMapper.CurrentPlannerSemanticsVersion, "game-1"),
            _ => new FcRecipeOutput(100, 1));

    private static FcChestSnapshot CompleteSnapshot(uint quantity = 2)
    {
        var containers = Enum.GetValues<FcChestContainer>()
            .Select(container => new FcChestContainerState(container, true, 1))
            .ToArray();
        var slots = Enum.GetValues<FcChestContainer>()
            .Select(container => new FcChestSlotAddress(container, 0))
            .ToArray();
        return new FcChestSnapshot(
            true,
            true,
            containers,
            slots.Where(slot => slot.IsFreeCompanyContainer),
            slots.Where(slot => slot.IsPlayerInventoryContainer),
            [new FcChestSlotItem(
                new FcChestSlotAddress(FcChestContainer.FreeCompanyPage1, 0),
                new FcChestItemKey(100, false),
                quantity)],
            Array.Empty<FcChestSlotItem>());
    }

    private static PublishedListRecord MakeSnapshotList(Guid listId, string displayName)
    {
        var header = new FcRecordHeader(
            FcProtocolVersion.Current,
            FcProtocolVersion.CurrentSchema,
            FcRecordTypes.PublishedList,
            listId,
            Author,
            1);
        var targets = new[] { new PublishedRecipeTarget(1, 200, 1, FcItemQuality.Nq) };
        var policy = new FcQualityPolicy([new FcQualityRule(200, FcItemQuality.Nq, 1)]);
        return new PublishedListRecord(
            header,
            listId,
            displayName,
            true,
            FcPublishedListMapper.CurrentPlannerSemanticsVersion,
            "game-1",
            targets,
            policy,
            FcQualityPolicy.Empty);
    }

    private sealed class FakeChestReader : IFcCompleteChestReader
    {
        public FakeChestReader(FcChestReadResult result) => Result = result;
        public FcChestReadResult Result { get; set; }
        public int Reads { get; private set; }
        public FcChestReadResult ReadCurrentCompleteSnapshot()
        {
            Reads++;
            return Result;
        }
    }

    private sealed class FakePublicationTransport : IFcPublicationTransport
    {
        public bool IsReady { get; set; } = true;
        public string? LocalAuthorId => Author;
        public FcWorldStore WorldStore { get; } = new(new FcManualClock(1_000_000));
        public bool FailPuts { get; set; }
        public List<PutCall> Puts { get; } = new();

        public FcNativeCallResult Put(
            ReadOnlySpan<byte> recordId,
            ReadOnlySpan<byte> key,
            ReadOnlySpan<byte> recordType,
            bool hasGeneration,
            ulong generation,
            ulong revision,
            ReadOnlySpan<byte> payload)
        {
            Puts.Add(new(
                System.Text.Encoding.UTF8.GetString(recordId),
                System.Text.Encoding.UTF8.GetString(key),
                System.Text.Encoding.UTF8.GetString(recordType),
                hasGeneration,
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
            ulong Revision,
            byte[] Payload);
    }

}
