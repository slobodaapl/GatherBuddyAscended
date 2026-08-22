using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using GatherBuddy.FcMesh.Native;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.Vulcan.Tests;

internal static class FcMeshPhase10Tests
{
    public static void Run(Action<bool, string> require)
    {
        var fixturePath = LocateFixture();
        require(fixturePath is not null, "phase10 managed-validation fixture must be available");
        if (fixturePath is null)
            return;

        var fixture = File.ReadAllBytes(fixturePath);
        var golden = FcNativeEnvelopeDecoder.Decode(fixture);
        require(golden.IsValid && golden.Record is not null,
            "the managed golden fixture must cross the strict native-envelope boundary");
        if (golden.IsValid && golden.Record is not null)
        {
            require(golden.Record.ProtocolVersion == 1
                    && golden.Record.RecordId == "00112233-4455-6677-8899-aabbccddeeff"
                    && golden.Record.ActualAuthorId == "ea4a6c63e29c520abef5507b132ec5f9954776aebebe7b92421eea691446d22c"
                    && golden.Record.Revision == 7
                    && golden.Record.Hlc.PhysicalUnixMs == 1_700_000_000_000
                    && golden.Record.Hlc.Logical == 7_301_444_403_200_000_000
                    && golden.Record.Hlc.NodeId == "22222222222222222222222222222222"
                    && golden.Record.PayloadHash == "b15740944697339d5caf7fca29223315a816ceceaa9f22f10b068284236054aa"
                    && golden.Record.Signature == "xsBV2Zpt6/6DMr7q1pRLVowUDXSW5DPrVrfLdtFZCY6VnXqHFHubniR64QT4idreT//IOgeYqOb0o/8jCntXBg=="
                    && golden.Record.DocumentKeyOwnerId == golden.Record.ActualAuthorId
                    && golden.Record.DocumentKey == "v1/workers/ea4a6c63e29c520abef5507b132ec5f9954776aebebe7b92421eea691446d22c"
                    && golden.Record.OriginalEnvelopeBytes.SequenceEqual(fixture),
                "managed golden envelope IDs, raw HLC, SHA-256, signature, key, and exact bytes must remain stable");

            var signingFixturePath = Path.Combine(
                Path.GetDirectoryName(fixturePath)!,
                "mesh-envelope-v1-signing-bytes.hex");
            require(File.Exists(signingFixturePath),
                "the cross-language postcard signing-byte fixture must be available");
            if (File.Exists(signingFixturePath))
            {
                var wire = JsonSerializer.Deserialize(
                    fixture,
                    FcJsonContext.Default.FcNativeEnvelopeWire);
                var signingBytes = Array.Empty<byte>();
                var encoded = wire is not null
                    && FcNativeEnvelopeCrypto.TryEncodeSigningBytes(
                        wire,
                        out signingBytes,
                        out _)
                    ? Convert.ToHexString(signingBytes).ToLowerInvariant()
                    : string.Empty;
                require(string.Equals(
                        encoded,
                        File.ReadAllText(signingFixturePath).Trim(),
                        StringComparison.Ordinal),
                    "managed postcard signing bytes must match the Rust checked-in vector exactly");
            }
            require(Convert.ToHexString(FcNativeEnvelopeCrypto.HashEnvelope(fixture)).ToLowerInvariant()
                    == "4f96c1b5388852c1b47f957ce89b40689ffc6b9fb908e6bb6da4e67d2c7a456c",
                "managed BLAKE3 must independently match the Rust full-envelope vector");

            var verified = FcNativeEnvelopeDecoder.Decode(
                fixture,
                Convert.FromHexString(golden.Record.ActualAuthorId),
                Convert.FromHexString("4f96c1b5388852c1b47f957ce89b40689ffc6b9fb908e6bb6da4e67d2c7a456c"),
                Encoding.UTF8.GetBytes(golden.Record.DocumentKey));
            require(verified.IsValid
                    && verified.VerifiedContext is not null
                    && verified.VerifiedContext.NativeVerified
                    && verified.VerifiedContext.ContentHash == "4f96c1b5388852c1b47f957ce89b40689ffc6b9fb908e6bb6da4e67d2c7a456c",
                "managed native context must retain the exact full-envelope BLAKE3 vector");

            var wrongAuthor = Convert.FromHexString(golden.Record.ActualAuthorId);
            wrongAuthor[0] ^= 1;
            require(!FcNativeEnvelopeDecoder.Decode(
                        fixture,
                        wrongAuthor,
                        Convert.FromHexString("4f96c1b5388852c1b47f957ce89b40689ffc6b9fb908e6bb6da4e67d2c7a456c"),
                        Encoding.UTF8.GetBytes(golden.Record.DocumentKey)).IsValid,
                "managed verification must reject a native event author mismatch");

            var wrongContentHash = Convert.FromHexString("4f96c1b5388852c1b47f957ce89b40689ffc6b9fb908e6bb6da4e67d2c7a456c");
            wrongContentHash[0] ^= 1;
            require(!FcNativeEnvelopeDecoder.Decode(
                        fixture,
                        Convert.FromHexString(golden.Record.ActualAuthorId),
                        wrongContentHash,
                        Encoding.UTF8.GetBytes(golden.Record.DocumentKey)).IsValid,
                "managed verification must reject a native full-envelope BLAKE3 mismatch");

            var badSignature = Encoding.UTF8.GetString(fixture).Replace(
                "xsBV2Zpt6/6DMr7q1pRLVowUDXSW5DPrVrfLdtFZCY6VnXqHFHubniR64QT4idreT//IOgeYqOb0o/8jCntXBg==",
                "ysBV2Zpt6/6DMr7q1pRLVowUDXSW5DPrVrfLdtFZCY6VnXqHFHubniR64QT4idreT//IOgeYqOb0o/8jCntXBg==",
                StringComparison.Ordinal);
            require(!FcNativeEnvelopeDecoder.Decode(Encoding.UTF8.GetBytes(badSignature)).IsValid,
                "managed verification must reject a canonical-base64 signature mutation");

            var badSigningField = Encoding.UTF8.GetString(fixture).Replace(
                "\"revision\":7",
                "\"revision\":8",
                StringComparison.Ordinal);
            require(!FcNativeEnvelopeDecoder.Decode(Encoding.UTF8.GetBytes(badSigningField)).IsValid,
                "managed verification must reject a signed revision mutation");

            var badPayload = Encoding.UTF8.GetString(fixture).Replace(
                "b3BhcXVlLWZpeHR1cmU=",
                "b3BhcXVlLWZpeHR1cmV=",
                StringComparison.Ordinal);
            require(!FcNativeEnvelopeDecoder.Decode(Encoding.UTF8.GetBytes(badPayload)).IsValid,
                "managed verification must reject a payload mutation with stale SHA-256");
        }
        var failures = 0;
        for (var seed = 1; seed <= 512; seed++)
        {
            var mutated = Mutate(fixture, seed);
            try
            {
                var decoded = FcNativeEnvelopeDecoder.Decode(mutated);
                if (decoded.IsValid
                    && (decoded.Record is null
                        || decoded.Record.OriginalEnvelopeBytes is null
                        || !decoded.Record.OriginalEnvelopeBytes.SequenceEqual(mutated)))
                    failures++;
            }
            catch (Exception)
            {
                failures++;
            }
        }
        for (var seed = 1; seed <= 512; seed++)
        {
            var arbitrary = Arbitrary(seed);
            try
            {
                var decoded = FcNativeEnvelopeDecoder.Decode(arbitrary);
                if (decoded.IsValid)
                    failures++;
            }
            catch (Exception)
            {
                failures++;
            }
        }
        require(failures == 0,
            "managed native-envelope ingestion must remain bounded and exact for deterministic arbitrary-byte mutations");

        var unknownEnvelopeField = fixture[..^1]
            .Concat(",\"optionalExtension\":true}"u8.ToArray())
            .ToArray();
        require(!FcNativeEnvelopeDecoder.Decode(unknownEnvelopeField).IsValid,
            "unknown envelope fields are not optional and must fail closed instead of being silently reserialized");

        var majorVersion = fixture.ToArray();
        var versionMarker = "\"protocolVersion\":1"u8.ToArray();
        var versionIndex = IndexOf(majorVersion, versionMarker);
        require(versionIndex >= 0, "golden fixture must expose its protocol version field");
        if (versionIndex >= 0)
        {
            majorVersion[versionIndex + versionMarker.Length - 1] = (byte)'2';
            require(!FcNativeEnvelopeDecoder.Decode(majorVersion).IsValid,
                "new major envelope protocol must be rejected before managed projection");
        }

        var oversized = new byte[(16 * 1024 * 1024) + 1];
        var oversizedResult = FcNativeEnvelopeDecoder.Decode(oversized);
        require(!oversizedResult.IsValid
                && oversizedResult.Error == "Native envelope exceeds 16 MiB maximum.",
            "oversized native envelope bytes must be rejected before JSON deserialization");

        VerifyPublishedListPayloadCompatibility(require);
        FcMeshPhase10PropertyTests.Run(require);
        if (string.Equals(
                Environment.GetEnvironmentVariable("GATHERMESH_NATIVE_SMOKE"),
                "1",
                StringComparison.Ordinal))
            VerifyRealNativePInvokeBoundary(require);
    }

