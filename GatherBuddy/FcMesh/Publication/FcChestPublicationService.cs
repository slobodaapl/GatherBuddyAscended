using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GatherBuddy.FcMesh.Chest;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Publication;

public interface IFcCompleteChestReader
{
    FcChestReadResult ReadCurrentCompleteSnapshot();
}

public sealed class FcCompleteChestReader : IFcCompleteChestReader
{
    private readonly FcChestSnapshotReader _reader;

    public FcCompleteChestReader(FcChestSnapshotReader? reader = null)
        => _reader = reader ?? new FcChestSnapshotReader();

    public FcChestReadResult ReadCurrentCompleteSnapshot()
        => _reader.ReadCurrentCompleteSnapshot();
}

public sealed record FcChestPublicationResult(
    bool Accepted,
    string Message,
    Guid RecordId,
    ulong Revision,
    FcPublicationCommandStatus Status,
    ChestSnapshotRecord? Snapshot);

/// <summary>
/// Converts one complete read-only chest observation into the observer-owned
/// chest register. It has no movement, travel, or compensation path.
/// </summary>
public sealed class FcChestPublicationService : IDisposable
{
    private readonly object _gate = new();
    private readonly IFcPublicationStateStore _stateStore;
    private readonly IFcPublicationTransport _transport;
    private readonly IFcCompleteChestReader _reader;
    private readonly Func<string?> _authorScopeProvider;
    private readonly Func<string?> _authorIdProvider;
    private readonly ConcurrentQueue<FcChestFrameworkRead> _frameworkReads = new();
    private readonly Channel<FcChestPublicationWork> _commands = Channel.CreateUnbounded<FcChestPublicationWork>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private FcPublicationState _state;
    private FcPublicationStateLoadStatus _loadStatus;
    private string _lastError = string.Empty;
    private bool _disposed;
    private int _pendingCommands;
    private bool _freshReadQueued;

