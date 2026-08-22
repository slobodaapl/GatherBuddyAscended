using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Native;

public sealed record FcMeshNativeDiagnostics(
    string Lifecycle,
    string GroupState,
    ulong LastEventSequence,
    ulong WorldEpoch,
    int PendingEvents,
    bool SnapshotInProgress,
    ulong SnapshotBaseEventSequence,
    bool AutomationDecisionsAllowed,
    string LastError)
{
    public FcMeshReadinessState ReadinessState { get; init; }
    public string? ContactedPeer { get; init; }
    public ulong SyncEventSequence { get; init; }
    public ulong SyncRecordCount { get; init; }
    public bool GroupMetadataCompatible { get; init; }
}

/// <summary>
/// Managed owner of the nonblocking native mesh service. The coordinator is
/// deliberately injectable: production uses the P/Invoke API, while tests
/// drive the same event/snapshot state machine with a fake API.
/// </summary>
public sealed class FcMeshNativeCoordinator : IDisposable
{
    private const int DefaultMaxEventsPerTick = 64;
    private const int MaxSnapshotRecords = 100_000;
    private readonly object _gate = new();
    private readonly IFcMeshNativeApi _api;
    private readonly Func<FcWorldStore> _worldStoreFactory;
    private readonly int _maxEventsPerTick;
    private readonly TimeSpan _statusInterval;
    private readonly Func<FcWorldStore, bool> _groupMetadataValidator;
    private FcWorldStore _worldStore;
    private FcMeshSafeHandle? _handle;
    private SnapshotSession? _snapshot;
    private readonly List<FcMeshNativeEvent> _queuedDuringSnapshot = new();
    private ulong _nextSnapshotRequestId;
    private ulong _lastEventSequence;
    private ulong _worldEpoch;
    private bool _decisionsPaused = true;
    private bool _disposed;
    private DateTime _lastStatusReadUtc;
    private FcMeshNativeStatus? _status;
    private string _lifecycle = "Not created";
    private string _groupState = "Not joined";
    private string _lastError = string.Empty;
    private FcMeshReadinessState _readinessState = FcMeshReadinessState.NotCreated;
    private string? _contactedPeer;
    private string? _localAuthorId;
    private string? _namespaceId;
    private string? _groupTicket;
    private string? _ticketForDiagnostics;
    private ulong _syncEventSequence;
    private ulong _syncEpoch;
    private ulong _syncRecordCount;
    private ulong _lastSnapshotBaseEventSequence;
    private bool _groupMetadataCompatible;
    private bool _hasInitialSyncSignal;
    private bool _requiresInitialSync;
    private bool _requiresManagedGroupMetadata;
    private byte[]? _pendingGroupMetadataPayload;

    internal FcMeshNativeCoordinator(
        IFcMeshNativeApi api,
        FcWorldStore? worldStore = null,
        Func<FcWorldStore>? worldStoreFactory = null,
        int maxEventsPerTick = DefaultMaxEventsPerTick,
        TimeSpan? statusInterval = null,
        Func<FcWorldStore, bool>? groupMetadataValidator = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _worldStore = worldStore ?? new FcWorldStore();
        _worldStoreFactory = worldStoreFactory ?? (() => new FcWorldStore());
        _maxEventsPerTick = maxEventsPerTick > 0
            ? maxEventsPerTick
            : throw new ArgumentOutOfRangeException(nameof(maxEventsPerTick));
        _statusInterval = statusInterval ?? TimeSpan.FromSeconds(1);
        _groupMetadataValidator = groupMetadataValidator ?? DefaultGroupMetadataValidator;
    }

    public FcWorldStore WorldStore
    {
        get
        {
            lock (_gate)
                return _worldStore;
        }
    }

    public bool AutomationDecisionsAllowed
    {
        get
        {
            lock (_gate)
                return _snapshot is null
                    && _readinessState == FcMeshReadinessState.Ready
                    && !_disposed
                    && !_decisionsPaused;
        }
    }

    public string? LocalAuthorId
    {
        get
        {
            lock (_gate)
                return _localAuthorId;
        }
    }

    public FcMeshReadiness Readiness
    {
        get
        {
            lock (_gate)
                return new(
                    _readinessState,
                    _readinessState == FcMeshReadinessState.Ready
                        && _snapshot is null
                        && !_disposed
                        && !_decisionsPaused,
                    _contactedPeer,
                    _syncEpoch == 0 ? _worldEpoch : _syncEpoch,
                    _syncEventSequence,
                    _snapshot?.BaseEventSequence ?? _lastSnapshotBaseEventSequence,
                    _syncRecordCount,
                    _groupMetadataCompatible,
                    _namespaceId,
                    _groupTicket,
                    _lastError);
        }
    }

    public FcMeshNativeStatus? Status
    {
        get
        {
            lock (_gate)
            return _status?.DeepCopy();
        }
    }