    private static void VerifyRealNativePInvokeBoundary(Action<bool, string> require)
    {
        var storage = Path.Combine(
            Path.GetTempPath(),
            "gathermesh-managed-smoke-" + Guid.NewGuid().ToString("N"));
        using var api = new FcMeshNativePInvokeApi();
        require(api.AbiVersion() == FcNativeAbi.Version,
            "real managed P/Invoke must read the native ABI version from the built GatherMesh library");

        var malformed = api.Create("{bad}"u8, out _);
        require(malformed.ErrorCode == FcNativeErrorCode.InvalidArgument
                && malformed.ErrorId != 0
                && !string.IsNullOrWhiteSpace(api.GetErrorMessage(malformed.ErrorId)),
            "real native create failure must return an owned error identifier and bounded retrievable detail");

        var created = api.Create(
            new FcNativeConfiguration(
                storage,
                EventCapacity: 32,
                CommandCapacity: 16,
                MaxKeyBytes: 4096,
                MaxValueBytes: 1024 * 1024,
                RelayMode: 1).ToJsonUtf8(),
            out var handle);
        require(created.Succeeded && handle != 0,
            "real managed P/Invoke must create a native service with the checked ABI configuration");
        if (!created.Succeeded || handle == 0)
            return;

        require(api.Start(handle).Succeeded,
            "real managed P/Invoke must start the native service without crossing an exception boundary");
        require(api.GetStatus(handle, out var status).Succeeded && status is not null,
            "real managed P/Invoke status retrieval must marshal bounded native buffers and free them");
        var poll = api.PollEvent(handle, out _);
        require(poll.Succeeded || poll.ErrorCode == FcNativeErrorCode.NoEvent,
            "real managed P/Invoke event polling must contain the no-event boundary without a managed exception");

        var characterAuthorKey = Enumerable.Repeat((byte)0x71, 32).ToArray();
        var authorSet = api.SetCharacterAuthor(handle, characterAuthorKey);
        require(authorSet.Succeeded,
            "real native character-author selection must precede signed Docs publication");
        var groupRequested = authorSet.Succeeded && api.CreateGroup(handle).Succeeded;
        require(groupRequested,
            "real native group creation must open a signed Docs namespace for publication");
        var nativeAuthor = groupRequested
            ? WaitForNativeGroupAuthor(api, handle, require)
            : Array.Empty<byte>();
        if (nativeAuthor.Length == 32)
        {
            var owner = Convert.ToHexString(nativeAuthor).ToLowerInvariant();
            var realWorld = new FcWorldStore();
            var compatibleId = Guid.Parse("00000000-0000-0000-0000-000000000711");
            var compatible = PublishRealNativePublishedList(
                api,
                handle,
                owner,
                compatibleId,
                plannerSemanticsVersion: 1,
                require: require);
            require(compatible.IsValid
                    && compatible.Record is not null
                    && compatible.VerifiedContext is { NativeVerified: true },
                "real native signed public-list insertion must cross the managed verified envelope boundary");
            if (compatible.IsValid
                && compatible.Record is not null
                && compatible.VerifiedContext is not null)
            {
                require(Encoding.UTF8.GetString(compatible.Record.Payload)
                            .Contains("\"optionalExtension\":true", StringComparison.Ordinal),
                    "real native public-list envelope must preserve the unknown optional payload member");
                var applied = FcMeshNativeCoordinator.ApplyDecodedRecord(
                    realWorld,
                    compatible.Record,
                    compatible.VerifiedContext);
                var projected = realWorld.Lists.TryGetValue(
                    owner + "/list/" + compatibleId.ToString("D"),
                    out var compatibleProjected);
                require((applied.Status is FcApplyStatus.Accepted or FcApplyStatus.Duplicate)
                        && projected,
                    "real native optional public-list payload must project through the coordinator");
                if (projected)
                {
                    var view = FcPublishedListCompatibility.Evaluate(
                        compatibleProjected,
                        new FcCompatibilityContext(1, "game-1"));
                    require(view.IsCompatible,
                        "real native optional public-list payload must remain executable when compatible");
                }
            }

            var newerId = Guid.Parse("00000000-0000-0000-0000-000000000712");
            var newer = PublishRealNativePublishedList(
                api,
                handle,
                owner,
                newerId,
                plannerSemanticsVersion: 99,
                require: require);
            require(newer.IsValid
                    && newer.Record is not null
                    && newer.VerifiedContext is { NativeVerified: true },
                "real native newer-semantics public-list insertion must remain structurally readable");
            if (newer.IsValid
                && newer.Record is not null
                && newer.VerifiedContext is not null)
            {
                var applied = FcMeshNativeCoordinator.ApplyDecodedRecord(
                    realWorld,
                    newer.Record,
                    newer.VerifiedContext);
                var projected = realWorld.Lists.TryGetValue(
                    owner + "/list/" + newerId.ToString("D"),
                    out var newerProjected);
                var view = FcPublishedListCompatibility.Evaluate(
                    projected ? newerProjected : null,
                    new FcCompatibilityContext(1, "game-1"));
                require((applied.Status is FcApplyStatus.Accepted or FcApplyStatus.Duplicate)
                        && projected
                        && !view.IsCompatible
                        && view.Reason.Contains("Planner semantics", StringComparison.Ordinal),
                    "real native newer planner semantics must remain displayable but non-executable");
            }
        }
        require(api.Shutdown(handle, 1_000).Succeeded,
            "real managed P/Invoke shutdown must reach the native supervisor boundary");

        var sawStopped = false;
        var shutdownComplete = false;
        var shutdownDeadline = Stopwatch.StartNew();
        while (shutdownDeadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            while (true)
            {
                var eventResult = api.PollEvent(handle, out var value);
                if (eventResult.ErrorCode == FcNativeErrorCode.NoEvent)
                    break;
                require(eventResult.Succeeded && value is not null,
                    "real native shutdown polling must return or explicitly report an empty event queue");
                if (!eventResult.Succeeded || value is null)
                    break;
                sawStopped |= value.Kind == FcNativeEventKind.Stopped;
            }

            var statusResult = api.GetStatus(handle, out var shutdownStatus);
            if (statusResult.Succeeded
                && shutdownStatus is not null
                && shutdownStatus.Lifecycle == FcNativeLifecycle.Closed
                && shutdownStatus.PendingCommands == 0
                && shutdownStatus.PendingEvents == 0
                && sawStopped)
            {
                shutdownComplete = true;
                break;
            }
            Thread.Sleep(5);
        }
        require(shutdownComplete,
            "native shutdown must emit Stopped, drain commands/events, and reach Closed before destruction");
        if (!shutdownComplete)
            return;

        require(api.Destroy(handle).Succeeded,
            "real managed P/Invoke destroy must release the native service handle");
        var afterDestroy = api.GetStatus(handle, out _);
        require(afterDestroy.ErrorCode == FcNativeErrorCode.InvalidHandle,
            "real managed P/Invoke must reject status polling after native destroy");

        if (Directory.Exists(storage))
            Directory.Delete(storage, recursive: true);
    }

