using Dalamud.Game.Inventory;
using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

internal static class CraftingInventoryCounter
{
    private static readonly GameInventoryType[] InventoryTypes =
    [
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
        GameInventoryType.Crystals,
    ];

    internal static (int NQ, int HQ) GetInventorySplitCounts(uint itemId)
    {
        var nq = 0;
        var hq = 0;
        foreach (var inventoryType in InventoryTypes)
        {
            foreach (var item in Dalamud.GameInventory.GetInventoryItems(inventoryType))
            {
                if (item.BaseItemId != itemId)
                    continue;

                if (item.IsHq)
                    hq += (int)item.Quantity;
                else
                    nq += (int)item.Quantity;
            }
        }

        return (nq, hq);
    }
}
