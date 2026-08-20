using System;
using System.Collections.Generic;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.Fulfillment;

public interface IItemQuantitySource
{
    int GetNq(uint itemId);

    int GetHq(uint itemId);

    int GetQuantity(uint itemId, FcItemQuality quality)
        => quality switch
        {
            FcItemQuality.Nq => GetNq(itemId),
            FcItemQuality.Hq => GetHq(itemId),
            _ => throw new ArgumentOutOfRangeException(nameof(quality)),
        };

    int GetQuantity(FcQuantityKey key) => GetQuantity(key.ItemId, key.Quality);

    int GetQuantity(ItemQuantityKey key) => GetQuantity(key.ItemId, key.Quality);
}

public class FcMapQuantitySource : IItemQuantitySource
{
    private readonly FcItemQuantityMap _map;

    public FcMapQuantitySource(FcItemQuantityMap map)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
    }

    public int GetNq(uint itemId) => _map.Get(itemId, FcItemQuality.Nq);

    public int GetHq(uint itemId) => _map.Get(itemId, FcItemQuality.Hq);

    public int GetQuantity(uint itemId, FcItemQuality quality)
        => quality switch
        {
            FcItemQuality.Nq => GetNq(itemId),
            FcItemQuality.Hq => GetHq(itemId),
            _ => throw new ArgumentOutOfRangeException(nameof(quality)),
        };

    public FcItemQuantityMap Snapshot => _map;
}

public sealed class FcRepresentedInventorySource : FcMapQuantitySource
{
    public FcRepresentedInventorySource(FcItemQuantityMap map)
        : base(map)
    {
    }
}

public sealed class FcLocalConsumableInventorySource : FcMapQuantitySource
{
    public FcLocalConsumableInventorySource(FcItemQuantityMap map)
        : base(map)
    {
    }
}

public sealed class FcCombinedInventorySource : IItemQuantitySource
{
    private readonly IReadOnlyList<IItemQuantitySource> _sources;

    public FcCombinedInventorySource(IEnumerable<IItemQuantitySource> sources)
    {
        _sources = new List<IItemQuantitySource>(sources ?? throw new ArgumentNullException(nameof(sources)));
    }

    public int GetNq(uint itemId)
    {
        var total = 0;
        foreach (var source in _sources)
            total = checked(total + source.GetNq(itemId));
        return total;
    }

    public int GetHq(uint itemId)
    {
        var total = 0;
        foreach (var source in _sources)
            total = checked(total + source.GetHq(itemId));
        return total;
    }

    public int GetQuantity(uint itemId, FcItemQuality quality)
        => quality switch
        {
            FcItemQuality.Nq => GetNq(itemId),
            FcItemQuality.Hq => GetHq(itemId),
            _ => throw new ArgumentOutOfRangeException(nameof(quality)),
        };
}
