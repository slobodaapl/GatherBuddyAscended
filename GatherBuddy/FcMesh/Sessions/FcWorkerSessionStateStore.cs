using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.Sessions;

public sealed class FcInMemoryWorkerSessionStateStore : IFcWorkerSessionStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FcWorkerSessionState> _states = new(StringComparer.Ordinal);
    private readonly HashSet<string> _markers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _corrupt = new(StringComparer.Ordinal);

    public bool FailWrites { get; set; }

    public FcWorkerSessionLoadResult Load(string authorScope)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(authorScope))
                return Corrupt(authorScope, "Character author scope is unavailable.");
            if (_corrupt.Contains(authorScope))
                return Corrupt(authorScope, "Stored worker session state is corrupt.");
            if (!_states.TryGetValue(authorScope, out var state))
            {
                if (_markers.Contains(authorScope))
                    return Corrupt(authorScope, "Worker session initialization marker exists but state is missing.");
                return new(FcWorkerSessionLoadStatus.Missing,
                    FcWorkerSessionState.Create(authorScope, string.Empty), string.Empty);
            }
            if (!_markers.Contains(authorScope))
                return Corrupt(authorScope, "Worker session state exists without its initialization marker.");
            try
            {
                Validate(authorScope, state);
                return new(FcWorkerSessionLoadStatus.Clean, Clone(state), string.Empty);
            }
            catch (Exception exception)
            {
                return Corrupt(authorScope, exception.Message);
            }
        }
    }

    public void Save(string authorScope, FcWorkerSessionState state)
    {
        if (FailWrites)
            throw new IOException("Synthetic worker session-state write failure.");
        Validate(authorScope, state);
        lock (_gate)
        {
            if (_markers.Contains(authorScope) && !_states.ContainsKey(authorScope))
                throw new InvalidDataException("Worker session initialization marker exists but state is missing.");
            if (_states.TryGetValue(authorScope, out var current))
                state = Merge(current, state);
            _markers.Add(authorScope);
            _states[authorScope] = Clone(state);
        }
    }

    public void MarkCorrupt(string authorScope)
    {
        lock (_gate)
            _corrupt.Add(authorScope);
    }

    public void MarkInitializationMarkerOnly(string authorScope)
    {
        lock (_gate)
            _markers.Add(authorScope);
    }

    private static FcWorkerSessionLoadResult Corrupt(string? scope, string error)
        => new(
            FcWorkerSessionLoadStatus.Corrupt,
            FcWorkerSessionState.Create(scope ?? string.Empty, string.Empty),
            error);

    internal static void Validate(string authorScope, FcWorkerSessionState state)
    {
        if (string.IsNullOrWhiteSpace(authorScope) || state is null
            || !string.Equals(state.AuthorScope, authorScope, StringComparison.Ordinal)
            || state.Version != FcWorkerSessionState.CurrentVersion
            || !string.Equals(state.BackendKind, FcWorkerSessionState.CurrentBackendKind, StringComparison.Ordinal)
            || state.BackendStorageVersion != FcWorkerSessionState.CurrentBackendStorageVersion
            || string.IsNullOrWhiteSpace(state.BackendMarker)
            || string.IsNullOrWhiteSpace(state.AuthorId)
            || state.WorkerRegisterId == Guid.Empty
            || state.WorkerRegisterId != FcWorkerSessionState.StableWorkerRegisterId(state.AuthorId))
            throw new InvalidDataException("Worker session state schema or scope is invalid.");

        if (state.SessionId == Guid.Empty
            && (state.SessionGeneration != 0 || state.LastReservedRevision != 0
                || state.LastAcceptedWorker is not null || state.PendingWorker is not null
                || state.LedgerRecovery is not null))
            throw new InvalidDataException("Worker session state has incomplete session identity.");
        if (state.SessionId != Guid.Empty && state.SessionGeneration == 0)
            throw new InvalidDataException("Worker session generation is missing.");
        if (state.PendingWorker is { } pending)
        {
            if (pending.Header is null
                || !string.Equals(pending.Header.OwnerAuthorId, state.AuthorId, StringComparison.Ordinal)
                || pending.Header.RecordId != state.WorkerRegisterId
                || pending.SessionId != state.SessionId
                || pending.SessionGeneration != state.SessionGeneration
                || pending.Header.Revision != state.LastReservedRevision
                || state.PendingKind is null)
                throw new InvalidDataException("Pending worker command does not match its reservation.");
        }
        else if (state.PendingKind is not null)
            throw new InvalidDataException("Worker pending command kind exists without a pending payload.");
        if (state.LastAcceptedWorker is { } accepted)
        {
            if (accepted.Header is null
                || !string.Equals(accepted.Header.OwnerAuthorId, state.AuthorId, StringComparison.Ordinal)
                || accepted.Header.RecordId != state.WorkerRegisterId
                || accepted.SessionId != state.SessionId
                || accepted.SessionGeneration != state.SessionGeneration
                || accepted.Header.Revision > state.LastReservedRevision)
                throw new InvalidDataException("Accepted worker state does not match its author reservation.");
        }
        if (state.LedgerRecovery is { } recovery)
        {
            if (recovery.SessionId != state.SessionId
                || recovery.SessionGeneration != state.SessionGeneration
                || recovery.Selection is null)
                throw new InvalidDataException("Contribution recovery belongs to another session.");
        }
        if (state.Selection is null)
            throw new InvalidDataException("Worker selection is missing.");
        if (state.ObservedAtomicTransfers is null
            || state.ObservedAtomicTransfers.Length > FcWorkerSessionService.MaxLogicalEntries
            || state.ObservedAtomicTransfers.Any(observation =>
                observation is null
                || observation.OperationId == Guid.Empty
                || string.IsNullOrWhiteSpace(observation.TransferSemanticHash)
                || observation.TransferSemanticHash.Length != 64)
            || state.ObservedAtomicTransfers.Select(observation => observation.OperationId).Distinct().Count()
                != state.ObservedAtomicTransfers.Length)
            throw new InvalidDataException("Atomic transfer observation ledger is invalid.");
        if (state.DependencyClosure is null
            || state.DependencyClosure.Length > FcWorkerSessionService.MaxLogicalEntries
            || state.DependencyClosure.Any(key => key.ItemId == 0 || !Enum.IsDefined(key.Quality))
            || state.DependencyClosure.Distinct().Count() != state.DependencyClosure.Length)
            throw new InvalidDataException("Worker dependency closure is invalid.");
        _ = new FcItemQuantityMap(state.LastAcceptedWorker?.HeldInventory ?? Array.Empty<ItemQuantityEntry>());
    }

    internal static FcWorkerSessionState Merge(FcWorkerSessionState current, FcWorkerSessionState incoming)
    {
        if (!string.Equals(current.AuthorScope, incoming.AuthorScope, StringComparison.Ordinal)
            || !string.Equals(current.AuthorId, incoming.AuthorId, StringComparison.Ordinal)
            || current.WorkerRegisterId != incoming.WorkerRegisterId)
            throw new InvalidDataException("Worker session state author identity changed.");
        if (incoming.LastReservedRevision < current.LastReservedRevision)
            throw new InvalidDataException("Worker revision reservation regressed.");
        if (incoming.SessionGeneration < current.SessionGeneration)
            throw new InvalidDataException("Worker generation regressed.");
        if (incoming.SessionGeneration == current.SessionGeneration
            && incoming.SessionId != Guid.Empty && current.SessionId != Guid.Empty
            && incoming.SessionId != current.SessionId)
            throw new InvalidDataException("Worker session identity changed within a generation.");
        return incoming;
    }

    internal static FcWorkerSessionState Clone(FcWorkerSessionState state)
        => state with
        {
            Selection = state.Selection is null
                ? FcFulfillmentSelection.Specific()
                : state.Selection with { ListIds = (state.Selection.ListIds ?? Array.Empty<Guid>()).ToArray() },
            DependencyClosure = (state.DependencyClosure ?? Array.Empty<FcQuantityKey>()).ToArray(),
            ObservedAtomicTransfers = (state.ObservedAtomicTransfers ?? Array.Empty<FcObservedAtomicTransfer>())
                .Select(observation => observation with { })
                .ToArray(),
            LastAcceptedWorker = Clone(state.LastAcceptedWorker),
            PendingWorker = Clone(state.PendingWorker),
            LedgerRecovery = Clone(state.LedgerRecovery),
        };

    internal static WorkerSessionRecord CloneWorker(WorkerSessionRecord value)
        => Clone(value) ?? throw new ArgumentNullException(nameof(value));

    private static WorkerSessionRecord? Clone(WorkerSessionRecord? value)
        => value is null
            ? null
            : value with
            {
                Header = value.Header with { },
                Character = value.Character with { },
                Selection = value.Selection with { ListIds = (value.Selection.ListIds ?? Array.Empty<Guid>()).ToArray() },
                HeldInventory = (value.HeldInventory ?? Array.Empty<ItemQuantityEntry>()).Select(entry => entry with { }).ToArray(),
                CurrentTarget = value.CurrentTarget is { } target ? target with { } : null,
                CraftQueue = (value.CraftQueue ?? Array.Empty<FcLogicalQueueEntry>()).Select(entry => entry with { }).ToArray(),
                GatherTargetOrder = (value.GatherTargetOrder ?? Array.Empty<uint>()).ToArray(),
            };

    private static FcContributionLedgerRecovery? Clone(FcContributionLedgerRecovery? value)
        => value is null
            ? null
            : new(
                value.SessionId,
                value.SessionGeneration,
                value.Selection with { ListIds = (value.Selection.ListIds ?? Array.Empty<Guid>()).ToArray() },
                value.StartingInventory,
                value.ProtectedInventory,
                value.AttributedInventory,
                value.UseOwnStock,
                (value.QueueReferences ?? Array.Empty<FcLogicalQueueEntry>()).Select(entry => entry with { }).ToArray());
}

