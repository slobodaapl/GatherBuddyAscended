using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GatherBuddy.FcMesh.Native;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.State;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace GatherBuddy.Vulcan.Tests;

internal static class FcMeshNativeTests
{
    internal static byte[] TestCharacterKey
        => Encoding.UTF8.GetBytes("0000000000000001");

    public static void Run(Action<bool, string> require)
    {
        var fixturePath = FindFixture();
        require(fixturePath is not null, "native envelope golden fixture must be available to managed tests");
        if (fixturePath is null)
            return;

        var fixture = File.ReadAllBytes(fixturePath);
        var decoded = FcNativeEnvelopeDecoder.Decode(
            fixture,
            Convert.FromHexString("ea4a6c63e29c520abef5507b132ec5f9954776aebebe7b92421eea691446d22c"),
            Convert.FromHexString("4f96c1b5388852c1b47f957ce89b40689ffc6b9fb908e6bb6da4e67d2c7a456c"),
            System.Text.Encoding.UTF8.GetBytes("v1/workers/ea4a6c63e29c520abef5507b132ec5f9954776aebebe7b92421eea691446d22c"));
        require(decoded.IsValid && decoded.Record is not null && decoded.VerifiedContext is not null,
            "the checked-in Rust native envelope must deserialize through the strict managed wire decoder");
        require(decoded.Record!.ProtocolVersion == 1
                && decoded.Record.RecordId == "00112233-4455-6677-8899-aabbccddeeff"
                && decoded.Record.ActualAuthorId.Length == 64
                && decoded.Record.Hlc.NodeId == "22222222222222222222222222222222"
                && decoded.Record.Hlc.NodeId != decoded.Record.ActualAuthorId,
            "native envelope protocol, canonical ID, author, and independent HLC node must remain exact");
        require(decoded.Record.OriginalEnvelopeBytes.SequenceEqual(fixture),
            "native decoder must retain exact received envelope bytes");
        var exposed = decoded.Record.OriginalEnvelopeBytes;
        exposed[0] = (byte)'X';
        require(decoded.Record.OriginalEnvelopeBytes[0] == (byte)'{',
            "exact envelope byte evidence must be defensively copied");
        require(decoded.VerifiedContext!.NativeVerified
                && decoded.VerifiedContext.PayloadHash == decoded.Record.PayloadHash
                && decoded.VerifiedContext.ContentHash != decoded.Record.PayloadHash,
            "native verification context must keep payload SHA-256 separate from full-envelope content hash");

        var nonCanonical = fixture.Concat(new byte[] { (byte)' ' }).ToArray();
        require(!FcNativeEnvelopeDecoder.Decode(nonCanonical).IsValid,
            "strict native decoder must reject noncanonical whitespace without rewriting evidence");
        var uppercase = fixture.ToArray();
        var authorIndex = Array.IndexOf(uppercase, (byte)'e');
        if (authorIndex >= 0)
            uppercase[authorIndex] = (byte)'E';
        require(!FcNativeEnvelopeDecoder.Decode(uppercase).IsValid,
            "strict native decoder must reject uppercase identity mutations");
        var badHashText = Encoding.UTF8.GetString(fixture).Replace(
            "b15740944697339d5caf7fca29223315a816ceceaa9f22f10b068284236054aa",
            "005740944697339d5caf7fca29223315a816ceceaa9f22f10b068284236054aa",
            StringComparison.Ordinal);
        require(!FcNativeEnvelopeDecoder.Decode(Encoding.UTF8.GetBytes(badHashText)).IsValid,
            "native decoder must recompute and reject a payload-hash mutation");
        var badSignatureText = Encoding.UTF8.GetString(fixture).Replace(
            "xsBV2Zpt6/6DMr7q1pRLVowUDXSW5DPrVrfLdtFZCY6VnXqHFHubniR64QT4idreT//IOgeYqOb0o/8jCntXBg==",
            "!sBV2Zpt6/6DMr7q1pRLVowUDXSW5DPrVrfLdtFZCY6VnXqHFHubniR64QT4idreT//IOgeYqOb0o/8jCntXBg==",
            StringComparison.Ordinal);
        require(!FcNativeEnvelopeDecoder.Decode(Encoding.UTF8.GetBytes(badSignatureText)).IsValid,
            "native decoder must reject malformed signature base64");

        var badAbi = new FakeNativeApi { AbiVersionValue = FcNativeAbi.Version + 1 };
        using (var badCoordinator = new FcMeshNativeCoordinator(badAbi))
            require(badCoordinator.Start(new FcNativeConfiguration("diagnostic-only"), TestCharacterKey).ErrorCode == FcNativeErrorCode.AbiMismatch,
                "managed service construction must reject an ABI version mismatch before creating a handle");

        var noIdentityFake = new FakeNativeApi();
        using (var noIdentityCoordinator = new FcMeshNativeCoordinator(noIdentityFake))
        {
            var noIdentity = noIdentityCoordinator.Start(new FcNativeConfiguration("no-identity"));
            require(noIdentity.ErrorCode == FcNativeErrorCode.InvalidState
                    && noIdentityFake.Calls.Count == 0,
                "mesh startup without a character identity must fail before native Create");
            var emptyKey = noIdentityCoordinator.Start(
                new FcNativeConfiguration("empty-identity"),
                ReadOnlySpan<byte>.Empty);
            require(emptyKey.ErrorCode == FcNativeErrorCode.InvalidArgument
                    && noIdentityFake.Calls.Count == 0,
                "mesh startup with an empty character identity must fail before native Create");
        }

        var orderFake = new FakeNativeApi();
        orderFake.RequiredAuthorBeforeStart = TestCharacterKey;
        using (var orderCoordinator = new FcMeshNativeCoordinator(orderFake))
        {
            require(orderCoordinator.Start(new FcNativeConfiguration("ordered-start"), TestCharacterKey).Succeeded,
                "keyed startup must cross the fake native boundary successfully");
        require(orderFake.Calls.SequenceEqual(new[] { "Create", "SetCharacterAuthor", "Start" })
                    && orderFake.SelectedAuthorAtStart is not null
                    && orderFake.SelectedAuthorAtStart.SequenceEqual(TestCharacterKey),
                "native startup must select the persisted-group author before Start reopens it");
        }

        var restoreFake = new FakeNativeApi();
        using (var restoreCoordinator = new FcMeshNativeCoordinator(restoreFake))
        {
            require(restoreCoordinator.Start(new FcNativeConfiguration("persisted-restore"), TestCharacterKey).Succeeded,
                "persisted-restore author fixture must start through the managed native boundary");
            restoreFake.EnqueueStarted(Convert.FromHexString(TestAuthor));
            restoreCoordinator.Tick(TimeSpan.FromMilliseconds(100));
            require(restoreCoordinator.LocalAuthorId == TestAuthor
                    && !string.IsNullOrWhiteSpace(restoreCoordinator.LocalAuthorId),
                "Started event author must install the local identity needed by persisted FC service restoration");
        }

        var restoredAuthorBytes = Enumerable.Repeat((byte)0xaa, 32).ToArray();
        var restoredAuthorId = Convert.ToHexString(restoredAuthorBytes).ToLowerInvariant();
        var restoredNamespaceId = new string('1', 64);
        var restoredReadyFake = new FakeNativeApi();
        using (var restoredReadyCoordinator = new FcMeshNativeCoordinator(
                   restoredReadyFake,
                   new FcWorldStore(),
                   () => new FcWorldStore(),
                   statusInterval: TimeSpan.Zero))
        {
            require(restoredReadyCoordinator.Start(
                        new FcNativeConfiguration("persisted-restore-ready"),
                        TestCharacterKey).Succeeded,
                "managed persisted-restore readiness fixture must start through the native boundary");
            restoredReadyFake.EnqueueStarted(restoredAuthorBytes);
            restoredReadyFake.EnqueueJoined(created: true);
            restoredReadyFake.EnqueueCompatibleGroupMetadataSnapshot();
            for (var index = 0; index < 16 && !restoredReadyCoordinator.AutomationDecisionsAllowed; index++)
                restoredReadyCoordinator.Tick(TimeSpan.FromMilliseconds(100));
            var restoredReadiness = restoredReadyCoordinator.Readiness;
            require(restoredReadyCoordinator.AutomationDecisionsAllowed
                    && restoredReadiness.State == FcMeshReadinessState.Ready
                    && restoredReadiness.NamespaceId == restoredNamespaceId
                    && restoredReadiness.GroupTicket == "phase5-created-ticket"
                    && restoredReadyCoordinator.LocalAuthorId == restoredAuthorId
                    && !restoredReadyFake.ObservedEventKinds.Contains(FcNativeEventKind.InitialSyncCompleted),
                "restored Joined plus a valid local metadata snapshot must reach Ready without peer sync evidence");
        }

        var missingMetadataFake = new FakeNativeApi();
        using (var missingMetadataCoordinator = new FcMeshNativeCoordinator(
                   missingMetadataFake,
                   new FcWorldStore(),
                   () => new FcWorldStore(),
                   statusInterval: TimeSpan.Zero))
        {
            require(missingMetadataCoordinator.Start(
                        new FcNativeConfiguration("persisted-restore-missing-metadata"),
                        TestCharacterKey).Succeeded,
                "missing-metadata restore fixture must start through the native boundary");
            missingMetadataFake.EnqueueStarted(restoredAuthorBytes);
            missingMetadataFake.EnqueueJoined(created: true);
            for (var index = 0; index < 8; index++)
                missingMetadataCoordinator.Tick(TimeSpan.FromMilliseconds(100));
            require(!missingMetadataCoordinator.AutomationDecisionsAllowed
                    && missingMetadataCoordinator.Readiness.State != FcMeshReadinessState.Ready
                    && !missingMetadataFake.ObservedEventKinds.Contains(FcNativeEventKind.InitialSyncCompleted),
                "restored local readiness must remain blocked when group metadata is missing");
        }

        var incompatibleMetadataFake = new FakeNativeApi();
        using (var incompatibleMetadataCoordinator = new FcMeshNativeCoordinator(
                   incompatibleMetadataFake,
                   new FcWorldStore(),
                   () => new FcWorldStore(),
                   statusInterval: TimeSpan.Zero))
        {
            require(incompatibleMetadataCoordinator.Start(
                        new FcNativeConfiguration("persisted-restore-incompatible-metadata"),
                        TestCharacterKey).Succeeded,
                "incompatible-metadata restore fixture must start through the native boundary");
            incompatibleMetadataFake.EnqueueStarted(restoredAuthorBytes);
            incompatibleMetadataFake.EnqueueJoined(created: true);
            incompatibleMetadataFake.EnqueueIncompatibleGroupMetadataSnapshot();
            for (var index = 0; index < 8; index++)
                incompatibleMetadataCoordinator.Tick(TimeSpan.FromMilliseconds(100));
            require(!incompatibleMetadataCoordinator.AutomationDecisionsAllowed
                    && incompatibleMetadataCoordinator.Readiness.State != FcMeshReadinessState.Ready
                    && !incompatibleMetadataFake.ObservedEventKinds.Contains(FcNativeEventKind.InitialSyncCompleted),
                "restored local readiness must remain blocked when group metadata is incompatible");
        }

        require(Marshal.SizeOf<FcNativeBuffer>() == 16
                && Marshal.SizeOf<FcNativeAbiResult>() == 16
                && Marshal.SizeOf<FcNativeEvent>() == 104
                && Marshal.SizeOf<FcNativeRecord>() == 152
                && Marshal.SizeOf<FcNativeStatus>() == 80
                && Marshal.OffsetOf<FcNativeEvent>(nameof(FcNativeEvent.Sequence)).ToInt32() == 16
                && Marshal.OffsetOf<FcNativeRecord>(nameof(FcNativeRecord.HlcPhysicalUnixMs)).ToInt32() == 120,
            "managed ABI structs must retain the x64 native field widths and offsets");

        var fake = new FakeNativeApi();
        using var coordinator = new FcMeshNativeCoordinator(
            fake,
            new FcWorldStore(),
            () => new FcWorldStore(),
            maxEventsPerTick: 2,
            statusInterval: TimeSpan.Zero);
        require(coordinator.Start(new FcNativeConfiguration("diagnostic-only"), TestCharacterKey).Succeeded,
            "managed coordinator must gate service construction on the expected ABI version");
        fake.EnqueueWarning("one");
        fake.EnqueueWarning("two");
        fake.EnqueueWarning("three");
        coordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(coordinator.Diagnostics.LastEventSequence == 2,
            "managed event pump must enforce a bounded event count per framework tick");
        var baselineEvidence = string.Empty;
        var baselineReady = EstablishReadyState(fake, coordinator, out baselineEvidence);
        require(baselineReady && fake.CreateGroupCalls == 1,
            $"mesh commands must establish a legitimate Ready baseline before recovery; {baselineEvidence}");

        var oldWorld = coordinator.WorldStore;
        fake.EnqueueInvalidation(1);
        coordinator.Tick(TimeSpan.FromMilliseconds(100));
        coordinator.Tick(TimeSpan.FromMilliseconds(100));
        coordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(coordinator.AutomationDecisionsAllowed
                && !ReferenceEquals(oldWorld, coordinator.WorldStore)
                && fake.SnapshotRequests == 2,
            "world invalidation must pause, build an empty shadow snapshot, then atomically swap and resume");

        var notReadyFake = new FakeNativeApi();
        using var notReadyCoordinator = new FcMeshNativeCoordinator(
            notReadyFake,
            new FcWorldStore(),
            () => new FcWorldStore(),
            statusInterval: TimeSpan.Zero);
        require(notReadyCoordinator.Start(new FcNativeConfiguration("not-ready-recovery"), TestCharacterKey).Succeeded,
            "not-ready recovery fixture must start through the managed native boundary");
        var notReadyOldWorld = notReadyCoordinator.WorldStore;
        notReadyFake.EnqueueInvalidation(1);
        for (var index = 0; index < 8; index++)
            notReadyCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(!notReadyCoordinator.AutomationDecisionsAllowed
                && notReadyCoordinator.Readiness.State != FcMeshReadinessState.Ready
                && !ReferenceEquals(notReadyOldWorld, notReadyCoordinator.WorldStore),
            "ordinary recovery must not fabricate Ready from a Started-only baseline");

        fake.ThrowOnPoll = true;
        coordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(!coordinator.AutomationDecisionsAllowed
                && !string.IsNullOrWhiteSpace(coordinator.Diagnostics.LastError),
            "native poll exceptions must be contained and pause FC decisions");
        coordinator.Dispose();
        coordinator.Dispose();
        require(fake.DestroyCalls == 1,
            "service SafeHandle destruction must be exactly once across double disposal");

        var staleFake = new FakeNativeApi();
        using var staleCoordinator = new FcMeshNativeCoordinator(
            staleFake,
            new FcWorldStore(),
            () => new FcWorldStore(),
            statusInterval: TimeSpan.Zero);
        var staleStarted = staleCoordinator.Start(new FcNativeConfiguration("diagnostic-only"), TestCharacterKey).Succeeded;
        var staleEvidence = string.Empty;
        var staleReady = staleStarted && EstablishReadyState(staleFake, staleCoordinator, out staleEvidence);
        require(staleReady,
            $"stale snapshot recovery must begin from a legitimate Ready baseline; {staleEvidence}");
        staleFake.StaleFirstSnapshotPoll = true;
        staleFake.EnqueueInvalidation(1);
        for (var index = 0; index < 32; index++)
        {
            staleCoordinator.Tick(TimeSpan.FromMilliseconds(100));
            if (staleFake.SnapshotRequests >= 2
                && staleFake.SnapshotDestroyCalls >= 2
                && staleCoordinator.AutomationDecisionsAllowed
                && !staleCoordinator.Diagnostics.SnapshotInProgress)
                break;
        }
        require(staleFake.SnapshotRequests >= 2
                && staleFake.SnapshotDestroyCalls >= 2
                && staleCoordinator.AutomationDecisionsAllowed
                && !staleCoordinator.Diagnostics.SnapshotInProgress,
            "stale snapshot cursors must be discarded and retried before resuming decisions");

        NativeBoundaryRuntime(require);
    }

