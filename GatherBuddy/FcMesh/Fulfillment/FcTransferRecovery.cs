using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GatherBuddy.FcMesh.Chest;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Sessions;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Fulfillment;

/// <summary>Complete local pre-operation evidence; it is never published as a mesh record.</summary>
public sealed record FcTransferPhysicalSnapshot(
    bool AddonVisible,
    bool AddonReady,
    FcChestContainerState[] ContainerStates,
    FcChestSlotAddress[] ChestSlots,
    FcChestSlotAddress[] PlayerSlots,
    FcChestSlotItem[] ChestItems,
    FcChestSlotItem[] PlayerItems)
{
    public static FcTransferPhysicalSnapshot Capture(FcChestSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(
            snapshot.AddonVisible,
            snapshot.AddonReady,
            snapshot.ContainerStates.Values.ToArray(),
            snapshot.ChestSlots.ToArray(),
            snapshot.PlayerSlots.ToArray(),
            snapshot.ChestItems.Values.ToArray(),
            snapshot.PlayerItems.Values.ToArray());
    }

    public FcChestSnapshot ToSnapshot()
        => new(
            AddonVisible,
            AddonReady,
            ContainerStates ?? Array.Empty<FcChestContainerState>(),
            ChestSlots ?? Array.Empty<FcChestSlotAddress>(),
            PlayerSlots ?? Array.Empty<FcChestSlotAddress>(),
            ChestItems ?? Array.Empty<FcChestSlotItem>(),
            PlayerItems ?? Array.Empty<FcChestSlotItem>());
}

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
    public FcTransferPhysicalSnapshot? PhysicalBefore { get; }
    public ItemTransferRequest[] Requested { get; }
    public FcItemQuantityMap WorkerHeldBefore { get; }
    /// <summary>
    /// Complete logical worker register reserved before dispatch.  The held
    /// map is retained separately for the physical delta contract, while this
    /// record prevents recovery from copying mutable post-reservation fields.
    /// </summary>
    public WorkerSessionRecord? WorkerBefore { get; }

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
        DateTimeOffset createdAt,
        FcTransferPhysicalSnapshot? physicalBefore = null,
        ItemTransferRequest[]? requested = null,
        FcItemQuantityMap? workerHeldBefore = null,
        WorkerSessionRecord? workerBefore = null)
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
        PhysicalBefore = physicalBefore;
        Requested = requested ?? Array.Empty<ItemTransferRequest>();
        WorkerHeldBefore = workerHeldBefore ?? FcItemQuantityMap.Empty;
        WorkerBefore = workerBefore;
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
        if (operation.Requested is null
            || operation.Requested.Any(request => !request.IsValid)
            || operation.Requested.Length > FcRecordValidator.MaxArrayEntries
            || operation.WorkerHeldBefore is null
            || operation.WorkerHeldBefore.Entries.Length > FcRecordValidator.MaxArrayEntries)
            throw new ArgumentException("Pre-operation transfer request evidence is invalid.", nameof(operation));
        if (operation.WorkerBefore is { } workerBefore)
        {
            if (workerBefore.SessionId != operation.SessionId
                || workerBefore.SessionGeneration != operation.SessionGeneration
                || workerBefore.State is not (FcWorkerState.Active or FcWorkerState.Waiting)
                || !workerBefore.HeldInventoryMap.Equals(operation.WorkerHeldBefore))
                throw new ArgumentException(
                    "Pre-operation worker baseline does not match the transfer session or held map.",
                    nameof(operation));
            FcValidationResult validation;
            try
            {
                validation = new FcRecordValidator().Validate(
                    workerBefore,
                    workerBefore.Header?.OwnerAuthorId ?? string.Empty);
            }
            catch (Exception exception)
            {
                throw new ArgumentException(
                    $"Pre-operation worker baseline could not be validated: {exception.Message}",
                    nameof(operation),
                    exception);
            }
            if (!validation.IsValid)
                throw new ArgumentException(
                    $"Pre-operation worker baseline is invalid: {validation.Error}",
                    nameof(operation));
        }
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
            operation.CreatedAt,
            operation.PhysicalBefore is { } physical
                ? new FcTransferPhysicalSnapshot(
                    physical.AddonVisible,
                    physical.AddonReady,
                    (physical.ContainerStates ?? Array.Empty<FcChestContainerState>()).ToArray(),
                    (physical.ChestSlots ?? Array.Empty<FcChestSlotAddress>()).ToArray(),
                    (physical.PlayerSlots ?? Array.Empty<FcChestSlotAddress>()).ToArray(),
                    (physical.ChestItems ?? Array.Empty<FcChestSlotItem>()).ToArray(),
                    (physical.PlayerItems ?? Array.Empty<FcChestSlotItem>()).ToArray())
                : null,
            (operation.Requested ?? Array.Empty<ItemTransferRequest>()).ToArray(),
            new FcItemQuantityMap(operation.WorkerHeldBefore.Entries.ToArray()),
            operation.WorkerBefore is { } worker
                ? FcInMemoryWorkerSessionStateStore.CloneWorker(worker)
                : null);
}

public interface IFcPendingTransferJournalStore
{
    FcPendingTransferJournalState? Load();
    void Save(FcPendingTransferJournalState state);
}

public sealed class FcInMemoryPendingTransferJournalStore : IFcPendingTransferJournalStore
{
    private FcPendingTransferJournalState _state
        = new(Array.Empty<FcTransferPreOperation>());
    public bool FailWrites { get; set; }

    public FcPendingTransferJournalState? Load() => _state;

    public void Save(FcPendingTransferJournalState state)
    {
        if (FailWrites)
            throw new IOException("Synthetic pending transfer persistence failure.");
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }
}

/// <summary>
/// Durable local operation journal. The state contains only pre-operation
/// evidence and is never published to the mesh. A malformed file is returned
/// as null so the coordinator blocks instead of dispatching against guesses.
/// </summary>
public sealed class FcFilePendingTransferJournalStore : IFcPendingTransferJournalStore
{
    private readonly string _path;

    public FcFilePendingTransferJournalStore(string directory, string scope)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Pending transfer state directory is required.", nameof(directory));
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Pending transfer state scope is required.", nameof(scope));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(scope));
        _path = Path.Combine(directory, "fc-pending-transfer-" + Convert.ToHexString(digest).ToLowerInvariant() + ".json");
    }

    public FcPendingTransferJournalState? Load()
    {
        if (!File.Exists(_path))
            return new FcPendingTransferJournalState(Array.Empty<FcTransferPreOperation>());
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllBytes(_path),
                FcJsonContext.Default.FcPendingTransferJournalState);
        }
        catch (Exception exception) when (exception is IOException
            or JsonException
            or InvalidDataException
            or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(FcPendingTransferJournalState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                state,
                FcJsonContext.Default.FcPendingTransferJournalState);
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
            File.Move(temporary, _path, true);
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
