using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GatherBuddy.FcMesh.Chest;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Publication;

public sealed record FcChestLocationPublicationState(
    int Version,
    string AuthorScope,
    ulong ReservedRevision,
    FcEstateChestLocationRecord? LastPublished,
    string LastPublishedHash,
    FcEstateChestLocationRecord? Pending,
    string LastError)
{
    public const int CurrentVersion = 1;

    public static FcChestLocationPublicationState Create(string scope)
        => new(CurrentVersion, scope ?? string.Empty, 0, null, string.Empty, null, string.Empty);
}

public enum FcChestLocationPublicationStatus : byte
{
    Pending,
    Accepted,
    Failed,
    Blocked,
}

public sealed record FcChestLocationPublicationResult(
    bool Accepted,
    string Message,
    ulong Revision,
    FcChestLocationPublicationStatus Status)
{
    public static FcChestLocationPublicationResult Blocked(string message)
        => new(false, message, 0, FcChestLocationPublicationStatus.Blocked);
}

public sealed record FcChestLocationPublicationDiagnostics(
    string State,
    string AuthorScope,
    bool WritesAllowed,
    bool HasPublishedLocation,
    bool HasPending,
    ulong ReservedRevision,
    string LastError);

public interface IFcChestLocationStateStore
{
    FcChestLocationPublicationState Load(string authorScope);
    void Save(string authorScope, FcChestLocationPublicationState state);
}

public sealed class FcFileChestLocationStateStore : IFcChestLocationStateStore
{
    private static readonly object Gate = new();
    private readonly string _directory;

    public FcFileChestLocationStateStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Location state directory is required.", nameof(directory));
        _directory = directory;
    }

    public FcChestLocationPublicationState Load(string authorScope)
    {
        if (string.IsNullOrWhiteSpace(authorScope))
            return FcChestLocationPublicationState.Create(string.Empty);
        lock (Gate)
        {
            var path = PathFor(authorScope);
            if (!File.Exists(path))
                return FcChestLocationPublicationState.Create(authorScope);
            try
            {
                var state = JsonSerializer.Deserialize(
                    File.ReadAllBytes(path),
                    FcJsonContext.Default.FcChestLocationPublicationState);
                if (state is null
                    || state.Version != FcChestLocationPublicationState.CurrentVersion
                    || !string.Equals(state.AuthorScope, authorScope, StringComparison.Ordinal)
                    || state.LastPublished?.Header is { } publishedHeader && publishedHeader.RecordId != FcChestLocationRegister.LocationId
                    || state.Pending?.Header is { } pendingHeader && pendingHeader.RecordId != FcChestLocationRegister.LocationId)
                    throw new InvalidDataException("FC chest location state schema or scope is invalid.");
                return state;
            }
            catch (Exception exception) when (exception is IOException
                or JsonException
                or InvalidDataException
                or NotSupportedException
                or ArgumentException)
            {
                // A corrupt local register cannot safely be repaired by
                // inventing a new revision. Keep the explicit invalid version
                // so every write path remains fail-closed until the state is
                // restored or removed by an operator.
                return FcChestLocationPublicationState.Create(authorScope) with
                {
                    Version = 0,
                    LastError = $"FC chest location state is corrupt: {exception.Message}",
                };
            }
        }
    }

    public void Save(string authorScope, FcChestLocationPublicationState state)
    {
        if (string.IsNullOrWhiteSpace(authorScope)
            || state is null
            || state.Version != FcChestLocationPublicationState.CurrentVersion
            || !string.Equals(state.AuthorScope, authorScope, StringComparison.Ordinal))
            throw new InvalidDataException("FC chest location state scope or schema is invalid.");
        lock (Gate)
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(authorScope);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(
                    state,
                    FcJsonContext.Default.FcChestLocationPublicationState);
                using (var stream = new FileStream(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                File.Move(temporary, path, true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch
                {
                    // Preserve the original failure; the old state remains intact.
                }
            }
        }
    }

    private string PathFor(string scope)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(scope));
        return Path.Combine(_directory, "fc-chest-location-" + Convert.ToHexString(digest).ToLowerInvariant() + ".json");
    }
}

public sealed class FcInMemoryChestLocationStateStore : IFcChestLocationStateStore
{
    private readonly Dictionary<string, FcChestLocationPublicationState> _states = new(StringComparer.Ordinal);
    public bool FailWrites { get; set; }

    public FcChestLocationPublicationState Load(string authorScope)
        => _states.TryGetValue(authorScope, out var state)
            ? state
            : FcChestLocationPublicationState.Create(authorScope);

    public void Save(string authorScope, FcChestLocationPublicationState state)
    {
        if (FailWrites)
            throw new IOException("Synthetic FC chest location persistence failure.");
        _states[authorScope] = state;
    }
}