    private static void NativeBoundaryRuntime(Action<bool, string> require)
    {
        var fixturePath = FindFixture();
        if (fixturePath is null)
            return;
        var fixture = File.ReadAllBytes(fixturePath);
        var bytesOnly = FcNativeEnvelopeDecoder.Decode(fixture);
        require(bytesOnly.IsValid && bytesOnly.Record is not null && bytesOnly.VerifiedContext is not null
                && !bytesOnly.VerifiedContext.NativeVerified
                && !bytesOnly.VerifiedContext.SignatureValid
                && !new FcVerifiedContextVerifier().Verify(bytesOnly.Record, bytesOnly.VerifiedContext),
            "bytes-only native envelope decoding cannot forge native signature verification acceptance");

        var worker = MakeWorker("alice", Guid.Parse("00000000-0000-0000-0000-000000000101"), 1, 1, 0);
        var changedWorker = worker with
        {
            Character = worker.Character with { DisplayName = "Changed" },
        };
        var firstEnvelopeBytes = Encoding.UTF8.GetBytes("first-envelope");
        var secondEnvelopeBytes = Encoding.UTF8.GetBytes("second-envelope");
        var firstEnvelope = FcMeshRecord.Create(
            worker,
            "alice",
            new FcHlcTimestamp(1_000, 0, "11111111111111111111111111111111"),
            worker.SessionGeneration,
            worker.Header.Revision,
            worker.Header.RecordType,
            worker.Header.RecordId.ToString("D")) with
        {
            ProtocolVersion = FcProtocolVersion.Current,
            Signature = "test-signature",
            SignatureAlgorithm = "test-algorithm",
            OriginalEnvelopeBytes = firstEnvelopeBytes,
        };
        var secondEnvelope = FcMeshRecord.Create(
            changedWorker,
            "alice",
            new FcHlcTimestamp(1_001, 0, "11111111111111111111111111111111"),
            changedWorker.SessionGeneration,
            changedWorker.Header.Revision,
            changedWorker.Header.RecordType,
            changedWorker.Header.RecordId.ToString("D")) with
        {
            ProtocolVersion = FcProtocolVersion.Current,
            Signature = "test-signature",
            SignatureAlgorithm = "test-algorithm",
            OriginalEnvelopeBytes = secondEnvelopeBytes,
        };
        var firstContext = FcVerifiedMeshContext.FromEnvelope(firstEnvelope) with { SignatureValid = true };
        var secondContext = FcVerifiedMeshContext.FromEnvelope(secondEnvelope) with { SignatureValid = true };
        var cloneStore = new FcWorldStore(new FcManualClock(1_000));
        require(cloneStore.Apply(firstEnvelope, worker, firstContext).Accepted
                && cloneStore.Apply(secondEnvelope, changedWorker, secondContext).Status == FcApplyStatus.Fork,
            "world-store same-writer fork retains both complete envelope candidates");
        firstEnvelopeBytes[0] = (byte)'X';
        secondEnvelopeBytes[0] = (byte)'Y';
        var retainedVariants = cloneStore.ForkVariants.Values.Single();
        var retained = retainedVariants.Select(value => value.Envelope.OriginalEnvelopeBytes[0]).OrderBy(value => value).ToArray();
        require(retainedVariants.All(value => !ReferenceEquals(value.Envelope, firstEnvelope)
                    && !ReferenceEquals(value.Envelope, secondEnvelope))
                && retained.SequenceEqual(new[] { (byte)'f', (byte)'s' }),
            "world-store envelope clones retain exact original bytes after caller mutation");

        TransferBoundary(require);
        SnapshotTransferBootstrapBoundary(require);
        MetadataAndSequenceBoundary(require);
        ReplayBoundary(require);
        SnapshotFailureAndRetryBoundary(require);
        ReentrantDisposeBoundary(require);
    }

