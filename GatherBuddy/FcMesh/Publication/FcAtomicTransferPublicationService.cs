using System;
using System.Text;
using GatherBuddy.FcMesh.Chest;
using GatherBuddy.FcMesh.Native;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Sessions;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Publication;

public sealed record FcAtomicTransferPublicationResult(
    bool Accepted,
    string Message,
    FcInventoryTransferRecord? Transfer)
{
    public static FcAtomicTransferPublicationResult Blocked(string message)
        => new(false, message, null);
}

/// <summary>
/// Publishes one indivisible physical chest action as one native transfer
/// record. The nested chest and worker after-states are serialized inside the
/// same payload; no separate post-transfer register Put is emitted.
/// </summary>
public sealed class FcAtomicTransferPublicationService
{
    private readonly IFcPublicationTransport _transport;
    private readonly FcWorkerSessionService _worker;
    private readonly Func<string?> _scopeProvider;
    private readonly Func<string?> _authorProvider;
    private readonly Func<FcCompatibilityContext> _compatibilityProvider;

    public FcAtomicTransferPublicationService(
        IFcPublicationTransport transport,
        FcWorkerSessionService worker,
        Func<string?> scopeProvider,
        Func<string?> authorProvider,
        Func<FcCompatibilityContext> compatibilityProvider)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));
        _authorProvider = authorProvider ?? throw new ArgumentNullException(nameof(authorProvider));
        _compatibilityProvider = compatibilityProvider ?? throw new ArgumentNullException(nameof(compatibilityProvider));
    }

    public FcAtomicTransferCommitResult Commit(FcAtomicTransferCommit commit)
    {
        if (commit is null || commit.OperationId == Guid.Empty)
            return new(FcAtomicTransferCommitStatus.Blocked, "Atomic transfer operation identity is unavailable.");
        if (commit.WorkerBefore is null || commit.WorkerHeldBefore is null)
            return new(
                FcAtomicTransferCommitStatus.Blocked,
                "Atomic transfer requires the complete journaled worker baseline before publication.");
        var scope = _scopeProvider();
        var author = _authorProvider();
        var context = _compatibilityProvider();
        if (string.IsNullOrWhiteSpace(scope)
            || string.IsNullOrWhiteSpace(author)
            || !_transport.IsReady
            || !context.IsValid
            || !string.Equals(author, _transport.LocalAuthorId, StringComparison.Ordinal))
            return new(FcAtomicTransferCommitStatus.Blocked, "Atomic transfer publication is not ready for the current author.");
        var transferKey = author + "/transfer/" + commit.OperationId.ToString("D");
        if (_transport.WorldStore?.Transfers.TryGetValue(transferKey, out var existing) == true)
        {
            if (!MatchesExisting(existing, commit))
                return new(
                    FcAtomicTransferCommitStatus.Blocked,
                    "A duplicate atomic transfer operation ID carries conflicting evidence.");
            var existingObservation = _worker.ObserveAtomicTransfer(existing);
            if (!existingObservation.Accepted)
                return new(
                    FcAtomicTransferCommitStatus.Blocked,
                    $"Existing atomic transfer could not repair local worker observation: {existingObservation.Message}");
            return new(
                FcAtomicTransferCommitStatus.Accepted,
                "Duplicate atomic transfer operation already committed and locally observed.");
        }
        var highWater = 0UL;
        if (_transport.WorldStore is { } worldStore
            && worldStore.RevisionHighWater.TryGetValue(transferKey, out var storedHighWater))
            highWater = storedHighWater;
        if (highWater == ulong.MaxValue)
            return new(FcAtomicTransferCommitStatus.Blocked, "Atomic transfer revision is exhausted.");
        var derived = _worker.BuildAtomicTransferRecord(commit, highWater + 1, out var transfer);
        if (!derived.Accepted)
            return new(FcAtomicTransferCommitStatus.Blocked, derived.Message);
        var validation = new FcRecordValidator().Validate(transfer, author);
        if (!validation.IsValid)
            return new(FcAtomicTransferCommitStatus.Blocked, $"Atomic transfer record is invalid: {validation.Error}");
        FcNativeCallResult result;
        try
        {
            result = _transport.Put(
                Encoding.UTF8.GetBytes(commit.OperationId.ToString("D")),
                Encoding.UTF8.GetBytes(FcMeshKey.ForRecord(
                    FcRecordTypes.InventoryTransfer,
                    author,
                    commit.OperationId.ToString("D"))),
                Encoding.UTF8.GetBytes(FcRecordTypes.InventoryTransfer),
                true,
                transfer.SessionGeneration,
                transfer.Header.Revision,
                FcCanonical.SerializeUtf8(transfer));
        }
        catch (Exception exception)
        {
            return new(FcAtomicTransferCommitStatus.Blocked, $"Atomic transfer native publication failed: {exception.Message}");
        }
        if (!result.Succeeded)
            return new(FcAtomicTransferCommitStatus.Blocked, $"Atomic transfer native publication rejected: {result.ErrorCode}.");

        // The transfer record is the sole mesh event. ObserveAtomicTransfer
        // updates the local ledger/refresh state without queuing a worker Put.
        var publishedObservation = _worker.ObserveAtomicTransfer(transfer);
        if (!publishedObservation.Accepted)
            return new(FcAtomicTransferCommitStatus.Blocked, $"Atomic transfer was published but local worker observation was rejected: {publishedObservation.Message}");
        return new(FcAtomicTransferCommitStatus.Accepted, "Atomic transfer published and observed as one mesh record.");
    }

    private bool MatchesExisting(
        FcInventoryTransferRecord existing,
        FcAtomicTransferCommit commit)
    {
        if (existing is null
            || commit is null
            || existing.Header is null
            || existing.ChestAfter is not { } existingChestAfter
            || existing.WorkerAfter is not { } existingWorkerAfter
            || commit.ChestAfter is not { } commitChestAfter
            || commit.ActualTransferred is null)
            return false;

        var transportAuthorId = _transport.LocalAuthorId;
        if (string.IsNullOrWhiteSpace(transportAuthorId))
            return false;

        FcValidationResult validation;
        try
        {
            validation = new FcRecordValidator().Validate(existing, transportAuthorId);
        }
        catch
        {
            return false;
        }
        if (!validation.IsValid
            || existingChestAfter.Header is null
            || existingWorkerAfter.Header is null)
            return false;

        return existing.OperationId == commit.OperationId
            && existing.Header.RecordId == commit.OperationId
            && existing.Header.RecordType == FcRecordTypes.InventoryTransfer
            && existing.Header.OwnerAuthorId == transportAuthorId
            && existing.Header.ProtocolVersion == FcProtocolVersion.Current
            && existing.Header.SchemaVersion == FcProtocolVersion.CurrentSchema
            && existing.Header.Revision != 0
            && existingChestAfter.Header.Revision != 0
            && existingWorkerAfter.Header.Revision != 0
            && existing.SessionId == commit.SessionId
            && existing.SessionGeneration == commit.SessionGeneration
            && existing.PurposeListId == commit.PurposeListId
            && existing.Kind == commit.Kind
            && existing.Outcome == commit.Outcome
            && existing.ActualTransferredMap.Equals(commit.ActualTransferred)
            && SameChestSnapshot(existingChestAfter, commitChestAfter)
            && _worker.MatchesExistingAtomicTransfer(existing, commit);
    }

    private static bool SameChestSnapshot(
        ChestSnapshotRecord left,
        ChestSnapshotRecord right)
        => left is not null
            && right is not null
            && left.Complete == right.Complete
            && left.LoadedPageMask == right.LoadedPageMask
            && new FcItemQuantityMap(left.Items ?? Array.Empty<ItemQuantityEntry>())
                .Equals(new FcItemQuantityMap(right.Items ?? Array.Empty<ItemQuantityEntry>()))
            && (left.Crystals ?? new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>()))
                .Equals(right.Crystals ?? new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>()));
}
