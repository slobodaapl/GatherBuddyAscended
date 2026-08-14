namespace GatherBuddy.Crafting;

public static class CraftingSpecialistResources
{
    public const uint CrafterDelineationItemId = 28724;

    public static int GetCrafterDelineationCount()
    {
        var (nq, hq) = CraftingInventoryCounter.GetInventorySplitCounts(CrafterDelineationItemId);
        return nq + hq;
    }
}