    private static void TransferBoundary(Action<bool, string> require)
    {
        var fake = new FakeNativeApi();
        using var coordinator = new FcMeshNativeCoordinator(
            fake,
            new FcWorldStore(new FcManualClock(2_000)),
            () => new FcWorldStore(new FcManualClock(2_000)),
            maxEventsPerTick: 32,
            statusInterval: TimeSpan.Zero);
        require(coordinator.Start(new FcNativeConfiguration("diagnostic-only"), TestCharacterKey).Succeeded,
            "transfer boundary fixture starts through the injectable native service");
        var worker = MakeWorker(NativeAuthor, NativeSession, 1, 1, 0);
        var chest = MakeChest(NativeAuthor, 1, 100);
        var transfer = MakeTransfer(NativeAuthor, NativeSession, 1, 2, chest, worker);
        fake.EnqueueRecord(BuildNativeRecord(worker, 1_000, fake.AllocateSequence()).Event);
        fake.EnqueueRecord(BuildNativeRecord(chest, 1_000, fake.AllocateSequence()).Event);
        coordinator.Tick(TimeSpan.FromMilliseconds(100));
        var before = coordinator.WorldStore.Revision.Number;
        fake.EnqueueRecord(BuildNativeRecord(transfer, 2_000, fake.AllocateSequence()).Event);
        coordinator.Tick(TimeSpan.FromMilliseconds(100));
        var after = coordinator.WorldStore.Revision.Number;
        require(after == before + 1
                && coordinator.WorldStore.Transfers.Count == 1
                && coordinator.WorldStore.Workers.Values.Single().HeldInventoryMap.Get(100) == 100
                && coordinator.WorldStore.Chests.Values.Single().ItemMap.Get(100) == 0,
            "native transfer RecordInserted remains one envelope, one world revision, and atomically updates chest plus worker");
    }

