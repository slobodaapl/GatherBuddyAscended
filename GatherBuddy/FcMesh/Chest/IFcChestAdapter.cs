using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GatherBuddy.FcMesh.Chest;

public enum FcChestLoadState : byte
{
    Closed,
    Opened,
    Loading,
    Complete,
    Failed,
}

public readonly record struct ItemTransferRequest(
    FcChestItemKey Item,
    uint Quantity)
{
    public bool IsValid => Item.ItemId != 0 && Quantity > 0;
}

public enum FcTransferResultStatus : byte
{
    Dispatched,
    Failed,
    Blocked,
}

public sealed record FcTransferResult(
    FcTransferResultStatus Status,
    string Message,
    IReadOnlyList<ItemTransferRequest> Requested)
{
    public bool Dispatched => Status == FcTransferResultStatus.Dispatched;
}

/// <summary>
/// Strict framework-thread boundary for FC chest interaction. Implementations
/// may return an already-completed task after one nonblocking dispatch; they
/// must never perform game access from a worker thread.
/// </summary>
public interface IFcChestAdapter
{
    bool IsChestOpen { get; }
    FcChestLoadState GetLoadState();
    bool TryReadCompleteSnapshot(out FcChestSnapshot snapshot);

    Task<FcTransferResult> WithdrawAsync(
        IReadOnlyList<ItemTransferRequest> items,
        CancellationToken cancellationToken);

    Task<FcTransferResult> DepositAsync(
        IReadOnlyList<ItemTransferRequest> items,
        CancellationToken cancellationToken);
}

/// <summary>Live adapter over the existing complete reader and dispatcher.</summary>
public unsafe sealed class FcLiveChestAdapter : IFcChestAdapter
{
    private readonly FcChestSnapshotReader _reader;
    private readonly FcChestTransferDispatcher _dispatcher;

    public FcLiveChestAdapter(
        FcChestSnapshotReader? reader = null,
        FcChestTransferDispatcher? dispatcher = null)
    {
        _reader = reader ?? new FcChestSnapshotReader();
        _dispatcher = dispatcher ?? new FcChestTransferDispatcher();
    }

    public bool IsChestOpen => FcChestSnapshotReader.TryGetReadyVisibleChestAddon(out _);

    public FcChestLoadState GetLoadState()
        => !IsChestOpen
            ? FcChestLoadState.Closed
            : TryReadCompleteSnapshot(out _)
                ? FcChestLoadState.Complete
                : FcChestLoadState.Loading;

    public bool TryReadCompleteSnapshot(out FcChestSnapshot snapshot)
    {
        var result = _reader.ReadCurrentCompleteSnapshot();
        snapshot = result.Snapshot!;
        return result.IsComplete;
    }

    public Task<FcTransferResult> WithdrawAsync(
        IReadOnlyList<ItemTransferRequest> items,
        CancellationToken cancellationToken)
        => Dispatch(items, FcChestTransferDirection.Withdrawal, cancellationToken);

    public Task<FcTransferResult> DepositAsync(
        IReadOnlyList<ItemTransferRequest> items,
        CancellationToken cancellationToken)
        => Dispatch(items, FcChestTransferDirection.Deposit, cancellationToken);

