using System;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.Interfaces;

namespace GatherBuddy.AutoGather.Lists;

public interface ICompletionCountProvider
{
    int GetCompletionCount(IGatherable item, uint completionItemId, FcItemQuality? quality = null);
}

public sealed class DefaultCompletionCountProvider : ICompletionCountProvider
{
    public int GetCompletionCount(IGatherable item, uint completionItemId, FcItemQuality? quality = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (quality is not { } requestedQuality)
            return item.GetCompletionCount(completionItemId);

        var itemId = completionItemId == 0 ? item.ItemId : completionItemId;
        try
        {
            var split = CraftingInventoryCounter.GetInventorySplitCounts(itemId);
            return requestedQuality == FcItemQuality.Hq ? split.HQ : split.NQ;
        }
        catch
        {
            return 0;
        }
    }
}

public sealed class FcCompletionCountProvider : ICompletionCountProvider
{
    private readonly IItemQuantitySource _representedInventory;

    public FcCompletionCountProvider(IItemQuantitySource representedInventory)
    {
        _representedInventory = representedInventory ?? throw new ArgumentNullException(nameof(representedInventory));
    }

    public int GetCompletionCount(IGatherable item, uint completionItemId, FcItemQuality? quality = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var itemId = completionItemId == 0 ? item.ItemId : completionItemId;
        return GetCompletionCount(itemId, quality);
    }

    public int GetCompletionCount(uint itemId, FcItemQuality? quality = null)
    {
        if (itemId == 0)
            return 0;
        return quality is { } requestedQuality
            ? _representedInventory.GetQuantity(itemId, requestedQuality)
            : SumClamped(_representedInventory.GetNq(itemId), _representedInventory.GetHq(itemId));
    }

    private static int SumClamped(int nq, int hq)
    {
        var total = (long)Math.Max(0, nq) + Math.Max(0, hq);
        return total >= int.MaxValue ? int.MaxValue : (int)total;
    }
}