    private static void SnapshotTransferBootstrapBoundary(Action<bool, string> require)
    {
        var historicalFake = new FakeNativeApi();
        using var historicalCoordinator = CreateSnapshotCoordinator(historicalFake, out _);
        var historicalBaselineWorld = historicalCoordinator.WorldStore;
        var historicalWorker = MakeWorker(NativeAuthor, NativeSession, 1, 1, 0);
        var historicalChest = MakeChest(NativeAuthor, 1, 100);
        var historicalTransfer = MakeTransfer(
            NativeAuthor,
            NativeSession,
            1,
            2,
            historicalChest,
            historicalWorker);
        var latestWorker = MakeWorker(NativeAuthor, NativeSession, 1, 3, 50);
        var latestChest = MakeChest(NativeAuthor, 3, 50);
        historicalFake.EnqueueSnapshotRecord(BuildNativeRecord(latestWorker, 3_000, 0).Snapshot);
        historicalFake.EnqueueSnapshotRecord(BuildNativeRecord(latestChest, 3_000, 0).Snapshot);
        historicalFake.EnqueueSnapshotRecord(BuildNativeRecord(historicalTransfer, 2_000, 0).Snapshot);
        historicalFake.EnqueueInvalidation(1);
        var historicalSwapped = CompleteSnapshot(historicalFake, historicalCoordinator);
        require(historicalSwapped
                && !ReferenceEquals(historicalBaselineWorld, historicalCoordinator.WorldStore)
                && historicalCoordinator.WorldStore.Transfers.Count == 1
                && historicalCoordinator.WorldStore.Workers.Values.Single().HeldInventoryMap.Get(100) == 50
                && historicalCoordinator.WorldStore.Chests.Values.Single().ItemMap.Get(100) == 50,
            "historical snapshot transfer without a preimage retains newer standalone worker and chest registers");

        var newestFake = new FakeNativeApi();
        using var newestCoordinator = CreateSnapshotCoordinator(newestFake, out _);
        var newestBaselineWorld = newestCoordinator.WorldStore;
        var newestWorker = MakeWorker(NativeAuthor, NativeSession, 1, 1, 0);
        var newestChest = MakeChest(NativeAuthor, 1, 100);
        var newestTransfer = MakeTransfer(NativeAuthor, NativeSession, 1, 2, newestChest, newestWorker);
        newestFake.EnqueueSnapshotRecord(BuildNativeRecord(newestWorker, 1_000, 0).Snapshot);
        newestFake.EnqueueSnapshotRecord(BuildNativeRecord(newestChest, 1_000, 0).Snapshot);
        newestFake.EnqueueSnapshotRecord(BuildNativeRecord(newestTransfer, 2_000, 0).Snapshot);
        newestFake.EnqueueInvalidation(1);
        var newestSwapped = CompleteSnapshot(newestFake, newestCoordinator);
        require(newestSwapped
                && !ReferenceEquals(newestBaselineWorld, newestCoordinator.WorldStore)
                && newestCoordinator.WorldStore.Transfers.Count == 1
                && newestCoordinator.WorldStore.Workers.Values.Single().HeldInventoryMap.Get(100) == 100
                && newestCoordinator.WorldStore.Chests.Values.Single().ItemMap.Get(100) == 0,
            "snapshot bootstrap transfer supplies both newest after-state registers when no newer standalone state exists");

        var mixedFake = new FakeNativeApi();
        using var mixedCoordinator = CreateSnapshotCoordinator(mixedFake, out _);
        var mixedBaselineWorld = mixedCoordinator.WorldStore;
        var mixedWorker = MakeWorker(NativeAuthor, NativeSession, 1, 1, 0);
        var mixedChest = MakeChest(NativeAuthor, 1, 100);
        var mixedTransfer = MakeTransfer(NativeAuthor, NativeSession, 1, 2, mixedChest, mixedWorker);
        var supersedingWorker = MakeWorker(NativeAuthor, NativeSession, 1, 3, 50);
        mixedFake.EnqueueSnapshotRecord(BuildNativeRecord(supersedingWorker, 3_000, 0).Snapshot);
        mixedFake.EnqueueSnapshotRecord(BuildNativeRecord(mixedChest, 1_000, 0).Snapshot);
        mixedFake.EnqueueSnapshotRecord(BuildNativeRecord(mixedTransfer, 2_000, 0).Snapshot);
        mixedFake.EnqueueInvalidation(1);
        var mixedSwapped = CompleteSnapshot(mixedFake, mixedCoordinator);
        require(mixedSwapped
                && !ReferenceEquals(mixedBaselineWorld, mixedCoordinator.WorldStore)
                && mixedCoordinator.WorldStore.Transfers.Count == 1
                && mixedCoordinator.WorldStore.Workers.Values.Single().HeldInventoryMap.Get(100) == 50
                && mixedCoordinator.WorldStore.Chests.Values.Single().ItemMap.Get(100) == 0,
            "snapshot bootstrap transfer merges each after-state register independently without exposing a half-transfer");

        var malformedFake = new FakeNativeApi();
        using var malformedCoordinator = CreateSnapshotCoordinator(malformedFake, out var malformedOldWorld);
        var malformedBaselineWorld = malformedCoordinator.WorldStore;
        var malformedBaselineRevision = malformedBaselineWorld.WorldRevision.Number;
        var malformedWorker = MakeWorker(NativeAuthor, NativeSession, 1, 1, 0);
        var malformedChest = MakeChest(NativeAuthor, 1, 100);
        var malformedTransfer = MakeTransfer(NativeAuthor, NativeSession, 1, 2, malformedChest, malformedWorker) with
        {
            PurposeListId = Guid.Parse("00000000-0000-0000-0000-000000000199"),
        };
        malformedFake.EnqueueSnapshotRecord(BuildNativeRecord(malformedWorker, 1_000, 0).Snapshot);
        malformedFake.EnqueueSnapshotRecord(BuildNativeRecord(malformedChest, 1_000, 0).Snapshot);
        malformedFake.EnqueueSnapshotRecord(BuildNativeRecord(malformedTransfer, 2_000, 0).Snapshot);
        malformedFake.EnqueueInvalidation(1);
        var malformedSwapped = CompleteSnapshot(malformedFake, malformedCoordinator);
        var malformedSameBaseline = ReferenceEquals(malformedBaselineWorld, malformedCoordinator.WorldStore);
        var malformedBaselineUnchanged = malformedCoordinator.WorldStore.WorldRevision.Number == malformedBaselineRevision;
        var malformedNoWorker = malformedCoordinator.WorldStore.Workers.Count == malformedBaselineWorld.Workers.Count;
        var malformedNoChest = malformedCoordinator.WorldStore.Chests.Count == malformedBaselineWorld.Chests.Count;
        var malformedNoTransfer = malformedCoordinator.WorldStore.Transfers.Count == malformedBaselineWorld.Transfers.Count;
        var malformedPaused = !malformedCoordinator.AutomationDecisionsAllowed;
        var malformedDiagnostics = malformedCoordinator.Diagnostics;
        require(!malformedSwapped
                && malformedSameBaseline
                && malformedBaselineUnchanged
                && malformedNoWorker
                && malformedNoChest
                && malformedNoTransfer
                && malformedPaused,
            $"malformed snapshot transfer rejects the complete shadow world without a partial swap; "
            + $"swapped={malformedSwapped}; sameBaseline={malformedSameBaseline}; "
            + $"baselineRevision={malformedBaselineRevision}; currentRevision={malformedCoordinator.WorldStore.WorldRevision.Number}; "
            + $"oldWorldIdentity={ReferenceEquals(malformedOldWorld, malformedCoordinator.WorldStore)}; "
            + $"workers={malformedCoordinator.WorldStore.Workers.Count}/{malformedBaselineWorld.Workers.Count}; "
            + $"chests={malformedCoordinator.WorldStore.Chests.Count}/{malformedBaselineWorld.Chests.Count}; "
            + $"transfers={malformedCoordinator.WorldStore.Transfers.Count}/{malformedBaselineWorld.Transfers.Count}; "
            + $"state={malformedCoordinator.Readiness.State}; paused={malformedPaused}; "
            + $"error={malformedCoordinator.Readiness.Error}; snapshot={malformedDiagnostics.SnapshotInProgress}; "
            + $"snapshotRequests={malformedFake.SnapshotRequests}; snapshotHandle={malformedFake.LastOpenedSnapshotHandle}; "
            + $"snapshotDestroys={malformedFake.SnapshotDestroyCalls}");

        var forkFake = new FakeNativeApi();
        using var forkCoordinator = CreateSnapshotCoordinator(forkFake, out var forkOldWorld);
        var forkBaselineWorld = forkCoordinator.WorldStore;
        var forkBaselineRevision = forkBaselineWorld.WorldRevision.Number;
        var forkWorker = MakeWorker(NativeAuthor, NativeSession, 1, 1, 0);
        var forkChest = MakeChest(NativeAuthor, 1, 100);
        var firstTransfer = MakeTransfer(NativeAuthor, NativeSession, 1, 2, forkChest, forkWorker);
        var secondTransfer = firstTransfer with
        {
            ActualTransferred = [new ItemQuantityEntry(100, FcItemQuality.Nq, 50)],
        };
        forkFake.EnqueueSnapshotRecord(BuildNativeRecord(forkWorker, 1_000, 0).Snapshot);
        forkFake.EnqueueSnapshotRecord(BuildNativeRecord(forkChest, 1_000, 0).Snapshot);
        forkFake.EnqueueSnapshotRecord(BuildNativeRecord(firstTransfer, 2_000, 0).Snapshot);
        forkFake.EnqueueSnapshotRecord(BuildNativeRecord(secondTransfer, 2_001, 0).Snapshot);
        forkFake.EnqueueInvalidation(1);
        var forkSwapped = CompleteSnapshot(forkFake, forkCoordinator);
        var forkSameBaseline = ReferenceEquals(forkBaselineWorld, forkCoordinator.WorldStore);
        var forkBaselineUnchanged = forkCoordinator.WorldStore.WorldRevision.Number == forkBaselineRevision;
        var forkNoWorker = forkCoordinator.WorldStore.Workers.Count == forkBaselineWorld.Workers.Count;
        var forkNoChest = forkCoordinator.WorldStore.Chests.Count == forkBaselineWorld.Chests.Count;
        var forkNoTransfer = forkCoordinator.WorldStore.Transfers.Count == forkBaselineWorld.Transfers.Count;
        var forkPaused = !forkCoordinator.AutomationDecisionsAllowed;
        var forkDiagnostics = forkCoordinator.Diagnostics;
        require(!forkSwapped
                && forkSameBaseline
                && forkBaselineUnchanged
                && forkNoWorker
                && forkNoChest
                && forkNoTransfer
                && forkPaused,
            $"forked snapshot transfer operation rejects the entire shadow world; "
            + $"swapped={forkSwapped}; sameBaseline={forkSameBaseline}; "
            + $"baselineRevision={forkBaselineRevision}; currentRevision={forkCoordinator.WorldStore.WorldRevision.Number}; "
            + $"oldWorldIdentity={ReferenceEquals(forkOldWorld, forkCoordinator.WorldStore)}; "
            + $"workers={forkCoordinator.WorldStore.Workers.Count}/{forkBaselineWorld.Workers.Count}; "
            + $"chests={forkCoordinator.WorldStore.Chests.Count}/{forkBaselineWorld.Chests.Count}; "
            + $"transfers={forkCoordinator.WorldStore.Transfers.Count}/{forkBaselineWorld.Transfers.Count}; "
            + $"state={forkCoordinator.Readiness.State}; paused={forkPaused}; "
            + $"error={forkCoordinator.Readiness.Error}; snapshot={forkDiagnostics.SnapshotInProgress}; "
            + $"snapshotRequests={forkFake.SnapshotRequests}; snapshotHandle={forkFake.LastOpenedSnapshotHandle}; "
            + $"snapshotDestroys={forkFake.SnapshotDestroyCalls}");
    }

