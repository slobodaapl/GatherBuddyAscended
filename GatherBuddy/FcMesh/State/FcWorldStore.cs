using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.State;

public sealed record FcForkVariant(
    string RegisterKey,
    string CanonicalHash,
    FcMeshRecord Envelope,
    byte[] Payload);

public sealed record FcRevisionHighWaterEntry(string RegisterKey, ulong Revision);

public sealed record FcRegisterHlcHighWaterEntry(string RegisterKey, FcHlcTimestamp Hlc);

public sealed record FcWorkerHighWaterEntry(string RegisterKey, ulong Generation, Guid? SessionId);

public sealed record FcWorldPersistenceState
{
    public FcRevisionHighWaterEntry[] RevisionHighWaters { get; }
    public FcRegisterHlcHighWaterEntry[] RegisterHlcHighWaters { get; }
    public FcWorkerHighWaterEntry[] WorkerHighWaters { get; }
    public FcHlcClockState? HlcState { get; }

    [JsonConstructor]
    public FcWorldPersistenceState(
        FcRevisionHighWaterEntry[] revisionHighWaters,
        FcRegisterHlcHighWaterEntry[] registerHlcHighWaters,
        FcWorkerHighWaterEntry[] workerHighWaters,
        FcHlcClockState? hlcState = null)
    {
        RevisionHighWaters = (revisionHighWaters ?? throw new ArgumentNullException(nameof(revisionHighWaters))).ToArray();
        RegisterHlcHighWaters = (registerHlcHighWaters ?? throw new ArgumentNullException(nameof(registerHlcHighWaters))).ToArray();
        WorkerHighWaters = (workerHighWaters ?? throw new ArgumentNullException(nameof(workerHighWaters))).ToArray();
        HlcState = hlcState;
    }

    public FcWorldPersistenceState(
        IReadOnlyDictionary<string, ulong> revisionHighWater,
        FcHlcClockState? hlcState = null)
        : this(
            (revisionHighWater ?? throw new ArgumentNullException(nameof(revisionHighWater)))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new FcRevisionHighWaterEntry(pair.Key, pair.Value))
                .ToArray(),
            Array.Empty<FcRegisterHlcHighWaterEntry>(),
            Array.Empty<FcWorkerHighWaterEntry>(),
            hlcState)
    {
    }

    [JsonIgnore]
    public IReadOnlyDictionary<string, ulong> RevisionHighWater
        => RevisionHighWaters.ToDictionary(entry => entry.RegisterKey, entry => entry.Revision, StringComparer.Ordinal);

    [JsonIgnore]
    public IReadOnlyDictionary<string, FcHlcTimestamp> RegisterHlcs
        => RegisterHlcHighWaters.ToDictionary(entry => entry.RegisterKey, entry => entry.Hlc, StringComparer.Ordinal);

    [JsonIgnore]
    public IReadOnlyDictionary<string, ulong> WorkerGenerationHighWater
        => WorkerHighWaters.ToDictionary(entry => entry.RegisterKey, entry => entry.Generation, StringComparer.Ordinal);

    [JsonIgnore]
    public IReadOnlyDictionary<string, Guid> WorkerSessionHighWater
        => WorkerHighWaters
            .Where(entry => entry.SessionId is not null)
            .ToDictionary(entry => entry.RegisterKey, entry => entry.SessionId!.Value, StringComparer.Ordinal);
}

public sealed record FcWorldStoreOptions(TimeSpan MaxFutureDelta)
{
    public static FcWorldStoreOptions Default { get; } = new(TimeSpan.FromMinutes(5));
}

public sealed class FcWorldStore
{
    private readonly FcRecordValidator _validator;
    private readonly FcHlcClock? _hlcClock;
    private readonly IFcClock _clock;
    private readonly FcWorldStoreOptions _options;
    private readonly Dictionary<string, PublishedListRecord> _lists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkerSessionRecord> _workers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChestSnapshotRecord> _chests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CapabilityRequestRecord> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CapabilityResponseRecord> _responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FcInventoryTransferRecord> _transfers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FcHlcTimestamp> _registerHlcs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _revisionHighWater = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _workerGenerationHighWater = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _workerSessionHighWater = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FcMeshRecord> _registerEnvelopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<string, FcForkVariant>> _forkVariants = new(StringComparer.Ordinal);
    private readonly HashSet<string> _forkedRegisters = new(StringComparer.Ordinal);
    private FcWorldRevision _revision = new(0, string.Empty);

