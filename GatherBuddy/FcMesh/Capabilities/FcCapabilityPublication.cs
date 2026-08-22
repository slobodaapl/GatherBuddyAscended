using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GatherBuddy.FcMesh.Native;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Capabilities;

public enum FcCapabilityPublicationStatus : byte
{
    Pending,
    AcceptedByNative,
    Failed,
}

public sealed record FcCapabilityPublicationEntry(
    Guid RequestId,
    ulong Revision,
    bool IsResponse,
    CapabilityRequestRecord? Request,
    CapabilityResponseRecord? Response,
    FcCapabilityPublicationStatus Status,
    string Error);

public sealed record FcCapabilityPublicationState(
    int Version,
    string BackendKind,
    int BackendStorageVersion,
    string BackendMarker,
    string AuthorScope,
    ulong ReservedRevision,
    CapabilityRequestRecord? LastRequest,
    FcCapabilityPublicationEntry[] Responses,
    FcCapabilityPublicationEntry[] Pending)
{
    public const int CurrentVersion = 1;
    public const string CurrentBackendKind = "iroh-docs";
    public const int CurrentBackendStorageVersion = 1;
    public const string CurrentInitializationMarker = "gatherbuddy-fcmesh-capability-state-v1";

    public static FcCapabilityPublicationState Create(string authorScope)
        => new(
            CurrentVersion,
            CurrentBackendKind,
            CurrentBackendStorageVersion,
            CurrentInitializationMarker,
            authorScope ?? string.Empty,
            0,
            null,
            Array.Empty<FcCapabilityPublicationEntry>(),
            Array.Empty<FcCapabilityPublicationEntry>());
}

public enum FcCapabilityPublicationLoadStatus : byte
{
    Clean,
    Missing,
    Corrupt,
}

public sealed record FcCapabilityPublicationLoadResult(
    FcCapabilityPublicationLoadStatus Status,
    FcCapabilityPublicationState State,
    string Error)
{
    public bool CanWrite => Status is FcCapabilityPublicationLoadStatus.Clean
        or FcCapabilityPublicationLoadStatus.Missing;
}

public interface IFcCapabilityPublicationStateStore
{
    FcCapabilityPublicationLoadResult Load(string authorScope);
    void Save(string authorScope, FcCapabilityPublicationState state);
}