/// <summary>
/// Author-owned shared FC-estate chest location register. The service reserves
/// and persists a revision before enqueueing native Put; a failed Put leaves a
/// durable retryable pending record.
/// </summary>
public sealed class FcChestLocationPublicationService : IDisposable
{
    private sealed record Work(FcEstateChestLocationRecord Record, bool PersistenceOnly = false);

    private readonly object _gate = new();
    private readonly IFcChestLocationStateStore _stateStore;
    private readonly IFcPublicationTransport _transport;
    private readonly Func<string?> _scopeProvider;
    private readonly Func<string?> _authorProvider;
    private readonly Func<FcCompatibilityContext> _compatibilityProvider;
    private readonly Channel<Work> _commands = Channel.CreateUnbounded<Work>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private FcChestLocationPublicationState _state;
    private string _lastError = string.Empty;
    private bool _disposed;
    private int _pendingCommands;

    public FcChestLocationPublicationService(
        IFcChestLocationStateStore stateStore,
        IFcPublicationTransport transport,
        Func<string?> scopeProvider,
        Func<string?> authorProvider,
        Func<FcCompatibilityContext> compatibilityProvider)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));
        _authorProvider = authorProvider ?? throw new ArgumentNullException(nameof(authorProvider));
        _compatibilityProvider = compatibilityProvider ?? throw new ArgumentNullException(nameof(compatibilityProvider));
        var scope = _scopeProvider() ?? string.Empty;
        _state = _stateStore.Load(scope);
        _lastError = _state.LastError;
        _worker = Task.Run(ProcessCommandsAsync);
    }

    public FcChestLocationPublicationState State
    {
        get { lock (_gate) return _state with { }; }
    }

    public FcChestLocationPublicationDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return new(
                    _state.Version == FcChestLocationPublicationState.CurrentVersion ? "Ready" : "Corrupt",
                    _state.AuthorScope,
                    CanWriteLocked(),
                    _state.LastPublished is { Published: true },
                    _state.Pending is not null,
                    _state.ReservedRevision,
                    string.IsNullOrWhiteSpace(_lastError) ? _state.LastError : _lastError);
            }
        }
    }

    public FcEstateChestLocationRecord? LastPublished
    {
        get { lock (_gate) return _state.LastPublished; }
    }

    public FcChestLocationPublicationResult Register(FcChestLocationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            if (!CanWriteLocked())
                return FcChestLocationPublicationResult.Blocked(WriteBlockReasonLocked());
            if (_state.Pending is not null)
                return FcChestLocationPublicationResult.Blocked("A pending FC chest location publication must be accepted or retried first.");
            var author = _authorProvider();
            var scope = _scopeProvider();
            if (string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(scope))
                return FcChestLocationPublicationResult.Blocked("Character author is unavailable.");
            if (IsForkedLocked(author))
                return FcChestLocationPublicationResult.Blocked("FC chest location register is forked; writes are blocked.");
            var revision = NextRevisionLocked(author);
            FcEstateChestLocationRecord record;
            try
            {
                record = FcChestLocationRegister.Create(observation, author, revision);
                _state = _state with
                {
                    ReservedRevision = revision,
                    Pending = record,
                    LastError = string.Empty,
                };
                _stateStore.Save(scope, _state);
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                return FcChestLocationPublicationResult.Blocked($"FC chest location registration could not be persisted: {exception.Message}");
            }
            return EnqueueLocked(record);
        }
    }

    public FcChestLocationPublicationResult Unregister()
    {
        lock (_gate)
        {
            if (!CanWriteLocked())
                return FcChestLocationPublicationResult.Blocked(WriteBlockReasonLocked());
            if (_state.Pending is not null)
                return FcChestLocationPublicationResult.Blocked("A pending FC chest location publication must be accepted or retried first.");
            if (_state.LastPublished is not { Published: true } previous)
                return FcChestLocationPublicationResult.Blocked("No registered FC chest location exists.");
            var author = _authorProvider();
            var scope = _scopeProvider();
            if (string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(scope))
                return FcChestLocationPublicationResult.Blocked("Character author is unavailable.");
            var revision = NextRevisionLocked(author);
            var tombstone = FcChestLocationRegister.CreateTombstone(previous, author, revision);
            try
            {
                _state = _state with { ReservedRevision = revision, Pending = tombstone, LastError = string.Empty };
                _stateStore.Save(scope, _state);
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                return FcChestLocationPublicationResult.Blocked($"FC chest location tombstone could not be persisted: {exception.Message}");
            }
            return EnqueueLocked(tombstone);
        }
    }

    public FcChestLocationPublicationResult RetryPending()
    {
        lock (_gate)
        {
            if (_state.Pending is not { } pending)
                return FcChestLocationPublicationResult.Blocked("No pending FC chest location publication exists.");
            if (!CanWriteLocked())
                return FcChestLocationPublicationResult.Blocked(WriteBlockReasonLocked());
            return EnqueueLocked(pending);
        }
    }

    public Task DrainAsync(TimeSpan timeout)
        => WaitForIdleAsync(timeout);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _commands.Writer.TryWrite(new Work(null!, true));
            _commands.Writer.TryComplete();
        }
        _ = FinishDisposeAsync();
    }

    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (var work in _commands.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    if (!work.PersistenceOnly)
                        await ProcessWorkAsync(work).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (!work.PersistenceOnly)
                        MarkFailed(work.Record, exception.Message);
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

    private async Task ProcessWorkAsync(Work work)
    {
        var record = work.Record;
        var author = _authorProvider();
        var scope = _scopeProvider();
        if (string.IsNullOrWhiteSpace(scope)
            || string.IsNullOrWhiteSpace(author)
            || !_transport.IsReady
            || !string.Equals(author, record.Header.OwnerAuthorId, StringComparison.Ordinal)
            || !string.Equals(_transport.LocalAuthorId, record.Header.OwnerAuthorId, StringComparison.Ordinal))
        {
            MarkFailed(record, "Native mesh author/readiness changed before FC chest location publication.");
            return;
        }
        lock (_gate)
        {
            var pending = _state.Pending;
            if (pending is null
                || pending.Header.Revision != record.Header.Revision
                || FcCanonical.SemanticHash(pending) != FcCanonical.SemanticHash(record))
                return;
        }
        var result = _transport.Put(
            Encoding.UTF8.GetBytes(FcChestLocationRegister.LocationId.ToString("D")),
            Encoding.UTF8.GetBytes(FcMeshKey.ForRecord(
                FcRecordTypes.FcChestLocation,
                record.Header.OwnerAuthorId,
                FcChestLocationRegister.LocationId.ToString("D"))),
            Encoding.UTF8.GetBytes(FcRecordTypes.FcChestLocation),
            false,
            0,
            record.Header.Revision,
            FcCanonical.SerializeUtf8(record));
        if (!result.Succeeded)
        {
            MarkFailed(record, $"Native FC chest location publication rejected: {result.ErrorCode}.");
            return;
        }
        lock (_gate)
        {
            var keepNewerPending = _state.Pending?.Header.Revision > record.Header.Revision;
            _state = _state with
            {
                LastPublished = record,
                LastPublishedHash = FcCanonical.PayloadHash(record),
                Pending = keepNewerPending ? _state.Pending : null,
                LastError = string.Empty,
            };
            _stateStore.Save(scope!, _state);
        }
        await Task.CompletedTask;
    }

    private FcChestLocationPublicationResult EnqueueLocked(FcEstateChestLocationRecord record)
    {
        _pendingCommands++;
        if (!_commands.Writer.TryWrite(new Work(record)))
        {
            _pendingCommands--;
            return FcChestLocationPublicationResult.Blocked("FC chest location publication queue is closed.");
        }
        return new(true, "FC chest location publication queued.", record.Header.Revision, FcChestLocationPublicationStatus.Pending);
    }

    private bool CanWriteLocked()
    {
        var author = _authorProvider();
        var scope = _scopeProvider();
        var context = _compatibilityProvider();
        return !_disposed
            && _state.Version == FcChestLocationPublicationState.CurrentVersion
            && !string.IsNullOrWhiteSpace(scope)
            && string.Equals(scope, _state.AuthorScope, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(author)
            && _transport.IsReady
            && string.Equals(author, _transport.LocalAuthorId, StringComparison.Ordinal)
            && context.IsValid;
    }

    private string WriteBlockReasonLocked()
        => !_transport.IsReady
            ? "Native FC mesh is not ready."
            : !_compatibilityProvider().IsValid
                ? "Game compatibility is unavailable."
                : "FC chest location publication state or character author is unavailable.";

    private bool IsForkedLocked(string author)
        => _transport.WorldStore?.IsForked(author + "/fc-chest-location") == true;

    private ulong NextRevisionLocked(string author)
    {
        var world = _transport.WorldStore;
        var highWater = world?.RevisionHighWater.GetValueOrDefault(author + "/fc-chest-location") ?? 0;
        return checked(Math.Max(_state.ReservedRevision, highWater) + 1);
    }

    private void MarkFailed(FcEstateChestLocationRecord record, string message)
    {
        lock (_gate)
        {
            _lastError = message;
            _state = _state with { Pending = record, LastError = message };
            try
            {
                var scope = _scopeProvider();
                if (!string.IsNullOrWhiteSpace(scope))
                    _stateStore.Save(scope, _state);
            }
            catch
            {
                // Preserve the in-memory failure; retry remains fail-closed.
            }
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
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private async Task FinishDisposeAsync()
    {
        try { await _worker.ConfigureAwait(false); }
        catch { }
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