public sealed class FcFileWorkerSessionStateStore : IFcWorkerSessionStateStore
{
    private static readonly object ProcessGate = new();
    private static readonly byte[] MarkerBytes =
        Encoding.UTF8.GetBytes(FcWorkerSessionState.CurrentInitializationMarker);
    private readonly string _directory;

    public FcFileWorkerSessionStateStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Worker session state directory is required.", nameof(directory));
        _directory = directory;
    }

    public FcWorkerSessionLoadResult Load(string authorScope)
    {
        lock (ProcessGate)
        {
            if (string.IsNullOrWhiteSpace(authorScope))
                return new(FcWorkerSessionLoadStatus.Corrupt,
                    FcWorkerSessionState.Create(string.Empty, string.Empty),
                    "Character author scope is unavailable.");
            var path = StatePath(authorScope);
            var marker = MarkerPath(authorScope);
            var stateExists = File.Exists(path);
            var markerExists = File.Exists(marker);
            if (!stateExists && !markerExists)
                return new(FcWorkerSessionLoadStatus.Missing,
                    FcWorkerSessionState.Create(authorScope, authorScope), string.Empty);
            if (!stateExists || !markerExists)
                return new(FcWorkerSessionLoadStatus.Corrupt,
                    FcWorkerSessionState.Create(authorScope, authorScope),
                    "Worker session state and initialization marker are not both present.");
            try
            {
                if (!File.ReadAllBytes(marker).AsSpan().SequenceEqual(MarkerBytes))
                    throw new InvalidDataException("Worker session initialization marker is invalid.");
                var state = JsonSerializer.Deserialize(
                    File.ReadAllBytes(path), FcJsonContext.Default.FcWorkerSessionState);
                if (state is null)
                    throw new InvalidDataException("Worker session state is empty.");
                FcInMemoryWorkerSessionStateStore.Validate(authorScope, state);
                return new(FcWorkerSessionLoadStatus.Clean,
                    FcInMemoryWorkerSessionStateStore.Clone(state), string.Empty);
            }
            catch (Exception exception)
            {
                return new(FcWorkerSessionLoadStatus.Corrupt,
                    FcWorkerSessionState.Create(authorScope, authorScope),
                    $"Worker session state is corrupt: {exception.Message}");
            }
        }
    }

    public void Save(string authorScope, FcWorkerSessionState state)
    {
        FcInMemoryWorkerSessionStateStore.Validate(authorScope, state);
        lock (ProcessGate)
        {
            Directory.CreateDirectory(_directory);
            var path = StatePath(authorScope);
            var marker = MarkerPath(authorScope);
            if (File.Exists(path) || File.Exists(marker))
            {
                var current = Load(authorScope);
                if (current.Status != FcWorkerSessionLoadStatus.Clean)
                    throw new InvalidDataException(
                        string.IsNullOrWhiteSpace(current.Error)
                            ? "Existing worker session state is not writable."
                            : current.Error);
                state = FcInMemoryWorkerSessionStateStore.Merge(current.State, state);
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, FcJsonContext.Default.FcWorkerSessionState);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                // Marker first is deliberate: a crash between marker and state
                // replacement fails closed instead of allowing revision reuse.
                File.WriteAllBytes(marker, MarkerBytes);
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
                    // Preserve the original persistence failure.
                }
            }
        }
    }

    private string StatePath(string authorScope)
        => Path.Combine(_directory, "worker-" + ScopeHash(authorScope) + ".json");

    private string MarkerPath(string authorScope)
        => StatePath(authorScope) + ".marker";

    private static string ScopeHash(string authorScope)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authorScope))).ToLowerInvariant();
}