    private static FcMeshNativeCoordinator CreateSnapshotCoordinator(
        FakeNativeApi fake,
        out FcWorldStore oldWorld)
    {
        oldWorld = new FcWorldStore(new FcManualClock(2_000));
        var coordinator = new FcMeshNativeCoordinator(
            fake,
            oldWorld,
            () => new FcWorldStore(new FcManualClock(2_000)),
            maxEventsPerTick: 64,
            statusInterval: TimeSpan.Zero);
        var baselineStarted = coordinator.Start(new FcNativeConfiguration("snapshot-baseline"), TestCharacterKey).Succeeded;
        var baselineEvidence = string.Empty;
        if (!baselineStarted || !EstablishReadyState(fake, coordinator, out baselineEvidence))
            throw new InvalidOperationException($"Snapshot fixture could not establish a Ready baseline; {baselineEvidence}");
        return coordinator;
    }

    private static bool EstablishReadyState(
        FakeNativeApi fake,
        FcMeshNativeCoordinator coordinator,
        out string evidence)
    {
        var createResult = coordinator.CreateGroup();
        if (!createResult.Succeeded)
        {
            evidence = $"CreateGroup command failed: code={createResult.ErrorCode}; errorId={createResult.ErrorId}";
            return false;
        }
        fake.EnqueueJoined(created: true);
        fake.EnqueueCompatibleGroupMetadataSnapshot();
        for (var index = 0; index < 32 && !coordinator.AutomationDecisionsAllowed; index++)
            coordinator.Tick(TimeSpan.FromMilliseconds(100));
        var readiness = coordinator.Readiness;
        var diagnostics = coordinator.Diagnostics;
        evidence = $"state={readiness.State}; allowed={readiness.IsReady}; lifecycle={diagnostics.Lifecycle}; "
            + $"group={diagnostics.GroupState}; events={diagnostics.LastEventSequence}; "
            + $"snapshot={diagnostics.SnapshotInProgress}; requests={fake.SnapshotRequests}; "
            + $"fakeSequence={fake.CurrentSequence}; pendingEvents={fake.PendingEvents}; "
            + $"pendingSnapshotRecords={fake.PendingSnapshotRecords}; "
            + $"base={readiness.SnapshotBaseEventSequence}; count={readiness.RecordCount}; "
            + $"metadata={readiness.GroupMetadataCompatible}; namespace={readiness.NamespaceId is not null}; "
            + $"error={readiness.Error}";
        return coordinator.AutomationDecisionsAllowed
            && coordinator.Readiness.State == FcMeshReadinessState.Ready;
    }

    private static bool CompleteSnapshot(
        FakeNativeApi fake,
        FcMeshNativeCoordinator coordinator)
    {
        for (var index = 0; index < 32; index++)
        {
            coordinator.Tick(TimeSpan.FromMilliseconds(100));
            if (fake.SnapshotRequests > 0
                && coordinator.AutomationDecisionsAllowed
                && !coordinator.Diagnostics.SnapshotInProgress)
                return true;
        }
        return false;
    }

    private static void MetadataAndSequenceBoundary(Action<bool, string> require)
    {
        var duplicateFake = new FakeNativeApi();
        using var duplicateCoordinator = new FcMeshNativeCoordinator(duplicateFake, statusInterval: TimeSpan.Zero);
        duplicateCoordinator.Start(new FcNativeConfiguration("diagnostic-only"), TestCharacterKey);
        duplicateFake.EnqueueWarning("first");
        duplicateFake.EnqueueRecord(new FcMeshNativeEvent(
            1,
            FcNativeEventKind.Warning,
            1,
            0,
            Array.Empty<byte>(),
            Encoding.UTF8.GetBytes("duplicate"),
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            0));
        duplicateCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(duplicateCoordinator.Diagnostics.LastEventSequence == 1
                && duplicateCoordinator.Diagnostics.LastError == "first",
            "duplicate nonzero native event sequences are ignored after first application");

        var zeroFake = new FakeNativeApi();
        using var zeroCoordinator = new FcMeshNativeCoordinator(zeroFake, statusInterval: TimeSpan.Zero);
        zeroCoordinator.Start(new FcNativeConfiguration("diagnostic-only"), TestCharacterKey);
        zeroFake.EnqueueRecord(new FcMeshNativeEvent(
            1,
            FcNativeEventKind.Warning,
            0,
            0,
            Array.Empty<byte>(),
            Encoding.UTF8.GetBytes("zero"),
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            0));
        zeroCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(zeroCoordinator.Diagnostics.LastEventSequence == 0
                && !zeroCoordinator.AutomationDecisionsAllowed
                && !string.IsNullOrWhiteSpace(zeroCoordinator.Diagnostics.LastError),
            "zero native event sequence is rejected and pauses decisions");

        var mismatchFake = new FakeNativeApi();
        using var mismatchCoordinator = new FcMeshNativeCoordinator(mismatchFake, statusInterval: TimeSpan.Zero);
        mismatchCoordinator.Start(new FcNativeConfiguration("diagnostic-only"), TestCharacterKey);
        var worker = MakeWorker(NativeAuthor, NativeSession, 1, 1, 0);
        var fixture = BuildNativeRecord(worker, 1_000, mismatchFake.AllocateSequence());
        mismatchFake.EnqueueRecord(fixture.Event with { Aux = fixture.Event.Aux + 1 });
        mismatchCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(!mismatchCoordinator.AutomationDecisionsAllowed
                && mismatchCoordinator.Diagnostics.LastError.Contains("metadata", StringComparison.OrdinalIgnoreCase),
            "RecordInserted envelope/event metadata mismatch is rejected and pauses decisions");

        var kindFake = new FakeNativeApi();
        using var kindCoordinator = new FcMeshNativeCoordinator(kindFake, statusInterval: TimeSpan.Zero);
        kindCoordinator.Start(new FcNativeConfiguration("diagnostic-only"), TestCharacterKey);
        kindFake.EnqueueRecord(new FcMeshNativeEvent(
            1,
            (FcNativeEventKind)999,
            1,
            0,
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            0));
        kindCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(!kindCoordinator.AutomationDecisionsAllowed
                && kindCoordinator.Diagnostics.LastError.Contains("unsupported", StringComparison.OrdinalIgnoreCase),
            "unsupported native event kind is rejected and pauses decisions");
    }

