using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Vulcan.Vendors;

public enum VendorShopType
{
    GilShop,
    SpecialCurrency,
    GrandCompanySeals,
}

public enum VendorMenuShopType
{
    GilShop,
    SpecialShop,
    InclusionShop,
    CollectablesShop,
    GrandCompanyShop,
    FreeCompanyCreditShop,
}

public enum VendorGilFilter { All, Gatherable, Fish, Craftable, Housing, Dyes, Other }

public enum VendorCurrencyGroup
{
    Gil,
    Tomestones,
    BicolorGemstones,
    HuntSeals,
    GrandCompanySeals,
    Scrips,
    MGP,
    PvP,
    Other,
}

/// <summary>
/// One currency component of a vendor transaction. A shop offer may require
/// more than one currency at once; callers must not infer the complete cost
/// from <see cref="VendorShopEntry.Cost"/> alone.
/// </summary>
public sealed record VendorCurrencyCost(
    uint                CurrencyItemId,
    uint                Amount,
    string              CurrencyName,
    VendorCurrencyGroup Group);

/// <summary>
/// One output of a vendor transaction. Special shops can return several
/// distinct items and can return more than one copy of each item.
/// </summary>
public sealed record VendorReceivedItem(uint ItemId, uint Quantity);

public static class VendorOfferMath
{
    public static uint GetPurchaseCount(uint desiredQuantity, uint receivedQuantity)
    {
        if (desiredQuantity == 0)
            return 0;
        if (receivedQuantity == 0)
            throw new ArgumentOutOfRangeException(nameof(receivedQuantity), "A vendor offer must return at least one item.");

        return (uint)(((ulong)desiredQuantity + receivedQuantity - 1) / receivedQuantity);
    }

    public static uint SumReceivedQuantity(
        IEnumerable<VendorReceivedItem> outputs,
        uint itemId)
    {
        if (itemId == 0 || outputs == null)
            return 0;

        ulong total = 0;
        foreach (var output in outputs)
        {
            if (output == null)
                return 0;
            if (output.ItemId == 0 && output.Quantity == 0)
                continue;
            if (output.ItemId == 0 || output.Quantity == 0)
                return 0;
            if (output.ItemId != itemId)
                continue;

            total = checked(total + output.Quantity);
        }

        return checked((uint)total);
    }

    public static bool HasValidCurrencyCosts(IEnumerable<VendorCurrencyCost>? costs)
    {
        if (costs == null)
            return false;

        var hasCost = false;
        foreach (var cost in costs)
        {
            if (cost == null)
                return false;
            if (cost.CurrencyItemId == 0 && cost.Amount == 0)
                continue;
            if (cost.CurrencyItemId == 0 || cost.Amount == 0)
                return false;
            hasCost = true;
        }

        return hasCost;
    }

    public static bool HasValidReceivedItems(
        IEnumerable<VendorReceivedItem>? outputs,
        uint requestedItemId)
    {
        if (requestedItemId == 0 || outputs == null)
            return false;

        var hasRequestedOutput = false;
        var hasOutput = false;
        foreach (var output in outputs)
        {
            if (output == null)
                return false;
            if (output.ItemId == 0 && output.Quantity == 0)
                continue;
            if (output.ItemId == 0 || output.Quantity == 0)
                return false;

            hasOutput = true;
            hasRequestedOutput |= output.ItemId == requestedItemId;
        }

        return hasOutput && hasRequestedOutput;
    }

    public static IReadOnlyDictionary<uint, ulong> GetCurrencyTotals(
        IReadOnlyList<VendorCurrencyCost> costs,
        uint purchaseCount)
    {
        if (!HasValidCurrencyCosts(costs))
            throw new ArgumentException("A vendor offer must contain a complete currency-cost vector.", nameof(costs));

        var totals = new Dictionary<uint, ulong>();
        foreach (var cost in costs)
        {
            if (cost.CurrencyItemId == 0 && cost.Amount == 0)
                continue;
            if (purchaseCount == 0)
                continue;

            totals[cost.CurrencyItemId] = checked(totals.GetValueOrDefault(cost.CurrencyItemId)
                + (ulong)cost.Amount * purchaseCount);
        }

        return totals;
    }

    public static bool HasSameOffer(
        VendorShopEntry left,
        VendorShopEntry right)
        => string.Equals(GetOfferSignature(left), GetOfferSignature(right), StringComparison.Ordinal);

    public static string GetOfferSignature(VendorShopEntry entry)
        => GetOfferSignature(entry.ShopType, entry.ItemId, entry.CurrencyCosts, entry.ReceivedItems);

    public static string GetTransactionSignature(VendorShopEntry entry)
        => GetTransactionSignature(entry.ShopType, entry.CurrencyCosts, entry.ReceivedItems);