public sealed class FcInMemoryCapabilityPublicationStateStore : IFcCapabilityPublicationStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FcCapabilityPublicationState> _states = new(StringComparer.Ordinal);
    private readonly HashSet<string> _initialized = new(StringComparer.Ordinal);

    public FcCapabilityPublicationLoadResult Load(string authorScope)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(authorScope))
                return new(FcCapabilityPublicationLoadStatus.Corrupt, FcCapabilityPublicationState.Create(string.Empty), "Character author scope is unavailable.");
            if (!_initialized.Contains(authorScope))
                return new(FcCapabilityPublicationLoadStatus.Missing, FcCapabilityPublicationState.Create(authorScope), string.Empty);
            if (!_states.TryGetValue(authorScope, out var state))
                return new(FcCapabilityPublicationLoadStatus.Corrupt, FcCapabilityPublicationState.Create(authorScope), "Capability state is missing after initialization.");
            try
            {
                Validate(state, authorScope);
                return new(FcCapabilityPublicationLoadStatus.Clean, Clone(state), string.Empty);
            }
            catch (Exception exception)
            {
                return new(FcCapabilityPublicationLoadStatus.Corrupt, FcCapabilityPublicationState.Create(authorScope), exception.Message);
            }
        }
    }

    public void Save(string authorScope, FcCapabilityPublicationState state)
    {
        if (string.IsNullOrWhiteSpace(authorScope))
            throw new ArgumentException("Character author scope is required.", nameof(authorScope));
        Validate(state, authorScope);
        lock (_gate)
        {
            if (_states.TryGetValue(authorScope, out var current))
                state = Merge(current, state);
            _states[authorScope] = Clone(state);
            _initialized.Add(authorScope);
        }
    }

    internal static FcCapabilityPublicationState Merge(
        FcCapabilityPublicationState current,
        FcCapabilityPublicationState incoming)
    {
        if (current.ReservedRevision > incoming.ReservedRevision)
            incoming = incoming with { ReservedRevision = current.ReservedRevision };
        var responsesById = current.Responses
            .ToDictionary(entry => entry.RequestId);
        foreach (var response in incoming.Responses)
        {
            if (!responsesById.TryGetValue(response.RequestId, out var previous)
                || response.Revision >= previous.Revision)
                responsesById[response.RequestId] = response;
        }
        var responses = responsesById.Values
            .OrderBy(entry => entry.RequestId)
            .ToArray();
        var pendingByKey = current.Pending
            .ToDictionary(entry => (entry.RequestId, entry.IsResponse));
        foreach (var pendingEntry in incoming.Pending)
        {
            var key = (pendingEntry.RequestId, pendingEntry.IsResponse);
            if (!pendingByKey.TryGetValue(key, out var previous)
                || pendingEntry.Revision >= previous.Revision)
                pendingByKey[key] = pendingEntry;
        }
        foreach (var response in incoming.Responses)
            pendingByKey.Remove((response.RequestId, true));
        if (incoming.LastRequest is { } incomingRequest)
        {
            var incomingRequestPending = incoming.Pending.Any(entry =>
                !entry.IsResponse && entry.RequestId == incomingRequest.RequestId);
            if (!incomingRequestPending)
                pendingByKey.Remove((incomingRequest.RequestId, false));
            foreach (var key in pendingByKey.Keys
                         .Where(key => !key.IsResponse
                             && key.RequestId != incomingRequest.RequestId)
                         .ToArray())
                pendingByKey.Remove(key);
        }
        var pending = pendingByKey.Values
            .OrderBy(entry => entry.RequestId)
            .ThenBy(entry => entry.IsResponse)
            .ToArray();
        var incomingLastRequest = incoming.LastRequest;
        var request = current.LastRequest is { } currentRequest
            && incomingLastRequest is { } incomingRequestToCompare
            && currentRequest.Header.Revision > incomingRequestToCompare.Header.Revision
            ? currentRequest
            : incomingLastRequest ?? current.LastRequest;
        return incoming with { LastRequest = request, Responses = responses, Pending = pending };
    }

    internal static void Validate(FcCapabilityPublicationState state, string authorScope)
    {
        if (state is null
            || state.Version != FcCapabilityPublicationState.CurrentVersion
            || !string.Equals(state.BackendKind, FcCapabilityPublicationState.CurrentBackendKind, StringComparison.Ordinal)
            || state.BackendStorageVersion != FcCapabilityPublicationState.CurrentBackendStorageVersion
            || !string.Equals(
                state.BackendMarker,
                FcCapabilityPublicationState.CurrentInitializationMarker,
                StringComparison.Ordinal)
            || !string.Equals(state.AuthorScope, authorScope, StringComparison.Ordinal)
            || state.Responses is null
            || state.Pending is null)
            throw new InvalidDataException("Capability publication state schema or scope is invalid.");
        var responseIds = new HashSet<Guid>();
        foreach (var response in state.Responses)
        {
            ValidateEntry(response, authorScope, responseExpected: true);
            if (!responseIds.Add(response.RequestId))
                throw new InvalidDataException("Capability response publication identity is duplicated.");
        }
        var pendingIds = new HashSet<(Guid, bool)>();
        foreach (var pending in state.Pending)
        {
            ValidateEntry(pending, authorScope, responseExpected: pending.IsResponse);
            if (!pendingIds.Add((pending.RequestId, pending.IsResponse)))
                throw new InvalidDataException("Capability pending publication identity is duplicated.");
        }
        if (state.LastRequest is { } request)
        {
            if (request.Header is null
                || request.RequestId == Guid.Empty
                || !IsSafeAuthorId(request.Header.OwnerAuthorId)
                || request.Header.Revision == 0
                || state.ReservedRevision < request.Header.Revision)
                throw new InvalidDataException("Capability request publication state is invalid.");
        }
        foreach (var entry in state.Pending)
        {
            if (state.ReservedRevision < entry.Revision)
                throw new InvalidDataException("Capability revision reservation regresses a pending publication.");
        }
    }

    private static void ValidateEntry(
        FcCapabilityPublicationEntry entry,
        string authorScope,
        bool responseExpected)
    {
        if (entry is null
            || entry.RequestId == Guid.Empty
            || entry.Revision == 0
            || !Enum.IsDefined(entry.Status)
            || string.IsNullOrWhiteSpace(entry.Error) && entry.Status == FcCapabilityPublicationStatus.Failed)
            throw new InvalidDataException("Capability publication entry is invalid.");
        if (responseExpected != entry.IsResponse)
            throw new InvalidDataException("Capability publication entry kind is inconsistent.");
        if (entry.IsResponse)
        {
            if (entry.Response is not { } response
                || response.RequestId != entry.RequestId
                || response.Header is null
                || !IsSafeAuthorId(response.Header.OwnerAuthorId)
                || response.Header.Revision != entry.Revision)
                throw new InvalidDataException("Capability response publication entry is inconsistent.");
        }
        else if (entry.Request is not { } request
            || request.RequestId != entry.RequestId
            || request.Header is null
            || !IsSafeAuthorId(request.Header.OwnerAuthorId)
            || request.Header.Revision != entry.Revision)
            throw new InvalidDataException("Capability request publication entry is inconsistent.");
    }

    internal static FcCapabilityPublicationState Clone(FcCapabilityPublicationState state)
        => state with
        {
            LastRequest = state.LastRequest,
            Responses = state.Responses?.ToArray() ?? Array.Empty<FcCapabilityPublicationEntry>(),
            Pending = state.Pending?.ToArray() ?? Array.Empty<FcCapabilityPublicationEntry>(),
        };

    private static bool IsSafeAuthorId(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && !value.Contains('/')
            && !value.Contains('\\')
            && !value.Contains('\0')
            && !value.Contains('\r')
            && !value.Contains('\n');
}

