using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Publication;

public sealed record FcPublicationResult(
    bool Accepted,
    string Message,
    Guid PublicListId,
    ulong Revision,
    FcPublicationCommandStatus Status)
{
    public static FcPublicationResult Blocked(string message)
        => new(false, message, Guid.Empty, 0, FcPublicationCommandStatus.Blocked);
}

public sealed record FcPublicationDiagnostics(
    string State,
    string AuthorScope,
    bool WritesAllowed,
    string LastError,
    int PendingCommands,
    ulong LastReservedRevision);

/// <summary>
/// Explicit copy-on-publish service for local crafting lists. The worker owns
/// all persistence and native queue calls; UI calls only capture immutable
/// snapshots and enqueue commands.
/// </summary>
public sealed class FcPublishedListService : IDisposable
{
    private readonly object _gate = new();
    private readonly IFcPublicationStateStore _stateStore;
    private readonly IFcPublicationTransport _transport;
    private readonly Func<string?> _authorScopeProvider;
    private readonly Func<string?> _authorIdProvider;
    private readonly Func<FcCompatibilityContext> _compatibilityProvider;
    private readonly Func<uint, FcRecipeOutput?> _recipeLookup;
    private readonly Func<FcLocalListIdentity, bool>? _localListExists;
    private readonly Channel<FcPublicationWork> _commands = Channel.CreateUnbounded<FcPublicationWork>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private FcPublicationState _state;
    private FcPublicationStateLoadStatus _loadStatus;
    private string _lastError = string.Empty;
    private bool _disposed;
    private int _pendingCommands;

