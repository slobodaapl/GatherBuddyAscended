using System.Collections.Generic;

namespace GatherBuddy.Marketboard;

public sealed class MarketListing
{
    public ulong  ListingId    { get; init; }
    public ulong  RetainerId   { get; init; }
    public int    PricePerUnit { get; init; }
    public int    Quantity     { get; init; }
    public bool   IsHq         { get; init; }
    public uint   WorldId      { get; init; }
    public string WorldName    { get; init; } = string.Empty;
    public long   TotalTax     { get; init; }
    public int    TownId       { get; init; }
    public bool?  IsMannequin  { get; init; }
    public bool?  IsSellingAsSet { get; init; }
}

public sealed class MarketItemData
{
    public uint   ItemId                { get; init; }
    public string ItemName              { get; set;  } = string.Empty;
    public uint   IconId                { get; set;  }
    public float  MinPrice              { get; init; }
    public List<MarketListing> Listings { get; init; } = new();
}

public sealed record MarketSearchResult(uint ItemId, string Name, uint IconId, int Score);