    public FcMeshNativeDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
                return new(
                    _lifecycle,
                    _groupState,
                    _lastEventSequence,
                    _worldEpoch,
                    _queuedDuringSnapshot.Count,
                    _snapshot is not null,
                    _snapshot?.BaseEventSequence ?? _lastSnapshotBaseEventSequence,
                    _snapshot is null
                        && _readinessState == FcMeshReadinessState.Ready
                        && !_disposed
                        && !_decisionsPaused,
                    _lastError)
                {
                    ReadinessState = _readinessState,
                    ContactedPeer = _contactedPeer,
                    SyncEventSequence = _syncEventSequence,
                    SyncRecordCount = _syncRecordCount,
                    GroupMetadataCompatible = _groupMetadataCompatible,
                };
        }
    }

    /// <summary>
    /// A mesh cannot be started without the current character identity. The
    /// keyed overload below is the only production startup path; retaining
    /// this overload makes an identity-less call fail closed rather than
    /// accidentally reopening a persisted group under the native default.
    /// </summary>
    public FcNativeCallResult Start(FcNativeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_gate)
            return _disposed
                ? Failure(FcNativeErrorCode.Closing, "Mesh coordinator is disposed.")
                : FailAndPause(FcNativeErrorCode.InvalidState, "Character identity is required before mesh startup.");
    }

    /// <summary>
    /// Creates the native handle, queues character-author selection, then
    /// queues Start. The native command channel preserves this order, so the
    /// selected author is active before startup restores any persisted group.
    /// </summary>
    public FcNativeCallResult Start(
        FcNativeConfiguration configuration,
        ReadOnlySpan<byte> characterKey)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var keyCopy = characterKey.ToArray();
        lock (_gate)
        {
            if (_disposed)
                return Failure(FcNativeErrorCode.Closing, "Mesh coordinator is disposed.");
            if (keyCopy.Length == 0)
                return FailAndPause(FcNativeErrorCode.InvalidArgument, "Character identity is required before mesh startup.");
            if (_handle is not null)
                return Success();
            try
            {
                if (_api.AbiVersion() != FcNativeAbi.Version)
                    return FailAndPause(FcNativeErrorCode.AbiMismatch, "Unsupported GatherBuddy mesh ABI version.");
                var result = _api.Create(configuration.ToJsonUtf8(), out var nativeHandle);
                if (!result.Succeeded || nativeHandle == 0)
                    return FailAndPause(result, "Native mesh service creation failed.");
                try
                {
                    _handle = new FcMeshSafeHandle(nativeHandle, DestroyNativeHandle);
                }
                catch (Exception exception)
                {
                    _ = _api.Destroy(nativeHandle);
                    return FailAndPause(FcNativeErrorCode.Internal, exception.Message);
                }
                _lifecycle = "Created";
                _readinessState = FcMeshReadinessState.Created;
                _groupMetadataCompatible = false;
                _requiresManagedGroupMetadata = false;

                result = _api.SetCharacterAuthor(nativeHandle, keyCopy);
                if (!result.Succeeded)
                {
                    _handle.Dispose();
                    _handle = null;
                    return FailAndPause(result, "Native character author selection failed before mesh startup.");
                }

                result = _api.Start(nativeHandle);
                if (!result.Succeeded)
                {
                    _handle.Dispose();
                    _handle = null;
                    return FailAndPause(result, "Native mesh service start failed.");
                }
                _lifecycle = "Starting";
                _decisionsPaused = true;
                _lastError = string.Empty;
                return result;
            }
            catch (Exception exception)
            {
                if (_handle is not null)
                {
                    try { _handle.Dispose(); }
                    catch (Exception disposeException) { SetLastError(disposeException.Message); }
                    _handle = null;
                }
                return FailAndPause(FcNativeErrorCode.Internal, exception.Message);
            }
        }
    }

    public FcNativeCallResult CreateGroup()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _readinessState = FcMeshReadinessState.Joining;
                _requiresInitialSync = false;
                _requiresManagedGroupMetadata = true;
                _hasInitialSyncSignal = false;
                _groupMetadataCompatible = false;
                _pendingGroupMetadataPayload = null;
                _namespaceId = null;
                _groupTicket = null;
                _decisionsPaused = true;
            }
        }
        return Invoke(handle => _api.CreateGroup(handle), "Create group command failed.");
    }

    public FcNativeCallResult JoinGroup(ReadOnlySpan<byte> ticket)
    {
        var ticketCopy = ticket.ToArray();
        lock (_gate)
        {
            _ticketForDiagnostics = ticketCopy.Length == 0
                ? null
                : Encoding.UTF8.GetString(ticketCopy);
            if (!_disposed)
            {
                _readinessState = FcMeshReadinessState.Joining;
                _requiresInitialSync = true;
                _requiresManagedGroupMetadata = true;
                _hasInitialSyncSignal = false;
                _groupMetadataCompatible = false;
                _pendingGroupMetadataPayload = null;
                _namespaceId = null;
                _groupTicket = null;
                _decisionsPaused = true;
            }
        }
        return Invoke(handle => _api.JoinGroup(handle, ticketCopy), "Join group command failed.");
    }

    public FcNativeCallResult LeaveGroup()
        => Invoke(handle => _api.LeaveGroup(handle), "Leave group command failed.");

    public FcNativeCallResult SetCharacterAuthor(ReadOnlySpan<byte> key)
    {
        var keyCopy = key.ToArray();
        return Invoke(handle => _api.SetCharacterAuthor(handle, keyCopy), "Set character author command failed.");
    }

    public FcNativeCallResult Put(
        ReadOnlySpan<byte> recordId,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> recordType,
        bool hasGeneration,
        ulong generation,
        ulong revision,
        ReadOnlySpan<byte> payload)
    {
        var recordIdCopy = recordId.ToArray();
        var keyCopy = key.ToArray();
        var recordTypeCopy = recordType.ToArray();
        var payloadCopy = payload.ToArray();
        return Invoke(
            handle => _api.Put(handle, recordIdCopy, keyCopy, recordTypeCopy, hasGeneration, generation, revision, payloadCopy),
            "Put command failed.",
            requireReady: true);
    }

    /// <summary>
    /// Processes a bounded number of native events and snapshot records. All
    /// calls are nonblocking ABI polls; no framework-thread disk/network work
    /// is performed here.
    /// </summary>
    public void Tick(TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero)
            return;
        var stopwatch = Stopwatch.StartNew();
        lock (_gate)
        {
            if (_disposed || _handle is null)
                return;
            try
            {
                for (var index = 0; index < _maxEventsPerTick && stopwatch.Elapsed < budget; index++)
                {
                    if (_disposed || _handle is null)
                        break;
                    var result = _api.PollEvent(_handle.Value, out var value);
                    if (!result.Succeeded)
                    {
                        if (result.ErrorCode == FcNativeErrorCode.NoEvent)
                            break;
                        FailAndPause(result, "Native event poll failed.");
                        break;
                    }
                    if (value is null)
                    {
                        FailAndPause(FcNativeErrorCode.Internal, "Native event poll returned no event.");
                        break;
                    }
                    if (_disposed || _handle is null)
                        break;
                    ProcessEvent(value);
                }

                if (_disposed || _handle is null)
                    return;
                OpenPendingSnapshot();
                PollSnapshot(stopwatch, budget);
                if (_disposed || _handle is null)
                    return;
                if (stopwatch.Elapsed < budget && DateTime.UtcNow - _lastStatusReadUtc >= _statusInterval)
                {
                    var statusResult = _api.GetStatus(_handle.Value, out var status);
                    _lastStatusReadUtc = DateTime.UtcNow;
                    if (statusResult.Succeeded && status is not null)
                    {
                        _status = status;
                        _worldEpoch = Math.Max(_worldEpoch, status.WorldEpoch);
                        _lifecycle = status.Lifecycle.ToString();
                        if (status.LastErrorId != 0)
                        {
                            var statusError = _api.GetErrorMessage(status.LastErrorId);
                            SetLastError(string.IsNullOrWhiteSpace(statusError)
                                ? "Native service reported an error."
                                : statusError);
                            _decisionsPaused = true;
                        }
                    }
                    else if (!statusResult.Succeeded)
                    {
                        FailAndPause(statusResult, "Native status read failed.");
                    }
                }
            }
            catch (Exception exception)
            {
                FailAndPause(FcNativeErrorCode.Internal, exception.Message);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _decisionsPaused = true;
            _readinessState = FcMeshReadinessState.Left;
            if (_snapshot is { Handle: not 0 } snapshot)
            {
                try { _ = _api.DestroySnapshot(snapshot.Handle); }
                catch (Exception exception) { SetLastError(exception.Message); }
            }
            _snapshot = null;
            _queuedDuringSnapshot.Clear();
            try
            {
                if (_handle is not null)
                {
                    _lifecycle = "Closing";
                    try
                    {
                        _ = _api.Shutdown(_handle.Value, 250);
                    }
                    catch (Exception exception)
                    {
                        SetLastError(exception.Message);
                    }
                    finally
                    {
                        _handle.Dispose();
                        _handle = null;
                    }
                }
            }
            catch (Exception exception)
            {
                SetLastError(exception.Message);
            }
            finally
            {
                try { _api.Dispose(); }
                catch (Exception exception) { SetLastError(exception.Message); }
                _lifecycle = "Disposed";
            }
        }
    }

    private FcNativeCallResult Invoke(
        Func<ulong, FcNativeCallResult> callback,
        string failureMessage,
        bool requireReady = false)
    {
        lock (_gate)
        {
            if (_disposed || _handle is null)
                return Failure(FcNativeErrorCode.InvalidHandle, "Mesh service is not running.");
            if (requireReady
                && (_readinessState != FcMeshReadinessState.Ready
                    || _snapshot is not null
                    || _decisionsPaused))
                return Failure(FcNativeErrorCode.InvalidState, "FC mesh is not ready for publication.");
            try
            {
                var result = callback(_handle.Value);
                if (!result.Succeeded)
                    RecordError(result, failureMessage);
                return result;
            }
            catch (Exception exception)
            {
                return FailAndPause(FcNativeErrorCode.Internal, exception.Message);
            }
        }
    }

    private FcNativeCallResult DestroyNativeHandle(ulong handle)
    {
        try { return _api.Destroy(handle); }
        catch (Exception exception)
        {
            SetLastError(exception.Message);
            return Failure(FcNativeErrorCode.Internal, exception.Message);
        }
    }

    private bool TryApplyStartedAuthor(FcMeshNativeEvent value)
    {
        if (value.ActualAuthor.Length == 0)
            return true;
        if (value.ActualAuthor.Length != 32)
        {
            FailAndPause(FcNativeErrorCode.InvalidRecord, "Native Started event author is malformed.");
            return false;
        }

        _localAuthorId = Convert.ToHexString(value.ActualAuthor).ToLowerInvariant();
        return true;
    }

    private void ProcessEvent(FcMeshNativeEvent value)
    {
        if (value.ProtocolVersion != FcProtocolVersion.Current)
        {
            FailAndPause(FcNativeErrorCode.InvalidRecord, "Native event protocol version is unsupported.");
            return;
        }
        _worldEpoch = Math.Max(_worldEpoch, value.WorldEpoch);
        if (value.Sequence == 0)
        {
            FailAndPause(FcNativeErrorCode.InvalidRecord, "Native event sequence is zero.");
            return;
        }
        if (value.Sequence <= _lastEventSequence)
            return;
        _lastEventSequence = Math.Max(_lastEventSequence, value.Sequence);

        if (value.Kind == FcNativeEventKind.WorldInvalidated)
        {
            if (InvalidateInitialSyncEvidence("Initial synchronization was invalidated before the managed snapshot became ready."))
                return;
            BeginSnapshot(
                value.WorldEpoch == 0 ? value.Aux : value.WorldEpoch,
                value.Sequence,
                restorePriorReadiness: true);
            return;
        }
        if (_snapshot is not null
            && value.Sequence > _snapshot.InvalidationSequence
            && value.Kind is not (FcNativeEventKind.InitialSyncCompleted or FcNativeEventKind.Joined))
        {
            if (value.Kind == FcNativeEventKind.RecordRemoved
                && InvalidateInitialSyncEvidence("Initial synchronization was invalidated before the managed snapshot became ready."))
                return;
            if (value.Kind == FcNativeEventKind.SnapshotReady)
            {
                HandleSnapshotReady(value);
                return;
            }
            if (_queuedDuringSnapshot.Count >= MaxSnapshotRecords)
            {
                RetrySnapshot("Native event replay queue exceeded the managed record limit.");
                return;
            }
            _queuedDuringSnapshot.Add(value.DeepCopy());
            return;
        }

        switch (value.Kind)
        {
            case FcNativeEventKind.Started:
                _lifecycle = "Starting";
                TryApplyStartedAuthor(value);
                break;
            case FcNativeEventKind.Stopped:
                _lifecycle = "Stopped";
                _decisionsPaused = true;
                _readinessState = FcMeshReadinessState.Error;
                break;
            case FcNativeEventKind.Joining:
                _groupState = "Joining";
                _readinessState = FcMeshReadinessState.Joining;
                _localAuthorId = value.ActualAuthor.Length == 32
                    ? Convert.ToHexString(value.ActualAuthor).ToLowerInvariant()
                    : _localAuthorId;
                _namespaceId = value.Key.Length == 32
                    ? Convert.ToHexString(value.Key).ToLowerInvariant()
                    : _namespaceId;
                _decisionsPaused = true;
                break;
            case FcNativeEventKind.Joined:
                _groupState = "Joined";
                _readinessState = FcMeshReadinessState.Joining;
                _localAuthorId = value.ActualAuthor.Length == 32
                    ? Convert.ToHexString(value.ActualAuthor).ToLowerInvariant()
                    : _localAuthorId;
                _namespaceId = value.Key.Length == 0
                    ? _namespaceId
                    : Convert.ToHexString(value.Key).ToLowerInvariant();
                if (_pendingGroupMetadataPayload is { } pendingMetadata && _namespaceId is { } namespaceId)
                {
                    _pendingGroupMetadataPayload = null;
                    if (!FcGroupMetadataCodec.IsCompatible(pendingMetadata, namespaceId, out var pendingMetadataError))
                    {
                        FailAndPause(FcNativeErrorCode.InvalidRecord, pendingMetadataError);
                        return;
                    }
                    _groupMetadataCompatible = true;
                }
                // Group creation and persisted restoration return the ticket
                // in Joined.value. A joined peer emits an empty Joined value
                // immediately before its InitialSyncCompleted signal.
                if (value.Value.Length > 0)
                {
                    _requiresInitialSync = false;
                    _requiresManagedGroupMetadata = true;
                    _hasInitialSyncSignal = false;
                    _groupTicket = Encoding.UTF8.GetString(value.Value);
                    _ticketForDiagnostics = _groupTicket;
                    BeginSnapshot(value.WorldEpoch, value.Sequence);
                }
                break;
            case FcNativeEventKind.Left:
                _groupState = "Not joined";
                _decisionsPaused = true;
                _readinessState = FcMeshReadinessState.Left;
                _hasInitialSyncSignal = false;
                _groupMetadataCompatible = false;
                _requiresInitialSync = false;
                _requiresManagedGroupMetadata = false;
                _pendingGroupMetadataPayload = null;
                _namespaceId = null;
                _groupTicket = null;
                _ticketForDiagnostics = null;
                _contactedPeer = null;
                break;
            case FcNativeEventKind.InitialSyncCompleted:
                if (value.ActualAuthor.Length != 32)
                {
                    FailAndPause(FcNativeErrorCode.InvalidRecord, "InitialSyncCompleted did not identify an authenticated contacted peer.");
                    break;
                }
                _contactedPeer = value.ActualAuthor.Length == 32
                    ? Convert.ToHexString(value.ActualAuthor).ToLowerInvariant()
                    : null;
                _syncEventSequence = value.Sequence;
                _syncEpoch = value.WorldEpoch;
                _syncRecordCount = value.Aux;
                _hasInitialSyncSignal = true;
                // Native emits this event only after authenticating a peer,
                // reconciling the namespace, and accepting compatible group
                // metadata. The managed snapshot is still required before
                // readiness is exposed; a metadata record present in that
                // snapshot is decoded and checked independently below.
                _groupMetadataCompatible = true;
                _decisionsPaused = true;
                _readinessState = FcMeshReadinessState.ReconcilingSnapshot;
                BeginSnapshot(value.WorldEpoch, value.Sequence);
                break;
            case FcNativeEventKind.PeerConnected:
                if (_readinessState == FcMeshReadinessState.Joining && value.ActualAuthor.Length == 32)
                    _contactedPeer = Convert.ToHexString(value.ActualAuthor).ToLowerInvariant();
                break;
            case FcNativeEventKind.PeerDisconnected:
                if (_readinessState is FcMeshReadinessState.Joining or FcMeshReadinessState.ReconcilingSnapshot)
                {
                    DiscardSnapshotAndPause("Contacted peer disconnected before initial synchronization completed.");
                    _hasInitialSyncSignal = false;
                    _syncEventSequence = 0;
                    _syncEpoch = 0;
                    _syncRecordCount = 0;
                    _contactedPeer = null;
                    _readinessState = FcMeshReadinessState.Joining;
                    _groupMetadataCompatible = false;
                    _pendingGroupMetadataPayload = null;
                    SetLastError("Contacted peer disconnected before initial synchronization completed.");
                }
                break;
            case FcNativeEventKind.PathChanged:
                break;
            case FcNativeEventKind.RecordInserted:
                ApplyEventRecord(value);
                break;
            case FcNativeEventKind.RecordRemoved:
                if (InvalidateInitialSyncEvidence("Initial synchronization was invalidated before the managed snapshot became ready."))
                    break;
                BeginSnapshot(value.WorldEpoch, value.Sequence, restorePriorReadiness: true);
                break;
            case FcNativeEventKind.Error:
                FailAndPause(FcNativeErrorCode.Internal, DecodeDiagnostic(value.Value, "Native mesh error."));
                break;
            case FcNativeEventKind.Warning:
                SetLastError(DecodeDiagnostic(value.Value, "Native mesh warning."));
                break;
            case FcNativeEventKind.SnapshotReady:
                HandleSnapshotReady(value);
                break;
            default:
                FailAndPause(FcNativeErrorCode.InvalidArgument, "Native event kind is unsupported.");
                break;
        }
    }

    private void ApplyEventRecord(FcMeshNativeEvent value)
    {
        if (value.Key.Length == 0 || value.ActualAuthor.Length != 32 || value.ContentHash.Length != 32)
        {
            FailAndPause(FcNativeErrorCode.InvalidRecord, "Native record event metadata is missing or malformed.");
            return;
        }
        var decoded = FcNativeEnvelopeDecoder.Decode(value.Value, value.ActualAuthor, value.ContentHash, value.Key);
        if (!decoded.IsValid || decoded.Record is null || decoded.VerifiedContext is null)
        {
            FailAndPause(FcNativeErrorCode.InvalidRecord, decoded.Error);
            return;
        }
        if (value.ProtocolVersion != decoded.Record.ProtocolVersion
            || value.Aux != 0 && value.Aux != decoded.Record.Revision)
        {
            FailAndPause(FcNativeErrorCode.InvalidRecord, "Native event metadata does not mirror its envelope.");
            return;
        }
        if (decoded.Record.RecordType == FcRecordTypes.GroupMetadata)
        {
            if (_namespaceId is null)
            {
                _pendingGroupMetadataPayload = decoded.Record.Payload.ToArray();
                return;
            }
            if (!FcGroupMetadataCodec.IsCompatible(decoded.Record.Payload, _namespaceId, out var metadataError))
                FailAndPause(FcNativeErrorCode.InvalidRecord, metadataError);
            else
                _groupMetadataCompatible = true;
            return;
        }
        var result = ApplyDecodedRecord(_worldStore, decoded.Record, decoded.VerifiedContext);
        if (result.Status is not (FcApplyStatus.Accepted or FcApplyStatus.Duplicate))
            FailAndPause(FcNativeErrorCode.InvalidRecord, result.Message);
    }

    private void BeginSnapshot(
        ulong epoch,
        ulong invalidationSequence,
        bool restorePriorReadiness = false)
    {
        var priorReadinessState = _readinessState;
        var priorDecisionsPaused = _decisionsPaused;
        var priorGroupMetadataCompatible = _groupMetadataCompatible;
        if (restorePriorReadiness && _snapshot is { RestorePriorReadiness: true } previousSnapshot)
        {
            priorReadinessState = previousSnapshot.PriorReadinessState;
            priorDecisionsPaused = previousSnapshot.PriorDecisionsPaused;
            priorGroupMetadataCompatible = previousSnapshot.PriorGroupMetadataCompatible;
        }
        if (_snapshot is not null
            && (epoch < _snapshot.Epoch
                || epoch == _snapshot.Epoch
                    && invalidationSequence <= _snapshot.InvalidationSequence))
            return;
        _decisionsPaused = true;
        _readinessState = FcMeshReadinessState.ReconcilingSnapshot;
        _lastError = string.Empty;
        if (_snapshot is { Handle: not 0 } oldSnapshot)
        {
            try { _ = _api.DestroySnapshot(oldSnapshot.Handle); }
            catch (Exception exception) { SetLastError(exception.Message); }
        }
        _snapshot = new SnapshotSession(
            epoch == 0 ? _worldEpoch : epoch,
            invalidationSequence,
            NextRequestId(),
            restorePriorReadiness,
            priorReadinessState,
            priorDecisionsPaused,
            priorGroupMetadataCompatible);
        // Native/live metadata evidence cannot stand in for metadata applied by
        // this exact shadow snapshot or its later replay.
        _snapshot.GroupMetadataCompatible = false;
        _queuedDuringSnapshot.Clear();
        RequestSnapshot(_snapshot);
    }

    private void RequestSnapshot(SnapshotSession snapshot)
    {
        if (_handle is null)
            return;
        var result = _api.RequestSnapshot(_handle.Value, snapshot.RequestId);
        if (!result.Succeeded)
            RecordError(result, "Native snapshot request failed.");
    }

    private void HandleSnapshotReady(FcMeshNativeEvent value)
    {
        if (_snapshot is null
            || _snapshot.Phase != SnapshotReconstructionPhase.WaitingForNativeSnapshot
            || value.Aux != _snapshot.RequestId
            || _handle is null)
            return;
        if (_snapshot.Handle != 0)
            return;
        var result = _api.OpenSnapshot(_handle.Value, _snapshot.RequestId, out var snapshotHandle, out var baseSequence);
        if (!result.Succeeded)
        {
            if (result.ErrorCode == FcNativeErrorCode.SnapshotStale)
                RetrySnapshot("Native snapshot became stale before opening.");
            else if (result.ErrorCode != FcNativeErrorCode.NotReady)
                RecordError(result, "Native snapshot open failed.");
            return;
        }
        if (snapshotHandle == 0)
        {
            FailAndPause(FcNativeErrorCode.Internal, "Native snapshot returned a zero handle.");
            return;
        }
        _snapshot.Handle = snapshotHandle;
        _snapshot.BaseEventSequence = baseSequence;
    }

    private void OpenPendingSnapshot()
    {
        if (_snapshot is null
            || _snapshot.Phase != SnapshotReconstructionPhase.WaitingForNativeSnapshot
            || _snapshot.Handle != 0
            || _handle is null)
            return;
        var result = _api.OpenSnapshot(_handle.Value, _snapshot.RequestId, out var snapshotHandle, out var baseSequence);
        if (!result.Succeeded)
        {
            if (result.ErrorCode == FcNativeErrorCode.SnapshotStale)
                RetrySnapshot("Native snapshot became stale before opening.");
            else if (result.ErrorCode != FcNativeErrorCode.NotReady)
                RecordError(result, "Native snapshot open failed.");
            return;
        }
        if (snapshotHandle == 0)
        {
            FailAndPause(FcNativeErrorCode.Internal, "Native snapshot returned a zero handle.");
            return;
        }
        _snapshot.Handle = snapshotHandle;
        _snapshot.BaseEventSequence = baseSequence;
    }

    private void PollSnapshot(Stopwatch stopwatch, TimeSpan budget)
    {
        if (_snapshot is null || _handle is null)
            return;
        if (_snapshot.Handle == 0)
        {
            AdvanceSnapshotReconstruction(stopwatch, budget);
            return;
        }
        if (_worldEpoch != 0 && _worldEpoch != _snapshot.Epoch)
        {
            RetrySnapshot("Native world epoch advanced while snapshot was open.");
            return;
        }
        for (var index = 0; index < _maxEventsPerTick && stopwatch.Elapsed < budget; index++)
        {
            var result = _api.PollSnapshot(_snapshot.Handle, out var value, out var done);
            if (!result.Succeeded)
            {
                if (result.ErrorCode == FcNativeErrorCode.SnapshotStale)
                    RetrySnapshot("Native snapshot became stale while polling.");
                else
                    RecordError(result, "Native snapshot poll failed.");
                return;
            }
            if (value is not null)
            {
                _snapshot.Records.Add(value.DeepCopy());
                if (_snapshot.Records.Count > MaxSnapshotRecords)
                {
                    RetrySnapshot("Native snapshot exceeded the managed record limit.");
                    return;
                }
            }
            if (done)
            {
                _ = _api.DestroySnapshot(_snapshot.Handle);
                _snapshot.Handle = 0;
                _snapshot.Phase = SnapshotReconstructionPhase.NonTransferRecords;
                _snapshot.RecordIndex = 0;
                _snapshot.ReplayIndex = 0;
                return;
            }
        }

        AdvanceSnapshotReconstruction(stopwatch, budget);
    }

    private void AdvanceSnapshotReconstruction(Stopwatch stopwatch, TimeSpan budget)
    {
        var snapshot = _snapshot;
        if (snapshot is null
            || snapshot.Handle != 0
            || snapshot.Phase == SnapshotReconstructionPhase.WaitingForNativeSnapshot)
            return;
        if (_worldEpoch != 0 && _worldEpoch != snapshot.Epoch)
        {
            RetrySnapshot("Native snapshot epoch no longer matches the latest world epoch.");
            return;
        }

        try
        {
            snapshot.Candidate ??= _worldStoreFactory();
            var candidate = snapshot.Candidate
                ?? throw new InvalidOperationException("Snapshot candidate was not created.");
            // Apply absolute registers in bounded scans; register revision/HLC comparison keeps
            // convergence deterministic regardless of snapshot arrival order. Transfers follow
            // after-state registers and use the explicit bootstrap merge because a snapshot
            // need not contain the physical pre-transfer preimage.
            var operations = 0;
            while (operations < _maxEventsPerTick && stopwatch.Elapsed < budget)
            {
                operations++;
                if (_worldEpoch != 0 && _worldEpoch != snapshot.Epoch)
                {
                    RetrySnapshot("Native world epoch advanced during snapshot reconstruction.");
                    return;
                }

                switch (snapshot.Phase)
                {
                    case SnapshotReconstructionPhase.NonTransferRecords:
                    {
                        if (snapshot.RecordIndex >= snapshot.Records.Count)
                        {
                            snapshot.RecordIndex = 0;
                            snapshot.Phase = SnapshotReconstructionPhase.TransferRecords;
                            continue;
                        }
                        var nonTransfer = snapshot.Records[snapshot.RecordIndex++];
                        if (nonTransfer.RecordType == FcRecordTypes.InventoryTransfer)
                            continue;
                        if (!TryApplySnapshotRecord(candidate, nonTransfer, out var nonTransferError))
                        {
                            DiscardSnapshotAndPause(nonTransferError);
                            return;
                        }
                        break;
                    }

                    case SnapshotReconstructionPhase.TransferRecords:
                    {
                        if (snapshot.RecordIndex >= snapshot.Records.Count)
                        {
                            snapshot.ReplayIndex = 0;
                            snapshot.Phase = SnapshotReconstructionPhase.Replay;
                            continue;
                        }
                        var transfer = snapshot.Records[snapshot.RecordIndex++];
                        if (transfer.RecordType != FcRecordTypes.InventoryTransfer)
                            continue;
                        if (!TryApplySnapshotRecord(candidate, transfer, out var transferError))
                        {
                            DiscardSnapshotAndPause(transferError);
                            return;
                        }
                        break;
                    }

                    case SnapshotReconstructionPhase.Replay:
                    {
                        if (snapshot.ReplayIndex >= _queuedDuringSnapshot.Count)
                        {
                            SwapSnapshot(snapshot);
                            return;
                        }
                        var queued = _queuedDuringSnapshot[snapshot.ReplayIndex++];
                        if (queued.Sequence <= snapshot.BaseEventSequence)
                            continue;
                        if (!ReplaySnapshotEvent(candidate, queued))
                            return;
                        break;
                    }

                    default:
                        DiscardSnapshotAndPause("Snapshot reconstruction phase is invalid.");
                        return;
                }
            }
        }
        catch (Exception exception)
        {
            DiscardSnapshotAndPause($"Managed snapshot validation failed: {exception.Message}");
        }
    }

    private bool ReplaySnapshotEvent(FcWorldStore candidate, FcMeshNativeEvent value)
    {
        switch (value.Kind)
        {
            case FcNativeEventKind.WorldInvalidated:
                RetrySnapshot("A newer invalidation arrived during snapshot replay.");
                return false;
            case FcNativeEventKind.RecordRemoved:
                RetrySnapshot("A record removal arrived during snapshot replay.");
                return false;
            case FcNativeEventKind.Error:
                DiscardSnapshotAndPause(DecodeDiagnostic(value.Value, "Native mesh error during snapshot replay."));
                return false;
            case FcNativeEventKind.RecordInserted:
            {
                var decoded = FcNativeEnvelopeDecoder.Decode(value.Value, value.ActualAuthor, value.ContentHash, value.Key);
                if (!decoded.IsValid || decoded.Record is null || decoded.VerifiedContext is null)
                {
                    DiscardSnapshotAndPause(decoded.Error);
                    return false;
                }
                if (value.ProtocolVersion != decoded.Record.ProtocolVersion
                    || value.Aux != 0 && value.Aux != decoded.Record.Revision)
                {
                    DiscardSnapshotAndPause("Native replay event metadata does not mirror its envelope.");
                    return false;
                }
                if (decoded.Record.RecordType == FcRecordTypes.GroupMetadata)
                {
                    if (!FcGroupMetadataCodec.IsCompatible(decoded.Record.Payload, _namespaceId, out var metadataError))
                        DiscardSnapshotAndPause(metadataError);
                    else if (_snapshot is not null)
                        _snapshot.GroupMetadataCompatible = true;
                    return _snapshot is not null;
                }
                var result = ApplyDecodedRecord(candidate, decoded.Record, decoded.VerifiedContext);
                if (result.Status is not (FcApplyStatus.Accepted or FcApplyStatus.Duplicate))
                {
                    DiscardSnapshotAndPause(result.Message);
                    return false;
                }
                return true;
            }
            case FcNativeEventKind.Started:
                _lifecycle = "Starting";
                return TryApplyStartedAuthor(value);
            case FcNativeEventKind.Joining:
                _groupState = "Joining";
                return true;
            case FcNativeEventKind.Joined:
                _groupState = "Joined";
                return true;
            case FcNativeEventKind.Warning:
                SetLastError(DecodeDiagnostic(value.Value, "Native mesh warning."));
                return true;
            case FcNativeEventKind.Stopped:
                DiscardSnapshotAndPause("Native mesh stopped during snapshot reconstruction.");
                return false;
            case FcNativeEventKind.Left:
                DiscardSnapshotAndPause("Native mesh left its group during snapshot reconstruction.");
                return false;
            case FcNativeEventKind.InitialSyncCompleted:
            case FcNativeEventKind.SnapshotReady:
            case FcNativeEventKind.PeerConnected:
            case FcNativeEventKind.PathChanged:
                return true;
            case FcNativeEventKind.PeerDisconnected:
                DiscardSnapshotAndPause("Contacted peer disconnected before initial synchronization completed.");
                _hasInitialSyncSignal = false;
                _syncEventSequence = 0;
                _syncEpoch = 0;
                _syncRecordCount = 0;
                _contactedPeer = null;
                _readinessState = FcMeshReadinessState.Joining;
                _groupMetadataCompatible = false;
                _pendingGroupMetadataPayload = null;
                return false;
            default:
                DiscardSnapshotAndPause("Native event kind is unsupported during snapshot replay.");
                return false;
        }
    }

    private void SwapSnapshot(SnapshotSession snapshot)
    {
        if (_worldEpoch != 0 && _worldEpoch != snapshot.Epoch)
        {
            RetrySnapshot("Native world epoch no longer matches the completed snapshot.");
            return;
        }
        if (snapshot.Candidate is null)
        {
            DiscardSnapshotAndPause("Snapshot candidate was not built.");
            return;
        }
        if (_requiresManagedGroupMetadata
            && (_hasInitialSyncSignal || !_requiresInitialSync)
            && !snapshot.GroupMetadataCompatible)
        {
            RetrySnapshot("Managed snapshot did not contain compatible FC group metadata.");
            return;
        }
        if (_hasInitialSyncSignal && snapshot.BaseEventSequence < _syncEventSequence)
        {
            RetrySnapshot("Managed snapshot base sequence predates InitialSyncCompleted.");
            return;
        }
        // Aux is the register count observed when InitialSyncCompleted fired.
        // Later registers may validly appear before this snapshot opens.
        if (_hasInitialSyncSignal && (ulong)snapshot.Records.Count < _syncRecordCount)
        {
            RetrySnapshot("Managed snapshot record count is below InitialSyncCompleted evidence.");
            return;
        }
        if (!_groupMetadataValidator(snapshot.Candidate))
        {
            DiscardSnapshotAndPause("Replicated FC group metadata is incompatible with this client.");
            return;
        }
        _worldStore = snapshot.Candidate;
        _lastSnapshotBaseEventSequence = snapshot.BaseEventSequence;
        _snapshot = null;
        _queuedDuringSnapshot.Clear();
        _lastError = string.Empty;
        if (_requiresInitialSync && !_hasInitialSyncSignal)
        {
            _groupMetadataCompatible = snapshot.GroupMetadataCompatible;
            _decisionsPaused = true;
            _readinessState = FcMeshReadinessState.Joining;
            return;
        }
        if (snapshot.RestorePriorReadiness)
        {
            _groupMetadataCompatible = snapshot.PriorGroupMetadataCompatible;
            _decisionsPaused = snapshot.PriorDecisionsPaused;
            _readinessState = snapshot.PriorReadinessState;
            return;
        }
        _groupMetadataCompatible = snapshot.GroupMetadataCompatible;
        _decisionsPaused = false;
        _readinessState = FcMeshReadinessState.Ready;
        _hasInitialSyncSignal = false;
        _requiresInitialSync = false;
        _requiresManagedGroupMetadata = false;
    }

    private bool TryApplySnapshotRecord(
        FcWorldStore target,
        FcMeshNativeSnapshotRecord value,
        out string error)
    {
        error = string.Empty;
        if (value.ProtocolVersion != FcProtocolVersion.Current)
        {
            error = "Snapshot record protocol version is unsupported.";
            return false;
        }
        if (value.Key.Length == 0 || value.ActualAuthor.Length != 32 || value.ContentHash.Length != 32)
        {
            error = "Snapshot record metadata is missing or malformed.";
            return false;
        }
        var decoded = FcNativeEnvelopeDecoder.Decode(value.Value, value.ActualAuthor, value.ContentHash, value.Key);
        if (!decoded.IsValid || decoded.Record is null || decoded.VerifiedContext is null)
        {
            error = decoded.Error;
            return false;
        }
        if (value.Generation != decoded.Record.Generation
            || value.Revision != decoded.Record.Revision
            || !string.Equals(value.RecordType, decoded.Record.RecordType, StringComparison.Ordinal)
            || value.HlcPhysicalUnixMs != decoded.Record.Hlc.PhysicalUnixMs
            || value.HlcLogical != decoded.Record.Hlc.Logical
            || !value.HlcNodeId.AsSpan().SequenceEqual(Convert.FromHexString(decoded.Record.Hlc.NodeId)))
        {
            error = "Snapshot metadata does not mirror its exact envelope.";
            return false;
        }
        if (decoded.Record.RecordType == FcRecordTypes.GroupMetadata)
        {
            if (!FcGroupMetadataCodec.IsCompatible(decoded.Record.Payload, _namespaceId, out error))
                return false;
            if (_snapshot is not null)
                _snapshot.GroupMetadataCompatible = true;
            return true;
        }
        var result = ApplyDecodedRecord(
            target,
            decoded.Record,
            decoded.VerifiedContext,
            snapshotBootstrap: true);
        if (result.Status is FcApplyStatus.Accepted or FcApplyStatus.Duplicate)
            return true;
        error = result.Message;
        return false;
    }

    internal static FcApplyResult ApplyDecodedRecord(
        FcWorldStore target,
        FcMeshRecord envelope,
        FcVerifiedMeshContext context,
        bool snapshotBootstrap = false)
    {
        try
        {
            object? payload = envelope.RecordType switch
            {
                FcRecordTypes.PublishedList => FcPublishedListPayloadCompatibility.Deserialize(envelope.Payload),
                FcRecordTypes.WorkerSession => JsonSerializer.Deserialize<WorkerSessionRecord>(envelope.Payload, FcJsonContext.Default.WorkerSessionRecord),
                FcRecordTypes.ChestSnapshot => JsonSerializer.Deserialize<ChestSnapshotRecord>(envelope.Payload, FcJsonContext.Default.ChestSnapshotRecord),
                FcRecordTypes.CapabilityRequest => JsonSerializer.Deserialize<CapabilityRequestRecord>(envelope.Payload, FcJsonContext.Default.CapabilityRequestRecord),
                FcRecordTypes.CapabilityResponse => JsonSerializer.Deserialize<CapabilityResponseRecord>(envelope.Payload, FcJsonContext.Default.CapabilityResponseRecord),
                FcRecordTypes.InventoryTransfer => JsonSerializer.Deserialize<FcInventoryTransferRecord>(envelope.Payload, FcJsonContext.Default.FcInventoryTransferRecord),
                FcRecordTypes.FcChestLocation => JsonSerializer.Deserialize<FcEstateChestLocationRecord>(envelope.Payload, FcJsonContext.Default.FcEstateChestLocationRecord),
                _ => null,
            };
            if (payload is null)
                return FcApplyResult.Reject(target.WorldRevision, "Native envelope record type is unsupported or payload is invalid.");
            // Snapshot bootstrap cannot assume the physical pre-transfer state is
            // present. Queued/live RecordInserted replay deliberately stays on the
            // ordinary ApplyTransfer path and revalidates its quantity delta.
            if (snapshotBootstrap && payload is FcInventoryTransferRecord transfer)
                return target.ApplySnapshotBootstrapTransfer(envelope, transfer, context);
            if (payload is PublishedListRecord publishedList)
                return target.ApplyPublishedListWithUnknownOptionalMembers(
                    envelope,
                    publishedList,
                    context);
            return target.Apply(envelope, payload, context);
        }
        catch (Exception exception)
        {
            return FcApplyResult.Reject(target.WorldRevision, $"Native payload decode failed: {exception.Message}");
        }
    }

    private void RetrySnapshot(string message)
    {
        if (_snapshot is { Handle: not 0 } snapshot)
        {
            try { _ = _api.DestroySnapshot(snapshot.Handle); }
            catch (Exception exception) { SetLastError(exception.Message); }
        }
        var epoch = Math.Max(_snapshot?.Epoch ?? 0, _worldEpoch);
        var sequence = _snapshot?.InvalidationSequence ?? _lastEventSequence;
        var previousSnapshot = _snapshot;
        _snapshot = new SnapshotSession(
            epoch,
            sequence,
            NextRequestId(),
            previousSnapshot?.RestorePriorReadiness ?? false,
            previousSnapshot?.PriorReadinessState ?? _readinessState,
            previousSnapshot?.PriorDecisionsPaused ?? _decisionsPaused,
            previousSnapshot?.PriorGroupMetadataCompatible ?? _groupMetadataCompatible);
        // Retry starts a fresh managed proof; prior readiness is retained only
        // as ordinary-recovery restoration state.
        _snapshot.GroupMetadataCompatible = false;
        _queuedDuringSnapshot.Clear();
        SetLastError(message);
        _decisionsPaused = true;
        _readinessState = FcMeshReadinessState.ReconcilingSnapshot;
        RequestSnapshot(_snapshot);
    }

    private bool InvalidateInitialSyncEvidence(string message)
    {
        if (!_requiresInitialSync || !_hasInitialSyncSignal)
            return false;
        if (_snapshot is { Handle: not 0 } snapshot)
        {
            try { _ = _api.DestroySnapshot(snapshot.Handle); }
            catch (Exception exception) { SetLastError(exception.Message); }
        }
        _snapshot = null;
        _queuedDuringSnapshot.Clear();
        _hasInitialSyncSignal = false;
        _syncEventSequence = 0;
        _syncEpoch = 0;
        _syncRecordCount = 0;
        _contactedPeer = null;
        _groupMetadataCompatible = false;
        _pendingGroupMetadataPayload = null;
        _decisionsPaused = true;
        _readinessState = FcMeshReadinessState.Joining;
        SetLastError(message);
        return true;
    }

    private void DiscardSnapshotAndPause(string message)
    {
        if (_snapshot is { Handle: not 0 } snapshot)
        {
            try { _ = _api.DestroySnapshot(snapshot.Handle); }
            catch (Exception exception) { SetLastError(exception.Message); }
        }
        _snapshot = null;
        _queuedDuringSnapshot.Clear();
        _hasInitialSyncSignal = false;
        _syncEventSequence = 0;
        _syncEpoch = 0;
        _syncRecordCount = 0;
        _contactedPeer = null;
        _groupMetadataCompatible = false;
        _pendingGroupMetadataPayload = null;
        _decisionsPaused = true;
        _readinessState = FcMeshReadinessState.Error;
        SetLastError(message);
    }

    private FcNativeCallResult FailAndPause(FcNativeCallResult result, string fallback)
    {
        RecordError(result, fallback);
        return result;
    }

    private FcNativeCallResult FailAndPause(FcNativeErrorCode code, string message)
    {
        if (_snapshot is not null)
            DiscardSnapshotAndPause(message);
        SetLastError(message);
        _decisionsPaused = true;
        _readinessState = FcMeshReadinessState.Error;
        return Failure(code, message);
    }

    private void RecordError(FcNativeCallResult result, string fallback)
    {
        var message = result.ErrorId == 0 ? null : _api.GetErrorMessage(result.ErrorId);
        var error = string.IsNullOrWhiteSpace(message) ? fallback : message;
        if (_snapshot is not null)
            DiscardSnapshotAndPause(error);
        SetLastError(error);
        _decisionsPaused = true;
        _readinessState = FcMeshReadinessState.Error;
    }

    private ulong NextRequestId()
    {
        _nextSnapshotRequestId = _nextSnapshotRequestId == ulong.MaxValue ? 1 : _nextSnapshotRequestId + 1;
        return _nextSnapshotRequestId;
    }

    private static FcNativeCallResult Success() => new((uint)FcNativeErrorCode.Ok, 0, 0);

    private static FcNativeCallResult Failure(FcNativeErrorCode code, string _)
        => new((uint)code, 0, 0);

    private void SetLastError(string message)
        => _lastError = SanitizeDiagnostic(message);

    private string SanitizeDiagnostic(string message)
    {
        var sanitized = message ?? string.Empty;
        foreach (var ticket in new[] { _ticketForDiagnostics, _groupTicket }
                     .Where(value => value is { Length: > 0 })
                     .OfType<string>()
                     .Distinct(StringComparer.Ordinal))
            sanitized = sanitized.Replace(ticket, "[redacted group ticket]", StringComparison.Ordinal);
        return sanitized;
    }

    private static string DecodeDiagnostic(byte[] value, string fallback)
        => value.Length == 0 ? fallback : Encoding.UTF8.GetString(value);

    private static bool DefaultGroupMetadataValidator(FcWorldStore store)
        => store.Lists.Values.All(value => value is not null
            && value.PlannerSemanticsVersion >= 0
            && !string.IsNullOrWhiteSpace(value.GameVersion));

    private enum SnapshotReconstructionPhase
    {
        WaitingForNativeSnapshot,
        NonTransferRecords,
        TransferRecords,
        Replay,
    }

    private sealed class SnapshotSession
    {
        public SnapshotSession(
            ulong epoch,
            ulong invalidationSequence,
            ulong requestId,
            bool restorePriorReadiness,
            FcMeshReadinessState priorReadinessState,
            bool priorDecisionsPaused,
            bool priorGroupMetadataCompatible)
        {
            Epoch = epoch;
            InvalidationSequence = invalidationSequence;
            RequestId = requestId;
            RestorePriorReadiness = restorePriorReadiness;
            PriorReadinessState = priorReadinessState;
            PriorDecisionsPaused = priorDecisionsPaused;
            PriorGroupMetadataCompatible = priorGroupMetadataCompatible;
        }

        public ulong Epoch { get; }
        public ulong InvalidationSequence { get; }
        public ulong RequestId { get; }
        public bool RestorePriorReadiness { get; }
        public FcMeshReadinessState PriorReadinessState { get; }
        public bool PriorDecisionsPaused { get; }
        public bool PriorGroupMetadataCompatible { get; }
        public ulong Handle { get; set; }
        public ulong BaseEventSequence { get; set; }
        public List<FcMeshNativeSnapshotRecord> Records { get; } = new();
        public SnapshotReconstructionPhase Phase { get; set; }
        public FcWorldStore? Candidate { get; set; }
        public bool GroupMetadataCompatible { get; set; }
        public int RecordIndex { get; set; }
        public int ReplayIndex { get; set; }
    }
}