    public FcPublishedListService(
        IFcPublicationStateStore stateStore,
        IFcPublicationTransport transport,
        Func<string?> authorScopeProvider,
        Func<string?> authorIdProvider,
        Func<FcCompatibilityContext> compatibilityProvider,
        Func<uint, FcRecipeOutput?>? recipeLookup = null,
        Func<FcLocalListIdentity, bool>? localListExists = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _authorScopeProvider = authorScopeProvider ?? throw new ArgumentNullException(nameof(authorScopeProvider));
        _authorIdProvider = authorIdProvider ?? throw new ArgumentNullException(nameof(authorIdProvider));
        _compatibilityProvider = compatibilityProvider ?? throw new ArgumentNullException(nameof(compatibilityProvider));
        _recipeLookup = recipeLookup ?? DefaultRecipeLookup;
        _localListExists = localListExists;
        var scope = _authorScopeProvider();
        _state = FcPublicationState.Create(scope ?? string.Empty);
        _loadStatus = FcPublicationStateLoadStatus.Corrupt;
        if (!string.IsNullOrWhiteSpace(scope))
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

    public FcPublicationDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                var highest = _state.Lists?.Select(value => value.ReservedRevision).DefaultIfEmpty(0UL).Max() ?? 0UL;
                highest = Math.Max(highest, _state.ChestReservedRevision);
                return new(
                    _loadStatus.ToString(),
                    _state.AuthorScope,
                    CanWriteLocked(),
                    string.IsNullOrWhiteSpace(_lastError) && !CanWriteLocked()
                        ? WriteBlockReasonLocked()
                        : _lastError,
                    _pendingCommands,
                    highest);
            }
        }
    }

    public FcPublicationState State
    {
        get
        {
            lock (_gate)
                return FcPublicationStateClone.Clone(_state);
        }
    }

    public bool IsLocalOwner(PublishedListRecord record)
        => record is not null
            && string.Equals(record.Header.OwnerAuthorId, _authorIdProvider(), StringComparison.Ordinal);

    public IReadOnlyList<FcPublicListView> PublicLists
    {
        get
        {
            var world = _transport.WorldStore;
            var context = _compatibilityProvider();
            var views = new List<FcPublicListView>();
            if (world is not null)
            {
                foreach (var record in world.Lists.Values
                             .Where(value => !world.IsForked(ListRegisterKey(value)))
                             .OrderBy(value => value.ListId)
                             .ThenBy(value => value.Header.OwnerAuthorId, StringComparer.Ordinal))
                {
                    var compatibility = FcPublishedListCompatibility.Evaluate(record, context);
                    views.Add(new FcPublicListView(
                        record,
                        compatibility.IsCompatible,
                        compatibility.Reason,
                        IsOrphaned(record)));
                }
            }

            // A deleted local list keeps its last public snapshot and mapping;
            // show it read-only so the user can explicitly unpublish it.
            lock (_gate)
            {
                foreach (var mapping in _state.Lists ?? Array.Empty<FcLocalPublishedListState>())
                {
                    if (mapping.LastPublishedSnapshot is not { } record
                        || mapping.Pending is not null
                        || world is null
                        || !IsWorldAccepted(world, record)
                        || world.IsForked(ListRegisterKey(record))
                        || views.Any(view => view.Record.ListId == record.ListId
                            && string.Equals(view.Record.Header.OwnerAuthorId, record.Header.OwnerAuthorId, StringComparison.Ordinal)))
                        continue;
                    var compatibility = FcPublishedListCompatibility.Evaluate(record, context);
                    views.Add(new FcPublicListView(record, compatibility.IsCompatible, compatibility.Reason, true));
                }
            }

            return views
                .OrderBy(view => view.Record.ListId)
                .ThenBy(view => view.Record.Header.OwnerAuthorId, StringComparer.Ordinal)
                .ThenByDescending(view => view.Record.Header.Revision)
                .ToArray();
        }
    }

    public FcPublicationResult Publish(CraftingListDefinition list)
        => QueueMappedList(list, FcPublicationCommandKind.Publish);

    public FcPublicationResult Update(CraftingListDefinition list)
        => QueueMappedList(list, FcPublicationCommandKind.Update);

    public FcPublicationResult Unpublish(CraftingListDefinition list)
    {
        if (list is null)
            return FcPublicationResult.Blocked("Crafting list is unavailable.");
        var identity = new FcLocalListIdentity(list.ID, list.CreatedAt);
        lock (_gate)
        {
            if (!TryPrepareScopeLocked(out var scopeError))
                return FcPublicationResult.Blocked(scopeError);
            if (!CanWriteLocked(requireCompatibility: false))
                return FcPublicationResult.Blocked(WriteBlockReasonLocked(requireCompatibility: false));
            var owner = _authorIdProvider();
            if (string.IsNullOrWhiteSpace(owner))
                return FcPublicationResult.Blocked("Native character author is not selected.");
            var mapping = FindMapping(identity);
            if (mapping is null)
                return FcPublicationResult.Blocked("This local list has no retained public snapshot to unpublish.");
            if ((mapping.LastPublishedSnapshot is { } publishedSnapshot
                    && !string.Equals(publishedSnapshot.Header.OwnerAuthorId, owner, StringComparison.Ordinal))
                || (mapping.PendingSnapshot is { } pendingSnapshot
                    && !string.Equals(pendingSnapshot.Header.OwnerAuthorId, owner, StringComparison.Ordinal)))
                return FcPublicationResult.Blocked("A retained public snapshot belongs to a different native author; writes are blocked.");
            var previous = mapping.LastPublishedSnapshot ?? mapping.PendingSnapshot;
            if (previous is null)
                return FcPublicationResult.Blocked("This local list has no retained public snapshot to unpublish.");
            if (IsForkedLocked(owner, mapping.PublicListId))
                return FcPublicationResult.Blocked("The local published-list register is forked; writes are blocked.");
            var revision = ReserveRevisionLocked(owner, mapping.PublicListId, mapping.ReservedRevision);
            var tombstone = previous with
            {
                Header = previous.Header with { Revision = revision },
                Published = false,
            };
            var result = QueueLocked(identity, mapping with
            {
                ReservedRevision = revision,
                Pending = CommandState(FcPublicationCommandKind.Unpublish, tombstone),
                PendingSnapshot = tombstone,
            }, tombstone);
            return result;
        }
    }

    public FcPublicationResult Unpublish(Guid publicListId)
    {
        lock (_gate)
        {
            if (!TryPrepareScopeLocked(out var scopeError))
                return FcPublicationResult.Blocked(scopeError);
            if (!CanWriteLocked(requireCompatibility: false))
                return FcPublicationResult.Blocked(WriteBlockReasonLocked(requireCompatibility: false));
            var owner = _authorIdProvider();
            if (string.IsNullOrWhiteSpace(owner))
                return FcPublicationResult.Blocked("Native character author is not selected.");
            var mapping = FindMappingByPublicId(publicListId);
            if (mapping is null)
                return FcPublicationResult.Blocked("No retained local public snapshot exists for this list.");
            if ((mapping.LastPublishedSnapshot is { } publishedSnapshot
                    && !string.Equals(publishedSnapshot.Header.OwnerAuthorId, owner, StringComparison.Ordinal))
                || (mapping.PendingSnapshot is { } pendingSnapshot
                    && !string.Equals(pendingSnapshot.Header.OwnerAuthorId, owner, StringComparison.Ordinal)))
                return FcPublicationResult.Blocked("A retained public snapshot belongs to a different native author; writes are blocked.");
            var previous = mapping.LastPublishedSnapshot ?? mapping.PendingSnapshot;
            if (previous is null)
                return FcPublicationResult.Blocked("No retained local public snapshot exists for this list.");
            if (IsForkedLocked(owner, publicListId))
                return FcPublicationResult.Blocked("The local published-list register is forked; writes are blocked.");
            var revision = ReserveRevisionLocked(owner, publicListId, mapping.ReservedRevision);
            var tombstone = previous with
            {
                Header = previous.Header with { Revision = revision },
                Published = false,
            };
            var identity = new FcLocalListIdentity(mapping.LocalListId, mapping.CreatedAtUtc);
            return QueueLocked(identity, mapping with
            {
                ReservedRevision = revision,
                Pending = CommandState(FcPublicationCommandKind.Unpublish, tombstone),
                PendingSnapshot = tombstone,
            }, tombstone);
        }
    }

    public FcPublicationResult RetryPending()
    {
        lock (_gate)
        {
            if (!CanWriteLocked(requireCompatibility: false))
                return FcPublicationResult.Blocked(WriteBlockReasonLocked(requireCompatibility: false));
            var work = new List<FcPublicationWork>();
            foreach (var mapping in _state.Lists ?? Array.Empty<FcLocalPublishedListState>())
            {
                if (mapping.PendingSnapshot is { } snapshot)
                    work.Add(new(
                        FcPublicationCommandKindFor(mapping.Pending),
                        snapshot,
                        mapping.PublicListId,
                        new FcLocalListIdentity(mapping.LocalListId, mapping.CreatedAtUtc).StorageKey));
            }
            foreach (var command in work)
                EnqueueLocked(command);
            if (work.Count == 0)
                return FcPublicationResult.Blocked("No pending publication remains.");
            if (work[0].Record is not { } firstRecord)
                return FcPublicationResult.Blocked("Pending publication record is unavailable.");
            return new(true, "Pending publication retry queued.", work[0].RecordId, firstRecord.Header.Revision, FcPublicationCommandStatus.Pending);
        }
    }

    public void ReconcileAuthoritativeState()
    {
        lock (_gate)
        {
            if (!TryPrepareScopeLocked(out _) || !_transport.IsReady)
                return;
            var owner = _authorIdProvider();
            var world = _transport.WorldStore;
            if (string.IsNullOrWhiteSpace(owner) || world is null)
                return;
            var changed = false;
            foreach (var mapping in _state.Lists ?? Array.Empty<FcLocalPublishedListState>())
            {
                var registerKey = owner + "/list/" + mapping.PublicListId.ToString("D");
                var latest = world.RevisionHighWater.TryGetValue(registerKey, out var authoritativeRevision)
                    ? authoritativeRevision
                    : 0UL;
                if (latest > mapping.ReservedRevision)
                {
                    ReplaceMappingLocked(mapping with { ReservedRevision = latest });
                    changed = true;
                }
            }
            var chestKey = owner + "/chest";
            var chestRevision = world.RevisionHighWater.TryGetValue(chestKey, out var authoritativeChestRevision)
                ? authoritativeChestRevision
                : 0UL;
            if (chestRevision > _state.ChestReservedRevision)
            {
                _state = _state with { ChestReservedRevision = chestRevision };
                changed = true;
            }
            if (changed)
                QueuePersistenceLocked();
        }
    }

    /// <summary>Test/integration seam: waits for already queued background work.</summary>
    public Task DrainAsync(TimeSpan timeout)
        => WaitForIdleAsync(timeout);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_commands.Writer.TryWrite(FcPublicationWork.ForPersistenceOnly()))
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
                _lastError = $"Publication worker shutdown failed: {exception.Message}";
        }
        finally
        {
            _shutdown.Cancel();
            _shutdown.Dispose();
        }
    }

    private FcPublicationResult QueueMappedList(CraftingListDefinition list, FcPublicationCommandKind kind)
    {
        if (list is null)
            return FcPublicationResult.Blocked("Crafting list is unavailable.");
        var identity = new FcLocalListIdentity(list.ID, list.CreatedAt);
        lock (_gate)
        {
            if (!TryPrepareScopeLocked(out var scopeError))
                return FcPublicationResult.Blocked(scopeError);
            if (!CanWriteLocked())
                return FcPublicationResult.Blocked(WriteBlockReasonLocked());
            var owner = _authorIdProvider();
            if (string.IsNullOrWhiteSpace(owner))
                return FcPublicationResult.Blocked("Native character author is not selected.");
            var existing = FindMapping(identity);
            if ((existing?.LastPublishedSnapshot is { } publishedSnapshot
                    && !string.Equals(publishedSnapshot.Header.OwnerAuthorId, owner, StringComparison.Ordinal))
                || (existing?.PendingSnapshot is { } pendingSnapshot
                    && !string.Equals(pendingSnapshot.Header.OwnerAuthorId, owner, StringComparison.Ordinal)))
                return FcPublicationResult.Blocked("A retained public snapshot belongs to a different native author; writes are blocked.");
            if (kind == FcPublicationCommandKind.Publish
                && existing?.LastPublishedSnapshot is { Published: true })
                return FcPublicationResult.Blocked("List is already published; use Update Published.");
            if (kind == FcPublicationCommandKind.Update
                && existing?.LastPublishedSnapshot is not { Published: true })
                return FcPublicationResult.Blocked("List has no published snapshot; use Publish.");
            var publicListId = existing?.PublicListId ?? Guid.NewGuid();
            if (IsForkedLocked(owner, publicListId))
                return FcPublicationResult.Blocked("The local published-list register is forked; writes are blocked.");
            var revision = ReserveRevisionLocked(owner, publicListId, existing?.ReservedRevision ?? 0);
            var mapped = FcPublishedListMapper.TryMap(
                list,
                owner,
                _compatibilityProvider().GameVersion,
                revision,
                publicListId,
                _recipeLookup);
            if (!mapped.IsValid || mapped.Record is null)
                return FcPublicationResult.Blocked(mapped.Error);
            var mapping = existing ?? new FcLocalPublishedListState(
                identity.ListId,
                identity.CreatedAtUtc,
                publicListId,
                0,
                null,
                string.Empty,
                null,
                null);
            return QueueLocked(identity, mapping with
            {
                ReservedRevision = revision,
                Pending = CommandState(kind, mapped.Record),
                PendingSnapshot = mapped.Record,
            }, mapped.Record);
        }
    }

    private FcPublicationResult QueueLocked(
        FcLocalListIdentity identity,
        FcLocalPublishedListState mapping,
        PublishedListRecord record)
    {
        ReplaceMappingLocked(mapping);
        var work = new FcPublicationWork(
            mapping.Pending?.Kind ?? FcPublicationCommandKind.Publish,
            record,
            mapping.PublicListId,
            identity.StorageKey);
        if (!EnqueueLocked(work))
        {
            ReplaceMappingLocked(mapping with
            {
                Pending = mapping.Pending is { } pending
                    ? pending with { Status = FcPublicationCommandStatus.Failed, Error = _lastError }
                    : null,
            });
            return new(false, _lastError, mapping.PublicListId, record.Header.Revision, FcPublicationCommandStatus.Failed);
        }
        return new(true, "Publication queued; native record acceptance remains authoritative.", mapping.PublicListId, record.Header.Revision, FcPublicationCommandStatus.Pending);
    }

    private bool EnqueueLocked(FcPublicationWork work)
    {
        _pendingCommands++;
        if (!_commands.Writer.TryWrite(work))
        {
            _pendingCommands--;
            _lastError = "Publication command queue is closed.";
            return false;
        }
        return true;
    }

    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (var work in _commands.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await ProcessCommandAsync(work).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (work.PersistenceOnly)
                    {
                        lock (_gate)
                            _lastError = $"Publication worker command failed: {exception.Message}";
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

    private Task ProcessCommandAsync(FcPublicationWork work)
    {
        FcPublicationState reservedState;
        string scope;
        lock (_gate)
        {
            if (!TryCapturePersistableStateLocked(out reservedState, out var stateError))
            {
                _lastError = stateError;
                return Task.CompletedTask;
            }
            scope = reservedState.AuthorScope;
        }

        try
        {
            // Reservation is persisted before the native put. A crash may
            // leave a gap, but it can never make a revision reusable.
            _stateStore.Save(scope, reservedState);
        }
        catch (Exception exception)
        {
            if (work.PersistenceOnly)
            {
                lock (_gate)
                    _lastError = $"Publication reservation persistence failed: {exception.Message}";
                return Task.CompletedTask;
            }
            SetFailure(work, $"Publication reservation persistence failed: {exception.Message}");
            return Task.CompletedTask;
        }

        if (work.PersistenceOnly || _disposed)
            return Task.CompletedTask;
        if (work.Record is not { } record)
            return Task.CompletedTask;
        if (!_transport.IsReady
            || string.IsNullOrWhiteSpace(_transport.LocalAuthorId)
            || !string.Equals(_transport.LocalAuthorId, record.Header.OwnerAuthorId, StringComparison.Ordinal)
            || !string.Equals(_authorIdProvider(), record.Header.OwnerAuthorId, StringComparison.Ordinal))
        {
            SetFailure(work, "Native mesh author/readiness changed before publication; retry is required.");
            return Task.CompletedTask;
        }

        try
        {
            var recordBytes = FcCanonical.SerializeUtf8(record);
            var result = _transport.Put(
                Encoding.UTF8.GetBytes(record.ListId.ToString("D")),
                Encoding.UTF8.GetBytes(FcMeshKey.ForRecord(
                    FcRecordTypes.PublishedList,
                    record.Header.OwnerAuthorId,
                    record.ListId.ToString("D"))),
                Encoding.UTF8.GetBytes(FcRecordTypes.PublishedList),
                false,
                0,
                record.Header.Revision,
                recordBytes);
            if (!result.Succeeded)
            {
                SetFailure(work, $"Native publication queue rejected the record: {result.ErrorCode}.");
                return Task.CompletedTask;
            }

            FcPublicationState acceptedState;
            lock (_gate)
            {
                var mapping = FindMappingByPublicId(record.ListId);
                if (mapping is null)
                    return Task.CompletedTask;
                var keepNewerPending = (mapping.Pending?.Revision ?? 0) > record.Header.Revision;
                var latestSnapshot = mapping.LastPublishedSnapshot is { } previous
                    && previous.Header.Revision > record.Header.Revision
                    ? previous
                    : record;
                ReplaceMappingLocked(mapping with
                {
                    LastPublishedSnapshot = latestSnapshot,
                    LastPublishedHash = FcCanonical.PayloadHash(latestSnapshot),
                    Pending = keepNewerPending ? mapping.Pending : null,
                    PendingSnapshot = keepNewerPending ? mapping.PendingSnapshot : null,
                });
                acceptedState = FcPublicationStateClone.Clone(_state);
            }
            try
            {
                _stateStore.Save(scope, acceptedState);
            }
            catch (Exception exception)
            {
                SetFailure(work, $"Publication acceptance persistence failed: {exception.Message}");
            }
        }
        catch (Exception exception)
        {
            SetFailure(work, $"Publication persistence or enqueue failed: {exception.Message}");
        }
        return Task.CompletedTask;
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

    private bool TryPrepareScopeLocked(out string error)
    {
        var scope = _authorScopeProvider();
        if (string.IsNullOrWhiteSpace(scope))
        {
            error = "Current character content ID is unavailable; publication is read-only.";
            return false;
        }
        if (string.Equals(_state.AuthorScope, scope, StringComparison.Ordinal))
        {
            error = string.Empty;
            return true;
        }
        error = "Character author scope changed; restart the FC mesh service before publishing.";
        return false;
    }

    private bool CanWriteLocked(bool requireCompatibility = true)
        => !_disposed
            && _loadStatus is FcPublicationStateLoadStatus.Clean or FcPublicationStateLoadStatus.Missing
            && _transport.IsReady
            && (!requireCompatibility || _compatibilityProvider().IsValid)
            && !string.IsNullOrWhiteSpace(_authorScopeProvider())
            && !string.IsNullOrWhiteSpace(_authorIdProvider());

    private string WriteBlockReasonLocked(bool requireCompatibility = true)
    {
        if (_loadStatus == FcPublicationStateLoadStatus.Corrupt)
            return string.IsNullOrWhiteSpace(_lastError) ? "Publication state is corrupt; writes are blocked." : _lastError;
        if (string.IsNullOrWhiteSpace(_authorScopeProvider()))
            return "Current character content ID is unavailable; publication is read-only.";
        if (string.IsNullOrWhiteSpace(_authorIdProvider()))
            return "Native character author is not selected.";
        if (!_transport.IsReady)
            return "FC mesh initial synchronization is incomplete; publication is read-only.";
        if (requireCompatibility && !_compatibilityProvider().IsValid)
            return "Game compatibility version is unavailable; publication is read-only.";
        return "Publication writes are unavailable.";
    }

    private FcLocalPublishedListState? FindMapping(FcLocalListIdentity identity)
        => (_state.Lists ?? Array.Empty<FcLocalPublishedListState>())
            .FirstOrDefault(value => value.LocalListId == identity.ListId
                && value.CreatedAtUtc == identity.CreatedAtUtc);

    private FcLocalPublishedListState? FindMappingByPublicId(Guid publicListId)
        => (_state.Lists ?? Array.Empty<FcLocalPublishedListState>())
            .FirstOrDefault(value => value.PublicListId == publicListId);

    private void ReplaceMappingLocked(FcLocalPublishedListState mapping)
    {
        var values = (_state.Lists ?? Array.Empty<FcLocalPublishedListState>()).ToList();
        var index = values.FindIndex(value => value.LocalListId == mapping.LocalListId
            && value.CreatedAtUtc == mapping.CreatedAtUtc);
        if (index < 0)
            values.Add(mapping);
        else
            values[index] = mapping;
        _state = _state with { Lists = values.OrderBy(value => value.LocalListId).ThenBy(value => value.CreatedAtUtc).ToArray() };
    }

    private ulong ReserveRevisionLocked(string owner, Guid publicListId, ulong localHighWater)
    {
        var registerKey = owner + "/list/" + publicListId.ToString("D");
        var replica = 0UL;
        if (_transport.WorldStore is { } world
            && world.RevisionHighWater.TryGetValue(registerKey, out var authoritativeRevision))
            replica = authoritativeRevision;
        return checked(Math.Max(localHighWater, replica) + 1);
    }

    private bool IsForkedLocked(string owner, Guid publicListId)
        => _transport.WorldStore?.IsForked(owner + "/list/" + publicListId.ToString("D")) == true;

    private FcPublicationCommandState CommandState(FcPublicationCommandKind kind, PublishedListRecord record)
        => new(kind, record.Header.Revision, FcCanonical.PayloadHash(record), FcPublicationCommandStatus.Pending, string.Empty);

    private void SetFailure(FcPublicationWork work, string message)
    {
        FcPublicationState? failedState = null;
        string? scope = null;
        lock (_gate)
        {
            if (work.Record is not { } failedRecord)
                return;
            _lastError = message;
            var mapping = FindMappingByPublicId(failedRecord.ListId);
            if (mapping is null)
                return;
            if ((mapping.Pending?.Revision ?? 0) > failedRecord.Header.Revision)
                return;
            if (mapping.LastPublishedSnapshot is { } previous
                && previous.Header.Revision > failedRecord.Header.Revision)
                return;
            ReplaceMappingLocked(mapping with
            {
                Pending = mapping.Pending is null
                    ? new(work.Kind, failedRecord.Header.Revision, FcCanonical.PayloadHash(failedRecord), FcPublicationCommandStatus.Failed, message)
                    : mapping.Pending with { Status = FcPublicationCommandStatus.Failed, Error = message },
                PendingSnapshot = failedRecord,
            });
            failedState = FcPublicationStateClone.Clone(_state);
            scope = _state.AuthorScope;
        }
        if (failedState is not null && scope is not null)
        {
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
    }

    private void QueuePersistenceLocked()
    {
        if (_disposed)
            return;
        EnqueueLocked(FcPublicationWork.ForPersistenceOnly());
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

    private bool IsOrphaned(PublishedListRecord record)
    {
        var owner = _authorIdProvider();
        if (string.IsNullOrWhiteSpace(owner)
            || !string.Equals(record.Header.OwnerAuthorId, owner, StringComparison.Ordinal))
            return false;
        var mapping = _state.Lists?.FirstOrDefault(value => value.PublicListId == record.ListId
            && value.LastPublishedSnapshot is not null);
        if (mapping is null)
            return true;
        return _localListExists?.Invoke(new FcLocalListIdentity(mapping.LocalListId, mapping.CreatedAtUtc)) == false;
    }

    private bool IsWorldAccepted(FcWorldStore world, PublishedListRecord record)
        => world.Lists.Values.Any(value => value.ListId == record.ListId
            && value.Header.Revision == record.Header.Revision
            && string.Equals(value.Header.OwnerAuthorId, record.Header.OwnerAuthorId, StringComparison.Ordinal)
            && string.Equals(FcCanonical.PayloadHash(value), FcCanonical.PayloadHash(record), StringComparison.Ordinal));

    private static string ListRegisterKey(PublishedListRecord record)
        => record.Header.OwnerAuthorId + "/list/" + record.ListId.ToString("D");

    private static FcRecipeOutput? DefaultRecipeLookup(uint recipeId)
    {
        var recipe = RecipeManager.GetRecipe(recipeId);
        if (recipe is not { } value)
            return null;
        var result = value.ItemResult.Value;
        return new FcRecipeOutput(
            result.RowId,
            checked((uint)value.AmountResult),
            result.CanBeHq);
    }

    private static FcPublicationCommandKind FcPublicationCommandKindFor(FcPublicationCommandState? state)
        => state?.Kind ?? FcPublicationCommandKind.Publish;

    private sealed record FcPublicationWork(
        FcPublicationCommandKind Kind,
        PublishedListRecord? Record,
        Guid RecordId,
        string IdentityKey,
        bool PersistenceOnly = false)
    {
        public static FcPublicationWork ForPersistenceOnly()
            => new(FcPublicationCommandKind.Publish, null, Guid.Empty, string.Empty, true);
    }
}
