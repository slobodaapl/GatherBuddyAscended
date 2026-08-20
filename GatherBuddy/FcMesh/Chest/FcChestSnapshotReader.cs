using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;

namespace GatherBuddy.FcMesh.Chest;

/// <summary>
/// Framework-thread-only bridge from the live game inventory to immutable Phase 1 snapshots.
/// This is intentionally not a mesh or public-list integration point.
/// </summary>
public unsafe sealed class FcChestSnapshotReader
{
    private static readonly (InventoryType Native, FcChestContainer Logical)[] FreeCompanyContainers =
    [
        (InventoryType.FreeCompanyPage1, FcChestContainer.FreeCompanyPage1),
        (InventoryType.FreeCompanyPage2, FcChestContainer.FreeCompanyPage2),
        (InventoryType.FreeCompanyPage3, FcChestContainer.FreeCompanyPage3),
        (InventoryType.FreeCompanyPage4, FcChestContainer.FreeCompanyPage4),
        (InventoryType.FreeCompanyPage5, FcChestContainer.FreeCompanyPage5),
        (InventoryType.FreeCompanyCrystals, FcChestContainer.FreeCompanyCrystals),
    ];

    private static readonly (InventoryType Native, FcChestContainer Logical)[] PlayerContainers =
    [
        (InventoryType.Inventory1, FcChestContainer.Inventory1),
        (InventoryType.Inventory2, FcChestContainer.Inventory2),
        (InventoryType.Inventory3, FcChestContainer.Inventory3),
        (InventoryType.Inventory4, FcChestContainer.Inventory4),
    ];

    public FcChestReadResult Read()
    {
        if (!TryGetReadyVisibleChestAddon(out _))
            return FcChestReadResult.Incomplete("Open the FC chest and wait until its window is ready.");

        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return FcChestReadResult.Incomplete("The game inventory manager is unavailable.");

        var chestSlots = new List<FcChestSlotAddress>();
        var playerSlots = new List<FcChestSlotAddress>();
        var chestItems = new List<FcChestSlotItem>();
        var playerItems = new List<FcChestSlotItem>();
        var containerStates = new List<FcChestContainerState>();

        if (!ReadContainers(inventory, FreeCompanyContainers, chestSlots, chestItems, containerStates))
        {
            return FcChestReadResult.Incomplete(
                "One or more FC chest pages or the FC crystal container is not loaded.");
        }

        if (!ReadContainers(inventory, PlayerContainers, playerSlots, playerItems, containerStates))
        {
            return FcChestReadResult.Incomplete(
                "One or more normal player-inventory pages is not loaded.");
        }

        var snapshot = new FcChestSnapshot(
            true,
            true,
            containerStates,
            chestSlots,
            playerSlots,
            chestItems,
            playerItems);
        return new FcChestReadResult(snapshot, string.Empty);
    }

    /// <summary>
    /// Read-only publication seam. It intentionally aliases the complete
    /// reader and never invokes the transfer dispatcher.
    /// </summary>
    public FcChestReadResult ReadCurrentCompleteSnapshot()
        => Read();

    public static bool TryGetReadyVisibleChestAddon(out AtkUnitBase* addon)
    {
        // The addon name is the only stable readiness seam available to this spike. The
        // generated AgentFreeCompanyChest wrapper is used only for the transfer call.
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("FreeCompanyChest", out addon))
            return false;
        return addon != null && addon->IsVisible;
    }

    private static bool ReadContainers(
        InventoryManager* inventory,
        IReadOnlyList<(InventoryType Native, FcChestContainer Logical)> containers,
        ICollection<FcChestSlotAddress> addresses,
        ICollection<FcChestSlotItem> items,
        ICollection<FcChestContainerState> containerStates)
    {
        foreach (var (native, logical) in containers)
        {
            var container = inventory->GetInventoryContainer(native);
            if (container == null || !container->IsLoaded)
                return false;

            var capacity = checked((int)container->Size);
            if (capacity <= 0)
                return false;

            containerStates.Add(new FcChestContainerState(logical, true, capacity));

            for (var slotIndex = 0; slotIndex < capacity; ++slotIndex)
            {
                var address = new FcChestSlotAddress(logical, (uint)slotIndex);
                addresses.Add(address);
                var item = container->GetInventorySlot(slotIndex);
                if (item == null || item->ItemId == 0)
                    continue;

                var rawQuantity = (long)item->Quantity;
                if (rawQuantity <= 0 || rawQuantity > uint.MaxValue)
                    return false;
                var quantity = (uint)rawQuantity;

                items.Add(new FcChestSlotItem(
                    address,
                    new FcChestItemKey(
                        item->ItemId,
                        item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)),
                    quantity));
            }
        }

        return true;
    }
}

