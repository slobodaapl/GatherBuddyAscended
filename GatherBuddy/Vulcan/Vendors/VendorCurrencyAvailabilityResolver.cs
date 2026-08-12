using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.AutoGather.Collectables;

namespace GatherBuddy.Vulcan.Vendors;

public enum VendorCurrencyAvailabilitySource
{
    Unknown,
    InventoryManagerGil,
    InventoryManagerGrandCompanySeals,
    InventoryManagerTomestones,
    InventoryManagerGoldSaucerCoin,
    InventoryManagerWolfMarks,
    InventoryManagerAlliedSeals,
    CurrencyManager,
    InventoryItemCount,
}

public readonly record struct VendorCurrencyAvailability(
    uint                             CurrencyItemId,
    string                           CurrencyName,
    uint                             AvailableAmount,
    VendorCurrencyAvailabilitySource Source
);

public readonly record struct VendorCurrencyWalletSnapshot(
    IReadOnlyDictionary<uint, long> Balances,
    IReadOnlyDictionary<uint, VendorCurrencyAvailabilitySource> Sources);

public static class VendorCurrencyAvailabilityResolver
{
    public const string NonAuthoritativeBalanceReason =
        "Vendor currency balance could not be verified from an authoritative wallet.";
    public const string UnexpectedBalanceDeltaReason =
        "Vendor currency balance delta did not match the completed transaction count.";

    public static bool IsAuthoritativeSource(VendorCurrencyAvailabilitySource source)
        => source is not VendorCurrencyAvailabilitySource.Unknown
            and not VendorCurrencyAvailabilitySource.InventoryItemCount;

    public static VendorCurrencyAvailability Resolve(VendorCurrencyGroup group, uint currencyItemId, string currencyName)
    {
        var normalizedCurrencyName = string.IsNullOrWhiteSpace(currencyName)
            ? currencyItemId == 0
                ? "currency"
                : $"currency {currencyItemId}"
            : currencyName;

        if (TryGetInventoryManagerAmount(group, currencyItemId, normalizedCurrencyName, out var availability))
            return availability;

        if (TryGetCurrencyManagerAmount(currencyItemId, normalizedCurrencyName, out availability))
            return availability;

        var inventoryAmount = currencyItemId == 0
            ? 0u
            : (uint)Math.Max(0, ItemHelper.GetInventoryAndArmoryItemCount(currencyItemId));
        return new VendorCurrencyAvailability(currencyItemId, normalizedCurrencyName, inventoryAmount, VendorCurrencyAvailabilitySource.InventoryItemCount);
    }

    public static bool TryCaptureAuthoritative(
        IEnumerable<VendorCurrencyCost> costs,
        out VendorCurrencyWalletSnapshot snapshot,
        out string failure)
    {
        var balances = new Dictionary<uint, long>();
        var sources = new Dictionary<uint, VendorCurrencyAvailabilitySource>();
        if (costs == null)
        {
            snapshot = new VendorCurrencyWalletSnapshot(balances, sources);
            failure = NonAuthoritativeBalanceReason;
            return false;
        }

        var costList = costs.ToList();
        if (!VendorOfferMath.HasValidCurrencyCosts(costList))
        {
            snapshot = new VendorCurrencyWalletSnapshot(balances, sources);
            failure = NonAuthoritativeBalanceReason;
            return false;
        }

        foreach (var cost in costList
                     .Where(cost => cost is null || cost.CurrencyItemId != 0 || cost.Amount != 0)
                     .GroupBy(cost => cost?.CurrencyItemId ?? 0)
                     .Select(group => group.First()))
        {
            if (cost == null || cost.CurrencyItemId == 0 || cost.Amount == 0)
            {
                snapshot = new VendorCurrencyWalletSnapshot(balances, sources);
                failure = NonAuthoritativeBalanceReason;
                return false;
            }

            var availability = Resolve(cost.Group, cost.CurrencyItemId, cost.CurrencyName);
            if (!IsAuthoritativeSource(availability.Source))
            {
                snapshot = new VendorCurrencyWalletSnapshot(balances, sources);
                failure = NonAuthoritativeBalanceReason;
                return false;
            }

            balances[cost.CurrencyItemId] = availability.AvailableAmount;
            sources[cost.CurrencyItemId] = availability.Source;
        }

        if (balances.Count == 0)
        {
            snapshot = new VendorCurrencyWalletSnapshot(balances, sources);
            failure = NonAuthoritativeBalanceReason;
            return false;
        }

        snapshot = new VendorCurrencyWalletSnapshot(balances, sources);
        failure = string.Empty;
        return true;
    }

    public static bool TryCalculateSpend(
        VendorCurrencyWalletSnapshot before,
        VendorCurrencyWalletSnapshot after,
        out IReadOnlyDictionary<uint, long> spent,
        out string failure)
    {
        var result = new Dictionary<uint, long>();
        foreach (var currencyId in before.Balances.Keys
                     .Concat(after.Balances.Keys)
                     .Distinct())
        {
            if (!before.Balances.TryGetValue(currencyId, out var beforeBalance)
                || !after.Balances.TryGetValue(currencyId, out var afterBalance)
                || !before.Sources.TryGetValue(currencyId, out var beforeSource)
                || !after.Sources.TryGetValue(currencyId, out var afterSource)
                || !IsAuthoritativeSource(beforeSource)
                || !IsAuthoritativeSource(afterSource))
            {
                spent = result;
                failure = NonAuthoritativeBalanceReason;
                return false;
            }

            if (beforeSource != afterSource)
            {
                spent = result;
                failure = UnexpectedBalanceDeltaReason;
                return false;
            }

            var delta = beforeBalance - afterBalance;
            if (delta < 0)
            {
                spent = result;
                failure = UnexpectedBalanceDeltaReason;
                return false;
            }

            result[currencyId] = delta;
        }

        spent = result;
        failure = string.Empty;
        return true;
    }

