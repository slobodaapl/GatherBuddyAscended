using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Vulcan.Vendors;

public sealed class VendorBuyListEntry
{
    public Guid               Id             { get; set; } = Guid.NewGuid();
    public uint               ItemId         { get; set; }
    public string             ItemName       { get; set; } = string.Empty;
    public ushort             IconId         { get; set; }
    public uint               Cost           { get; set; }
    public uint               CurrencyItemId { get; set; }
    public string             CurrencyName   { get; set; } = string.Empty;
    public VendorShopType     ShopType       { get; set; } = VendorShopType.GilShop;
    public uint               SourceShopId   { get; set; }
    public int                ShopItemIndex  { get; set; } = -1;
    public int                GcRankIndex    { get; set; } = -1;
    public int                GcCategoryIndex { get; set; } = -1;
    public uint               VendorNpcId    { get; set; }
    public string             VendorNpcName  { get; set; } = string.Empty;
    public VendorMenuShopType MenuShopType   { get; set; } = VendorMenuShopType.GilShop;
    public uint               ShopId         { get; set; }
    public uint               UnlockQuestId  { get; set; }
    public uint               RequiredGrandCompanyRank { get; set; }
    public bool               Enabled        { get; set; } = true;
    public uint               TargetQuantity { get; set; } = 1;
    // Added after the original list format. Empty vectors mean legacy data;
    // the live shop entry supplies the complete offer in that case.
    public List<VendorCurrencyCost> CurrencyCosts { get; set; } = new();
    public List<VendorReceivedItem> ReceivedItems { get; set; } = new();
    public uint               RequiredAlliedSocietyId { get; set; }
    public uint               RequiredAlliedSocietyRank { get; set; }
    public bool               AlliedRequirementKnown { get; set; } = true;

    public IReadOnlyList<VendorCurrencyCost> EffectiveCurrencyCosts
        => CurrencyCosts is { Count: > 0 }
            ? CurrencyCosts
                .Where(cost => cost is null || cost.CurrencyItemId != 0 || cost.Amount != 0)
                .ToArray()
            : CurrencyItemId == 0 && Cost == 0
                ? Array.Empty<VendorCurrencyCost>()
                : [new VendorCurrencyCost(CurrencyItemId, Cost, CurrencyName, VendorShopResolver.GetCurrencyGroup(ShopType, CurrencyItemId))];

    public IReadOnlyList<VendorReceivedItem> EffectiveReceivedItems
        => ReceivedItems is { Count: > 0 }
            ? ReceivedItems
                .Where(output => output is null || output.ItemId != 0 || output.Quantity != 0)
                .ToArray()
            : [new VendorReceivedItem(ItemId, 1)];

    public string OfferSignature
        => VendorOfferMath.GetOfferSignature(ShopType, ItemId, EffectiveCurrencyCosts, EffectiveReceivedItems);
}