    private static void SnapshotFailureAndRetryBoundary(Action<bool, string> require)
    {
        var invalidFake = new FakeNativeApi();
        using var invalidCoordinator = new FcMeshNativeCoordinator(
            invalidFake,
            new FcWorldStore(new FcManualClock(2_000)),
            () => new FcWorldStore(new FcManualClock(2_000)),
            maxEventsPerTick: 8,
            statusInterval: TimeSpan.Zero);
        var invalidStarted = invalidCoordinator.Start(new FcNativeConfiguration("diagnostic-only"), TestCharacterKey).Succeeded;
        var invalidEvidence = string.Empty;
        var invalidReady = invalidStarted
            && EstablishReadyState(invalidFake, invalidCoordinator, out invalidEvidence);
        require(invalidReady,
            $"invalid snapshot fixture must begin from a legitimate Ready baseline; {invalidEvidence}");
        var oldWorld = invalidCoordinator.WorldStore;
        invalidFake.EnqueueSnapshotRecord(new FcMeshNativeSnapshotRecord(
            1,
            Encoding.UTF8.GetBytes("invalid-key"),
            Array.Empty<byte>(),
            new byte[32],
            new byte[32],
            null,
            1,
            FcRecordTypes.WorkerSession,
            1_000,
            0,
            new byte[16]));
        invalidFake.EnqueueInvalidation(1);
        invalidCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        invalidCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(ReferenceEquals(oldWorld, invalidCoordinator.WorldStore)
                && !invalidCoordinator.AutomationDecisionsAllowed
                && !invalidCoordinator.Diagnostics.SnapshotInProgress,
            "invalid snapshot record leaves the old world intact and decisions paused without a partial swap");

        var retryFake = new FakeNativeApi();
        var candidateCount = 0;
        using var retryCoordinator = new FcMeshNativeCoordinator(
            retryFake,
            new FcWorldStore(new FcManualClock(2_000)),
            () =>
            {
                candidateCount++;
                return new FcWorldStore(new FcManualClock(2_000));
            },
            maxEventsPerTick: 1,
            statusInterval: TimeSpan.Zero);
        var retryStarted = retryCoordinator.Start(new FcNativeConfiguration("diagnostic-only"), TestCharacterKey).Succeeded;
        var retryEvidence = string.Empty;
        var retryReady = retryStarted
            && EstablishReadyState(retryFake, retryCoordinator, out retryEvidence);
        require(retryReady,
            $"retry snapshot fixture must begin from a legitimate Ready baseline; {retryEvidence}");
        var retryOldWorld = retryCoordinator.WorldStore;
        var snapshotWorker = MakeWorker(NativeAuthor, NativeSession, 1, 1, 0);
        retryFake.EnqueueSnapshotRecord(BuildNativeRecord(snapshotWorker, 1_000, 1).Snapshot);
        retryFake.EnqueueInvalidation(1);
        retryCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        retryCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        retryCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        retryFake.PollEventHook = () => retryFake.EnqueueInvalidation(2);
        retryCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        retryCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(candidateCount >= 2
                && retryFake.SnapshotRequests >= 2
                && ReferenceEquals(retryOldWorld, retryCoordinator.WorldStore)
                && !retryCoordinator.AutomationDecisionsAllowed,
            "invalidation during bounded snapshot reconstruction discards the shadow candidate and retries while retaining the old world");
        for (var index = 0; index < 12; index++)
            retryCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(retryCoordinator.AutomationDecisionsAllowed
                && !ReferenceEquals(retryOldWorld, retryCoordinator.WorldStore),
            "retried snapshot swaps only after its replacement reconstruction completes");

        var overflowFake = new FakeNativeApi();
        using var overflowCoordinator = new FcMeshNativeCoordinator(
            overflowFake,
            new FcWorldStore(new FcManualClock(2_000)),
            () => new FcWorldStore(new FcManualClock(2_000)),
            maxEventsPerTick: 4_096,
            statusInterval: TimeSpan.Zero);
        var overflowStarted = overflowCoordinator.Start(new FcNativeConfiguration("diagnostic-only"), TestCharacterKey).Succeeded;
        var overflowEvidence = string.Empty;
        var overflowReady = overflowStarted
            && EstablishReadyState(overflowFake, overflowCoordinator, out overflowEvidence);
        require(overflowReady,
            $"overflow snapshot fixture must begin from a legitimate Ready baseline; {overflowEvidence}");
        var overflowOldWorld = overflowCoordinator.WorldStore;
        overflowFake.EnableSnapshotOverflow(100_001);
        overflowFake.EnqueueInvalidation(1);
        for (var index = 0; index < 64 && overflowFake.SnapshotRequests < 2; index++)
            overflowCoordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(overflowFake.SnapshotRequests >= 2
                && ReferenceEquals(overflowOldWorld, overflowCoordinator.WorldStore)
                && !overflowCoordinator.AutomationDecisionsAllowed,
            "snapshot overflow retries without exposing a partial candidate or replacing the old world");
    }

    private static void ReplayBoundary(Action<bool, string> require)
    {
        var fake = new FakeNativeApi();
        using var coordinator = new FcMeshNativeCoordinator(
            fake,
            new FcWorldStore(new FcManualClock(2_000)),
            () => new FcWorldStore(new FcManualClock(2_000)),
            maxEventsPerTick: 1,
            statusInterval: TimeSpan.Zero);
        var replayStarted = coordinator.Start(new FcNativeConfiguration("diagnostic-only"), TestCharacterKey).Succeeded;
        var replayEvidence = string.Empty;
        var replayReady = replayStarted
            && EstablishReadyState(fake, coordinator, out replayEvidence);
        require(replayReady,
            $"replay fixture must begin from a legitimate Ready baseline; {replayEvidence}");
        fake.EnqueueInvalidation(1);
        coordinator.Tick(TimeSpan.FromMilliseconds(100));
        var worker = MakeWorker(NativeAuthor, NativeSession, 1, 1, 0);
        fake.EnqueueRecord(BuildNativeRecord(worker, 1_000, fake.AllocateSequence()).Event);
        for (var index = 0; index < 8 && !coordinator.AutomationDecisionsAllowed; index++)
            coordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(coordinator.AutomationDecisionsAllowed
                && coordinator.WorldStore.Workers.Count == 1,
            "snapshot reconstruction replays only a later-sequence RecordInserted into the shadow world before atomic swap");
    }

    private static void ReentrantDisposeBoundary(Action<bool, string> require)
    {
        var fake = new FakeNativeApi();
        using var coordinator = new FcMeshNativeCoordinator(fake, statusInterval: TimeSpan.Zero);
        coordinator.Start(new FcNativeConfiguration("diagnostic-only"), TestCharacterKey);
        fake.PollEventHook = coordinator.Dispose;
        coordinator.Tick(TimeSpan.FromMilliseconds(100));
        require(fake.DestroyCalls == 1 && coordinator.Diagnostics.LastError == string.Empty,
            "dispose reentrant with the framework tick is contained and destroys the native handle once");
    }