    private Task<FcTransferResult> Dispatch(
        IReadOnlyList<ItemTransferRequest> requests,
        FcChestTransferDirection direction,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<FcTransferResult>(cancellationToken);
        if (requests is null || requests.Count != 1 || !requests[0].IsValid)
            return Task.FromResult(new FcTransferResult(
                FcTransferResultStatus.Blocked,
                "A chest transfer dispatch requires exactly one valid item request.",
                requests ?? Array.Empty<ItemTransferRequest>()));
        if (!TryReadCompleteSnapshot(out var snapshot))
            return Task.FromResult(new FcTransferResult(
                FcTransferResultStatus.Blocked,
                "A complete chest and player-inventory snapshot is required before dispatch.",
                requests));

        var request = requests[0];
        FcChestSlotAddress source;
        FcChestSlotAddress destination;
        if (direction == FcChestTransferDirection.Withdrawal)
        {
            var sourceItem = snapshot.ChestItems.Values
                .Where(item => item.Item == request.Item && item.Quantity <= request.Quantity)
                .OrderBy(item => item.Address.Container)
                .ThenBy(item => item.Address.Slot)
                .FirstOrDefault();
            if (sourceItem.Address == default && !snapshot.ChestItems.ContainsKey(sourceItem.Address))
                return Task.FromResult(new FcTransferResult(
                    FcTransferResultStatus.Blocked,
                    "No chest stack can satisfy the immediate withdrawal quantity without guessing.",
                    requests));
            source = sourceItem.Address;
            destination = snapshot.EmptyPlayerSlots
                .OrderBy(slot => slot.Container)
                .ThenBy(slot => slot.Slot)
                .FirstOrDefault();
            if (destination == default && !snapshot.PlayerSlots.Contains(destination))
                return Task.FromResult(new FcTransferResult(
                    FcTransferResultStatus.Blocked,
                    "No confirmed empty player slot is available for withdrawal.",
                    requests));
        }
        else
        {
            var sourceItem = snapshot.PlayerItems.Values
                .Where(item => item.Item == request.Item && item.Quantity <= request.Quantity)
                .OrderBy(item => item.Address.Container)
                .ThenBy(item => item.Address.Slot)
                .FirstOrDefault();
            if (sourceItem.Address == default && !snapshot.PlayerItems.ContainsKey(sourceItem.Address))
                return Task.FromResult(new FcTransferResult(
                    FcTransferResultStatus.Blocked,
                    "No player stack can satisfy the immediate deposit quantity without guessing.",
                    requests));
            source = sourceItem.Address;
            destination = snapshot.EmptyChestSlots
                .OrderBy(slot => slot.Container)
                .ThenBy(slot => slot.Slot)
                .FirstOrDefault();
            if (destination == default && !snapshot.ChestSlots.Contains(destination))
                return Task.FromResult(new FcTransferResult(
                    FcTransferResultStatus.Blocked,
                    "No confirmed empty FC chest slot is available for deposit.",
                    requests));
        }

        if (!_dispatcher.TryMove(source, destination, out var failure))
            return Task.FromResult(new FcTransferResult(FcTransferResultStatus.Failed, failure, requests));
        return Task.FromResult(new FcTransferResult(
            FcTransferResultStatus.Dispatched,
            "One FC chest stack transfer was dispatched.",
            [new ItemTransferRequest(request.Item, direction == FcChestTransferDirection.Withdrawal
                ? snapshot.ChestItems[source].Quantity
                : snapshot.PlayerItems[source].Quantity)]));
    }
}

/// <summary>Pure adapter used by state-machine tests and failure injection.</summary>
public sealed class FcFakeChestAdapter : IFcChestAdapter
{
    private readonly Func<IReadOnlyList<ItemTransferRequest>, FcTransferResult> _withdraw;
    private readonly Func<IReadOnlyList<ItemTransferRequest>, FcTransferResult> _deposit;
    private FcChestSnapshot? _snapshot;

    public FcFakeChestAdapter(
        FcChestSnapshot? snapshot = null,
        Func<IReadOnlyList<ItemTransferRequest>, FcTransferResult>? withdraw = null,
        Func<IReadOnlyList<ItemTransferRequest>, FcTransferResult>? deposit = null)
    {
        _snapshot = snapshot;
        _withdraw = withdraw ?? (items => new(FcTransferResultStatus.Dispatched, string.Empty, items));
        _deposit = deposit ?? (items => new(FcTransferResultStatus.Dispatched, string.Empty, items));
    }

    public bool IsChestOpen { get; set; }
    public FcChestLoadState LoadState { get; set; } = FcChestLoadState.Closed;
    public FcChestSnapshot? Snapshot
    {
        get => _snapshot;
        set => _snapshot = value;
    }

    public FcChestLoadState GetLoadState() => LoadState;

    public bool TryReadCompleteSnapshot(out FcChestSnapshot snapshot)
    {
        snapshot = _snapshot!;
        return IsChestOpen && _snapshot?.IsComplete == true;
    }

    public Task<FcTransferResult> WithdrawAsync(IReadOnlyList<ItemTransferRequest> items, CancellationToken cancellationToken)
        => Task.FromResult(cancellationToken.IsCancellationRequested
            ? new FcTransferResult(FcTransferResultStatus.Blocked, "Transfer cancellation requested.", items)
            : _withdraw(items));

    public Task<FcTransferResult> DepositAsync(IReadOnlyList<ItemTransferRequest> items, CancellationToken cancellationToken)
        => Task.FromResult(cancellationToken.IsCancellationRequested
            ? new FcTransferResult(FcTransferResultStatus.Blocked, "Transfer cancellation requested.", items)
            : _deposit(items));
}
