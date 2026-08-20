using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GatherBuddy.FcMesh.Chest;

public enum FcChestContainer : byte
{
    FreeCompanyPage1,
    FreeCompanyPage2,
    FreeCompanyPage3,
    FreeCompanyPage4,
    FreeCompanyPage5,
    FreeCompanyCrystals,
    Inventory1,
    Inventory2,
    Inventory3,
    Inventory4,
}

public readonly record struct FcChestSlotAddress(FcChestContainer Container, uint Slot)
{
    public bool IsFreeCompanyContainer
        => Container is FcChestContainer.FreeCompanyPage1
            or FcChestContainer.FreeCompanyPage2
            or FcChestContainer.FreeCompanyPage3
            or FcChestContainer.FreeCompanyPage4
            or FcChestContainer.FreeCompanyPage5
            or FcChestContainer.FreeCompanyCrystals;

    public bool IsFreeCompanyItemContainer
        => Container is FcChestContainer.FreeCompanyPage1
            or FcChestContainer.FreeCompanyPage2
            or FcChestContainer.FreeCompanyPage3
            or FcChestContainer.FreeCompanyPage4
            or FcChestContainer.FreeCompanyPage5;

    public bool IsPlayerInventoryContainer
        => Container is FcChestContainer.Inventory1
            or FcChestContainer.Inventory2
            or FcChestContainer.Inventory3
            or FcChestContainer.Inventory4;

    public override string ToString()
        => $"{Container}[{Slot}]";
}

public readonly record struct FcChestItemKey(uint ItemId, bool IsHq)
{
    public override string ToString()
        => $"{ItemId}{(IsHq ? " HQ" : " NQ")}";
}

public readonly record struct FcChestSlotItem(
    FcChestSlotAddress Address,
    FcChestItemKey Item,
    uint Quantity);

public readonly record struct FcChestContainerState(
    FcChestContainer Container,
    bool IsLoaded,
    int Capacity)
{
    public bool IsUsable
        => IsLoaded && Capacity > 0;
}

/// <summary>
/// Complete absolute observations of the FC chest and the normal player bags.
/// Empty slots are represented by <see cref="ChestSlots"/>/<see cref="PlayerSlots"/>
/// but are absent from the occupied-item collections.
/// </summary>
public sealed class FcChestSnapshot
{
    private readonly IReadOnlyDictionary<FcChestSlotAddress, FcChestSlotItem> _chestItems;
    private readonly IReadOnlyDictionary<FcChestSlotAddress, FcChestSlotItem> _playerItems;
    private readonly IReadOnlyDictionary<FcChestContainer, FcChestContainerState> _containerStates;

    private static readonly FcChestContainer[] RequiredFreeCompanyContainers =
    [
        FcChestContainer.FreeCompanyPage1,
        FcChestContainer.FreeCompanyPage2,
        FcChestContainer.FreeCompanyPage3,
        FcChestContainer.FreeCompanyPage4,
        FcChestContainer.FreeCompanyPage5,
        FcChestContainer.FreeCompanyCrystals,
    ];

    private static readonly FcChestContainer[] RequiredPlayerContainers =
    [
        FcChestContainer.Inventory1,
        FcChestContainer.Inventory2,
        FcChestContainer.Inventory3,
        FcChestContainer.Inventory4,
    ];