    private static readonly byte[] NativeAuthorSeed =
    [
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff, 0x10,
        0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80, 0x90,
        0xa0, 0xb0, 0xc0, 0xd0, 0xe0, 0xf0, 0x01, 0x02,
    ];
    private static readonly string NativeAuthor = Convert.ToHexString(
            new Ed25519PrivateKeyParameters(NativeAuthorSeed, 0)
        .GeneratePublicKey()
        .GetEncoded())
        .ToLowerInvariant();
    private static readonly byte[] TestAuthorSeed =
    [
        0x21, 0x32, 0x43, 0x54, 0x65, 0x76, 0x87, 0x98,
        0xa9, 0xba, 0xcb, 0xdc, 0xed, 0xfe, 0x0f, 0x11,
        0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99,
        0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff, 0x10, 0x20,
    ];
    private static readonly byte[] TestRemoteAuthorSeed =
    [
        0x31, 0x42, 0x53, 0x64, 0x75, 0x86, 0x97, 0xa8,
        0xb9, 0xca, 0xdb, 0xec, 0xfd, 0x0e, 0x1f, 0x21,
        0x32, 0x43, 0x54, 0x65, 0x76, 0x87, 0x98, 0xa9,
        0xba, 0xcb, 0xdc, 0xed, 0xfe, 0x1f, 0x30, 0x40,
    ];
    private static readonly string TestAuthor = PublicAuthor(TestAuthorSeed);
    private static readonly string TestRemoteAuthor = PublicAuthor(TestRemoteAuthorSeed);
    private static readonly IReadOnlyDictionary<string, byte[]> TestAuthorSeeds
        = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [NativeAuthor] = NativeAuthorSeed,
            [TestAuthor] = TestAuthorSeed,
            [TestRemoteAuthor] = TestRemoteAuthorSeed,
        };
    private static readonly JsonSerializerOptions RustCanonicalJsonOptions
        = new(FcJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    private const string NativeHlcNode = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly Guid NativeSession = Guid.Parse("00000000-0000-0000-0000-000000000111");
    private static readonly Guid NativeList = Guid.Parse("00000000-0000-0000-0000-000000000112");
    private static readonly Guid NativeChestRecord = Guid.Parse("00000000-0000-0000-0000-000000000113");
    private static readonly Guid NativeTransfer = Guid.Parse("00000000-0000-0000-0000-000000000114");

    internal static string DeterministicAuthor => TestAuthor;
    internal static string DeterministicRemoteAuthor => TestRemoteAuthor;

    private static string PublicAuthor(byte[] seed)
        => Convert.ToHexString(
                new Ed25519PrivateKeyParameters(seed, 0)
                    .GeneratePublicKey()
                    .GetEncoded())
            .ToLowerInvariant();

    private static WorkerSessionRecord MakeWorker(
        string owner,
        Guid sessionId,
        ulong generation,
        ulong revision,
        int held)
        => new(
            Header(FcRecordTypes.WorkerSession, sessionId, owner, revision),
            sessionId,
            generation,
            FcWorkerState.Active,
            new CharacterIdentity(owner, owner, "world"),
            FcFulfillmentSelection.Specific(NativeList),
            false,
            held == 0
                ? Array.Empty<ItemQuantityEntry>()
                : [new ItemQuantityEntry(100, FcItemQuality.Nq, held)],
            null,
            Array.Empty<FcLogicalQueueEntry>(),
            [100],
            "world");

    private static ChestSnapshotRecord MakeChest(string owner, ulong revision, int quantity)
        => new(
            Header(FcRecordTypes.ChestSnapshot, NativeChestRecord, owner, revision),
            true,
            FcRecordValidator.CompleteChestPageMask,
            quantity == 0
                ? Array.Empty<ItemQuantityEntry>()
                : [new ItemQuantityEntry(100, FcItemQuality.Nq, quantity)],
            new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>()));

    private static FcInventoryTransferRecord MakeTransfer(
        string owner,
        Guid sessionId,
        ulong generation,
        ulong revision,
        ChestSnapshotRecord chest,
        WorkerSessionRecord worker)
    {
        var afterChest = chest with
        {
            Header = chest.Header with { Revision = revision },
            Items = Array.Empty<ItemQuantityEntry>(),
        };
        var afterWorker = worker with
        {
            Header = worker.Header with { Revision = revision },
            HeldInventory = [new ItemQuantityEntry(100, FcItemQuality.Nq, 100)],
        };
        return new FcInventoryTransferRecord(
            Header(FcRecordTypes.InventoryTransfer, NativeTransfer, owner, revision),
            NativeTransfer,
            sessionId,
            generation,
            NativeList,
            FcInventoryTransferKind.Withdraw,
            FcInventoryTransferOutcome.Committed,
            [new ItemQuantityEntry(100, FcItemQuality.Nq, 100)],
            afterChest,
            afterWorker);
    }

    private static FcRecordHeader Header(string type, Guid id, string owner, ulong revision)
        => new(
            FcProtocolVersion.Current,
            FcProtocolVersion.CurrentSchema,
            type,
            id,
            owner,
            revision);

    internal static NativeRecordFixture BuildNativeRecord(object payload, long physical, ulong sequence, ulong epoch = 0)
    {
        var header = HeaderOf(payload);
        var generation = payload switch
        {
            WorkerSessionRecord worker => worker.SessionGeneration,
            FcInventoryTransferRecord transfer => transfer.SessionGeneration,
            _ => 0UL,
        };
        var hasGeneration = payload is WorkerSessionRecord or FcInventoryTransferRecord;
        var payloadBytes = FcCanonical.SerializeUtf8(payload);
        var generationValue = hasGeneration ? generation : (ulong?)null;
        var wire = new FcNativeEnvelopeWire
        {
            ProtocolVersion = FcProtocolVersion.Current,
            RecordId = header.RecordId.ToString("D"),
            ActualAuthorId = header.OwnerAuthorId,
            Generation = generationValue,
            Revision = header.Revision,
            Hlc = new FcNativeHlcWire
            {
                PhysicalUnixMs = physical,
                Logical = 0,
                NodeId = NativeHlcNode,
            },
            RecordType = header.RecordType,
            Payload = payloadBytes,
            PayloadHash = FcMeshSignature.HashPayload(payloadBytes),
            Signature = string.Empty,
            DocumentKeyOwnerId = header.OwnerAuthorId,
            DocumentKey = FcMeshKey.ForRecord(header.RecordType, header.OwnerAuthorId, header.RecordId.ToString("D")),
            SignatureAlgorithm = FcNativeEnvelopeDecoder.SignatureAlgorithm,
        };
        var signed = SignNativeEnvelope(wire);
        var eventValue = new FcMeshNativeEvent(
            FcProtocolVersion.Current,
            FcNativeEventKind.RecordInserted,
            sequence,
            epoch,
            signed.Key,
            signed.Envelope,
            signed.Author,
            signed.ContentHash,
            header.Revision);
        var snapshotValue = new FcMeshNativeSnapshotRecord(
            FcProtocolVersion.Current,
            signed.Key,
            signed.Envelope,
            signed.Author,
            signed.ContentHash,
            generationValue,
            header.Revision,
            header.RecordType,
            physical,
            0,
            Convert.FromHexString(NativeHlcNode));
        return new NativeRecordFixture(eventValue, snapshotValue);
    }

    private static SignedNativeEnvelope SignNativeEnvelope(FcNativeEnvelopeWire wire)
    {
        if (!TestAuthorSeeds.TryGetValue(wire.ActualAuthorId, out var authorSeed))
            throw new InvalidOperationException(
                "fake native envelope author is not registered with a deterministic Ed25519 test key");
        if (!string.Equals(PublicAuthor(authorSeed), wire.ActualAuthorId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "fake native envelope author does not match its deterministic Ed25519 public key");
        if (!FcNativeEnvelopeCrypto.TryEncodeSigningBytes(
                wire,
                out var signingBytes,
                out var error))
            throw new InvalidOperationException($"fake native envelope signing shape rejected: {error}");

        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(authorSeed, 0));
        signer.BlockUpdate(signingBytes, 0, signingBytes.Length);
        wire.Signature = Convert.ToBase64String(signer.GenerateSignature());
        var envelope = JsonSerializer.SerializeToUtf8Bytes(
            wire,
            RustCanonicalJsonOptions);
        return new SignedNativeEnvelope(
            Encoding.UTF8.GetBytes(wire.DocumentKey),
            envelope,
            Convert.FromHexString(wire.ActualAuthorId),
            FcNativeEnvelopeCrypto.HashEnvelope(envelope));
    }

    private static FcRecordHeader HeaderOf(object payload)
        => payload switch
        {
            PublishedListRecord value => value.Header,
            WorkerSessionRecord value => value.Header,
            ChestSnapshotRecord value => value.Header,
            CapabilityRequestRecord value => value.Header,
            CapabilityResponseRecord value => value.Header,
            FcInventoryTransferRecord value => value.Header,
            _ => throw new ArgumentException("Unsupported native test payload.", nameof(payload)),
        };

    internal sealed record NativeRecordFixture(
        FcMeshNativeEvent Event,
        FcMeshNativeSnapshotRecord Snapshot);

    private sealed record SignedNativeEnvelope(
        byte[] Key,
        byte[] Envelope,
        byte[] Author,
        byte[] ContentHash);

    private static string? FindFixture()
    {
        var candidates = new List<string>
        {
            Path.Combine(Directory.GetCurrentDirectory(), "gathermesh", "tests", "fixtures", "mesh-envelope-v1.json"),
        };
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var index = 0; index < 12 && directory is not null; index++, directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "gathermesh", "tests", "fixtures", "mesh-envelope-v1.json"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    internal sealed class FakeNativeApi : IFcMeshNativeApi
    {
        private readonly Queue<FcMeshNativeEvent> _events = new();
        private readonly Queue<FcMeshNativeSnapshotRecord> _snapshotRecords = new();
        private ulong _sequence;
        private ulong _worldEpoch;
        private bool _staleReturned;
        private int _overflowRecordsRemaining;
        private byte[]? _selectedAuthor;

        public int CreateGroupCalls { get; private set; }
        public int DestroyCalls { get; private set; }
        public int SnapshotRequests { get; private set; }
        public bool ThrowOnPoll { get; set; }
        public bool StaleFirstSnapshotPoll { get; set; }
        public uint AbiVersionValue { get; set; } = FcNativeAbi.Version;
        public Action? PollEventHook { get; set; }
        public int SnapshotDestroyCalls { get; private set; }
        public ulong CurrentSequence => _sequence;
        public int PendingEvents => _events.Count;
        public int PendingSnapshotRecords => _snapshotRecords.Count;
        public ulong LastOpenedSnapshotHandle { get; private set; }
        public ulong? SnapshotBaseSequenceOverride { get; set; }
        public ulong LastSnapshotBaseEventSequence { get; private set; }
        public int SnapshotRecordsPolled { get; private set; }
        public List<string> Calls { get; } = new();
        public List<FcNativeEventKind> ObservedEventKinds { get; } = new();
        public byte[]? SelectedAuthorAtStart { get; private set; }
        public byte[]? RequiredAuthorBeforeStart { get; set; }

        public uint AbiVersion() => AbiVersionValue;

        public FcNativeCallResult Create(ReadOnlySpan<byte> configJson, out ulong handle)
        {
            Calls.Add("Create");
            handle = 7;
            return Ok();
        }

        public FcNativeCallResult Start(ulong handle)
        {
            Calls.Add("Start");
            SelectedAuthorAtStart = _selectedAuthor?.ToArray();
            return RequiredAuthorBeforeStart is null
                || SelectedAuthorAtStart is not null
                    && SelectedAuthorAtStart.SequenceEqual(RequiredAuthorBeforeStart)
                ? Ok()
                : Error(FcNativeErrorCode.InvalidState);
        }

        public FcNativeCallResult CreateGroup(ulong handle)
        {
            CreateGroupCalls++;
            return Ok();
        }

        public FcNativeCallResult JoinGroup(ulong handle, ReadOnlySpan<byte> ticket) => Ok();
        public FcNativeCallResult LeaveGroup(ulong handle) => Ok();

        public FcNativeCallResult Put(
            ulong handle,
            ReadOnlySpan<byte> recordId,
            ReadOnlySpan<byte> key,
            ReadOnlySpan<byte> recordType,
            bool hasGeneration,
            ulong generation,
            ulong revision,
            ReadOnlySpan<byte> payload) => Ok();

        public FcNativeCallResult PollEvent(ulong handle, out FcMeshNativeEvent? value)
        {
            var hook = PollEventHook;
            PollEventHook = null;
            hook?.Invoke();
            if (ThrowOnPoll)
                throw new InvalidOperationException("fake poll failure");
            if (_events.Count == 0)
            {
                value = null;
                return Error(FcNativeErrorCode.NoEvent);
            }
            var dequeued = _events.Dequeue();
            value = dequeued;
            ObservedEventKinds.Add(dequeued.Kind);
            return Ok();
        }

        public FcNativeCallResult GetStatus(ulong handle, out FcMeshNativeStatus? value)
        {
            value = new FcMeshNativeStatus(
                FcNativeLifecycle.Running,
                false,
                0,
                0,
                _worldEpoch,
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                0);
            return Ok();
        }

        public FcNativeCallResult Shutdown(ulong handle, uint timeoutMs) => Ok();

        public FcNativeCallResult Destroy(ulong handle)
        {
            DestroyCalls++;
            return Ok();
        }

        public FcNativeCallResult SetCharacterAuthor(ulong handle, ReadOnlySpan<byte> key)
        {
            Calls.Add("SetCharacterAuthor");
            _selectedAuthor = key.ToArray();
            return Ok();
        }

        public FcNativeCallResult RequestSnapshot(ulong handle, ulong requestId)
        {
            SnapshotRequests++;
            Enqueue(new FcMeshNativeEvent(
                1,
                FcNativeEventKind.SnapshotReady,
                ++_sequence,
                _worldEpoch,
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                requestId));
            return Ok();
        }

        public FcNativeCallResult OpenSnapshot(ulong handle, ulong requestId, out ulong snapshot, out ulong baseEventSequence)
        {
            snapshot = 99;
            LastOpenedSnapshotHandle = snapshot;
            baseEventSequence = SnapshotBaseSequenceOverride
                ?? (_sequence == 0 ? 0 : _sequence - 1);
            LastSnapshotBaseEventSequence = baseEventSequence;
            return Ok();
        }

        public FcNativeCallResult PollSnapshot(ulong snapshot, out FcMeshNativeSnapshotRecord? value, out bool done)
        {
            if (StaleFirstSnapshotPoll && !_staleReturned)
            {
                _staleReturned = true;
                value = null;
                done = false;
                return Error(FcNativeErrorCode.SnapshotStale);
            }
            if (_overflowRecordsRemaining > 0)
            {
                _overflowRecordsRemaining--;
                SnapshotRecordsPolled++;
                value = new FcMeshNativeSnapshotRecord(
                    1,
                    [1],
                    [1],
                    new byte[32],
                    new byte[32],
                    null,
                    1,
                    FcRecordTypes.WorkerSession,
                    1_000,
                    0,
                    new byte[16]);
                done = false;
                return Ok();
            }
            if (_snapshotRecords.Count != 0)
            {
                SnapshotRecordsPolled++;
                value = _snapshotRecords.Dequeue();
                done = false;
                return Ok();
            }
            value = null;
            done = true;
            return Ok();
        }

        public FcNativeCallResult DestroySnapshot(ulong snapshot)
        {
            SnapshotDestroyCalls++;
            return Ok();
        }
        public string? GetErrorMessage(ulong errorId) => null;

        public void EnqueueWarning(string text)
            => Enqueue(new FcMeshNativeEvent(
                1,
                FcNativeEventKind.Warning,
                ++_sequence,
                _worldEpoch,
                Array.Empty<byte>(),
                System.Text.Encoding.UTF8.GetBytes(text),
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                0));

        public void EnqueueStarted(byte[] actualAuthor)
            => Enqueue(new FcMeshNativeEvent(
                1,
                FcNativeEventKind.Started,
                ++_sequence,
                _worldEpoch,
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                actualAuthor.ToArray(),
                Array.Empty<byte>(),
                0));

        public void EnqueueJoined(bool created = false)
            => Enqueue(new FcMeshNativeEvent(
                1,
                FcNativeEventKind.Joined,
                ++_sequence,
                _worldEpoch,
                Enumerable.Repeat((byte)0x11, 32).ToArray(),
                created ? Encoding.UTF8.GetBytes("phase5-created-ticket") : Array.Empty<byte>(),
                Enumerable.Repeat((byte)0xaa, 32).ToArray(),
                Array.Empty<byte>(),
                0));

        public void EnqueueInitialSync(ulong recordCount = 0)
            => Enqueue(new FcMeshNativeEvent(
                1,
                FcNativeEventKind.InitialSyncCompleted,
                ++_sequence,
                _worldEpoch,
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                Enumerable.Repeat((byte)0xbb, 32).ToArray(),
                Array.Empty<byte>(),
                recordCount));

        public void EnqueueCompatibleGroupMetadataEvent()
        {
            var fixture = BuildGroupMetadataRecord(AllocateSequence());
            EnqueueRecord(fixture.Event);
        }

        public void EnqueueCompatibleGroupMetadataSnapshot()
            => EnqueueSnapshotRecord(BuildGroupMetadataRecord(0).Snapshot);

        public void EnqueueIncompatibleGroupMetadataSnapshot()
            => EnqueueSnapshotRecord(BuildGroupMetadataRecord(0, 0x22).Snapshot);

        public void EnqueueInvalidation(ulong epoch)
        {
            _worldEpoch = epoch;
            Enqueue(new FcMeshNativeEvent(
                1,
                FcNativeEventKind.WorldInvalidated,
                ++_sequence,
                epoch,
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                epoch));
        }

        public ulong AllocateSequence() => ++_sequence;

        public void EnqueueRecord(FcMeshNativeEvent value)
        {
            _sequence = Math.Max(_sequence, value.Sequence);
            Enqueue(value);
        }

        public void EnqueueSnapshotRecord(FcMeshNativeSnapshotRecord value)
            => _snapshotRecords.Enqueue(value);

        public void EnableSnapshotOverflow(int recordCount)
            => _overflowRecordsRemaining = recordCount;

        private void Enqueue(FcMeshNativeEvent value) => _events.Enqueue(value);

        private static NativeRecordFixture BuildGroupMetadataRecord(
            ulong sequence,
            byte namespaceFill = 0x11)
        {
            const string recordId = "00000000-0000-0000-0000-000000000115";
            var owner = NativeAuthor;
            var namespaceBytes = Enumerable.Repeat(namespaceFill, 32).ToArray();
            var payload = new[]
                    { (byte)FcProtocolVersion.Current, (byte)9 }
                .Concat(Encoding.UTF8.GetBytes("iroh-docs"))
                .Concat(new byte[] { 1 })
                .Concat(namespaceBytes)
                .ToArray();
            var recordType = FcRecordTypes.GroupMetadata;
            var documentKey = FcMeshKey.ForRecord(recordType, owner, recordId);
            var wire = new FcNativeEnvelopeWire
            {
                ProtocolVersion = FcProtocolVersion.Current,
                RecordId = recordId,
                ActualAuthorId = owner,
                Generation = null,
                Revision = 1,
                Hlc = new FcNativeHlcWire
                {
                    PhysicalUnixMs = 1_000,
                    Logical = 0,
                    NodeId = NativeHlcNode,
                },
                RecordType = recordType,
                Payload = payload,
                PayloadHash = FcMeshSignature.HashPayload(payload),
                Signature = string.Empty,
                DocumentKeyOwnerId = owner,
                DocumentKey = documentKey,
                SignatureAlgorithm = FcNativeEnvelopeDecoder.SignatureAlgorithm,
            };
            var signed = SignNativeEnvelope(wire);
            var eventValue = new FcMeshNativeEvent(
                FcProtocolVersion.Current,
                FcNativeEventKind.RecordInserted,
                sequence,
                0,
                signed.Key,
                signed.Envelope,
                signed.Author,
                signed.ContentHash,
                1);
            var snapshotValue = new FcMeshNativeSnapshotRecord(
                FcProtocolVersion.Current,
                signed.Key,
                signed.Envelope,
                signed.Author,
                signed.ContentHash,
                null,
                1,
                recordType,
                1_000,
                0,
                Convert.FromHexString(wire.Hlc.NodeId));
            return new NativeRecordFixture(eventValue, snapshotValue);
        }

        public void Dispose()
        {
        }

        private static FcNativeCallResult Ok() => new((uint)FcNativeErrorCode.Ok, 0, 0);
        private static FcNativeCallResult Error(FcNativeErrorCode code) => new((uint)code, 0, 0);
    }
}