    public FcChestPublicationService(
        IFcPublicationStateStore stateStore,
        IFcPublicationTransport transport,
        IFcCompleteChestReader reader,
        Func<string?> authorScopeProvider,
        Func<string?> authorIdProvider)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _authorScopeProvider = authorScopeProvider ?? throw new ArgumentNullException(nameof(authorScopeProvider));
        _authorIdProvider = authorIdProvider ?? throw new ArgumentNullException(nameof(authorIdProvider));
        var scope = _authorScopeProvider();
        if (string.IsNullOrWhiteSpace(scope))
        {
            _state = FcPublicationState.Create(string.Empty);
            _loadStatus = FcPublicationStateLoadStatus.Corrupt;
            _lastError = "Current character content ID is unavailable; chest publication is read-only.";
        }
        else
        {
            var loaded = _stateStore.Load(scope);
            _state = loaded.State;
            _loadStatus = loaded.Status;
            _lastError = loaded.Error;
            if (loaded.Status == FcPublicationStateLoadStatus.Missing)
            {
                try
                {
                    _stateStore.Save(scope, _state);
                    _loadStatus = FcPublicationStateLoadStatus.Clean;
                }
                catch (Exception exception)
                {
                    _loadStatus = FcPublicationStateLoadStatus.Corrupt;
                    _lastError = $"Publication state initialization failed: {exception.Message}";
                }
            }
        }
        _worker = Task.Run(ProcessCommandsAsync);
    }

    public string LastError
    {
        get { lock (_gate) return _lastError; }
    }

    public int PendingCommands
    {
        get { lock (_gate) return _pendingCommands + (_freshReadQueued ? 1 : 0); }
    }

    public ChestSnapshotRecord? LastPublishedSnapshot
    {
        get { lock (_gate) return _state.LastChestSnapshot; }
    }

    public FcChestPublicationResult PublishCurrentCompleteObservation()
        => RequestFreshObservation();

    public FcChestPublicationResult Publish(FcChestReadResult result)
    {
        if (!result.IsComplete)
        {
            var failure = string.IsNullOrWhiteSpace(result.FailureReason)
                ? "FC chest observation is incomplete or stale."
                : result.FailureReason;
            lock (_gate)
                _lastError = failure;
            return Blocked(failure);
        }
        return RequestFreshObservation();
    }

    public FcChestPublicationResult Publish(FcChestSnapshot snapshot)
        => snapshot is null
            ? Blocked("FC chest observation is unavailable.")
            : RequestFreshObservation();

    /// <summary>
    /// Runs at most one fresh game-state read on the framework thread. The
    /// reservation is already durable when this queue is populated; no UI
    /// snapshot can become stale while waiting for persistence.
    /// </summary>
    public void ProcessFrameworkCommands()
    {
        if (!_frameworkReads.TryDequeue(out var request))
            return;
        lock (_gate)
            _freshReadQueued = false;
        FcChestReadResult result;
        try
        {
            result = _reader.ReadCurrentCompleteSnapshot();
        }
        catch (Exception exception)
        {
            FailFreshObservation(request, $"FC chest observation failed: {exception.Message}");
            return;
        }

        if (!result.IsComplete || result.Snapshot is not { } snapshot)
        {
            var failure = string.IsNullOrWhiteSpace(result.FailureReason)
                ? "FC chest observation is incomplete or stale."
                : result.FailureReason;
            FailFreshObservation(request, failure);
            return;
        }

        lock (_gate)
        {
            if (_disposed)
                return;
            if (_state.PendingChest is not { } pending
                || pending.Revision != request.Revision
                || pending.Status != FcPublicationCommandStatus.AwaitingFreshObservation)
                return;
            if (!TryPrepareScopeLocked(out var scopeError))
            {
                FailFreshObservationLocked(request, scopeError);
                return;
            }
            var owner = _authorIdProvider();
            if (owner is not { Length: > 0 } currentOwner
                || string.IsNullOrWhiteSpace(currentOwner)
                || !string.Equals(currentOwner, request.OwnerAuthorId, StringComparison.Ordinal)
                || !_transport.IsReady)
            {
                FailFreshObservationLocked(
                    request,
                    "Native mesh author/readiness changed before the fresh chest observation; retry is required.");
                return;
            }
            if (_transport.WorldStore?.IsForked(currentOwner + "/chest") == true)
            {
                FailFreshObservationLocked(request, "The local chest register is forked; writes are blocked.");
                return;
            }

            ChestSnapshotRecord record;
            try
            {
                record = ToRecord(snapshot, currentOwner, request.RecordId, request.Revision);
            }
            catch (Exception exception)
            {
                FailFreshObservationLocked(request, $"FC chest observation could not be encoded: {exception.Message}");
                return;
            }

            _state = _state with
            {
                PendingChest = pending with
                {
                    Status = FcPublicationCommandStatus.Pending,
                    PayloadHash = FcCanonical.PayloadHash(record),
                    Error = string.Empty,
                },
                PendingChestSnapshot = record,
            };
            _pendingCommands++;
            if (!_commands.Writer.TryWrite(new FcChestPublicationWork(record)))
            {
                _pendingCommands--;
                FailFreshObservationLocked(
                    request,
                    "Chest publication command queue is closed.",
                    record);
            }
        }
    }

    private FcChestPublicationResult RequestFreshObservation()
    {
        lock (_gate)
        {
            if (!TryPrepareScopeLocked(out var scopeError))
                return Blocked(scopeError);
            if (!_transport.IsReady)
                return Blocked("FC mesh initial synchronization is incomplete; chest publication is read-only.");
            if (_state.PendingChest?.Status == FcPublicationCommandStatus.AwaitingFreshObservation)
                return Blocked("A fresh FC chest observation is already pending.");
            var owner = _authorIdProvider();
            if (string.IsNullOrWhiteSpace(owner))
                return Blocked("Native character author is not selected.");
            if ((_state.LastChestSnapshot is { } publishedSnapshot
                    && !string.Equals(publishedSnapshot.Header.OwnerAuthorId, owner, StringComparison.Ordinal))
                || (_state.PendingChestSnapshot is { } pendingSnapshot
                    && !string.Equals(pendingSnapshot.Header.OwnerAuthorId, owner, StringComparison.Ordinal)))
                return Blocked("A retained chest snapshot belongs to a different native author; writes are blocked.");
            var world = _transport.WorldStore;
            var registerKey = owner + "/chest";
            if (world?.IsForked(registerKey) == true)
                return Blocked("The local chest register is forked; writes are blocked.");

            try
            {
                var worldRevision = 0UL;
                if (world is not null
                    && world.RevisionHighWater.TryGetValue(registerKey, out var authoritativeRevision))
                    worldRevision = authoritativeRevision;
                var revision = checked(Math.Max(_state.ChestReservedRevision, worldRevision) + 1);
                _state = _state with
                {
                    ChestReservedRevision = revision,
                    PendingChest = new FcPublicationCommandState(
                        FcPublicationCommandKind.ChestObservation,
                        revision,
                        string.Empty,
                        FcPublicationCommandStatus.AwaitingFreshObservation,
                        string.Empty),
                    PendingChestSnapshot = null,
                };
                var work = FcChestPublicationWork.ForFreshObservation(
                    _state.ChestRecordId,
                    owner,
                    revision);
                _pendingCommands++;
                if (!_commands.Writer.TryWrite(work))
                {
                    _pendingCommands--;
                    _lastError = "Chest publication command queue is closed.";
                    var pending = _state.PendingChest
                        ?? new FcPublicationCommandState(
                            FcPublicationCommandKind.ChestObservation,
                            revision,
                            string.Empty,
                            FcPublicationCommandStatus.AwaitingFreshObservation,
                            string.Empty);
                    _state = _state with
                    {
                        PendingChest = pending with
                        {
                            Status = FcPublicationCommandStatus.Failed,
                            Error = _lastError,
                        },
                        PendingChestSnapshot = null,
                    };
                    return new(false, _lastError, _state.ChestRecordId, revision, FcPublicationCommandStatus.Failed, null);
                }

                _lastError = string.Empty;
                return new(
                    true,
                    "Chest revision reserved; a fresh complete observation will be read on the next framework tick.",
                    _state.ChestRecordId,
                    revision,
                    FcPublicationCommandStatus.AwaitingFreshObservation,
                    null);
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                return new(false, _lastError, Guid.Empty, 0, FcPublicationCommandStatus.Blocked, null);
            }
        }
    }

    public FcChestPublicationResult RetryPending()
    {
        lock (_gate)
        {
            if (!TryPrepareScopeLocked(out var scopeError))
                return Blocked(scopeError);
            if (!_transport.IsReady)
                return Blocked("FC mesh initial synchronization is incomplete; chest publication is read-only.");
            if (_state.PendingChest is { Status: FcPublicationCommandStatus.AwaitingFreshObservation } pendingReservation
                && _state.PendingChestSnapshot is null)
            {
                if (_pendingCommands > 0 || _freshReadQueued)
                    return new(
                        true,
                        "Fresh chest observation is already queued.",
                        _state.ChestRecordId,
                        pendingReservation.Revision,
                        FcPublicationCommandStatus.AwaitingFreshObservation,
                        null);
                var owner = _authorIdProvider();
                if (string.IsNullOrWhiteSpace(owner))
                    return Blocked("Native character author is not selected.");
                _frameworkReads.Enqueue(new FcChestFrameworkRead(
                    _state.ChestRecordId,
                    owner,
                    pendingReservation.Revision));
                _freshReadQueued = true;
                return new(
                    true,
                    "Fresh chest observation retry queued for the next framework tick.",
                    _state.ChestRecordId,
                    pendingReservation.Revision,
                    FcPublicationCommandStatus.AwaitingFreshObservation,
                    null);
            }
            if (_state.PendingChestSnapshot is not { } snapshot)
                return Blocked("No pending chest publication remains.");
            _state = _state with
            {
                PendingChest = _state.PendingChest is { } pending
                    ? pending with { Status = FcPublicationCommandStatus.Pending, Error = string.Empty }
                    : new FcPublicationCommandState(
                        FcPublicationCommandKind.ChestObservation,
                        snapshot.Header.Revision,
                        FcCanonical.PayloadHash(snapshot),
                        FcPublicationCommandStatus.Pending,
                        string.Empty),
            };
            _pendingCommands++;
            if (!_commands.Writer.TryWrite(new FcChestPublicationWork(snapshot)))
            {
                _pendingCommands--;
                const string error = "Chest publication command queue is closed.";
                _lastError = error;
                var failedPending = _state.PendingChest
                    ?? new FcPublicationCommandState(
                        FcPublicationCommandKind.ChestObservation,
                        snapshot.Header.Revision,
                        FcCanonical.PayloadHash(snapshot),
                        FcPublicationCommandStatus.Pending,
                        string.Empty);
                _state = _state with
                {
                    PendingChest = failedPending with
                    {
                        Status = FcPublicationCommandStatus.Failed,
                        Error = error,
                    },
                };
                return new(false, error, snapshot.Header.RecordId, snapshot.Header.Revision, FcPublicationCommandStatus.Failed, snapshot);
            }
            return new(true, "Pending chest publication retry queued.", snapshot.Header.RecordId, snapshot.Header.Revision, FcPublicationCommandStatus.Pending, snapshot);
        }
    }

    /// <summary>Waits for already queued persistence/native work without running it on the framework thread.</summary>
    public Task DrainAsync(TimeSpan timeout)
        => WaitForIdleAsync(timeout);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_commands.Writer.TryWrite(FcChestPublicationWork.ForPersistenceOnly()))
                _pendingCommands++;
            _commands.Writer.TryComplete();
        }
        _ = FinishDisposeAsync();
    }

    private async Task FinishDisposeAsync()
    {
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_gate)
                _lastError = $"Chest publication worker shutdown failed: {exception.Message}";
        }
        finally
        {
            _shutdown.Cancel();
            _shutdown.Dispose();
        }
    }

    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (var work in _commands.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    ProcessCommand(work);
                }
                catch (Exception exception)
                {
                    if (work.PersistenceOnly)
                    {
                        lock (_gate)
                            _lastError = $"Chest publication worker command failed: {exception.Message}";
                    }
                    else
                        SetFailure(work, exception.Message);
                }
                finally
                {
                    lock (_gate)
                        _pendingCommands = Math.Max(0, _pendingCommands - 1);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private void ProcessCommand(FcChestPublicationWork work)
    {
        FcPublicationState reservedState;
        string scope;
        lock (_gate)
        {
            if (!TryCapturePersistableStateLocked(out reservedState, out var stateError))
            {
                _lastError = stateError;
                return;
            }
            scope = reservedState.AuthorScope;
        }

        try
        {
            // Reserve-before-put: the durable reservation is written by this
            // worker before the native queue call. A crash may leave a gap.
            _stateStore.Save(scope, reservedState);
        }
        catch (Exception exception)
        {
            if (work.PersistenceOnly)
            {
                lock (_gate)
                    _lastError = $"Chest publication reservation persistence failed: {exception.Message}";
                return;
            }
            SetFailure(work, $"Chest publication reservation persistence failed: {exception.Message}");
            return;
        }

        if (work.PersistenceOnly || _disposed)
            return;
        if (work.AwaitFreshObservation)
        {
            lock (_gate)
            {
                if (_state.PendingChest is { } pending
                    && pending.Revision == work.Revision
                    && pending.Status == FcPublicationCommandStatus.AwaitingFreshObservation)
                {
                    _frameworkReads.Enqueue(new FcChestFrameworkRead(
                        work.RecordId,
                        work.OwnerAuthorId,
                        work.Revision));
                    _freshReadQueued = true;
                }
            }
            return;
        }
        if (work.Record is not { } record)
            return;
        if (!_transport.IsReady
            || string.IsNullOrWhiteSpace(_transport.LocalAuthorId)
            || !string.Equals(_transport.LocalAuthorId, record.Header.OwnerAuthorId, StringComparison.Ordinal)
            || !string.Equals(_authorIdProvider(), record.Header.OwnerAuthorId, StringComparison.Ordinal))
        {
            SetFailure(work, "Native mesh author/readiness changed before chest publication; retry is required.");
            return;
        }

        try
        {
            var payload = FcCanonical.SerializeUtf8(record);
            var owner = record.Header.OwnerAuthorId;
            var native = _transport.Put(
                System.Text.Encoding.UTF8.GetBytes(record.Header.RecordId.ToString("D")),
                System.Text.Encoding.UTF8.GetBytes(FcMeshKey.ForRecord(
                    FcRecordTypes.ChestSnapshot,
                    owner,
                    record.Header.RecordId.ToString("D"))),
                System.Text.Encoding.UTF8.GetBytes(FcRecordTypes.ChestSnapshot),
                false,
                0,
                record.Header.Revision,
                payload);
            if (!native.Succeeded)
            {
                SetFailure(work, $"Native chest publication queue rejected the record: {native.ErrorCode}.");
                return;
            }

            FcPublicationState acceptedState;
            lock (_gate)
            {
                var keepNewerPending = (_state.PendingChest?.Revision ?? 0) > record.Header.Revision;
                var latestSnapshot = _state.LastChestSnapshot is { } previous
                    && previous.Header.Revision > record.Header.Revision
                    ? previous
                    : record;
                _state = _state with
                {
                    LastChestSnapshot = latestSnapshot,
                    LastChestHash = FcCanonical.PayloadHash(latestSnapshot),
                    PendingChest = keepNewerPending
                        ? _state.PendingChest
                        : _state.PendingChest is { } pending
                            ? pending with
                            {
                                Status = FcPublicationCommandStatus.AcceptedByNative,
                                Error = string.Empty,
                            }
                            : null,
                    PendingChestSnapshot = keepNewerPending ? _state.PendingChestSnapshot : null,
                };
                acceptedState = FcPublicationStateClone.Clone(_state);
            }
            try
            {
                _stateStore.Save(scope, acceptedState);
            }
            catch (Exception exception)
            {
                SetFailure(work, $"Chest acceptance persistence failed: {exception.Message}");
            }
        }
        catch (Exception exception)
        {
            SetFailure(work, $"Chest publication persistence or enqueue failed: {exception.Message}");
        }
    }

    private async Task WaitForIdleAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_pendingCommands == 0)
                    return;
            }
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private void SetFailure(FcChestPublicationWork work, string message)
    {
        if (work.Record is not { } failedRecord)
        {
            if (work.AwaitFreshObservation)
                SetReservationFailure(work.Revision, message);
            return;
        }
        FcPublicationState failedState;
        string scope;
        lock (_gate)
        {
            _lastError = message;
            if ((_state.PendingChest?.Revision ?? 0) > failedRecord.Header.Revision
                || _state.LastChestSnapshot is { } previous
                    && previous.Header.Revision > failedRecord.Header.Revision)
                return;
            _state = _state with
            {
                PendingChest = _state.PendingChest is { } pending
                    ? pending with { Status = FcPublicationCommandStatus.Failed, Error = message }
                    : new FcPublicationCommandState(
                        FcPublicationCommandKind.ChestObservation,
                        failedRecord.Header.Revision,
                        FcCanonical.PayloadHash(failedRecord),
                        FcPublicationCommandStatus.Failed,
                        message),
                PendingChestSnapshot = failedRecord,
            };
            failedState = FcPublicationStateClone.Clone(_state);
            scope = _state.AuthorScope;
        }
        try
        {
            _stateStore.Save(scope, failedState);
        }
        catch
        {
            // Keep the in-memory failed reservation visible; the next restart
            // remains fail-closed if the durable write could not complete.
        }
    }

    private void SetReservationFailure(ulong revision, string message)
    {
        FcPublicationState failedState;
        string scope;
        lock (_gate)
        {
            _lastError = message;
            if (_state.PendingChest is not { } pending || pending.Revision != revision)
                return;
            _state = _state with
            {
                PendingChest = pending with
                {
                    Status = FcPublicationCommandStatus.Failed,
                    PayloadHash = string.Empty,
                    Error = message,
                },
                PendingChestSnapshot = null,
            };
            failedState = FcPublicationStateClone.Clone(_state);
            scope = _state.AuthorScope;
        }
        try
        {
            _stateStore.Save(scope, failedState);
        }
        catch
        {
            // Keep the failed reservation visible; the next restart remains
            // fail-closed if the durable failure update could not complete.
        }
    }

    private void FailFreshObservation(FcChestFrameworkRead request, string message)
    {
        lock (_gate)
            FailFreshObservationLocked(request, message);
    }

    private void FailFreshObservationLocked(
        FcChestFrameworkRead request,
        string message,
        ChestSnapshotRecord? failedSnapshot = null)
    {
        _lastError = message;
        if (_state.PendingChest is not { } pending || pending.Revision != request.Revision)
            return;
        _state = _state with
        {
            PendingChest = pending with
            {
                Status = FcPublicationCommandStatus.Failed,
                PayloadHash = failedSnapshot is null ? string.Empty : FcCanonical.PayloadHash(failedSnapshot),
                Error = message,
            },
            PendingChestSnapshot = failedSnapshot,
        };
        QueuePersistenceLocked();
    }

    private void QueuePersistenceLocked()
    {
        if (_disposed)
            return;
        _pendingCommands++;
        if (!_commands.Writer.TryWrite(FcChestPublicationWork.ForPersistenceOnly()))
            _pendingCommands--;
    }

    private bool TryCapturePersistableStateLocked(
        out FcPublicationState state,
        out string error)
    {
        if (!TryPrepareScopeLocked(out error))
        {
            state = FcPublicationStateClone.Clone(_state);
            return false;
        }
        state = FcPublicationStateClone.Clone(_state);
        return true;
    }

    private bool TryPrepareScopeLocked(out string error)
    {
        var scope = _authorScopeProvider();
        if (string.IsNullOrWhiteSpace(scope))
        {
            error = "Current character content ID is unavailable; chest publication is read-only.";
            return false;
        }
        if (string.Equals(_state.AuthorScope, scope, StringComparison.Ordinal))
        {
            error = string.Empty;
            return _loadStatus is FcPublicationStateLoadStatus.Clean or FcPublicationStateLoadStatus.Missing;
        }
        error = "Character author scope changed; restart the FC mesh service before publishing.";
        return false;
    }

    private static ChestSnapshotRecord ToRecord(
        FcChestSnapshot snapshot,
        string owner,
        Guid recordId,
        ulong revision)
    {
        if (!snapshot.IsComplete)
            throw new InvalidOperationException(snapshot.IncompleteReason ?? "FC chest observation is incomplete.");
        var items = snapshot.GetChestTotals()
            .Select(pair => new ItemQuantityEntry(
                pair.Key.ItemId,
                pair.Key.IsHq ? FcItemQuality.Hq : FcItemQuality.Nq,
                checked((int)pair.Value)))
            .OrderBy(entry => entry.ItemId)
            .ThenBy(entry => entry.Quality)
            .ToArray();
        var crystals = new CrystalQuantityMap(snapshot.GetCrystalTotals()
            .Select(pair => new CrystalQuantityEntry(pair.Key, checked((int)pair.Value)))
            .OrderBy(entry => entry.CrystalId)
            .ToArray());
        var header = new FcRecordHeader(
            FcProtocolVersion.Current,
            FcProtocolVersion.CurrentSchema,
            FcRecordTypes.ChestSnapshot,
            recordId,
            owner,
            revision);
        return new ChestSnapshotRecord(
            header,
            true,
            FcRecordValidator.CompleteChestPageMask,
            items,
            crystals);
    }

    private static FcChestPublicationResult Blocked(string message)
        => new(false, message, Guid.Empty, 0, FcPublicationCommandStatus.Blocked, null);

    private sealed record FcChestPublicationWork(
        ChestSnapshotRecord? Record,
        bool PersistenceOnly = false,
        bool AwaitFreshObservation = false,
        Guid RecordId = default,
        string OwnerAuthorId = "",
        ulong Revision = 0)
    {
        public static FcChestPublicationWork ForPersistenceOnly()
            => new(null, true);

        public static FcChestPublicationWork ForFreshObservation(
            Guid recordId,
            string ownerAuthorId,
            ulong revision)
            => new(null, false, true, recordId, ownerAuthorId, revision);
    }

    private sealed record FcChestFrameworkRead(Guid RecordId, string OwnerAuthorId, ulong Revision);
}