    public FcChestSnapshot(
        bool addonVisible,
        bool addonReady,
        IEnumerable<FcChestContainerState> containerStates,
        IEnumerable<FcChestSlotAddress> chestSlots,
        IEnumerable<FcChestSlotAddress> playerSlots,
        IEnumerable<FcChestSlotItem> chestItems,
        IEnumerable<FcChestSlotItem> playerItems,
        string? incompleteReason = null)
    {
        AddonVisible = addonVisible;
        AddonReady = addonReady;
        _containerStates = new ReadOnlyDictionary<FcChestContainer, FcChestContainerState>(
            BuildContainerMap(containerStates));
        ChestSlots = chestSlots.Distinct().OrderBy(static x => x.Container).ThenBy(static x => x.Slot).ToArray();
        PlayerSlots = playerSlots.Distinct().OrderBy(static x => x.Container).ThenBy(static x => x.Slot).ToArray();
        _chestItems = new ReadOnlyDictionary<FcChestSlotAddress, FcChestSlotItem>(
            BuildItemMap(chestItems, ChestSlots, "FC chest", expectFreeCompany: true));
        _playerItems = new ReadOnlyDictionary<FcChestSlotAddress, FcChestSlotItem>(
            BuildItemMap(playerItems, PlayerSlots, "player inventory", expectFreeCompany: false));
        IncompleteReason = incompleteReason ?? (AddonVisible && AddonReady
            ? null
            : "The FC chest addon is not visible, fully loaded, and ready.");
    }

    public bool AddonVisible { get; }
    public bool AddonReady { get; }
    public IReadOnlyDictionary<FcChestContainer, FcChestContainerState> ContainerStates => _containerStates;
    public bool AllFreeCompanyContainersLoaded
        => AreContainersUsable(RequiredFreeCompanyContainers)
            && HasSlotCoverage(RequiredFreeCompanyContainers, ChestSlots);
    public bool AllPlayerContainersLoaded
        => AreContainersUsable(RequiredPlayerContainers)
            && HasSlotCoverage(RequiredPlayerContainers, PlayerSlots);
    public bool IsComplete
        => AddonVisible && AddonReady && AllFreeCompanyContainersLoaded && AllPlayerContainersLoaded;

    public string? IncompleteReason { get; }
    public IReadOnlyList<FcChestSlotAddress> ChestSlots { get; }
    public IReadOnlyList<FcChestSlotAddress> PlayerSlots { get; }
    public IReadOnlyDictionary<FcChestSlotAddress, FcChestSlotItem> ChestItems => _chestItems;
    public IReadOnlyDictionary<FcChestSlotAddress, FcChestSlotItem> PlayerItems => _playerItems;

    public IEnumerable<FcChestSlotAddress> EmptyChestSlots
        => ChestSlots.Where(static slot => slot.IsFreeCompanyItemContainer)
            .Where(slot => !_chestItems.ContainsKey(slot));

    public IEnumerable<FcChestSlotAddress> EmptyPlayerSlots
        => PlayerSlots.Where(static slot => slot.IsPlayerInventoryContainer)
            .Where(slot => !_playerItems.ContainsKey(slot));

    public bool TryGetChestItem(FcChestSlotAddress address, out FcChestSlotItem item)
        => _chestItems.TryGetValue(address, out item);

    public bool TryGetPlayerItem(FcChestSlotAddress address, out FcChestSlotItem item)
        => _playerItems.TryGetValue(address, out item);

    public IReadOnlyDictionary<FcChestItemKey, uint> GetChestTotals()
        => Aggregate(_chestItems.Values.Where(static item => item.Address.IsFreeCompanyItemContainer));

    public IReadOnlyDictionary<uint, uint> GetCrystalTotals()
    {
        var totals = new Dictionary<uint, uint>();
        foreach (var item in _chestItems.Values.Where(static item => item.Address.Container == FcChestContainer.FreeCompanyCrystals))
            totals[item.Item.ItemId] = checked(totals.GetValueOrDefault(item.Item.ItemId) + item.Quantity);
        return new ReadOnlyDictionary<uint, uint>(totals);
    }

    public IReadOnlyDictionary<FcChestItemKey, uint> GetPlayerTotals()
        => Aggregate(_playerItems.Values);

