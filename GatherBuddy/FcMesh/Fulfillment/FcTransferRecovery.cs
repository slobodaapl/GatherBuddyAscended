using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.Fulfillment;

/// <summary>Local pre-operation evidence; it is never published as a mesh record.</summary>
public sealed record FcTransferPreOperation
{
    public Guid OperationId { get; }
    public Guid SessionId { get; }
    public ulong SessionGeneration { get; }
    public FcInventoryTransferKind Kind { get; }
    public Guid? PurposeListId { get; }
    public string ChestFingerprint { get; }
    public string WorkerFingerprint { get; }
    public CrystalQuantityMap ChestCrystals { get; }
    public DateTimeOffset CreatedAt { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public FcTransferPreOperation(
        Guid operationId,
        Guid sessionId,
        ulong sessionGeneration,
        FcInventoryTransferKind kind,
        Guid? purposeListId,
        string chestFingerprint,
        string workerFingerprint,
        CrystalQuantityMap chestCrystals,
        DateTimeOffset createdAt)
    {
        OperationId = operationId;
        SessionId = sessionId;
        SessionGeneration = sessionGeneration;
        Kind = kind;
        PurposeListId = purposeListId;
        ChestFingerprint = chestFingerprint ?? throw new ArgumentNullException(nameof(chestFingerprint));
        WorkerFingerprint = workerFingerprint ?? throw new ArgumentNullException(nameof(workerFingerprint));
        ChestCrystals = chestCrystals ?? throw new ArgumentNullException(nameof(chestCrystals));
        CreatedAt = createdAt;
    }
}

public enum FcPendingTransferRecoveryStatus : byte
{
    Missing,
    RequiresReconciliation,
    FingerprintMismatch,
    Reconciled,
}

public sealed record FcPendingTransferJournalState
{
    public FcTransferPreOperation[] Operations { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public FcPendingTransferJournalState(FcTransferPreOperation[] operations)
    {
        Operations = (operations ?? throw new ArgumentNullException(nameof(operations))).ToArray();
    }
}

public sealed record FcPendingTransferRecoveryResult(
    FcPendingTransferRecoveryStatus Status,
    FcTransferPreOperation? Operation);

public sealed class FcPendingTransferJournal
{
    private readonly Dictionary<Guid, FcTransferPreOperation> _pending = new();

    public IReadOnlyDictionary<Guid, FcTransferPreOperation> Pending => _pending;
    public bool HasPending => _pending.Count != 0;
    public bool AllowsWorkerPublication => !HasPending;

    public void Begin(FcTransferPreOperation operation)
    {
        if (operation is null)
            throw new ArgumentNullException(nameof(operation));
        if (operation.OperationId == Guid.Empty || operation.SessionId == Guid.Empty
            || operation.SessionGeneration == 0 || !Enum.IsDefined(operation.Kind)
            || operation.ChestCrystals is null
            || string.IsNullOrWhiteSpace(operation.ChestFingerprint)
            || string.IsNullOrWhiteSpace(operation.WorkerFingerprint))
            throw new ArgumentException("Pre-operation fingerprints and operation identity are required.", nameof(operation));
        if (_pending.ContainsKey(operation.OperationId))
            throw new InvalidOperationException("Operation ID is already pending.");
        _pending.Add(operation.OperationId, Clone(operation));
    }

    public bool TryGet(Guid operationId, out FcTransferPreOperation operation)
        => _pending.TryGetValue(operationId, out operation!);

    public bool Complete(Guid operationId)
        => _pending.Remove(operationId);

    public FcPendingTransferJournalState ExportState()
        => new(_pending
            .OrderBy(pair => pair.Key)
            .Select(pair => Clone(pair.Value))
            .ToArray());

    public static FcPendingTransferJournal Restore(FcPendingTransferJournalState state)
    {
        if (!TryRestore(state, out var journal))
            throw new ArgumentException("Pending transfer journal state is corrupt or missing.", nameof(state));
        return journal;
    }

    public static bool TryRestore(
        FcPendingTransferJournalState? state,
        out FcPendingTransferJournal journal)
    {
        journal = new FcPendingTransferJournal();
        if (state?.Operations is null)
            return false;
        try
        {
            foreach (var operation in state.Operations)
                journal.Begin(operation);
            return true;
        }
        catch (ArgumentException)
        {
            journal = new FcPendingTransferJournal();
            return false;
        }
        catch (InvalidOperationException)
        {
            journal = new FcPendingTransferJournal();
            return false;
        }
    }

    public FcPendingTransferRecoveryResult Recover(
        Guid operationId,
        string? observedChestFingerprint,
        string? observedWorkerFingerprint)
    {
        if (!_pending.TryGetValue(operationId, out var operation))
            return new(FcPendingTransferRecoveryStatus.Missing, null);
        if (string.IsNullOrWhiteSpace(observedChestFingerprint)
            || string.IsNullOrWhiteSpace(observedWorkerFingerprint))
            return new(FcPendingTransferRecoveryStatus.RequiresReconciliation, operation);
        if (!string.Equals(operation.ChestFingerprint, observedChestFingerprint, StringComparison.Ordinal)
            || !string.Equals(operation.WorkerFingerprint, observedWorkerFingerprint, StringComparison.Ordinal))
            return new(FcPendingTransferRecoveryStatus.FingerprintMismatch, operation);
        _pending.Remove(operationId);
        return new(FcPendingTransferRecoveryStatus.Reconciled, operation);
    }

    public bool TryComplete(
        Guid operationId,
        string? observedChestFingerprint,
        string? observedWorkerFingerprint)
        => Recover(operationId, observedChestFingerprint, observedWorkerFingerprint).Status
            == FcPendingTransferRecoveryStatus.Reconciled;

    public void Clear()
        => _pending.Clear();

    private static FcTransferPreOperation Clone(FcTransferPreOperation operation)
        => new(
            operation.OperationId,
            operation.SessionId,
            operation.SessionGeneration,
            operation.Kind,
            operation.PurposeListId,
            operation.ChestFingerprint,
            operation.WorkerFingerprint,
            new CrystalQuantityMap(operation.ChestCrystals.Entries.ToArray()),
            operation.CreatedAt);
}

/// <summary>
/// Crystals use a separate local reconciliation path. They are intentionally
/// not smuggled into the item transfer array or partially applied to the mesh.
/// </summary>
public sealed record FcCrystalTransferReconciliation(
    Guid OperationId,
    CrystalQuantityMap Before,
    CrystalQuantityMap After)
{
    public bool IsObserved => OperationId != Guid.Empty && Before is not null && After is not null;
}
