using System;

namespace GatherBuddy.FcMesh.Native;

public enum FcMeshReadinessState : byte
{
    NotCreated,
    Created,
    Joining,
    ReconcilingSnapshot,
    Ready,
    Left,
    Error,
}

/// <summary>
/// Managed readiness evidence. InitialSyncCompleted is only a signal; Ready
/// requires the corresponding managed world snapshot to have swapped.
/// </summary>
public sealed record FcMeshReadiness(
    FcMeshReadinessState State,
    bool IsReady,
    string? ContactedPeer,
    ulong WorldEpoch,
    ulong SyncEventSequence,
    ulong SnapshotBaseEventSequence,
    ulong RecordCount,
    bool GroupMetadataCompatible,
    string? NamespaceId,
    string? GroupTicket,
    string Error)
{
    public static FcMeshReadiness NotCreated { get; }
        = new(FcMeshReadinessState.NotCreated, false, null, 0, 0, 0, 0, false, null, null, string.Empty);
}