    private static Dictionary<FcChestSlotAddress, FcChestSlotItem> BuildItemMap(
        IEnumerable<FcChestSlotItem> items,
        IReadOnlyList<FcChestSlotAddress> knownSlots,
        string description,
        bool expectFreeCompany)
    {
        var known = knownSlots.ToHashSet();
        var result = new Dictionary<FcChestSlotAddress, FcChestSlotItem>();
        foreach (var item in items)
        {
            if (!known.Contains(item.Address))
                throw new ArgumentException($"{description} item {item.Address} is not in its slot set.", nameof(items));
            if (item.Item.ItemId == 0 || item.Quantity == 0)
                throw new ArgumentOutOfRangeException(nameof(items), "Occupied snapshot items require a non-zero item and quantity.");
            if (item.Address.IsFreeCompanyContainer != expectFreeCompany)
                throw new ArgumentException($"{description} item {item.Address} belongs to the wrong inventory domain.", nameof(items));
            if (item.Address.Container == FcChestContainer.FreeCompanyCrystals && item.Item.IsHq)
                throw new ArgumentException("FC crystal quantities cannot be marked HQ.", nameof(items));
            if (!result.TryAdd(item.Address, item))
                throw new ArgumentException($"Duplicate snapshot item at {item.Address}.", nameof(items));
        }

        return result;
    }

    private static Dictionary<FcChestContainer, FcChestContainerState> BuildContainerMap(
        IEnumerable<FcChestContainerState> states)
    {
        var result = new Dictionary<FcChestContainer, FcChestContainerState>();
        foreach (var state in states)
        {
            if (state.Capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(states), "Container capacity cannot be negative.");
            if (!result.TryAdd(state.Container, state))
                throw new ArgumentException($"Duplicate container state for {state.Container}.", nameof(states));
        }

        return result;
    }

    private bool AreContainersUsable(IEnumerable<FcChestContainer> requiredContainers)
        => requiredContainers.All(container =>
            _containerStates.TryGetValue(container, out var state) && state.IsUsable);

    private bool HasSlotCoverage(
        IEnumerable<FcChestContainer> requiredContainers,
        IReadOnlyList<FcChestSlotAddress> slots)
    {
        foreach (var container in requiredContainers)
        {
            if (!_containerStates.TryGetValue(container, out var state) || !state.IsUsable)
                return false;

            var containerSlots = slots.Where(slot => slot.Container == container)
                .OrderBy(static slot => slot.Slot)
                .ToArray();
            if (containerSlots.Length != state.Capacity)
                return false;
            for (var index = 0; index < containerSlots.Length; ++index)
            {
                if (containerSlots[index].Slot != (uint)index)
                    return false;
            }
        }

        return true;
    }

    private static Dictionary<FcChestItemKey, uint> Aggregate(IEnumerable<FcChestSlotItem> items)
    {
        var totals = new Dictionary<FcChestItemKey, uint>();
        foreach (var item in items)
        {
            totals[item.Item] = checked(totals.GetValueOrDefault(item.Item) + item.Quantity);
        }

        return totals;
    }
}

public readonly record struct FcChestReadResult(FcChestSnapshot? Snapshot, string FailureReason)
{
    public bool IsComplete => Snapshot?.IsComplete == true;

    public static FcChestReadResult Incomplete(string reason)
        => new(null, reason);
}

public readonly record struct FcChestTransferPlan(
    FcChestSlotAddress Source,
    FcChestSlotAddress Destination,
    FcChestItemKey Item,
    uint Quantity = 1);

public readonly record struct FcChestPreparation(
    FcChestSnapshot Snapshot,
    FcChestTransferPlan Withdrawal);

public enum FcChestTransferDirection : byte
{
    Withdrawal,
    Deposit,
}

public enum FcChestDeltaFailure : byte
{
    None,
    IncompleteSnapshot,
    NoDelta,
    SourceChanged,
    DestinationOccupied,
    WrongDelta,
    UnrelatedMutation,
    AmbiguousMutation,
}

public readonly record struct FcChestDeltaValidation(
    bool Accepted,
    FcChestDeltaFailure Failure,
    string Message)
{
    public static FcChestDeltaValidation Success { get; }
        = new(true, FcChestDeltaFailure.None, "Exact paired inventory delta observed.");
}