public unsafe sealed class FcChestTransferDispatcher
{
    public bool TryMove(
        FcChestSlotAddress source,
        FcChestSlotAddress destination,
        out string failureReason)
    {
        if (!source.IsFreeCompanyContainer && !source.IsPlayerInventoryContainer)
        {
            failureReason = "The transfer source is not a recognized FC or player container.";
            return false;
        }

        if (!destination.IsFreeCompanyContainer && !destination.IsPlayerInventoryContainer)
        {
            failureReason = "The transfer destination is not a recognized FC or player container.";
            return false;
        }

        if (source.IsFreeCompanyContainer == destination.IsFreeCompanyContainer)
        {
            failureReason = "A probe transfer must cross the FC chest/player-inventory boundary.";
            return false;
        }

        if ((!source.IsFreeCompanyItemContainer && source.IsFreeCompanyContainer)
            || (!destination.IsFreeCompanyItemContainer && destination.IsFreeCompanyContainer))
        {
            failureReason = "Crystal-container transfers are not permitted by the probe.";
            return false;
        }

        // Recheck independently at the native-call boundary. The caller's snapshot
        // validation may have run on an earlier framework frame.
        if (!FcChestSnapshotReader.TryGetReadyVisibleChestAddon(out _))
        {
            failureReason = "The FC chest addon is no longer visible, fully loaded, and ready.";
            return false;
        }

        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentFreeCompanyChest.Instance();
        if (agent == null)
        {
            failureReason = "The FC chest agent is unavailable.";
            return false;
        }

        try
        {
            agent->MoveItemInChest(
                ToNative(source.Container),
                source.Slot,
                ToNative(destination.Container),
                destination.Slot);
            failureReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            // The generated wrapper is expected not to let native C++ exceptions cross
            // its C# ABI. This catches only a managed wrapper failure; it is not a
            // substitute for an ABI exception boundary. The probe never compensates
            // after any failed invocation.
            failureReason = $"FC chest transfer invocation failed: {ex.Message}";
            return false;
        }
    }

    private static InventoryType ToNative(FcChestContainer container)
        => container switch
        {
            FcChestContainer.FreeCompanyPage1 => InventoryType.FreeCompanyPage1,
            FcChestContainer.FreeCompanyPage2 => InventoryType.FreeCompanyPage2,
            FcChestContainer.FreeCompanyPage3 => InventoryType.FreeCompanyPage3,
            FcChestContainer.FreeCompanyPage4 => InventoryType.FreeCompanyPage4,
            FcChestContainer.FreeCompanyPage5 => InventoryType.FreeCompanyPage5,
            FcChestContainer.FreeCompanyCrystals => InventoryType.FreeCompanyCrystals,
            FcChestContainer.Inventory1 => InventoryType.Inventory1,
            FcChestContainer.Inventory2 => InventoryType.Inventory2,
            FcChestContainer.Inventory3 => InventoryType.Inventory3,
            FcChestContainer.Inventory4 => InventoryType.Inventory4,
            _ => throw new ArgumentOutOfRangeException(nameof(container), container, null),
        };
}
