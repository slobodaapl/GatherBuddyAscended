using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Vulcan.Vendors;

public static class VendorPreferenceHelper
{
    public static string GetKey(uint itemId, uint currencyId, uint cost)
        => $"{itemId}_{currencyId}_{cost}";
    public static string GetKey(VendorShopType shopType, uint itemId, uint currencyId, uint cost)
        => $"{(int)shopType}_{itemId}_{currencyId}_{cost}";

    public static string GetKey(VendorShopEntry entry)
        => GetKey(entry.ShopType, entry.ItemId, entry.CurrencyItemId, entry.Cost);

    public static string GetKey(VendorBuyListEntry entry)
        => GetKey(entry.ShopType, entry.ItemId, entry.CurrencyItemId, entry.Cost);

    public static string GetCanonicalKey(VendorShopEntry entry)
        => $"offer_{entry.OfferSignature}";

    public static string GetCanonicalKey(VendorBuyListEntry entry)
        => $"offer_{entry.OfferSignature}";

    public static string GetRouteKey(VendorNpc npc)
        => $"{npc.NpcId}_{(int)npc.MenuShopType}_{npc.ShopId}_{npc.InclusionPageIndex}_{npc.InclusionSubPageIndex}_{npc.SourceShopId}_{npc.ShopItemIndex}_{npc.GcRankIndex}_{npc.GcCategoryIndex}_{npc.UnlockQuestId}_{npc.RequiredGrandCompanyRank}_{npc.RequiredAlliedSocietyId}_{npc.RequiredAlliedSocietyRank}_{(npc.AlliedRequirementKnown ? 1 : 0)}";

    public static uint GetPreferredNpcId(uint itemId, uint currencyId, uint cost)
    {
        GatherBuddy.Config.VendorNpcPreferences.TryGetValue(GetKey(itemId, currencyId, cost), out var npcId);
        return npcId;
    }

    public static bool MatchesVendor(VendorNpc? left, VendorNpc? right)
        => left != null
        && right != null
        && left.NpcId == right.NpcId
        && left.MenuShopType == right.MenuShopType
        && left.ShopId == right.ShopId
        && left.InclusionPageIndex == right.InclusionPageIndex
        && left.InclusionSubPageIndex == right.InclusionSubPageIndex
        && left.SourceShopId == right.SourceShopId
        && left.ShopItemIndex == right.ShopItemIndex
        && left.GcRankIndex == right.GcRankIndex
        && left.GcCategoryIndex == right.GcCategoryIndex
        && left.UnlockQuestId == right.UnlockQuestId
        && left.RequiredGrandCompanyRank == right.RequiredGrandCompanyRank
        && left.RequiredAlliedSocietyId == right.RequiredAlliedSocietyId
        && left.RequiredAlliedSocietyRank == right.RequiredAlliedSocietyRank
        && left.AlliedRequirementKnown == right.AlliedRequirementKnown;

    public static void SetPreferredNpc(VendorShopEntry entry, VendorNpc npc)
    {
        GatherBuddy.Config.VendorRoutePreferences[GetCanonicalKey(entry)] = GetRouteKey(npc);
        GatherBuddy.Config.VendorNpcPreferences[GetKey(entry.ItemId, entry.CurrencyItemId, entry.Cost)] = npc.NpcId;
        GatherBuddy.Config.Save();
    }

    public static VendorNpc? ResolvePreferredNpc(VendorShopEntry entry)
        => ResolvePreferredNpc(entry.Npcs, entry.ShopType, entry.ItemId, entry.CurrencyItemId, entry.Cost, GetCanonicalKey(entry));

    public static VendorNpc? ResolvePreferredNpc(VendorShopEntry entry, IReadOnlyList<VendorNpc> npcs)
        => ResolvePreferredNpc(npcs, entry.ShopType, entry.ItemId, entry.CurrencyItemId, entry.Cost, GetCanonicalKey(entry));

    public static VendorNpc? ResolvePreferredNpc(VendorBuyListEntry entry, IReadOnlyList<VendorNpc> npcs)
        => ResolvePreferredNpc(npcs, entry.ShopType, entry.ItemId, entry.CurrencyItemId, entry.Cost, GetCanonicalKey(entry));

    private static VendorNpc? ResolvePreferredNpc(
        IReadOnlyList<VendorNpc> npcs,
        VendorShopType shopType,
        uint itemId,
        uint currencyId,
        uint cost,
        string? canonicalKey = null)
    {
        var selectableNpcs = VendorDevExclusions.GetSelectableNpcs(npcs, "resolving preferred vendors");
        if (selectableNpcs.Count == 0)
            return null;

        GatherBuddy.Config.VendorRoutePreferences ??= new();
        if (canonicalKey != null
         && GatherBuddy.Config.VendorRoutePreferences.TryGetValue(canonicalKey, out var canonicalPreferredRouteKey))
        {
            var canonicalPreferredRoute = selectableNpcs.FirstOrDefault(npc => GetRouteKey(npc) == canonicalPreferredRouteKey);
            if (canonicalPreferredRoute != null)
                return canonicalPreferredRoute;
        }

        if (GatherBuddy.Config.VendorRoutePreferences.TryGetValue(GetKey(shopType, itemId, currencyId, cost), out var preferredRouteKey))
        {
            var preferredRoute = selectableNpcs.FirstOrDefault(npc => GetRouteKey(npc) == preferredRouteKey);
            if (preferredRoute != null)
                return preferredRoute;
        }
        var preferredNpcId = GetPreferredNpcId(itemId, currencyId, cost);
        var preferredNpc   = selectableNpcs.FirstOrDefault(npc => npc.NpcId == preferredNpcId);
        if (preferredNpc != null)
            return preferredNpc;
        foreach (var npc in selectableNpcs)
            if (VendorNpcLocationCache.TryGetFirstLocation(npc.NpcId) != null)
                return npc;
        return selectableNpcs[0];
    }
}