public sealed class FcFileCapabilityPublicationStateStore : IFcCapabilityPublicationStateStore
{
    private static readonly object ProcessGate = new();
    private readonly string _directory;

    public FcFileCapabilityPublicationStateStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Capability state directory is required.", nameof(directory));
        _directory = directory;
    }

    public FcCapabilityPublicationLoadResult Load(string authorScope)
    {
        lock (ProcessGate)
        {
            if (string.IsNullOrWhiteSpace(authorScope))
                return new(FcCapabilityPublicationLoadStatus.Corrupt, FcCapabilityPublicationState.Create(string.Empty), "Character author scope is unavailable.");
            var path = PathFor(authorScope);
            if (!File.Exists(path))
                return new(FcCapabilityPublicationLoadStatus.Missing, FcCapabilityPublicationState.Create(authorScope), string.Empty);
            try
            {
                var state = System.Text.Json.JsonSerializer.Deserialize(
                    File.ReadAllBytes(path),
                    FcJsonContext.Default.FcCapabilityPublicationState);
                if (state is null)
                    throw new InvalidDataException("Capability state is empty.");
                FcInMemoryCapabilityPublicationStateStore.Validate(state, authorScope);
                return new(FcCapabilityPublicationLoadStatus.Clean, FcInMemoryCapabilityPublicationStateStore.Clone(state), string.Empty);
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException
                or System.Text.Json.JsonException
                or NotSupportedException
                or UnauthorizedAccessException)
            {
                return new(FcCapabilityPublicationLoadStatus.Corrupt, FcCapabilityPublicationState.Create(authorScope), $"Capability state could not be loaded: {exception.Message}");
            }
        }
    }

    public void Save(string authorScope, FcCapabilityPublicationState state)
    {
        if (string.IsNullOrWhiteSpace(authorScope))
            throw new ArgumentException("Capability author scope is required.", nameof(authorScope));
        FcInMemoryCapabilityPublicationStateStore.Validate(state, authorScope);
        lock (ProcessGate)
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(authorScope);
            var current = Load(authorScope);
            if (current.Status == FcCapabilityPublicationLoadStatus.Clean)
                state = Merge(current.State, state);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    state,
                    FcJsonContext.Default.FcCapabilityPublicationState);
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
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }

    private static FcCapabilityPublicationState Merge(
        FcCapabilityPublicationState current,
        FcCapabilityPublicationState incoming)
        => FcInMemoryCapabilityPublicationStateStore.Merge(current, incoming);

    private string PathFor(string authorScope)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authorScope))).ToLowerInvariant();
        return Path.Combine(_directory, "capabilities-" + hash + ".json");
    }

}

/// <summary>
/// Durable requester/responder register publisher. Reservations are persisted
/// synchronously before a native Put is queued; failures leave a retryable
/// absolute record and never reuse the reserved revision.
/// </summary>
public sealed class FcCapabilityPublicationService : IDisposable
{
    private readonly object _gate = new();
    private readonly IFcCapabilityPublicationStateStore _stateStore;
    private readonly IFcPublicationTransport _transport;
    private readonly Func<string?> _authorScopeProvider;
    private readonly Func<string?> _authorIdProvider;
    private readonly Func<FcCompatibilityContext> _compatibilityProvider;
    private readonly Func<string?> _worldFingerprintProvider;
    private readonly Func<WorkerSessionRecord?> _sessionProvider;
    private readonly Func<uint, uint> _recipeJobResolver;
    private readonly IFcClock _clock;
    private readonly Channel<FcCapabilityPublicationEntry> _commands = Channel.CreateUnbounded<FcCapabilityPublicationEntry>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private FcCapabilityPublicationState _state;
    private FcCapabilityPublicationLoadStatus _loadStatus;
    private string _lastError = string.Empty;
    private int _pendingCommands;
    private bool _disposed;

