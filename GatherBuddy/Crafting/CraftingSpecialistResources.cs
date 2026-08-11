using FFXIVClientStructs.FFXIV.Client.Game;

namespace GatherBuddy.Crafting;

public static class CraftingSpecialistResources
{
    public const uint CrafterDelineationItemId = 28724;

    public static unsafe int GetCrafterDelineationCount()
    {
        var inventory = InventoryManager.Instance();
        return inventory == null
            ? 0
            : (int)inventory->GetInventoryItemCount(CrafterDelineationItemId, false, false, false);
    }
}
