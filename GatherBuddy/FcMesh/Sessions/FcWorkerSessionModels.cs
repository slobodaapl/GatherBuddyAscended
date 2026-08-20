using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;

namespace GatherBuddy.FcMesh.Sessions;

public enum FcWorkerSessionCommandKind : byte
{
    Start,
    MeaningfulUpdate,
    IdleRefresh,
    Stop,
}

public enum FcWorkerSessionCommandStatus : byte
{
    None,
    Pending,
    AcceptedByNative,
    Failed,
    Blocked,
}

public enum FcWorkerSessionLoadStatus : byte
{
    Clean,
    Missing,
    Corrupt,
}

/// <summary>
/// Author-scoped, local-only recovery state. It is never put into the mesh.
/// The pending worker is the complete absolute command reserved for the
/// worker register. Keeping that payload, rather than a delta, makes retries
/// idempotent across a crash before native Put or before RecordInserted.
/// </summary>
public sealed record FcWorkerSessionState
{
    public const int CurrentVersion = 1;
    public const string CurrentBackendKind = "iroh-docs";
    public const int CurrentBackendStorageVersion = 1;
    public const string CurrentInitializationMarker = "gatherbuddy-fcmesh-worker-state-v1";

    public int Version { get; init; } = CurrentVersion;
    public string BackendKind { get; init; } = CurrentBackendKind;
    public int BackendStorageVersion { get; init; } = CurrentBackendStorageVersion;
    public string BackendMarker { get; init; } = string.Empty;
    public string AuthorScope { get; init; } = string.Empty;
    public string AuthorId { get; init; } = string.Empty;
    public Guid WorkerRegisterId { get; init; }
    public Guid SessionId { get; init; }
    public ulong SessionGeneration { get; init; }
    public ulong LastReservedRevision { get; init; }
    public WorkerSessionRecord? LastAcceptedWorker { get; init; }
    public WorkerSessionRecord? PendingWorker { get; init; }
    public FcWorkerSessionCommandKind? PendingKind { get; init; }
    public long LastCommunicationUnixMilliseconds { get; init; }
    public long NextRefreshUnixMilliseconds { get; init; }
    public FcWorkerState State { get; init; } = FcWorkerState.Unsubscribed;
    public bool UseOwnStock { get; init; }
    public FcFulfillmentSelection Selection { get; init; } = FcFulfillmentSelection.Specific();
    public FcQuantityKey[] DependencyClosure { get; init; } = Array.Empty<FcQuantityKey>();
    public FcContributionLedgerRecovery? LedgerRecovery { get; init; }
    public bool CleanShutdown { get; init; }
    public bool ExplicitUnsubscribed { get; init; }
    public string LastError { get; init; } = string.Empty;

    public static FcWorkerSessionState Create(string authorScope, string authorId)
        => new()
        {
            BackendMarker = Guid.NewGuid().ToString("N"),
            AuthorScope = authorScope ?? string.Empty,
            AuthorId = authorId ?? string.Empty,
            WorkerRegisterId = StableWorkerRegisterId(authorId ?? string.Empty),
        };

    public static Guid StableWorkerRegisterId(string authorId)
    {
        if (string.IsNullOrWhiteSpace(authorId))
            return Guid.Empty;
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("gatherbuddy/fcmesh/worker/" + authorId));
        return new Guid(hash.AsSpan(0, 16));
    }
}

public sealed record FcWorkerSessionLoadResult(
    FcWorkerSessionLoadStatus Status,
    FcWorkerSessionState State,
    string Error)
{
    public bool CanWrite => Status is FcWorkerSessionLoadStatus.Clean or FcWorkerSessionLoadStatus.Missing;
}

public interface IFcWorkerSessionStateStore
{
    FcWorkerSessionLoadResult Load(string authorScope);

    void Save(string authorScope, FcWorkerSessionState state);
}

public sealed record FcWorkerSessionResult(
    bool Accepted,
    string Message,
    FcWorkerSessionCommandStatus Status,
    ulong Revision = 0,
    Guid SessionId = default,
    ulong SessionGeneration = 0)
{
    public static FcWorkerSessionResult Blocked(string message)
        => new(false, message, FcWorkerSessionCommandStatus.Blocked);
}

public sealed record FcWorkerSessionDiagnostics(
    string State,
    string AuthorScope,
    string AuthorId,
    bool WritesAllowed,
    bool RecoveryRequired,
    bool Forked,
    FcWorkerState WorkerState,
    Guid SessionId,
    ulong SessionGeneration,
    ulong Revision,
    long LastCommunicationUnixMilliseconds,
    long NextRefreshUnixMilliseconds,
    int PendingCommands,
    string LastError)
{
    public bool IsSubscribed
        => WorkerState is FcWorkerState.Active or FcWorkerState.Waiting;
}

public sealed record FcWorkerSessionStatus(
    bool IsSubscribed,
    bool IsRecoveryRequired,
    WorkerSessionRecord? Desired,
    WorkerSessionRecord? Accepted,
    FcHlcTimestamp? AcceptedHlc,
    FcContributionLedger? Ledger,
    string Error);

public sealed record FcWorkerSessionStartRequest(
    IReadOnlyCollection<Guid> ListIds,
    bool AllPublishedLists,
    bool UseOwnStock,
    FcItemQuantityMap CurrentPhysical,
    IReadOnlyCollection<FcQuantityKey> DependencyClosure,
    CharacterIdentity Character,
    string WorldFingerprint);

public static class FcWorkerSessionSelection
{
    public static Guid[] Normalize(IEnumerable<Guid>? listIds)
        => (listIds ?? Array.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

    public static FcFulfillmentSelection ToSelection(
        bool allPublishedLists,
        IEnumerable<Guid>? listIds)
        => allPublishedLists
            ? FcFulfillmentSelection.All
            : FcFulfillmentSelection.Specific(Normalize(listIds));
}

/// <summary>
/// Narrow framework-thread handoff for the physical inventory snapshot. The
/// service never reads game inventory from its background worker.
/// </summary>
public interface IFcWorkerPhysicalInventory
{
    FcWorkerPhysicalInventorySnapshotResult CaptureCurrent(IEnumerable<FcQuantityKey> dependencyClosure);
}