    public FcCapabilityPublicationService(
        IFcCapabilityPublicationStateStore stateStore,
        IFcPublicationTransport transport,
        Func<string?> authorScopeProvider,
        Func<string?> authorIdProvider,
        Func<FcCompatibilityContext> compatibilityProvider,
        Func<string?> worldFingerprintProvider,
        Func<WorkerSessionRecord?> sessionProvider,
        Func<uint, uint> recipeJobResolver,
        IFcClock? clock = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _authorScopeProvider = authorScopeProvider ?? throw new ArgumentNullException(nameof(authorScopeProvider));
        _authorIdProvider = authorIdProvider ?? throw new ArgumentNullException(nameof(authorIdProvider));
        _compatibilityProvider = compatibilityProvider ?? throw new ArgumentNullException(nameof(compatibilityProvider));
        _worldFingerprintProvider = worldFingerprintProvider ?? throw new ArgumentNullException(nameof(worldFingerprintProvider));
        _sessionProvider = sessionProvider ?? throw new ArgumentNullException(nameof(sessionProvider));
        _recipeJobResolver = recipeJobResolver ?? throw new ArgumentNullException(nameof(recipeJobResolver));
        _clock = clock ?? FcSystemClock.Instance;
        var scope = _authorScopeProvider();
        _state = FcCapabilityPublicationState.Create(scope ?? string.Empty);
        _loadStatus = FcCapabilityPublicationLoadStatus.Corrupt;
        if (!string.IsNullOrWhiteSpace(scope))
        {
            var loaded = _stateStore.Load(scope);
            _state = loaded.State;
            _loadStatus = loaded.Status;
            _lastError = loaded.Error;
            if (loaded.Status == FcCapabilityPublicationLoadStatus.Missing)
            {
                try
                {
                    _stateStore.Save(scope, _state);
                    _loadStatus = FcCapabilityPublicationLoadStatus.Clean;
                }
                catch (Exception exception)
                {
                    _loadStatus = FcCapabilityPublicationLoadStatus.Corrupt;
                    _lastError = exception.Message;
                }
            }
        }
        _worker = Task.Run(ProcessCommandsAsync);
    }

    public CapabilityRequestRecord? CurrentRequest
    {
        get { lock (_gate) return _state.LastRequest; }
    }

    public FcWorldStore? WorldStore => _transport.WorldStore;

    public bool IsForkedRequest(CapabilityRequestRecord request)
        => request is not null
            && _transport.WorldStore?.IsForked(
                request.Header.OwnerAuthorId + "/request/" + request.RequestId.ToString("D")) == true;

    public IReadOnlyList<CapabilityResponseRecord> Responses
    {
        get
        {
            lock (_gate)
                return _state.Responses.Where(entry => entry.Response is not null).Select(entry => entry.Response!).ToArray();
        }
    }