    public static string GetOfferSignature(
        VendorShopType shopType,
        uint itemId,
        IEnumerable<VendorCurrencyCost> currencyCosts,
        IEnumerable<VendorReceivedItem> receivedItems)
    {
        var costs = GetCurrencyVectorSignature(currencyCosts);
        var outputs = GetReceivedVectorSignature(receivedItems);
        return $"{(int)shopType}|{itemId}|{outputs}|{costs}";
    }

    public static string GetTransactionSignature(
        VendorShopType shopType,
        IEnumerable<VendorCurrencyCost> currencyCosts,
        IEnumerable<VendorReceivedItem> receivedItems)
    {
        var costs = GetCurrencyVectorSignature(currencyCosts);
        var outputs = GetReceivedVectorSignature(receivedItems);
        return $"{(int)shopType}|{outputs}|{costs}";
    }

    public static bool MatchesPersistedOffer(VendorBuyListEntry saved, VendorShopEntry live)
        => (saved.CurrencyCosts is not { Count: > 0 }
            || string.Equals(GetCurrencyVectorSignature(saved.CurrencyCosts), GetCurrencyVectorSignature(live.CurrencyCosts), StringComparison.Ordinal))
        && (saved.ReceivedItems is not { Count: > 0 }
            || string.Equals(GetReceivedVectorSignature(saved.ReceivedItems), GetReceivedVectorSignature(live.ReceivedItems), StringComparison.Ordinal));

    private static string GetCurrencyVectorSignature(IEnumerable<VendorCurrencyCost> currencyCosts)
        => string.Join(",", currencyCosts
            .Where(cost => cost is null || cost.CurrencyItemId != 0 || cost.Amount != 0)
            .OrderBy(cost => cost?.CurrencyItemId ?? 0)
            .ThenBy(cost => cost?.Amount ?? 0)
            .ThenBy(cost => cost?.Group)
            .Select(cost => cost == null
                ? "<invalid>"
                : $"{cost.CurrencyItemId}:{cost.Amount}:{(int)cost.Group}"));

    private static string GetReceivedVectorSignature(IEnumerable<VendorReceivedItem> receivedItems)
        => string.Join(",", receivedItems
            .Where(output => output is null || output.ItemId != 0 || output.Quantity != 0)
            .OrderBy(output => output?.ItemId ?? 0)
            .ThenBy(output => output?.Quantity ?? 0)
            .Select(output => output == null
                ? "<invalid>"
                : $"{output.ItemId}:{output.Quantity}"));
}

public sealed record VendorNpc(
    uint               NpcId,
    string             Name,
    uint               ShopId,
    VendorMenuShopType MenuShopType       = VendorMenuShopType.GilShop,
    int                InclusionPageIndex = -1,
    int                InclusionSubPageIndex = -1,
    int                ShopItemIndex         = -1,
    uint               SourceShopId          = 0,
    int                GcRankIndex           = -1,
    int                GcCategoryIndex       = -1,
    uint               UnlockQuestId         = 0,
    uint               RequiredGrandCompanyRank = 0,
    uint               RequiredAlliedSocietyId = 0,
    uint               RequiredAlliedSocietyRank = 0,
    bool               AlliedRequirementKnown = true
);

public sealed record VendorShopEntry(
    uint                ItemId,
    string              ItemName,
    ushort              IconId,
    uint                Cost,
    uint                CurrencyItemId,
    string              CurrencyName,
    List<VendorNpc>     Npcs,
    VendorShopType      ShopType,
    VendorCurrencyGroup Group,
    IReadOnlyList<uint>? RequiredQuestIds = null,
    uint                RequiredAchievementId = 0,
    uint                RequiredContentId = 0,
    bool                RequiredContentMustBeComplete = false,
    IReadOnlyList<VendorCurrencyCost>? CurrencyCostVector = null,
    IReadOnlyList<VendorReceivedItem>? ReceivedOutputs = null,
    uint                RequiredAlliedSocietyId = 0,
    uint                RequiredAlliedSocietyRank = 0)
{
    public IReadOnlyList<VendorCurrencyCost> CurrencyCosts
        => CurrencyCostVector is not null
            ? CurrencyCostVector
                .Where(cost => cost is null || cost.CurrencyItemId != 0 || cost.Amount != 0)
                .ToArray()
            : [new VendorCurrencyCost(CurrencyItemId, Cost, CurrencyName, Group)];

    public IReadOnlyList<VendorReceivedItem> ReceivedItems
        => ReceivedOutputs is not null
            ? ReceivedOutputs
                .Where(output => output is null || output.ItemId != 0 || output.Quantity != 0)
                .ToArray()
            : [new VendorReceivedItem(ItemId, 1)];

    public uint ReceivedQuantity
        => VendorOfferMath.SumReceivedQuantity(ReceivedItems, ItemId);

    public string OfferSignature
        => VendorOfferMath.GetOfferSignature(this);

    public string TransactionSignature
        => VendorOfferMath.GetTransactionSignature(this);

    public uint PurchaseCountFor(uint desiredQuantity)
        => VendorOfferMath.GetPurchaseCount(desiredQuantity, ReceivedQuantity);
}
