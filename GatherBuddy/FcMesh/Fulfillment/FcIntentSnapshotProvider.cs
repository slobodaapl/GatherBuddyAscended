using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Sessions;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Fulfillment;

public sealed record FcIntentSnapshot(
    IReadOnlyList<WorkerSessionRecord> ActiveWorkers,
    WorkerSessionRecord LocalWorker);

public interface IFcIntentSnapshotProvider
{
    bool TryGetCurrent(out FcIntentSnapshot snapshot);
}

/// <summary>
/// Framework-local current projection seam for one character/controller.
/// It never publishes or serializes intent. Scope, author, world, session,
/// generation, and revision changes make the provider unusable, preventing a
/// prior character/session/world from being reused.
/// </summary>
public sealed class FcProjectedIntentSnapshotProvider : IFcIntentSnapshotProvider, IDisposable
{
    private readonly Func<FcProjectedWorld?> _worldProvider;
    private readonly Func<WorkerSessionRecord?> _localWorkerProvider;
    private readonly Func<FcWorkerSessionDiagnostics> _sessionDiagnosticsProvider;
    private readonly string _scope;
    private readonly string _authorId;
    private readonly string _worldFingerprint;
    private bool _disposed;

    public FcProjectedIntentSnapshotProvider(
        Func<FcProjectedWorld?> worldProvider,
        Func<WorkerSessionRecord?> localWorkerProvider,
        Func<FcWorkerSessionDiagnostics> sessionDiagnosticsProvider,
        string scope,
        string authorId,
        string worldFingerprint)
    {
        _worldProvider = worldProvider ?? throw new ArgumentNullException(nameof(worldProvider));
        _localWorkerProvider = localWorkerProvider ?? throw new ArgumentNullException(nameof(localWorkerProvider));
        _sessionDiagnosticsProvider = sessionDiagnosticsProvider ?? throw new ArgumentNullException(nameof(sessionDiagnosticsProvider));
        _scope = scope ?? string.Empty;
        _authorId = authorId ?? string.Empty;
        _worldFingerprint = worldFingerprint ?? string.Empty;
    }

    public bool TryGetCurrent(out FcIntentSnapshot snapshot)
    {
        snapshot = null!;
        if (_disposed
            || string.IsNullOrWhiteSpace(_scope)
            || string.IsNullOrWhiteSpace(_authorId))
            return false;

        try
        {
            var diagnostics = _sessionDiagnosticsProvider();
            if (!string.Equals(diagnostics.AuthorScope, _scope, StringComparison.Ordinal)
                || !string.Equals(diagnostics.AuthorId, _authorId, StringComparison.Ordinal)
                || !diagnostics.IsSubscribed
                || !diagnostics.WritesAllowed
                || diagnostics.RecoveryRequired
                || diagnostics.Forked
                || diagnostics.SessionId == Guid.Empty
                || diagnostics.SessionGeneration == 0)
                return false;

            var localWorker = _localWorkerProvider();
            if (localWorker is null
                || localWorker.Header is null
                || !string.Equals(localWorker.Header.OwnerAuthorId, _authorId, StringComparison.Ordinal)
                || localWorker.State is not (FcWorkerState.Active or FcWorkerState.Waiting)
                || localWorker.SessionId == Guid.Empty
                || localWorker.SessionGeneration == 0
                || diagnostics.WorkerState != localWorker.State
                || diagnostics.SessionId != localWorker.SessionId
                || diagnostics.SessionGeneration != localWorker.SessionGeneration
                || diagnostics.Revision == 0
                || localWorker.Header.Revision == 0
                || diagnostics.Revision != localWorker.Header.Revision
                || string.IsNullOrWhiteSpace(localWorker.WorldFingerprint)
                || !string.Equals(localWorker.WorldFingerprint, _worldFingerprint, StringComparison.Ordinal))
                return false;

            // Do not read a projected world after the local identity/session
            // has already failed validation. This prevents a stale character
            // from observing another scope's current projection.
            var world = _worldProvider();
            if (world is null)
                return false;

            // FcWorldProjection excludes stale and forked worker registers.
            // Require the exact desired register to still be present in that
            // projection; a caller-supplied local record alone is not proof
            // that the current mesh view accepts it.
            var projectedLocals = world.ActiveWorkers.Where(worker =>
                worker is not null
                && worker.Header is not null
                && string.Equals(
                    worker.Header.OwnerAuthorId,
                    localWorker.Header.OwnerAuthorId,
                    StringComparison.Ordinal)
                && worker.SessionId == localWorker.SessionId
                && worker.SessionGeneration == localWorker.SessionGeneration)
                .ToArray();
            if (projectedLocals.Length != 1)
                return false;
            var projectedLocal = projectedLocals[0];
            if (projectedLocal.Header.Revision != localWorker.Header.Revision
                || !string.Equals(
                    projectedLocal.WorldFingerprint,
                    localWorker.WorldFingerprint,
                    StringComparison.Ordinal)
                || !string.Equals(
                    FcCanonical.SemanticHash(projectedLocal),
                    FcCanonical.SemanticHash(localWorker),
                    StringComparison.Ordinal))
                return false;

            snapshot = new FcIntentSnapshot(
                world.ActiveWorkers.ToArray(),
                localWorker);
            return true;
        }
        catch (Exception)
        {
            // A current projection is advisory. A failed framework-local read
            // must retain the incumbent order, never invent a new one.
            return false;
        }
    }

    public void Dispose()
        => _disposed = true;
}