    public FcWorldStore(
        IFcClock? clock = null,
        FcRecordValidator? validator = null,
        FcHlcClock? hlcClock = null,
        FcWorldStoreOptions? options = null,
        FcWorldPersistenceState? persisted = null)
    {
        _clock = clock ?? FcSystemClock.Instance;
        _validator = validator ?? new FcRecordValidator();
        _hlcClock = hlcClock;
        _options = options ?? FcWorldStoreOptions.Default;
        if (_options.MaxFutureDelta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (persisted is not null)
        {
            ValidatePersistenceState(persisted);
            foreach (var entry in persisted.RevisionHighWaters)
                _revisionHighWater[entry.RegisterKey] = entry.Revision;
            foreach (var entry in persisted.RegisterHlcHighWaters)
                _registerHlcs[entry.RegisterKey] = entry.Hlc;
            foreach (var entry in persisted.WorkerHighWaters)
            {
                _workerGenerationHighWater[entry.RegisterKey] = entry.Generation;
                if (entry.SessionId is { } sessionId)
                    _workerSessionHighWater[entry.RegisterKey] = sessionId;
            }
        }
        if (_hlcClock is not null && persisted?.HlcState is { } persistedHlc)
            _hlcClock.Observe(persistedHlc.Last, _clock);
    }

    public FcWorldRevision Revision => _revision;
    public FcWorldRevision WorldRevision => _revision;
    public FcWorldStoreOptions Options => _options;
    public IReadOnlyDictionary<string, ulong> RevisionHighWater => _revisionHighWater;

    /// <summary>
    /// Reserves an author/register revision before the physical record is put.
    /// Persisting the returned high-water state prevents reuse after a crash.
    /// </summary>
    public FcApplyResult ReserveRevision(string registerKey, ulong revision)
    {
        if (!IsSafeRegisterKey(registerKey) || revision == 0)
            return FcApplyResult.Reject(_revision, "Register key or reservation revision is invalid.");
        if (!CanUseRevision(registerKey, revision))
            return FcApplyResult.Reject(_revision, "Revision reservation reuses a persisted high-water value.");
        _revisionHighWater[registerKey] = revision;
        AdvanceRevision();
        return new FcApplyResult(FcApplyStatus.Accepted, "Revision reserved.", _revision);
    }

    public FcWorldPersistenceState ExportPersistenceState()
        => new(
            _revisionHighWater
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new FcRevisionHighWaterEntry(pair.Key, pair.Value))
                .ToArray(),
            _registerHlcs
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new FcRegisterHlcHighWaterEntry(pair.Key, pair.Value))
                .ToArray(),
            _workerGenerationHighWater
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new FcWorkerHighWaterEntry(
                    pair.Key,
                    pair.Value,
                    _workerSessionHighWater.TryGetValue(pair.Key, out var sessionId) ? sessionId : null))
                .ToArray(),
            _hlcClock?.ExportState());

    public IReadOnlyList<string> Diagnostics
        => _forkVariants
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"same-writer-fork:{pair.Key}:{string.Join(",", pair.Value.Keys)}")
            .ToArray();

    public IReadOnlySet<string> ForkedRegisters => _forkedRegisters;
    public IReadOnlyDictionary<string, PublishedListRecord> Lists => _lists;
    public IReadOnlyDictionary<string, WorkerSessionRecord> Workers => _workers;
    public IReadOnlyDictionary<string, ChestSnapshotRecord> Chests => _chests;
    public IReadOnlyDictionary<string, CapabilityRequestRecord> CapabilityRequests => _requests;
    public IReadOnlyDictionary<string, CapabilityResponseRecord> CapabilityResponses => _responses;
    public IReadOnlyDictionary<string, FcInventoryTransferRecord> Transfers => _transfers;
    public IReadOnlyDictionary<string, IReadOnlyList<FcForkVariant>> ForkVariants
        => _forkVariants.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<FcForkVariant>)pair.Value.Values.ToArray(),
            StringComparer.Ordinal);

    /// <summary>Payload-only application cannot invent native HLC/signature metadata.</summary>
    public FcApplyResult Apply(object record, string transportAuthorId)
        => FcApplyResult.Reject(_revision, "Native FcMeshRecord envelope is required.");

    /// <summary>Legacy test-double overload deliberately remains fail-closed.</summary>
    public FcApplyResult Apply(object record, string transportAuthorId, FcHlcTimestamp authoritativeHlc, ulong? generation = null)
        => FcApplyResult.Reject(_revision, "Native FcMeshRecord envelope is required.");

    public FcApplyResult Apply<T>(
        FcMeshRecord envelope,
        T payload,
        FcVerifiedMeshContext? verifiedContext = null)
    {
        var validation = ValidateEnvelopePayload(envelope, payload, verifiedContext);
        if (!validation.IsValid)
            return FcApplyResult.Reject(_revision, validation.Error);
        if (payload is not object nonNullPayload)
            return FcApplyResult.Reject(_revision, "Payload is null.");
        return ApplyPayload(nonNullPayload, envelope);
    }

    /// <summary>
    /// Applies an inventory transfer while reconstructing a native bootstrap snapshot.
    /// The signed transfer contains both after-states, but the snapshot may not contain
    /// the physical pre-transfer state. Validate all structural and authoritative
    /// invariants available from that envelope, then merge both nested registers as one
    /// invisible candidate set. Live transfers must continue through ApplyTransfer so
    /// their quantity delta is checked against the current preimage.
    /// </summary>
    internal FcApplyResult ApplySnapshotBootstrapTransfer(
        FcMeshRecord envelope,
        FcInventoryTransferRecord transfer,
        FcVerifiedMeshContext? verifiedContext = null)
    {
        var validation = ValidateEnvelopePayload(envelope, transfer, verifiedContext);
        if (!validation.IsValid)
            return FcApplyResult.Reject(_revision, validation.Error);
        return ApplySnapshotBootstrapTransferValidated(transfer, envelope);
    }

    private FcValidationResult ValidateEnvelopePayload<T>(
        FcMeshRecord envelope,
        T payload,
        FcVerifiedMeshContext? verifiedContext)
    {
        if (envelope is null)
            return FcValidationResult.Invalid("Envelope is null.");
        if (payload is null)
            return FcValidationResult.Invalid("Payload is null.");
        var future = CheckFutureDelta(envelope.Hlc);
        if (!future.IsValid)
            return future;
        var payloadFuture = payload switch
        {
            CapabilityRequestRecord request => CheckFutureDelta(request.ExpiresAt),
            CapabilityResponseRecord response => CheckFutureDelta(response.ExpiresAt),
            _ => FcValidationResult.Valid,
        };
        if (!payloadFuture.IsValid)
            return payloadFuture;
        try
        {
            return _validator.Validate(envelope, payload, verifiedContext);
        }
        catch (Exception exception)
        {
            return FcValidationResult.Invalid($"Record validation failed: {exception.Message}");
        }
    }

    public FcApplyResult ApplyEnvelope<T>(FcMeshRecord envelope, T payload, FcVerifiedMeshContext? verifiedContext = null)
        => Apply(envelope, payload, verifiedContext);

    public FcApplyResult Apply<T>(FcMeshRecord envelope, Func<byte[], T> deserialize, FcVerifiedMeshContext? verifiedContext = null)
    {
        if (envelope is null || deserialize is null)
            return FcApplyResult.Reject(_revision, "Envelope or deserializer is null.");
        if (envelope.Payload is null || envelope.Payload.Length > FcRecordValidator.MaxPayloadBytes)
            return FcApplyResult.Reject(_revision, "Envelope payload exceeds the configured size limit.");
        try
        {
            return Apply(envelope, deserialize(envelope.Payload), verifiedContext);
        }
        catch (Exception exception)
        {
            return FcApplyResult.Reject(_revision, $"Payload deserialization failed: {exception.Message}");
        }
    }

    public T? DeserializePayload<T>(FcMeshRecord envelope)
        => envelope is null ? default : JsonSerializer.Deserialize<T>(envelope.Payload, FcJsonContext.Default.Options);

    public FcHlcTimestamp? GetListHlc(string ownerAuthorId, Guid listId)
        => _registerHlcs.TryGetValue(ownerAuthorId + "/list/" + listId.ToString("D"), out var value) ? value : null;

    public FcHlcTimestamp? GetWorkerHlc(string ownerAuthorId)
        => _registerHlcs.TryGetValue(ownerAuthorId + "/worker", out var value) ? value : null;

    public FcHlcTimestamp? GetChestHlc(string ownerAuthorId)
        => _registerHlcs.TryGetValue(ownerAuthorId + "/chest", out var value) ? value : null;

    public IEnumerable<PublishedListRecord> PublishedListsFor(string ownerAuthorId)
        => _lists.Values.Where(value => value.Header.OwnerAuthorId == ownerAuthorId);

    public bool IsForked(string registerKey) => _forkedRegisters.Contains(registerKey);
    public bool IsWriteBlocked(string registerKey) => IsForked(registerKey);

    public IEnumerable<object> AllRecords()
        => _lists.Values.Cast<object>()
            .Concat(_workers.Values)
            .Concat(_chests.Values)
            .Concat(_requests.Values)
            .Concat(_responses.Values)
            .Concat(_transfers.Values);

    private FcValidationResult CheckFutureDelta(FcHlcTimestamp hlc)
    {
        if (hlc.PhysicalUnixMs < 0 || string.IsNullOrWhiteSpace(hlc.NodeId))
            return FcValidationResult.Invalid("Authoritative HLC is invalid.");
        var maxFuture = checked((long)_options.MaxFutureDelta.TotalMilliseconds);
        var latestAllowed = checked(_clock.UnixMilliseconds + maxFuture);
        return hlc.PhysicalUnixMs > latestAllowed
            ? FcValidationResult.Invalid("Authoritative HLC exceeds the configured maximum future delta; quarantined.")
            : FcValidationResult.Valid;
    }

    private static void ValidatePersistenceState(FcWorldPersistenceState state)
    {
        if (state.RevisionHighWaters is null
            || state.RegisterHlcHighWaters is null
            || state.WorkerHighWaters is null)
            throw new ArgumentException("Persistence high-water arrays are required.", nameof(state));

        var revisionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in state.RevisionHighWaters)
        {
            if (entry is null || !IsSafeRegisterKey(entry.RegisterKey) || entry.Revision == 0
                || !revisionKeys.Add(entry.RegisterKey))
                throw new ArgumentException("Persistence revision high-water state is invalid.", nameof(state));
        }

        var hlcKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in state.RegisterHlcHighWaters)
        {
            if (entry is null || !IsSafeRegisterKey(entry.RegisterKey)
                || entry.Hlc.PhysicalUnixMs < 0 || string.IsNullOrWhiteSpace(entry.Hlc.NodeId)
                || !hlcKeys.Add(entry.RegisterKey))
                throw new ArgumentException("Persistence register HLC state is invalid.", nameof(state));
        }

        var workerKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in state.WorkerHighWaters)
        {
            if (entry is null || !IsSafeRegisterKey(entry.RegisterKey) || entry.Generation == 0
                || !workerKeys.Add(entry.RegisterKey))
                throw new ArgumentException("Persistence worker high-water state is invalid.", nameof(state));
            if (entry.SessionId is { } sessionId && sessionId == Guid.Empty)
                throw new ArgumentException("Persistence worker session state is invalid.", nameof(state));
        }

        if (state.HlcState is { } hlcState
            && (hlcState.Last.PhysicalUnixMs < 0
                || string.IsNullOrWhiteSpace(hlcState.NodeId)
                || !string.Equals(hlcState.NodeId, hlcState.Last.NodeId, StringComparison.Ordinal)))
            throw new ArgumentException("Persistence local HLC state is invalid.", nameof(state));
    }

    private static bool IsSafeRegisterKey(string value)
        => !string.IsNullOrWhiteSpace(value)
            && !value.Contains('\0')
            && !value.Contains('\r')
            && !value.Contains('\n');

    private FcApplyResult ApplyPayload(object record, FcMeshRecord envelope)
        => record switch
        {
            PublishedListRecord value => ApplyRegister(value, ListKey(value), _lists, envelope),
            WorkerSessionRecord value => ApplyRegister(value, WorkerKey(value), _workers, envelope),
            ChestSnapshotRecord value => ApplyRegister(value, ChestKey(value), _chests, envelope),
            CapabilityRequestRecord value => ApplyRegister(value, RequestKey(value), _requests, envelope),
            CapabilityResponseRecord value => ApplyRegister(value, ResponseKey(value), _responses, envelope),
            FcInventoryTransferRecord value => ApplyTransfer(value, envelope),
            _ => FcApplyResult.Reject(_revision, "Unsupported record type.")
        };

    private FcApplyResult ApplyTransfer(FcInventoryTransferRecord record, FcMeshRecord envelope)
    {
        var transferKey = TransferKey(record);
        var transferHash = CandidateHash(envelope);
        if (_transfers.TryGetValue(transferKey, out var existingTransfer))
        {
            var existingHash = _registerEnvelopes.TryGetValue(transferKey, out var existingEnvelope)
                ? CandidateHash(existingEnvelope)
                : CandidateHash(existingTransfer);
            if (existingTransfer.Header.Revision == record.Header.Revision && existingHash == transferHash)
            {
                ObserveAccepted(envelope.Hlc);
                return new FcApplyResult(FcApplyStatus.Duplicate, "Duplicate transfer ignored.", _revision, _forkedRegisters.Contains(transferKey));
            }
            var repair = _forkedRegisters.Contains(transferKey)
                && record.Header.Revision > _forkVariants[transferKey].Values
                    .Select(variant => variant.Envelope.Revision)
                    .DefaultIfEmpty(existingTransfer.Header.Revision)
                    .Max();
            if (!repair)
                return RetainFork(transferKey, record, envelope, transferHash, "Conflicting operation ID blocks this transfer.");
            if (_registerHlcs.TryGetValue(transferKey, out var oldHlc) && envelope.Hlc < oldHlc)
                return FcApplyResult.Reject(_revision, "Repair transfer revision regresses HLC.");
            if (!CanUseRevision(transferKey, record.Header.Revision))
                return FcApplyResult.Reject(_revision, "Repair transfer revision reuses a persisted author high-water value.");
        }
        else if (!CanUseRevision(transferKey, record.Header.Revision))
        {
            return FcApplyResult.Reject(_revision, "Transfer revision reuses a persisted author high-water value.");
        }

        var workerKey = WorkerKey(record.WorkerAfter);
        var chestKey = ChestKey(record.ChestAfter);
        if (!_workers.TryGetValue(workerKey, out var beforeWorker)
            || !_chests.TryGetValue(chestKey, out var beforeChest))
            return FcApplyResult.Reject(_revision, "Atomic transfer requires current chest and worker state.");
        FcValidationResult physical;
        try
        {
            physical = ValidateTransferDelta(record, beforeChest, beforeWorker);
        }
        catch (OverflowException)
        {
            return FcApplyResult.Reject(_revision, "Atomic transfer quantity arithmetic overflowed.");
        }
        if (!physical.IsValid)
            return FcApplyResult.Reject(_revision, physical.Error);

        var workerDecision = PreviewCandidate(record.WorkerAfter, workerKey, beforeWorker, envelope.Hlc);
        var chestDecision = PreviewCandidate(record.ChestAfter, chestKey, beforeChest, envelope.Hlc);
        if (workerDecision.Status == CandidateStatus.Fork || chestDecision.Status == CandidateStatus.Fork)
            return RetainFork(transferKey, record, envelope, transferHash, "Transfer after-state has a same-writer fork.");
        if (workerDecision.Status is not (CandidateStatus.Accept or CandidateStatus.Duplicate)
            || chestDecision.Status is not (CandidateStatus.Accept or CandidateStatus.Duplicate))
            return FcApplyResult.Reject(_revision, "Transfer after-state cannot be applied atomically.");

        // All validation and both candidate decisions complete before the first
        // register is changed. This is the atomic semantic boundary.
        _transfers[transferKey] = record;
        _workers[workerKey] = record.WorkerAfter;
        _chests[chestKey] = record.ChestAfter;
        _registerHlcs[transferKey] = envelope.Hlc;
        _registerHlcs[workerKey] = envelope.Hlc;
        _registerHlcs[chestKey] = envelope.Hlc;
        _registerEnvelopes[transferKey] = CloneEnvelope(envelope);
        SetHighWater(transferKey, record.Header.Revision);
        SetHighWater(workerKey, record.WorkerAfter.Header.Revision);
        SetHighWater(chestKey, record.ChestAfter.Header.Revision);
        SetWorkerGenerationHighWater(workerKey, record.WorkerAfter);
        ClearFork(transferKey);
        ClearFork(workerKey);
        ClearFork(chestKey);
        ObserveAccepted(envelope.Hlc);
        AdvanceRevision();
        return new FcApplyResult(FcApplyStatus.Accepted, "Atomic transfer accepted.", _revision);
    }

    private FcApplyResult ApplySnapshotBootstrapTransferValidated(
        FcInventoryTransferRecord record,
        FcMeshRecord envelope)
    {
        var transferKey = TransferKey(record);
        var transferHash = CandidateHash(envelope);
        if (_transfers.TryGetValue(transferKey, out var existingTransfer))
        {
            var existingHash = _registerEnvelopes.TryGetValue(transferKey, out var existingEnvelope)
                ? CandidateHash(existingEnvelope)
                : CandidateHash(existingTransfer);
            if (existingTransfer.Header.Revision == record.Header.Revision && existingHash == transferHash)
            {
                if (_forkedRegisters.Contains(transferKey))
                    return new FcApplyResult(
                        FcApplyStatus.Fork,
                        "Forked transfer register remains write-blocked during snapshot bootstrap.",
                        _revision,
                        WriteBlocked: true);
                ObserveAccepted(envelope.Hlc);
                return new FcApplyResult(
                    FcApplyStatus.Duplicate,
                    "Duplicate snapshot transfer ignored.",
                    _revision,
                    _forkedRegisters.Contains(transferKey));
            }

            // A snapshot containing two different values for one operation is not
            // repairable without an authority. Fail closed; the coordinator discards
            // this invisible candidate and keeps the old world.
            return RetainFork(
                transferKey,
                record,
                envelope,
                transferHash,
                "Conflicting operation ID blocks this snapshot transfer.");
        }

        if (_forkedRegisters.Contains(transferKey))
            return new FcApplyResult(
                FcApplyStatus.Fork,
                "Forked transfer register remains write-blocked during snapshot bootstrap.",
                _revision,
                WriteBlocked: true);
        if (!CanUseRevision(transferKey, record.Header.Revision))
            return FcApplyResult.Reject(_revision, "Snapshot transfer revision reuses a persisted author high-water value.");

        try
        {
            // Validates quantity shape and arithmetic independently of the absent
            // physical preimage. Do not call ValidateTransferDelta here.
            _ = record.ActualTransferredMap;
        }
        catch (OverflowException)
        {
            return FcApplyResult.Reject(_revision, "Snapshot transfer quantity arithmetic overflowed.");
        }
        catch (ArgumentException exception)
        {
            return FcApplyResult.Reject(_revision, $"Snapshot transfer quantities are invalid: {exception.Message}");
        }
        if (record.WorkerAfter.Header.RecordId != record.WorkerAfter.SessionId)
            return FcApplyResult.Reject(_revision, "Snapshot transfer worker record identity does not match its session.");

        var workerKey = WorkerKey(record.WorkerAfter);
        var chestKey = ChestKey(record.ChestAfter);
        var beforeWorker = _workers.TryGetValue(workerKey, out var worker) ? worker : null;
        var beforeChest = _chests.TryGetValue(chestKey, out var chest) ? chest : null;
        // Nested after-states have no independent envelope. Their authoritative
        // HLC is the outer signed transfer HLC; each register still orders by its
        // nested author revision and rejects HLC regression.
        var workerDecision = PreviewSnapshotBootstrapCandidate(
            record.WorkerAfter,
            workerKey,
            beforeWorker,
            envelope.Hlc);
        var chestDecision = PreviewSnapshotBootstrapCandidate(
            record.ChestAfter,
            chestKey,
            beforeChest,
            envelope.Hlc);
        if (workerDecision.Status == SnapshotCandidateStatus.Fork
            || chestDecision.Status == SnapshotCandidateStatus.Fork)
            return new FcApplyResult(
                FcApplyStatus.Fork,
                "Snapshot transfer after-state has a same-writer fork.",
                _revision,
                WriteBlocked: true);
        if (workerDecision.Status == SnapshotCandidateStatus.Reject
            || chestDecision.Status == SnapshotCandidateStatus.Reject)
            return FcApplyResult.Reject(
                _revision,
                workerDecision.Status == SnapshotCandidateStatus.Reject
                    ? workerDecision.Message
                    : chestDecision.Message);

        // All structural validation and both per-register decisions complete before
        // the first mutation. The transfer operation and any accepted after-states
        // become visible together only when the shadow world is swapped.
        _transfers[transferKey] = record;
        _registerHlcs[transferKey] = envelope.Hlc;
        _registerEnvelopes[transferKey] = CloneEnvelope(envelope);
        SetHighWater(transferKey, record.Header.Revision);
        if (workerDecision.Status == SnapshotCandidateStatus.Accept)
        {
            _workers[workerKey] = record.WorkerAfter;
            _registerHlcs[workerKey] = envelope.Hlc;
            _registerEnvelopes.Remove(workerKey);
            SetHighWater(workerKey, record.WorkerAfter.Header.Revision);
            SetWorkerGenerationHighWater(workerKey, record.WorkerAfter);
            ClearFork(workerKey);
        }
        if (chestDecision.Status == SnapshotCandidateStatus.Accept)
        {
            _chests[chestKey] = record.ChestAfter;
            _registerHlcs[chestKey] = envelope.Hlc;
            _registerEnvelopes.Remove(chestKey);
            SetHighWater(chestKey, record.ChestAfter.Header.Revision);
            ClearFork(chestKey);
        }
        ClearFork(transferKey);
        ObserveAccepted(envelope.Hlc);
        AdvanceRevision();
        return new FcApplyResult(FcApplyStatus.Accepted, "Snapshot bootstrap transfer accepted.", _revision);
    }

    private static FcValidationResult ValidateTransferDelta(
        FcInventoryTransferRecord transfer,
        ChestSnapshotRecord beforeChest,
        WorkerSessionRecord beforeWorker)
    {
        var beforeChestItems = new FcItemQuantityMap(beforeChest.Items);
        var afterChestItems = new FcItemQuantityMap(transfer.ChestAfter.Items);
        var beforeWorkerItems = new FcItemQuantityMap(beforeWorker.HeldInventory);
        var afterWorkerItems = new FcItemQuantityMap(transfer.WorkerAfter.HeldInventory);
        var actual = transfer.ActualTransferredMap;
        if (!beforeChest.Crystals.Equals(transfer.ChestAfter.Crystals))
            return FcValidationResult.Invalid("Crystal transfer requires the separate crystal reconciliation path.");
        if (transfer.Kind == FcInventoryTransferKind.Withdraw)
        {
            if (actual.Entries.Any(entry => beforeChestItems.Get(entry.Key) < entry.Quantity))
                return FcValidationResult.Invalid("Withdraw exceeds current chest quantity.");
            var expectedChest = beforeChestItems.SubtractClamped(actual);
            var expectedWorker = beforeWorkerItems.Add(actual);
            if (!afterChestItems.Equals(expectedChest) || !afterWorkerItems.Equals(expectedWorker))
                return FcValidationResult.Invalid("Withdraw actual quantities do not match both observed after-states.");
        }
        else
        {
            if (actual.Entries.Any(entry => beforeWorkerItems.Get(entry.Key) < entry.Quantity))
                return FcValidationResult.Invalid("Deposit exceeds current worker quantity.");
            var expectedChest = beforeChestItems.Add(actual);
            var expectedWorker = beforeWorkerItems.SubtractClamped(actual);
            if (!afterChestItems.Equals(expectedChest) || !afterWorkerItems.Equals(expectedWorker))
                return FcValidationResult.Invalid("Deposit actual quantities do not match both observed after-states.");
        }
        return FcValidationResult.Valid;
    }

    private FcApplyResult ApplyRegister<T>(
        T candidate,
        string key,
        IDictionary<string, T> register,
        FcMeshRecord envelope)
        where T : class
    {
        var current = register.TryGetValue(key, out var value) ? value : null;
        // Snapshot bootstrap after-states have no independent envelope evidence.
        // When no exact register envelope exists, compare semantic payloads rather
        // than treating a same-revision standalone observation as a fork.
        var decision = PreviewCandidate(
            candidate,
            key,
            current,
            envelope.Hlc,
            _registerEnvelopes.ContainsKey(key) ? envelope : null);
        if (decision.Status == CandidateStatus.Reject)
            return decision.Fork
                ? new FcApplyResult(FcApplyStatus.Fork, decision.Message, _revision, WriteBlocked: true)
                : FcApplyResult.Reject(_revision, decision.Message);
        if (decision.Status == CandidateStatus.Duplicate)
        {
            ObserveAccepted(envelope.Hlc);
            return new FcApplyResult(FcApplyStatus.Duplicate, decision.Message, _revision, _forkedRegisters.Contains(key));
        }
        if (decision.Status == CandidateStatus.Fork)
            return RetainFork(key, candidate, envelope, CandidateHash(envelope), decision.Message);

        register[key] = candidate;
        _registerHlcs[key] = envelope.Hlc;
        _registerEnvelopes[key] = CloneEnvelope(envelope);
        SetHighWater(key, GetHeader(candidate)!.Revision);
        SetWorkerGenerationHighWater(key, candidate);
        ClearFork(key);
        ObserveAccepted(envelope.Hlc);
        AdvanceRevision();
        return new FcApplyResult(FcApplyStatus.Accepted, decision.Message, _revision);
    }

    private SnapshotCandidateDecision PreviewSnapshotBootstrapCandidate<T>(
        T candidate,
        string key,
        T? current,
        FcHlcTimestamp candidateHlc)
        where T : class
    {
        var candidateHeader = GetHeader(candidate);
        if (candidateHeader is null)
            return SnapshotCandidateDecision.Reject("Snapshot register candidate has no header.");
        if (_forkedRegisters.Contains(key))
            return SnapshotCandidateDecision.Forked("Forked snapshot register remains write-blocked.");

        if (current is null)
        {
            if (_registerHlcs.TryGetValue(key, out var persistedHlc) && candidateHlc < persistedHlc)
                return SnapshotCandidateDecision.Reject("Snapshot register HLC regresses persisted high-water state.");
            if (candidate is WorkerSessionRecord persistedWorker
                && _workerGenerationHighWater.TryGetValue(key, out var persistedGeneration)
                && (persistedWorker.SessionGeneration < persistedGeneration
                    || (persistedWorker.SessionGeneration == persistedGeneration
                        && _workerSessionHighWater.TryGetValue(key, out var persistedSession)
                        && persistedWorker.SessionId != persistedSession)))
                return SnapshotCandidateDecision.Reject("Snapshot worker session identity or generation regresses persisted high-water state.");
            return CanUseRevision(key, candidateHeader.Revision)
                ? SnapshotCandidateDecision.Accept("New snapshot register accepted.")
                : SnapshotCandidateDecision.Reject("Snapshot register revision reuses a persisted author high-water value.");
        }

        var currentHeader = GetHeader(current);
        if (currentHeader is null)
            return SnapshotCandidateDecision.Reject("Current snapshot register has no header.");
        if (candidateHeader.Revision < currentHeader.Revision)
            return SnapshotCandidateDecision.Superseded("Older nested after-state was superseded by the snapshot register.");
        var candidateHash = CandidateHash(candidate);
        var currentHash = CandidateHash(current);
        if (candidateHeader.Revision == currentHeader.Revision)
            return candidateHash == currentHash
                ? SnapshotCandidateDecision.Duplicate("Duplicate nested after-state ignored.")
                : SnapshotCandidateDecision.Forked("Same-writer nested after-state fork blocks the snapshot.");

        var currentHlc = _registerHlcs.GetValueOrDefault(key);
        if (currentHlc != default && candidateHlc < currentHlc)
            return SnapshotCandidateDecision.Reject("Newer nested after-state regresses the register HLC.");
        if (!CanUseRevision(key, candidateHeader.Revision))
            return SnapshotCandidateDecision.Reject("Snapshot register revision reuses a persisted author high-water value.");
        if (candidate is WorkerSessionRecord worker && current is WorkerSessionRecord oldWorker
            && ((worker.SessionGeneration == oldWorker.SessionGeneration
                    && worker.SessionId != oldWorker.SessionId)
                || worker.SessionGeneration < oldWorker.SessionGeneration
                || (_workerGenerationHighWater.TryGetValue(key, out var generationHighWater)
                    && worker.SessionGeneration < generationHighWater)))
            return SnapshotCandidateDecision.Reject("Snapshot worker session identity or generation regresses.");
        return SnapshotCandidateDecision.Accept("Newer nested after-state accepted.");
    }

    private CandidateDecision PreviewCandidate<T>(
        T candidate,
        string key,
        T? current,
        FcHlcTimestamp candidateHlc,
        FcMeshRecord? candidateEnvelope = null)
        where T : class
    {
        var candidateHeader = GetHeader(candidate)!;
        if (_forkedRegisters.Contains(key))
        {
            var maximumForkRevision = _forkVariants[key].Values
                .Select(variant => variant.Envelope.Revision)
                .DefaultIfEmpty(candidateHeader.Revision)
                .Max();
            if (candidateHeader.Revision <= maximumForkRevision)
                return CandidateDecision.Forked("Forked register remains write-blocked until a strictly greater repair revision.");
        }
        if (current is null)
        {
            if (_registerHlcs.TryGetValue(key, out var persistedHlc) && candidateHlc < persistedHlc)
                return CandidateDecision.Reject("Register HLC regresses persisted high-water state.");
            if (candidate is WorkerSessionRecord persistedWorker
                && _workerGenerationHighWater.TryGetValue(key, out var persistedGeneration)
                && (persistedWorker.SessionGeneration < persistedGeneration
                    || (persistedWorker.SessionGeneration == persistedGeneration
                        && _workerSessionHighWater.TryGetValue(key, out var persistedSession)
                        && persistedWorker.SessionId != persistedSession)))
                return CandidateDecision.Reject("Worker session identity or generation regresses persisted high-water state.");
            return CanUseRevision(key, candidateHeader.Revision)
                ? CandidateDecision.Accept("New register accepted.")
                : CandidateDecision.Reject("Register revision reuses a persisted author high-water value.");
        }

        var currentHeader = GetHeader(current)!;
        var candidateHash = candidateEnvelope is null
            ? CandidateHash(candidate)
            : CandidateHash(candidateEnvelope);
        var currentHash = candidateEnvelope is null
            ? CandidateHash(current)
            : _registerEnvelopes.TryGetValue(key, out var currentEnvelope)
                ? CandidateHash(currentEnvelope)
                : CandidateHash(current);
        if (candidateHeader.Revision < currentHeader.Revision)
            return CandidateDecision.Reject("Older register revision ignored.");
        if (candidateHeader.Revision == currentHeader.Revision)
        {
            if (candidateHash == currentHash)
                return CandidateDecision.Duplicate("Duplicate register ignored.");
            return CandidateDecision.Forked("Same-writer fork blocks this register.");
        }

        var currentHlc = _registerHlcs.GetValueOrDefault(key);
        if (currentHlc != default && candidateHlc < currentHlc)
            return CandidateDecision.Reject("Newer register revision regresses HLC.");
        if (!CanUseRevision(key, candidateHeader.Revision))
            return CandidateDecision.Reject("Register revision reuses a persisted author high-water value.");
        if (candidate is WorkerSessionRecord worker && current is WorkerSessionRecord oldWorker
            && ((worker.SessionGeneration == oldWorker.SessionGeneration
                    && worker.SessionId != oldWorker.SessionId)
                || worker.SessionGeneration < oldWorker.SessionGeneration
                || (_workerGenerationHighWater.TryGetValue(key, out var generationHighWater)
                    && worker.SessionGeneration < generationHighWater)))
            return CandidateDecision.Reject("Worker session identity or generation regresses.");
        return CandidateDecision.Accept("Newer register revision accepted.");
    }

    private FcApplyResult RetainFork<T>(string key, T candidate, FcMeshRecord envelope, string candidateHash, string message)
        where T : class
    {
        var evidence = _forkVariants.GetValueOrDefault(key)
            ?? new SortedDictionary<string, FcForkVariant>(StringComparer.Ordinal);
        var changed = false;
        if (!evidence.ContainsKey(candidateHash))
        {
            var retainedEnvelope = CloneEnvelope(envelope);
            evidence[candidateHash] = new FcForkVariant(key, candidateHash, retainedEnvelope, retainedEnvelope.Payload.ToArray());
            changed = true;
        }
        if (!_forkedRegisters.Contains(key) && _registerEnvelopes.TryGetValue(key, out var currentEnvelope))
        {
            var current = FindCurrent(key);
            if (current is not null)
            {
                var currentHash = CandidateHash(currentEnvelope);
                if (!evidence.ContainsKey(currentHash))
                {
                    var retainedEnvelope = CloneEnvelope(currentEnvelope);
                    evidence[currentHash] = new FcForkVariant(key, currentHash, retainedEnvelope, retainedEnvelope.Payload.ToArray());
                    changed = true;
                }
            }
        }
        _forkVariants[key] = evidence;
        changed |= _forkedRegisters.Add(key);
        SetWorkerGenerationHighWater(key, candidate, updateSession: false);
        if (!_registerHlcs.TryGetValue(key, out var forkHlc) || envelope.Hlc > forkHlc)
            _registerHlcs[key] = envelope.Hlc;
        ObserveAccepted(envelope.Hlc);
        if (changed)
            AdvanceRevision();
        return new FcApplyResult(FcApplyStatus.Fork, message, _revision, WriteBlocked: true);
    }

    private object? FindCurrent(string key)
    {
        if (_lists.TryGetValue(key, out var list))
            return list;
        if (_workers.TryGetValue(key, out var worker))
            return worker;
        if (_chests.TryGetValue(key, out var chest))
            return chest;
        if (_requests.TryGetValue(key, out var request))
            return request;
        if (_responses.TryGetValue(key, out var response))
            return response;
        if (_transfers.TryGetValue(key, out var transfer))
            return transfer;
        return null;
    }

    private void ClearFork(string key)
    {
        _forkedRegisters.Remove(key);
        _forkVariants.Remove(key);
    }

    private void ObserveAccepted(FcHlcTimestamp authoritativeHlc)
        => _hlcClock?.Observe(authoritativeHlc, _clock);

    private bool CanUseRevision(string key, ulong revision)
        => !_revisionHighWater.TryGetValue(key, out var highWater) || revision > highWater;

    private void SetHighWater(string key, ulong revision)
    {
        if (!_revisionHighWater.TryGetValue(key, out var current) || revision > current)
            _revisionHighWater[key] = revision;
    }

    private void SetWorkerGenerationHighWater<T>(string key, T record, bool updateSession = true)
        where T : class
    {
        if (record is not WorkerSessionRecord worker)
            return;
        if (!_workerGenerationHighWater.TryGetValue(key, out var current)
            || worker.SessionGeneration > current)
        {
            _workerGenerationHighWater[key] = worker.SessionGeneration;
            if (updateSession)
                _workerSessionHighWater[key] = worker.SessionId;
        }
        else if (updateSession && worker.SessionGeneration == current)
        {
            _workerSessionHighWater[key] = worker.SessionId;
        }
    }

    private void AdvanceRevision()
    {
        var hashes = AllRecords()
            .Where(value => !IsRecordForked(value))
            .Select(value => FcCanonical.SemanticHash(value))
            .OrderBy(hash => hash, StringComparer.Ordinal)
            .Concat(_registerHlcs
                .Where(pair => !_forkedRegisters.Contains(pair.Key))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"hlc:{pair.Key}:{pair.Value.PhysicalUnixMs}:{pair.Value.Logical}:{pair.Value.NodeId}"))
            .Concat(_revisionHighWater
                .Where(pair => !_forkedRegisters.Contains(pair.Key))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"revision:{pair.Key}:{pair.Value}"))
            .Concat(_workerGenerationHighWater
                .Where(pair => !_forkedRegisters.Contains(pair.Key))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"generation:{pair.Key}:{pair.Value}"))
            .Concat(_workerSessionHighWater
                .Where(pair => !_forkedRegisters.Contains(pair.Key))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"session:{pair.Key}:{pair.Value:D}"))
            .Concat(_forkVariants
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => "fork:" + pair.Key + ":" + string.Join(",", pair.Value.Keys)))
            .ToArray();
        _revision = new FcWorldRevision(
            checked(_revision.Number + 1),
            HashBytes(Encoding.UTF8.GetBytes(string.Join("\n", hashes))));
    }

    private static FcRecordHeader? GetHeader<T>(T record)
        => record switch
        {
            PublishedListRecord value => value.Header,
            WorkerSessionRecord value => value.Header,
            ChestSnapshotRecord value => value.Header,
            CapabilityRequestRecord value => value.Header,
            CapabilityResponseRecord value => value.Header,
            FcInventoryTransferRecord value => value.Header,
            _ => null,
        };

    private static string CandidateHash<T>(T value)
        => FcCanonical.SemanticHash(value);

    private static string CandidateHash(FcMeshRecord envelope)
        => FcCanonical.SemanticHash(envelope);

    private static FcMeshRecord CloneEnvelope(FcMeshRecord envelope)
        => envelope with
        {
            Payload = envelope.Payload.ToArray(),
            OriginalEnvelopeBytes = envelope.OriginalEnvelopeBytes,
        };

    private bool IsRecordForked(object value)
        => value switch
        {
            PublishedListRecord list => IsForked(ListKey(list)),
            WorkerSessionRecord worker => IsForked(WorkerKey(worker)),
            ChestSnapshotRecord chest => IsForked(ChestKey(chest)),
            CapabilityRequestRecord request => IsForked(RequestKey(request)),
            CapabilityResponseRecord response => IsForked(ResponseKey(response)),
            FcInventoryTransferRecord transfer => IsForked(TransferKey(transfer)),
            _ => false,
        };

    private static string ListKey(PublishedListRecord value) => value.Header.OwnerAuthorId + "/list/" + value.ListId.ToString("D");
    private static string WorkerKey(WorkerSessionRecord value) => value.Header.OwnerAuthorId + "/worker";
    private static string ChestKey(ChestSnapshotRecord value) => value.Header.OwnerAuthorId + "/chest";
    private static string RequestKey(CapabilityRequestRecord value) => value.Header.OwnerAuthorId + "/request/" + value.RequestId.ToString("D");
    private static string ResponseKey(CapabilityResponseRecord value) => value.Header.OwnerAuthorId + "/response/" + value.RequestId.ToString("D");
    private static string TransferKey(FcInventoryTransferRecord value) => value.Header.OwnerAuthorId + "/transfer/" + value.OperationId.ToString("D");

    private static string HashBytes(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private enum CandidateStatus
    {
        Accept,
        Duplicate,
        Reject,
        Fork,
    }

    private enum SnapshotCandidateStatus
    {
        Accept,
        Duplicate,
        Superseded,
        Reject,
        Fork,
    }

    private sealed record CandidateDecision(CandidateStatus Status, string Message, bool Fork = false)
    {
        public static CandidateDecision Accept(string message) => new(CandidateStatus.Accept, message);
        public static CandidateDecision Reject(string message) => new(CandidateStatus.Reject, message);
        public static CandidateDecision Forked(string message) => new(CandidateStatus.Fork, message, true);
        public static CandidateDecision Duplicate(string message) => new(CandidateStatus.Duplicate, message);
    }

    private sealed record SnapshotCandidateDecision(SnapshotCandidateStatus Status, string Message)
    {
        public static SnapshotCandidateDecision Accept(string message) => new(SnapshotCandidateStatus.Accept, message);
        public static SnapshotCandidateDecision Duplicate(string message) => new(SnapshotCandidateStatus.Duplicate, message);
        public static SnapshotCandidateDecision Superseded(string message) => new(SnapshotCandidateStatus.Superseded, message);
        public static SnapshotCandidateDecision Reject(string message) => new(SnapshotCandidateStatus.Reject, message);
        public static SnapshotCandidateDecision Forked(string message) => new(SnapshotCandidateStatus.Fork, message);
    }
}
