using System;
using GatherBuddy.FcMesh.Native;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Publication;

/// <summary>
/// Narrow managed publication boundary. Implementations must enqueue native
/// work and must not wait for network, disk, or framework completion.
/// </summary>
public interface IFcPublicationTransport
{
    bool IsReady { get; }
    string? LocalAuthorId { get; }
    FcWorldStore? WorldStore { get; }

    FcNativeCallResult Put(
        ReadOnlySpan<byte> recordId,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> recordType,
        bool hasGeneration,
        ulong generation,
        ulong revision,
        ReadOnlySpan<byte> payload);
}

public sealed class FcMeshNativePublicationTransport : IFcPublicationTransport
{
    private readonly FcMeshNativeCoordinator _coordinator;

    public FcMeshNativePublicationTransport(FcMeshNativeCoordinator coordinator)
        => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public bool IsReady => _coordinator.Readiness.IsReady;
    public string? LocalAuthorId => _coordinator.LocalAuthorId;
    public FcWorldStore? WorldStore => _coordinator.WorldStore;

    public FcNativeCallResult Put(
        ReadOnlySpan<byte> recordId,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> recordType,
        bool hasGeneration,
        ulong generation,
        ulong revision,
        ReadOnlySpan<byte> payload)
        => _coordinator.Put(recordId, key, recordType, hasGeneration, generation, revision, payload);
}