    public FcCapabilityPublicationDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
                return new(
                    _loadStatus.ToString(),
                    _state.AuthorScope,
                    CanWriteLocked(),
                    _lastError,
                    _pendingCommands,
                    _state.ReservedRevision,
                    _state.LastRequest?.RequestId,
                    _state.Responses.Length);
        }
    }

    public FcCapabilityRequestResult Request(
        IEnumerable<RequiredCraftCapability> capabilities,
        TimeSpan? lifetime = null)
    {
        lock (_gate)
        {
            if (!CanWriteLocked())
                return FcCapabilityRequestResult.Blocked(WriteBlockReasonLocked());
            var owner = _authorIdProvider();
            var world = _worldFingerprintProvider();
            var compatibility = _compatibilityProvider();
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(world) || !compatibility.IsValid)
                return FcCapabilityRequestResult.Blocked("Capability request requires character, world, and compatibility identity.");
            var recipes = NormalizeCapabilities(capabilities, out var error);
            if (error.Length != 0)
                return FcCapabilityRequestResult.Blocked(error);
            if (recipes.Length == 0)
                return FcCapabilityRequestResult.Blocked("Capability request contains no recipes.");
            var planner = FcCapabilityFingerprints.Planner(compatibility.PlannerSemanticsVersion, compatibility.GameVersion);
            var existing = _state.LastRequest;
            if (existing is not null
                && _transport.WorldStore?.IsForked(
                    existing.Header.OwnerAuthorId + "/request/" + existing.RequestId.ToString("D")) == true)
                return FcCapabilityRequestResult.Blocked("The capability request register is forked; writes are blocked.");
            if (existing is not null
                && existing.ExpiresAt.PhysicalUnixMs > _clock.UnixMilliseconds
                && string.Equals(existing.Header.OwnerAuthorId, owner, StringComparison.Ordinal)
                && string.Equals(existing.WorldFingerprint, world, StringComparison.Ordinal)
                && string.Equals(existing.GameVersion, compatibility.GameVersion, StringComparison.Ordinal)
                && string.Equals(existing.PlannerFingerprint, planner, StringComparison.Ordinal)
                && HasSameCapabilities(existing.Recipes, recipes))
            {
                var existingPending = _state.Pending.FirstOrDefault(entry =>
                    !entry.IsResponse && entry.RequestId == existing.RequestId);
                if (existingPending is { Status: FcCapabilityPublicationStatus.Failed })
                {
                    var retry = existingPending with
                    {
                        Status = FcCapabilityPublicationStatus.Pending,
                        Error = string.Empty,
                    };
                    _state = _state with { Pending = ReplacePending(_state.Pending, retry) };
                    if (!PersistLocked(out var retryError))
                        return FcCapabilityRequestResult.Blocked(retryError);
                    if (!_commands.Writer.TryWrite(retry))
                    {
                        SetPendingErrorLocked(retry, "Capability publication command queue is closed.");
                        return FcCapabilityRequestResult.Blocked(_lastError);
                    }
                    _pendingCommands++;
                    return new(true, "Existing capability request queued for retry.", existing.RequestId, existing.Header.Revision, FcPublicationCommandStatus.Pending, existing);
                }
                return new(
                    true,
                    "Existing capability request is still current.",
                    existing.RequestId,
                    existing.Header.Revision,
                    existingPending is null
                        ? FcPublicationCommandStatus.AcceptedByNative
                        : FcPublicationCommandStatus.Pending,
                    existing);
            }

            var requestId = Guid.NewGuid();
            var revision = ReserveRevisionLocked(owner, requestId, isResponse: false);
            FcHlcTimestamp expiresAt;
            try
            {
                expiresAt = Expiry(lifetime ?? TimeSpan.FromMinutes(2), owner);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return FcCapabilityRequestResult.Blocked(exception.Message);
            }
            var record = new CapabilityRequestRecord(
                new FcRecordHeader(
                    FcProtocolVersion.Current,
                    FcProtocolVersion.CurrentSchema,
                    FcRecordTypes.CapabilityRequest,
                    requestId,
                    owner,
                    revision),
                requestId,
                world,
                expiresAt,
                recipes)
            {
                RequesterAuthorId = owner,
                GameVersion = compatibility.GameVersion,
                PlannerFingerprint = planner,
            };
            var pending = new FcCapabilityPublicationEntry(
                requestId,
                revision,
                false,
                record,
                null,
                FcCapabilityPublicationStatus.Pending,
                string.Empty);
            _state = _state with
            {
                LastRequest = record,
                Pending = ReplacePending(
                    _state.Pending.Where(entry => entry.IsResponse || entry.RequestId == requestId),
                    pending),
            };
            if (!PersistLocked(out error))
                return FcCapabilityRequestResult.Blocked(error);
            if (!_commands.Writer.TryWrite(pending))
            {
                SetPendingErrorLocked(pending, "Capability publication command queue is closed.");
                return FcCapabilityRequestResult.Blocked(_lastError);
            }
            _pendingCommands++;
            return new(true, "Capability request reserved and queued.", requestId, revision, FcPublicationCommandStatus.Pending, record);
        }
    }

    public FcCapabilityRequestResult PublishResponse(
        CapabilityRequestRecord request,
        IReadOnlyList<FcCapabilityAssessment> assessments,
        TimeSpan? lifetime = null)
    {
        if (request is null || assessments is null)
            return FcCapabilityRequestResult.Blocked("Capability response input is unavailable.");
        lock (_gate)
        {
            if (!CanWriteLocked())
                return FcCapabilityRequestResult.Blocked(WriteBlockReasonLocked());
            var owner = _authorIdProvider();
            var world = _worldFingerprintProvider();
            var compatibility = _compatibilityProvider();
            var session = _sessionProvider();
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(world)
                || !compatibility.IsValid || session is null
                || session.Header is null
                || session.State is not (FcWorkerState.Active or FcWorkerState.Waiting)
                || !string.Equals(owner, session.Header.OwnerAuthorId, StringComparison.Ordinal))
                return FcCapabilityRequestResult.Blocked("Capability response requires a fresh subscribed worker and compatible world.");
            if (request.Header is null
                || request.RequestId == Guid.Empty
                || !string.Equals(request.Header.RecordType, FcRecordTypes.CapabilityRequest, StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(request.RequesterAuthorId)
                    && !string.Equals(request.RequesterAuthorId, request.Header.OwnerAuthorId, StringComparison.Ordinal))
                || !string.Equals(request.WorldFingerprint, world, StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(request.GameVersion)
                    && !string.Equals(request.GameVersion, compatibility.GameVersion, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(request.PlannerFingerprint)
                    && !string.Equals(
                        request.PlannerFingerprint,
                        FcCapabilityFingerprints.Planner(
                            compatibility.PlannerSemanticsVersion,
                            compatibility.GameVersion),
                        StringComparison.Ordinal))
                || request.ExpiresAt.PhysicalUnixMs <= _clock.UnixMilliseconds)
                return FcCapabilityRequestResult.Blocked("Capability request is expired or belongs to another world.");
            if (_transport.WorldStore?.IsForked(
                    request.Header.OwnerAuthorId + "/request/" + request.RequestId.ToString("D")) == true
                || _transport.WorldStore?.IsForked(
                    owner + "/response/" + request.RequestId.ToString("D")) == true)
                return FcCapabilityRequestResult.Blocked("The capability request or response register is forked; writes are blocked.");
            var requestedRecipes = request.Recipes ?? Array.Empty<RequiredCraftCapability>();
            if (!HasExactAssessments(requestedRecipes, assessments))
                return FcCapabilityRequestResult.Blocked("Capability response does not cover the exact requested recipe set.");
            var inFlight = _state.Pending.FirstOrDefault(entry =>
                entry.IsResponse
                && entry.RequestId == request.RequestId
                && entry.Status == FcCapabilityPublicationStatus.Pending);
            if (inFlight is not null)
                return new(
                    true,
                    "Capability response is already queued.",
                    request.RequestId,
                    inFlight.Revision,
                    FcPublicationCommandStatus.Pending,
                    request);
            foreach (var assessment in assessments)
            {
                if (!assessment.CanCraft)
                    continue;
                var requiredJob = _recipeJobResolver(assessment.RecipeId);
                if (requiredJob == 0 || assessment.SelectedJobId != requiredJob)
                    return FcCapabilityRequestResult.Blocked(
                        $"Capability response selected an invalid job for recipe {assessment.RecipeId}.");
            }
            var planner = FcCapabilityFingerprints.Planner(compatibility.PlannerSemanticsVersion, compatibility.GameVersion);
            var responseId = request.RequestId;
            var revision = ReserveRevisionLocked(owner, responseId, isResponse: true);
            var results = assessments
                .OrderBy(assessment => assessment.RecipeId)
                .Select(assessment => assessment.ToWireResult())
                .ToArray();
            FcHlcTimestamp responseExpiry;
            try
            {
                responseExpiry = Expiry(lifetime ?? TimeSpan.FromMinutes(2), owner);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return FcCapabilityRequestResult.Blocked(exception.Message);
            }
            if (request.ExpiresAt < responseExpiry)
                responseExpiry = request.ExpiresAt;
            if (responseExpiry.PhysicalUnixMs <= _clock.UnixMilliseconds)
                return FcCapabilityRequestResult.Blocked("Capability response would already be expired with its request.");
            var response = new CapabilityResponseRecord(
                new FcRecordHeader(
                    FcProtocolVersion.Current,
                    FcProtocolVersion.CurrentSchema,
                    FcRecordTypes.CapabilityResponse,
                    responseId,
                    owner,
                    revision),
                responseId,
                responseExpiry,
                request.WorldFingerprint,
                compatibility.GameVersion,
                planner,
                AggregateFingerprint("gear", assessments.Select(assessment => assessment.GearsetFingerprint)),
                AggregateFingerprint("solver", assessments.Select(assessment => assessment.SolverFingerprint)),
                results)
            {
                RequestedRecipes = requestedRecipes.ToArray(),
                SessionId = session.SessionId,
                SessionGeneration = session.SessionGeneration,
                ResponderAuthorId = owner,
            };
            var pending = new FcCapabilityPublicationEntry(
                responseId,
                revision,
                true,
                null,
                response,
                FcCapabilityPublicationStatus.Pending,
                string.Empty);
            _state = _state with { Pending = ReplacePending(_state.Pending, pending) };
            if (!PersistLocked(out var error))
                return FcCapabilityRequestResult.Blocked(error);
            if (!_commands.Writer.TryWrite(pending))
            {
                SetPendingErrorLocked(pending, "Capability publication command queue is closed.");
                return FcCapabilityRequestResult.Blocked(_lastError);
            }
            _pendingCommands++;
            return new(true, "Capability response reserved and queued.", responseId, revision, FcPublicationCommandStatus.Pending, request);
        }
    }

    public void ReconcileAuthoritativeState()
    {
        lock (_gate)
        {
            var owner = _authorIdProvider();
            var world = _transport.WorldStore;
            if (string.IsNullOrWhiteSpace(owner) || world is null)
                return;
            var highWater = world.RevisionHighWater
                .Where(pair => pair.Key.StartsWith(owner + "/request/", StringComparison.Ordinal)
                    || pair.Key.StartsWith(owner + "/response/", StringComparison.Ordinal))
                .Select(pair => pair.Value)
                .DefaultIfEmpty(0UL)
                .Max();
            if (highWater > _state.ReservedRevision)
            {
                _state = _state with { ReservedRevision = highWater };
                PersistLocked(out _);
            }
        }
    }

    public FcCapabilityRequestResult RetryPending()
    {
        lock (_gate)
        {
            var pending = _state.Pending.Where(entry => entry.Status != FcCapabilityPublicationStatus.AcceptedByNative).ToArray();
            foreach (var entry in pending)
            {
                if (_commands.Writer.TryWrite(entry))
                    _pendingCommands++;
            }
            return pending.Length == 0
                ? FcCapabilityRequestResult.Blocked("No pending capability publication remains.")
                : new(true, "Pending capability publications queued for retry.", pending[0].RequestId, pending[0].Revision, FcPublicationCommandStatus.Pending, pending[0].Request);
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
            _commands.Writer.TryComplete();
        }
        _shutdown.Cancel();
        try { _worker.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch { }
        _shutdown.Dispose();
    }

    private async Task ProcessCommandsAsync()
    {
        try
        {
            await foreach (var entry in _commands.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try { await ProcessCommandAsync(entry).ConfigureAwait(false); }
                catch (Exception exception) { SetFailure(entry, exception.Message); }
                finally
                {
                    lock (_gate)
                        _pendingCommands = Math.Max(0, _pendingCommands - 1);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private Task ProcessCommandAsync(FcCapabilityPublicationEntry entry)
    {
        lock (_gate)
        {
            if (_disposed)
                return Task.CompletedTask;
            if (!PersistLocked(out var error))
            {
                SetFailureLocked(entry, error);
                return Task.CompletedTask;
            }
            if (!_transport.IsReady || string.IsNullOrWhiteSpace(_transport.LocalAuthorId)
                || !string.Equals(_transport.LocalAuthorId, _authorIdProvider(), StringComparison.Ordinal)
                || !string.Equals(recordOwner(entry), _authorIdProvider(), StringComparison.Ordinal))
            {
                SetFailureLocked(entry, "Native mesh author/readiness changed before capability publication; retry is required.");
                return Task.CompletedTask;
            }
        }
        FcNativeCallResult result;
        if (entry.IsResponse)
        {
            if (entry.Response is not { } response)
            {
                SetFailure(entry, "Capability publication response is missing.");
                return Task.CompletedTask;
            }
            result = _transport.Put(
                Encoding.UTF8.GetBytes(response.Header.RecordId.ToString("D")),
                Encoding.UTF8.GetBytes(FcMeshKey.ForRecord(
                    FcRecordTypes.CapabilityResponse,
                    response.Header.OwnerAuthorId,
                    response.Header.RecordId.ToString("D"))),
                Encoding.UTF8.GetBytes(FcRecordTypes.CapabilityResponse),
                false,
                0,
                response.Header.Revision,
                FcCanonical.SerializeUtf8(response));
        }
        else
        {
            if (entry.Request is not { } request)
            {
                SetFailure(entry, "Capability publication request is missing.");
                return Task.CompletedTask;
            }
            result = _transport.Put(
                Encoding.UTF8.GetBytes(request.Header.RecordId.ToString("D")),
                Encoding.UTF8.GetBytes(FcMeshKey.ForRecord(
                    FcRecordTypes.CapabilityRequest,
                    request.Header.OwnerAuthorId,
                    request.Header.RecordId.ToString("D"))),
                Encoding.UTF8.GetBytes(FcRecordTypes.CapabilityRequest),
                false,
                0,
                request.Header.Revision,
                FcCanonical.SerializeUtf8(request));
        }
        if (!result.Succeeded)
        {
            SetFailure(entry, $"Native capability publication queue rejected the record: {result.ErrorCode}.");
            return Task.CompletedTask;
        }
        lock (_gate)
        {
            var pending = _state.Pending
                .Where(value => !(value.RequestId == entry.RequestId && value.IsResponse == entry.IsResponse))
                .ToArray();
            var accepted = entry with { Status = FcCapabilityPublicationStatus.AcceptedByNative, Error = string.Empty };
            var responses = entry.IsResponse
                ? _state.Responses
                    .Where(value => value.RequestId != entry.RequestId)
                    .Append(accepted)
                    .OrderBy(value => value.RequestId)
                    .ToArray()
                : _state.Responses;
            var lastRequest = !entry.IsResponse
                && entry.Request is { } request
                && (_state.LastRequest is null
                    || request.Header.Revision >= _state.LastRequest.Header.Revision)
                ? request
                : _state.LastRequest;
            _state = _state with { LastRequest = lastRequest, Responses = responses, Pending = pending };
            if (!PersistLocked(out var error))
                _lastError = error;
        }
        return Task.CompletedTask;

        static string? recordOwner(FcCapabilityPublicationEntry value)
            => value.IsResponse ? value.Response?.Header.OwnerAuthorId : value.Request?.Header.OwnerAuthorId;
    }

    private void SetFailure(FcCapabilityPublicationEntry entry, string error)
    {
        lock (_gate)
            SetFailureLocked(entry, error);
    }

    private void SetFailureLocked(FcCapabilityPublicationEntry entry, string error)
    {
        _lastError = error;
        var pending = _state.Pending
            .Where(value => !(value.RequestId == entry.RequestId && value.IsResponse == entry.IsResponse))
            .Append(entry with { Status = FcCapabilityPublicationStatus.Failed, Error = error })
            .ToArray();
        _state = _state with { Pending = pending };
        PersistLocked(out _);
    }

    private void SetPendingErrorLocked(FcCapabilityPublicationEntry entry, string error)
        => SetFailureLocked(entry, error);

    private bool PersistLocked(out string error)
    {
        error = string.Empty;
        try
        {
            _stateStore.Save(_state.AuthorScope, FcInMemoryCapabilityPublicationStateStore.Clone(_state));
            return true;
        }
        catch (Exception exception)
        {
            _lastError = error = $"Capability publication state persistence failed: {exception.Message}";
            return false;
        }
    }

    private bool CanWriteLocked()
        => !_disposed
            && _loadStatus is FcCapabilityPublicationLoadStatus.Clean or FcCapabilityPublicationLoadStatus.Missing
            && _transport.IsReady
            && !string.IsNullOrWhiteSpace(_authorScopeProvider())
            && !string.IsNullOrWhiteSpace(_authorIdProvider());

    private string WriteBlockReasonLocked()
        => _disposed
            ? "Capability publication service is disposed."
            : _loadStatus == FcCapabilityPublicationLoadStatus.Corrupt
                ? "Capability publication state is corrupt; writes are blocked."
                : !_transport.IsReady
                    ? "FC mesh is not ready for capability publication."
                    : "Character capability publication identity is unavailable.";

    private ulong ReserveRevisionLocked(string owner, Guid requestId, bool isResponse)
    {
        var prefix = isResponse ? "/response/" : "/request/";
        var worldRevision = _transport.WorldStore?.RevisionHighWater
            .Where(pair => pair.Key.StartsWith(owner + prefix, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .DefaultIfEmpty(0UL)
            .Max() ?? 0UL;
        var revision = checked(Math.Max(_state.ReservedRevision, worldRevision) + 1);
        _state = _state with { ReservedRevision = revision };
        return revision;
    }

    private FcHlcTimestamp Expiry(TimeSpan lifetime, string owner)
    {
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        return new FcHlcTimestamp(checked(_clock.UnixMilliseconds + (long)lifetime.TotalMilliseconds), 0, owner);
    }

    private static FcCapabilityPublicationEntry[] ReplacePending(
        IEnumerable<FcCapabilityPublicationEntry> values,
        FcCapabilityPublicationEntry entry)
        => values.Where(value => !(value.RequestId == entry.RequestId && value.IsResponse == entry.IsResponse))
            .Append(entry)
            .OrderBy(value => value.RequestId)
            .ThenBy(value => value.IsResponse)
            .ToArray();

    private static RequiredCraftCapability[] NormalizeCapabilities(
        IEnumerable<RequiredCraftCapability> values,
        out string error)
    {
        error = string.Empty;
        var result = new List<RequiredCraftCapability>();
        foreach (var value in values ?? Array.Empty<RequiredCraftCapability>())
        {
            if (value is null || value.RecipeId == 0)
            {
                error = "Capability request contains an invalid recipe.";
                return Array.Empty<RequiredCraftCapability>();
            }
            var normalized = value with
            {
                QualityPolicy = value.QualityPolicy ?? FcQualityPolicy.Empty,
                FinalQualityPolicy = value.FinalQualityPolicy ?? value.QualityPolicy ?? FcQualityPolicy.Empty,
                PrecraftQualityPolicy = value.PrecraftQualityPolicy ?? FcQualityPolicy.Empty,
            };
            var prior = result.FirstOrDefault(item => item.RecipeId == value.RecipeId);
            if (prior is not null)
            {
                if (!SameCapability(prior, normalized))
                {
                    error = "Capability request contains duplicate recipe IDs with different policies.";
                    return Array.Empty<RequiredCraftCapability>();
                }
                continue;
            }
            result.Add(normalized);
        }
        return result.OrderBy(value => value.RecipeId).ToArray();
    }

    private static bool HasSameCapabilities(
        IReadOnlyList<RequiredCraftCapability> left,
        IReadOnlyList<RequiredCraftCapability> right)
        => left.Count == right.Count
            && left.OrderBy(value => value.RecipeId).Zip(
                right.OrderBy(value => value.RecipeId),
                (a, b) => a.RecipeId == b.RecipeId && SameCapability(a, b))
                .All(value => value);

    private static bool SameCapability(RequiredCraftCapability left, RequiredCraftCapability right)
        => left.RecipeId == right.RecipeId
            && left.IsPrecraft == right.IsPrecraft
            && FcCanonical.Hash(left.EffectiveQualityPolicy) == FcCanonical.Hash(right.EffectiveQualityPolicy);

    private static bool HasExactAssessments(
        IReadOnlyList<RequiredCraftCapability> requested,
        IReadOnlyList<FcCapabilityAssessment> assessments)
        => requested.Count == assessments.Count
            && requested.OrderBy(value => value.RecipeId).Zip(
                assessments.OrderBy(value => value.RecipeId),
                (request, assessment) => request.RecipeId == assessment.RecipeId
                    && request.IsPrecraft == assessment.IsPrecraft)
                .All(value => value);

    private static string AggregateFingerprint(string kind, IEnumerable<string> values)
    {
        var joined = string.Join("|", values.Where(value => !string.IsNullOrWhiteSpace(value)).OrderBy(value => value, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "-v1|" + joined))).ToLowerInvariant();
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
}