    private static byte[] WaitForNativeGroupAuthor(
        FcMeshNativePInvokeApi api,
        ulong handle,
        Action<bool, string> require)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            var result = api.PollEvent(handle, out var value);
            if (result.ErrorCode == FcNativeErrorCode.NoEvent)
            {
                Thread.Sleep(5);
                continue;
            }
            require(result.Succeeded && value is not null,
                "real native group creation must produce bounded events without a managed boundary failure");
            if (!result.Succeeded || value is null)
                break;
            if (value.Kind != FcNativeEventKind.Joined)
                continue;
            require(value.ActualAuthor.Length == 32,
                "real native Joined event must carry the authenticated Docs author");
            return value.ActualAuthor;
        }
        require(false,
            "real native group creation must reach Joined with an authenticated author within the bounded smoke window");
        return Array.Empty<byte>();
    }

    private static FcNativeEnvelopeDecodeResult PublishRealNativePublishedList(
        FcMeshNativePInvokeApi api,
        ulong handle,
        string owner,
        Guid listId,
        int plannerSemanticsVersion,
        Action<bool, string> require)
    {
        var recordId = Encoding.UTF8.GetBytes(listId.ToString("D"));
        var key = Encoding.UTF8.GetBytes($"v1/lists/{owner}/{listId:D}");
        var recordType = Encoding.UTF8.GetBytes(FcRecordTypes.PublishedList);
        var payload = BuildPublishedListPayload(owner, listId, plannerSemanticsVersion);
        var put = api.Put(
            handle,
            recordId,
            key,
            recordType,
            hasGeneration: false,
            generation: 0,
            revision: 1,
            payload: payload);
        require(put.Succeeded,
            "real native public-list publication must accept a canonical payload and sign it in Docs");
        if (!put.Succeeded)
            return FcNativeEnvelopeDecodeResult.Invalid("Real native public-list publication failed.");

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            var result = api.PollEvent(handle, out var value);
            if (result.ErrorCode == FcNativeErrorCode.NoEvent)
            {
                Thread.Sleep(5);
                continue;
            }
            require(result.Succeeded && value is not null,
                "real native public-list publication must emit bounded insertion metadata");
            if (!result.Succeeded || value is null)
                break;
            if (value.Kind != FcNativeEventKind.RecordInserted
                || !value.Key.AsSpan().SequenceEqual(key))
                continue;

            var expectedAuthor = Convert.FromHexString(owner);
            require(value.ActualAuthor.AsSpan().SequenceEqual(expectedAuthor)
                    && value.ContentHash.Length == 32
                    && value.Value.Length > 0,
                "real native insertion must carry the authenticated author, full content hash, and envelope bytes");
            var decoded = FcNativeEnvelopeDecoder.Decode(
                value.Value,
                value.ActualAuthor,
                value.ContentHash,
                value.Key);
            var boundedReason = decoded.Error.Length <= 160
                ? decoded.Error
                : decoded.Error[..160];
            var decodeEvidence =
                $"valid={decoded.IsValid}; reason={boundedReason}; envelopeBytes={value.Value.Length}; "
                + $"keyBytes={value.Key.Length}; authorBytes={value.ActualAuthor.Length}; "
                + $"contentHashBytes={value.ContentHash.Length}; recordPresent={decoded.Record is not null}; "
                + $"contextNativeVerified={decoded.VerifiedContext?.NativeVerified == true}; "
                + $"originalBytesMatch={decoded.Record?.OriginalEnvelopeBytes.SequenceEqual(value.Value) == true}";
            require(decoded.IsValid
                    && decoded.Record is not null
                    && decoded.VerifiedContext is { NativeVerified: true }
                    && !string.Equals(
                        decoded.Record.Signature,
                        Convert.ToBase64String(new byte[64]),
                        StringComparison.Ordinal)
                    && decoded.Record.OriginalEnvelopeBytes.SequenceEqual(value.Value),
                "real native signed insertion must decode with exact envelope bytes and native verification context; "
                + decodeEvidence);
            return decoded;
        }
        require(false,
            "real native public-list publication must emit its signed RecordInserted event within the bounded smoke window");
        return FcNativeEnvelopeDecodeResult.Invalid("Real native public-list insertion event was not observed.");
    }

    private static byte[] BuildPublishedListPayload(
        string owner,
        Guid listId,
        int plannerSemanticsVersion)
    {
        var list = CreatePublishedListRecord(owner, listId, plannerSemanticsVersion);
        var canonicalPayload = FcCanonical.SerializeUtf8(list);
        return canonicalPayload[..^1]
            .Concat(",\"optionalExtension\":true}"u8.ToArray())
            .ToArray();
    }

    private static PublishedListRecord CreatePublishedListRecord(
        string owner,
        Guid listId,
        int plannerSemanticsVersion)
    {
        var header = new FcRecordHeader(
            FcProtocolVersion.Current,
            FcProtocolVersion.CurrentSchema,
            FcRecordTypes.PublishedList,
            listId,
            owner,
            1);
        return new PublishedListRecord(
            header,
            listId,
            "Compatibility fixture",
            true,
            plannerSemanticsVersion,
            "game-1",
            [new PublishedRecipeTarget(1, 2, 1, FcItemQuality.Nq)],
            new FcQualityPolicy([new FcQualityRule(2, FcItemQuality.Nq, 1)]),
            FcQualityPolicy.Empty);
    }

    private static void VerifyPublishedListPayloadCompatibility(Action<bool, string> require)
    {
        const string owner = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var compatibleId = Guid.Parse("00000000-0000-0000-0000-000000000701");
        var compatible = DecodePublishedListEnvelope(owner, compatibleId, 1);
        require(compatible.IsValid && compatible.Record is not null && compatible.VerifiedContext is not null,
            "a valid native envelope carrying a public-list payload must cross the strict envelope decoder");
        if (!compatible.IsValid || compatible.Record is null || compatible.VerifiedContext is null)
            return;

        var world = new FcWorldStore();
        var applied = FcMeshNativeCoordinator.ApplyDecodedRecord(
            world,
            compatible.Record,
            compatible.VerifiedContext);
        require((applied.Status is FcApplyStatus.Accepted or FcApplyStatus.Duplicate)
                && world.Lists.ContainsKey(owner + "/list/" + compatibleId.ToString("D")),
            "unknown optional public-list payload members must be ignored at projection while the native envelope remains exact");

        var projected = world.Lists[owner + "/list/" + compatibleId.ToString("D")];
        var compatibleView = FcPublishedListCompatibility.Evaluate(
            projected,
            new FcCompatibilityContext(1, "game-1"));
        require(compatibleView.IsCompatible,
            "a public-list payload with an unknown optional member must remain executable when compatibility matches");

        var newerId = Guid.Parse("00000000-0000-0000-0000-000000000702");
        var newer = DecodePublishedListEnvelope(owner, newerId, 99);
        require(newer.IsValid && newer.Record is not null && newer.VerifiedContext is not null,
            "a list with newer planner semantics must remain structurally displayable through the native boundary");
        if (newer.IsValid && newer.Record is not null && newer.VerifiedContext is not null)
        {
            var newerApply = FcMeshNativeCoordinator.ApplyDecodedRecord(
                world,
                newer.Record,
                newer.VerifiedContext);
            var newerView = FcPublishedListCompatibility.Evaluate(
                newerApply.Status is FcApplyStatus.Accepted or FcApplyStatus.Duplicate
                    ? world.Lists[owner + "/list/" + newerId.ToString("D")]
                    : null,
                new FcCompatibilityContext(1, "game-1"));
            require((newerApply.Status is FcApplyStatus.Accepted or FcApplyStatus.Duplicate)
                    && !newerView.IsCompatible
                    && newerView.Reason.Contains("Planner semantics", StringComparison.Ordinal),
                "newer planner semantics must stay displayable with a visible non-executable compatibility reason");
        }
    }

    private static FcNativeEnvelopeDecodeResult DecodePublishedListEnvelope(
        string owner,
        Guid listId,
        int plannerSemanticsVersion)
    {
        var payload = BuildPublishedListPayload(owner, listId, plannerSemanticsVersion);
        var wire = new FcNativeEnvelopeWire
        {
            ProtocolVersion = FcProtocolVersion.Current,
            RecordId = listId.ToString("D"),
            ActualAuthorId = owner,
            Generation = null,
            Revision = 1,
            Hlc = new FcNativeHlcWire
            {
                PhysicalUnixMs = 1_000,
                Logical = 0,
                NodeId = "11111111111111111111111111111111",
            },
            RecordType = FcRecordTypes.PublishedList,
            Payload = payload,
            PayloadHash = FcMeshSignature.HashPayload(payload),
            Signature = Convert.ToBase64String(new byte[64]),
            DocumentKeyOwnerId = owner,
            DocumentKey = FcMeshKey.ForRecord(FcRecordTypes.PublishedList, owner, listId.ToString("D")),
            SignatureAlgorithm = FcNativeEnvelopeDecoder.SignatureAlgorithm,
        };
        var envelope = JsonSerializer.SerializeToUtf8Bytes(
            wire,
            FcJsonContext.Default.FcNativeEnvelopeWire);
        // This fixture isolates public-list payload compatibility from native
        // cryptography. The real signed native insertion path above exercises
        // FcNativeEnvelopeDecoder and the independent BLAKE3/Ed25519 gate.
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
            OriginalEnvelopeBytes = envelope,
        };
        var context = FcVerifiedMeshContext.FromEnvelope(record) with { SignatureValid = true };
        return FcNativeEnvelopeDecodeResult.Valid(record, context);
    }

    private static byte[] Mutate(byte[] source, int seed)
    {
        var result = source.ToArray();
        var state = unchecked((uint)seed * 0x9E3779B9u);
        var edits = 1 + (seed % 7);
        for (var edit = 0; edit < edits; edit++)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            var index = (int)(state % (uint)result.Length);
            state = unchecked(state * 1664525u + 1013904223u);
            result[index] ^= (byte)(1u << (int)(state % 8));
        }
        if ((seed & 1) == 0)
            result = result.Concat(new byte[] { (byte)' ' }).ToArray();
        if ((seed & 3) == 0)
            result = result.Take(Math.Max(0, result.Length - 1)).ToArray();
        return result;
    }

    private static byte[] Arbitrary(int seed)
    {
        var state = unchecked((uint)seed * 0x85EBCA6Bu);
        var result = new byte[seed % 257];
        for (var index = 0; index < result.Length; index++)
        {
            state = unchecked(state * 1103515245u + 12345u);
            result[index] = (byte)(state >> 24);
        }
        return result;
    }

    private static int IndexOf(byte[] source, byte[] needle)
    {
        for (var start = 0; start <= source.Length - needle.Length; start++)
        {
            if (source.AsSpan(start, needle.Length).SequenceEqual(needle))
                return start;
        }
        return -1;
    }

    private static string? LocateFixture()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "fixtures", "mesh-envelope-v1.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "gathermesh", "tests", "fixtures", "mesh-envelope-v1.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "gathermesh", "tests", "fixtures", "mesh-envelope-v1.json"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