    public static bool TryValidateExactSpend(
        IEnumerable<VendorCurrencyCost> costs,
        uint transactionCount,
        VendorCurrencyWalletSnapshot before,
        VendorCurrencyWalletSnapshot after,
        out string failure)
    {
        if (transactionCount == 0)
        {
            failure = NonAuthoritativeBalanceReason;
            return false;
        }

        if (costs == null)
        {
            failure = NonAuthoritativeBalanceReason;
            return false;
        }

        var costList = costs.ToList();
        if (!VendorOfferMath.HasValidCurrencyCosts(costList))
        {
            failure = NonAuthoritativeBalanceReason;
            return false;
        }

        if (!TryCalculateSpend(before, after, out var spent, out failure))
            return false;

        var expectedTotals = costList
                     .Where(cost => cost is null || cost.CurrencyItemId != 0 || cost.Amount != 0)
                     .GroupBy(cost => cost?.CurrencyItemId ?? 0)
                     .Select(group => new
                     {
                         CurrencyId = group.Key,
                         Amount = checked(group.Aggregate(
                             0UL,
                             (total, cost) => checked(total + (ulong)(cost?.Amount ?? 0)))),
                     })
                     .ToDictionary(cost => cost.CurrencyId, cost => cost.Amount);
        if (expectedTotals.Count == 0
            || spent.Count != expectedTotals.Count
            || spent.Keys.Any(currencyId => !expectedTotals.ContainsKey(currencyId)))
        {
            failure = UnexpectedBalanceDeltaReason;
            return false;
        }

        foreach (var cost in expectedTotals)
        {
            var expected = checked(cost.Value * transactionCount);
            var actual = spent.GetValueOrDefault(cost.Key, -1);
            if (actual < 0 || expected > (ulong)long.MaxValue || (ulong)actual != expected)
            {
                failure = UnexpectedBalanceDeltaReason;
                return false;
            }
        }

        failure = string.Empty;
        return true;
    }

    private static unsafe bool TryGetInventoryManagerAmount(VendorCurrencyGroup group, uint currencyItemId, string currencyName,
        out VendorCurrencyAvailability availability)
    {
        availability = default;
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return false;

        if (group == VendorCurrencyGroup.Gil || currencyItemId == VendorShopResolver.GilCurrencyItemId)
        {
            availability = new VendorCurrencyAvailability(currencyItemId, currencyName, inventoryManager->GetGil(), VendorCurrencyAvailabilitySource.InventoryManagerGil);
            return true;
        }

        var isGrandCompanySealCurrency = VendorShopResolver.TryGetGameGrandCompanyIdFromSealCurrencyItemId(currencyItemId, out var grandCompanyId);
        if ((group == VendorCurrencyGroup.GrandCompanySeals || isGrandCompanySealCurrency)
         && isGrandCompanySealCurrency)
        {
            availability = new VendorCurrencyAvailability(currencyItemId, currencyName, inventoryManager->GetCompanySeals(grandCompanyId),
                VendorCurrencyAvailabilitySource.InventoryManagerGrandCompanySeals);
            return true;
        }

        if (group == VendorCurrencyGroup.Tomestones && currencyItemId != 0)
        {
            availability = new VendorCurrencyAvailability(currencyItemId, currencyName, inventoryManager->GetTomestoneCount(currencyItemId),
                VendorCurrencyAvailabilitySource.InventoryManagerTomestones);
            return true;
        }

        if (group == VendorCurrencyGroup.MGP || currencyItemId == VendorShopResolver.MgpCurrencyItemId)
        {
            availability = new VendorCurrencyAvailability(currencyItemId, currencyName, inventoryManager->GetGoldSaucerCoin(),
                VendorCurrencyAvailabilitySource.InventoryManagerGoldSaucerCoin);
            return true;
        }

        if (group == VendorCurrencyGroup.PvP || currencyItemId == VendorShopResolver.WolfMarkCurrencyItemId)
        {
            availability = new VendorCurrencyAvailability(currencyItemId, currencyName, inventoryManager->GetWolfMarks(),
                VendorCurrencyAvailabilitySource.InventoryManagerWolfMarks);
            return true;
        }

        if (currencyItemId == VendorShopResolver.AlliedSealCurrencyItemId)
        {
            availability = new VendorCurrencyAvailability(currencyItemId, currencyName, inventoryManager->GetAlliedSeals(),
                VendorCurrencyAvailabilitySource.InventoryManagerAlliedSeals);
            return true;
        }

        return false;
    }

    private static unsafe bool TryGetCurrencyManagerAmount(uint currencyItemId, string currencyName, out VendorCurrencyAvailability availability)
    {
        availability = default;
        if (currencyItemId == 0)
            return false;

        var currencyManager = CurrencyManager.Instance();
        if (currencyManager == null || !currencyManager->HasItem(currencyItemId))
            return false;

        availability = new VendorCurrencyAvailability(currencyItemId, currencyName, currencyManager->GetItemCount(currencyItemId),
            VendorCurrencyAvailabilitySource.CurrencyManager);
        return true;
    }

}
