using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Vulcan.Tests;

public static class VendorAcceptanceTests
{
    public static int Run()
    {
        var assertions = 0;

        void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
            assertions++;
        }

        var uldahLocation = new VendorNpcLocation(1, "Scrip Exchange", 130, 1, Vector3.Zero);
        var tuliyollalLocation = new VendorNpcLocation(1, "Scrip Exchange", 1185, 2, Vector3.One);
        var sharedVendorLocations = new[] { uldahLocation, tuliyollalLocation };
        Require(VendorNpcLocationCache.SelectPreferredLocation(sharedVendorLocations, 1185) == tuliyollalLocation,
            "a vendor NPC present in the current territory must use that local route instead of the cache's first remote route");
        Require(VendorNpcLocationCache.SelectPreferredLocation(sharedVendorLocations, 9999) == uldahLocation,
            "vendor location selection must retain the deterministic cache fallback when no current-territory route exists");

        var costs = new List<VendorCurrencyCost>
        {
            new(100u, 4u, "First currency", VendorCurrencyGroup.Other),
            new(200u, 3u, "Second currency", VendorCurrencyGroup.Other),
        };
        var outputs = new List<VendorReceivedItem>
        {
            new(500u, 2u),
            new(600u, 1u),
        };
        var entry = new VendorShopEntry(
            500u,
            "Output",
            1,
            4u,
            100u,
            "First currency",
            new List<VendorNpc>(),
            VendorShopType.SpecialCurrency,
            VendorCurrencyGroup.Other,
            CurrencyCostVector: costs,
            ReceivedOutputs: outputs);

        Require(entry.ReceivedQuantity == 2u,
            "vendor offer must expose the requested output quantity, not an implicit one-per-transaction quantity");
        Require(entry.PurchaseCountFor(5u) == 3u,
            "vendor purchase count must ceil desired output by received quantity");

        var totals = VendorOfferMath.GetCurrencyTotals(costs, 3u);
        Require(totals.Count == 2 && totals[100u] == 12u && totals[200u] == 9u,
            "vendor currency totals must scale every component of a multi-currency offer");
        Require(entry.ReceivedItems.SequenceEqual(outputs),
            "vendor offer must retain co-products for live transaction accounting");
        var alias = entry with { ItemId = 600u };
        Require(entry.TransactionSignature == alias.TransactionSignature
                    && !string.Equals(entry.OfferSignature, alias.OfferSignature, StringComparison.Ordinal),
            "co-product aliases must share transaction identity while retaining distinct UI row identity");
        Require(VendorOfferMath.HasValidCurrencyCosts(costs)
                && VendorOfferMath.HasValidReceivedItems(outputs, entry.ItemId),
            "a complete multi-component offer must remain purchasable");

        var currencyBefore = new VendorCurrencyWalletSnapshot(
            new Dictionary<uint, long> { [100u] = 100, [200u] = 50 },
            new Dictionary<uint, VendorCurrencyAvailabilitySource>
            {
                [100u] = VendorCurrencyAvailabilitySource.CurrencyManager,
                [200u] = VendorCurrencyAvailabilitySource.InventoryManagerTomestones,
            });
        var currencyAfter = new VendorCurrencyWalletSnapshot(
            new Dictionary<uint, long> { [100u] = 88, [200u] = 41 },
            new Dictionary<uint, VendorCurrencyAvailabilitySource>
            {
                [100u] = VendorCurrencyAvailabilitySource.CurrencyManager,
                [200u] = VendorCurrencyAvailabilitySource.InventoryManagerTomestones,
            });
        Require(VendorCurrencyAvailabilityResolver.TryValidateExactSpend(costs, 3u, currencyBefore, currencyAfter, out _),
            "multi-currency wallet deltas must equal every vector component scaled by transaction count");

        var gilCosts = new List<VendorCurrencyCost>
        {
            new(VendorShopResolver.GilCurrencyItemId, 5u, "Gil", VendorCurrencyGroup.Gil),
        };
        var gilBefore = new VendorCurrencyWalletSnapshot(
            new Dictionary<uint, long> { [VendorShopResolver.GilCurrencyItemId] = 100 },
            new Dictionary<uint, VendorCurrencyAvailabilitySource>
            {
                [VendorShopResolver.GilCurrencyItemId] = VendorCurrencyAvailabilitySource.InventoryManagerGil,
            });
        var gilAfter = gilBefore with
        {
            Balances = new Dictionary<uint, long> { [VendorShopResolver.GilCurrencyItemId] = 85 },
        };
        Require(VendorCurrencyAvailabilityResolver.TryValidateExactSpend(gilCosts, 3u, gilBefore, gilAfter, out _),
            "ordinary Gil wallet deltas must remain accepted as authoritative spend");

        var wrongCurrencyAfter = currencyAfter with
        {
            Balances = new Dictionary<uint, long> { [100u] = 88, [200u] = 40 },
        };
        Require(!VendorCurrencyAvailabilityResolver.TryValidateExactSpend(costs, 3u, currencyBefore, wrongCurrencyAfter, out var wrongCurrencyFailure)
                && wrongCurrencyFailure == VendorCurrencyAvailabilityResolver.UnexpectedBalanceDeltaReason,
            "a wrong multi-currency wallet delta must reject the transaction");

        var extraCurrencyAfter = currencyAfter with
        {
            Balances = new Dictionary<uint, long> { [100u] = 88, [200u] = 41, [300u] = 7 },
            Sources = new Dictionary<uint, VendorCurrencyAvailabilitySource>
            {
                [100u] = VendorCurrencyAvailabilitySource.CurrencyManager,
                [200u] = VendorCurrencyAvailabilitySource.InventoryManagerTomestones,
                [300u] = VendorCurrencyAvailabilitySource.CurrencyManager,
            },
        };
        var extraCurrencyBefore = currencyBefore with
        {
            Balances = new Dictionary<uint, long> { [100u] = 100, [200u] = 50, [300u] = 7 },
            Sources = new Dictionary<uint, VendorCurrencyAvailabilitySource>
            {
                [100u] = VendorCurrencyAvailabilitySource.CurrencyManager,
                [200u] = VendorCurrencyAvailabilitySource.InventoryManagerTomestones,
                [300u] = VendorCurrencyAvailabilitySource.CurrencyManager,
            },
        };
        Require(!VendorCurrencyAvailabilityResolver.TryValidateExactSpend(costs, 3u, extraCurrencyBefore, extraCurrencyAfter, out var extraCurrencyFailure)
                && extraCurrencyFailure == VendorCurrencyAvailabilityResolver.UnexpectedBalanceDeltaReason,
            "an unexpected wallet component must reject exact vendor-spend verification");

        var unknownCurrencyBefore = currencyBefore with
        {
            Sources = new Dictionary<uint, VendorCurrencyAvailabilitySource>
            {
                [100u] = VendorCurrencyAvailabilitySource.InventoryItemCount,
                [200u] = VendorCurrencyAvailabilitySource.InventoryManagerTomestones,
            },
        };
        Require(!VendorCurrencyAvailabilityResolver.TryValidateExactSpend(costs, 3u, unknownCurrencyBefore, currencyAfter, out var unknownCurrencyFailure)
                && unknownCurrencyFailure == VendorCurrencyAvailabilityResolver.NonAuthoritativeBalanceReason,
            "an inventory-item currency fallback must reject exact wallet verification");

        var unresolvedCurrencyBefore = currencyBefore with
        {
            Sources = new Dictionary<uint, VendorCurrencyAvailabilitySource>
            {
                [100u] = VendorCurrencyAvailabilitySource.Unknown,
                [200u] = VendorCurrencyAvailabilitySource.InventoryManagerTomestones,
            },
        };
        Require(!VendorCurrencyAvailabilityResolver.TryValidateExactSpend(costs, 3u, unresolvedCurrencyBefore, currencyAfter, out var unresolvedCurrencyFailure)
                && unresolvedCurrencyFailure == VendorCurrencyAvailabilityResolver.NonAuthoritativeBalanceReason,
            "an unknown currency source must reject exact wallet verification");

        Require(VendorPurchaseManager.ValidateOutputInventory(
                    outputs,
                    new Dictionary<uint, int> { [500u] = 4, [600u] = 2 },
                    2u,
                    out _)
                && !VendorPurchaseManager.ValidateOutputInventory(
                    outputs,
                    new Dictionary<uint, int> { [500u] = 4, [600u] = 1 },
                    2u,
                    out _)
                && VendorPurchaseManager.ValidateOutputInventory(
                    [new VendorReceivedItem(500u, 2u), new VendorReceivedItem(500u, 1u), new VendorReceivedItem(600u, 1u)],
                    new Dictionary<uint, int> { [500u] = 6, [600u] = 2 },
                    2u,
                    out _),
            "live vendor completion must require every co-product inventory delta for the submitted transaction count");

        var fixedSlots = entry with
        {
            CurrencyCostVector =
            [
                new VendorCurrencyCost(0u, 0u, "Empty cost slot", VendorCurrencyGroup.Other),
                new VendorCurrencyCost(100u, 4u, "First currency", VendorCurrencyGroup.Other),
            ],
            ReceivedOutputs =
            [
                new VendorReceivedItem(0u, 0u),
                new VendorReceivedItem(500u, 2u),
                new VendorReceivedItem(600u, 1u),
            ],
        };
        Require(VendorOfferMath.HasValidCurrencyCosts(fixedSlots.CurrencyCosts)
                && VendorOfferMath.HasValidReceivedItems(fixedSlots.ReceivedItems, fixedSlots.ItemId)
                && fixedSlots.CurrencyCosts.Count == 1
                && fixedSlots.ReceivedItems.Count == 2,
            "fully empty fixed offer slots must be ignored while active partial slots remain strict");

        var zeroCost = entry with
        {
            CurrencyCostVector = [new VendorCurrencyCost(0u, 0u, "Unknown currency", VendorCurrencyGroup.Other)],
        };
        Require(!VendorOfferMath.HasValidCurrencyCosts(zeroCost.CurrencyCosts),
            "zero currency components must make an offer unresolved instead of fabricating a free purchase");
        Require(!VendorOfferMath.HasValidCurrencyCosts(
                    [
                        new VendorCurrencyCost(100u, 4u, "First currency", VendorCurrencyGroup.Other),
                        new VendorCurrencyCost(0u, 3u, "Unresolved currency", VendorCurrencyGroup.Other),
                    ])
                && !VendorOfferMath.HasValidReceivedItems(
                    [new VendorReceivedItem(500u, 2u), new VendorReceivedItem(0u, 1u)],
                    500u),
            "partial currency/output slots must reject the whole offer");
        Require(!VendorOfferMath.HasValidCurrencyCosts(Array.Empty<VendorCurrencyCost>())
                && !VendorOfferMath.HasValidReceivedItems(Array.Empty<VendorReceivedItem>(), entry.ItemId),
            "an offer containing only empty fixed slots must remain unresolved");
        Require(VendorShopResolver.TryNormalizeSpecialShopReceivedItems(
                    [(12345u, 1u), (0u, 1u)], out var paddedSpecialShopOutputs)
                && paddedSpecialShopOutputs.SequenceEqual([new VendorReceivedItem(12345u, 1u)]),
            "SpecialShop receive slots with no item must be ignored even when sheet padding supplies a default count");
        Require(!VendorShopResolver.TryNormalizeSpecialShopReceivedItems(
                    [(12345u, 0u), (0u, 1u)], out _),
            "an active SpecialShop receive slot with zero quantity must remain invalid");
        RequireThrows<ArgumentException>(
            () => _ = VendorOfferMath.GetCurrencyTotals(
                [new VendorCurrencyCost(0u, 0u, "Unknown currency", VendorCurrencyGroup.Other)], 1u),
            "currency total calculation must reject unresolved costs instead of silently undercounting");
        var unresolvedLegacyEntry = new VendorBuyListEntry { ItemId = entry.ItemId };
        Require(unresolvedLegacyEntry.EffectiveCurrencyCosts.Count == 0
                && !VendorOfferMath.HasValidCurrencyCosts(unresolvedLegacyEntry.EffectiveCurrencyCosts),
            "legacy entries with no scalar currency must not hydrate a fabricated zero-cost component");

        var repeatedOutputs = new VendorShopEntry(
            500u,
            "Repeated output",
            1,
            4u,
            100u,
            "First currency",
            new List<VendorNpc>(),
            VendorShopType.SpecialCurrency,
            VendorCurrencyGroup.Other,
            ReceivedOutputs:
            [
                new VendorReceivedItem(500u, 2u),
                new VendorReceivedItem(600u, 1u),
                new VendorReceivedItem(500u, 3u),
            ]);
        Require(repeatedOutputs.ReceivedQuantity == 5u
                && repeatedOutputs.PurchaseCountFor(11u) == 3u,
            "repeated rows for the requested output must sum before purchase-count rounding");
        Require(!string.Equals(entry.OfferSignature, repeatedOutputs.OfferSignature, StringComparison.Ordinal),
            "offer identity must include the complete output vector");

        var overflowOutputs = new VendorShopEntry(
            500u,
            "Overflow output",
            1,
            4u,
            100u,
            "First currency",
            new List<VendorNpc>(),
            VendorShopType.SpecialCurrency,
            VendorCurrencyGroup.Other,
            ReceivedOutputs:
            [
                new VendorReceivedItem(500u, uint.MaxValue),
                new VendorReceivedItem(500u, 1u),
            ]);
        RequireThrows<OverflowException>(
            () => _ = overflowOutputs.ReceivedQuantity,
            "received-output quantity overflow must fail closed instead of saturating");

        var zeroOutput = new VendorShopEntry(
            500u,
            "Zero output",
            1,
            4u,
            100u,
            "First currency",
            new List<VendorNpc>(),
            VendorShopType.SpecialCurrency,
            VendorCurrencyGroup.Other,
            ReceivedOutputs: [new VendorReceivedItem(500u, 0u)]);
        Require(zeroOutput.ReceivedQuantity == 0u,
            "zero receive counts must remain an unknown offer quantity");
        Require(!VendorOfferMath.HasValidReceivedItems(zeroOutput.ReceivedItems, zeroOutput.ItemId),
            "zero receive counts must make direct purchase unsupported");
        RequireThrows<ArgumentOutOfRangeException>(
            () => _ = zeroOutput.PurchaseCountFor(1u),
            "zero receive counts must not fabricate one output per purchase");

        var legacyEntry = new VendorBuyListEntry
        {
            ItemId = entry.ItemId,
            Cost = entry.Cost,
            CurrencyItemId = entry.CurrencyItemId,
        };
        Require(legacyEntry.EffectiveCurrencyCosts.Count == 1
                && legacyEntry.EffectiveCurrencyCosts[0].CurrencyItemId == entry.CurrencyItemId
                && legacyEntry.EffectiveReceivedItems.Count == 1
                && legacyEntry.EffectiveReceivedItems[0].Quantity == 1u
                && VendorOfferMath.MatchesPersistedOffer(legacyEntry, entry),
            "legacy vendor-list entries must hydrate scalar cost/output fields while matching the live offer");

        var mismatchedOffer = new VendorBuyListEntry
        {
            CurrencyCosts = [new VendorCurrencyCost(100u, 99u, "First currency", VendorCurrencyGroup.Other)],
            ReceivedItems = outputs.ToList(),
        };
        Require(!VendorOfferMath.MatchesPersistedOffer(mismatchedOffer, entry),
            "persisted offer vectors must reject a changed live transaction");

        for (byte gameGrandCompanyId = 0; gameGrandCompanyId < 3; gameGrandCompanyId++)
        {
            var sealCurrencyItemId = VendorShopResolver.GetSealCurrencyItemIdForGameGrandCompany(gameGrandCompanyId);
            Require(VendorShopResolver.TryGetGameGrandCompanyIdFromSealCurrencyItemId(
                        sealCurrencyItemId,
                        out var roundTrippedGrandCompanyId)
                    && roundTrippedGrandCompanyId == gameGrandCompanyId,
                "game Grand Company IDs and seal currencies must be bijective");
        }
        Require(!VendorShopResolver.TryGetGameGrandCompanyIdFromSealCurrencyItemId(999999u, out _)
                && VendorShopResolver.GetSealCurrencyItemIdForSheetGrandCompany(1u) == 20u
                && VendorShopResolver.GetSealCurrencyItemIdForSheetGrandCompany(3u) == 22u,
            "unknown seal currencies must not resolve and Lumina sheet IDs must remain one-based");

        var grandCompanyOffer = new VendorShopEntry(
            5501u,
            "Potash",
            1,
            200u,
            VendorShopResolver.GetSealCurrencyItemIdForGameGrandCompany(0),
            "Maelstrom Seals",
            new List<VendorNpc>(),
            VendorShopType.GrandCompanySeals,
            VendorCurrencyGroup.GrandCompanySeals);
        Require(VendorShopResolver.MatchesGrandCompany(grandCompanyOffer, 0)
                && !VendorShopResolver.MatchesGrandCompany(grandCompanyOffer, 1)
                && !VendorShopResolver.MatchesGrandCompany(grandCompanyOffer, 2),
            "Grand Company vendor offers must select the quartermaster matching the character's seal currency");

        // Production fixtures: Kojin shop 1769818 uses currency 21081 and
        // society 9; Ananta shop 1769847 uses currency 21935 and society 10.
        var alliedSocietyCurrencyMap = VendorShopResolver.BuildUniqueAlliedSocietyCurrencyMap(
        [
            (9u, 21081u),
            (10u, 21935u),
        ]);
        Require(alliedSocietyCurrencyMap[21081u] == 9u
                && alliedSocietyCurrencyMap[21935u] == 10u
                && VendorShopResolver.ResolveAlliedSocietyForCurrencyCosts([21081u, 999999u], alliedSocietyCurrencyMap) == 0u
                && VendorShopResolver.ResolveAlliedSocietyForCurrencyCosts([21081u, 21935u], alliedSocietyCurrencyMap) == 0u,
            "any unmapped or conflicting active currency must leave allied-society resolution unknown");
        var ambiguousAlliedSocietyCurrencyMap = VendorShopResolver.BuildUniqueAlliedSocietyCurrencyMap(
        [
            (9u, 21081u),
            (10u, 21081u),
        ]);
        Require(!ambiguousAlliedSocietyCurrencyMap.ContainsKey(21081u),
            "a currency shared by multiple allied societies must remain unresolved");

        var locked = new StubAvailabilityQueries
        {
            QuestResult = new VendorAvailabilityCheck(true, false, "Unlock vendor"),
        };
        var lockedResult = VendorAvailabilityResolver.Resolve(
            entry with { RequiredQuestIds = [42u] },
            new VendorNpc(1u, "Vendor", 2u),
            locked);
        Require(lockedResult.State == VendorAvailabilityState.Locked
                && lockedResult.Reason.Contains("Unlock vendor", StringComparison.Ordinal),
            "locked vendor requirements must be rejected with their user-facing reason");

        var unknown = new StubAvailabilityQueries
        {
            QuestResult = new VendorAvailabilityCheck(false, false, "quest state unavailable"),
        };
        var unknownResult = VendorAvailabilityResolver.Resolve(
            entry with { RequiredQuestIds = [42u] },
            new VendorNpc(1u, "Vendor", 2u),
            unknown);
        Require(unknownResult.State == VendorAvailabilityState.Unknown,
            "unknown vendor requirements must not be treated as available");

        var alliedLocked = new StubAvailabilityQueries
        {
            AlliedResult = new VendorAvailabilityCheck(true, false, "Sahagin rank 7"),
        };
        var alliedResult = VendorAvailabilityResolver.Resolve(
            entry,
            new VendorNpc(1u, "Vendor", 2u, RequiredAlliedSocietyId: 9u, RequiredAlliedSocietyRank: 7u),
            alliedLocked);
        Require(alliedResult.State == VendorAvailabilityState.Locked
                && alliedResult.Reason.Contains("Sahagin rank 7", StringComparison.Ordinal),
            "allied-society rank requirements must reject under-ranked characters");

        var knownKojin = entry with
        {
            RequiredQuestIds = [68510u, 68700u],
            RequiredAlliedSocietyId = 9u,
            RequiredAlliedSocietyRank = 0u,
        };
        var ranklessAlliedQueries = new StubAvailabilityQueries();
        var knownKojinResult = VendorAvailabilityResolver.Resolve(
            knownKojin,
            new VendorNpc(1u, "Kojin vendor", 1769818u, VendorMenuShopType.InclusionShop,
                AlliedRequirementKnown: false),
            ranklessAlliedQueries);
        Require(knownKojinResult.State == VendorAvailabilityState.Available,
            "a known allied-society route with an authoritative shop quest may proceed when no separate rank gate exists");

        var knownAnanta = entry with
        {
            RequiredQuestIds = [68572u],
            RequiredAlliedSocietyId = 10u,
            RequiredAlliedSocietyRank = 0u,
        };
        var knownAnantaResult = VendorAvailabilityResolver.Resolve(
            knownAnanta,
            new VendorNpc(1u, "Ananta vendor", 1769847u, VendorMenuShopType.InclusionShop,
                AlliedRequirementKnown: false),
            new StubAvailabilityQueries());
        Require(knownAnantaResult.State == VendorAvailabilityState.Available,
            "Ananta routes with an authoritative shop quest may proceed when no separate rank gate exists");

        var ranklessWithoutQuestResult = VendorAvailabilityResolver.Resolve(
            entry with { RequiredAlliedSocietyId = 9u, RequiredAlliedSocietyRank = 0u },
            new VendorNpc(1u, "Unresolved Kojin vendor", 1769818u, VendorMenuShopType.InclusionShop,
                AlliedRequirementKnown: true),
            ranklessAlliedQueries);
        Require(ranklessWithoutQuestResult.State == VendorAvailabilityState.Unknown,
            "a known allied-society ID with neither an authoritative rank nor quest gate must remain unknown");

        var knownUnlockedAlliedResult = VendorAvailabilityResolver.Resolve(
            entry,
            new VendorNpc(1u, "Known Kojin vendor", 1769818u, VendorMenuShopType.InclusionShop,
                RequiredAlliedSocietyId: 9u, RequiredAlliedSocietyRank: 7u, AlliedRequirementKnown: true),
            new StubAvailabilityQueries
            {
                AlliedResult = new VendorAvailabilityCheck(true, true, "Kojin rank 7"),
            });
        Require(knownUnlockedAlliedResult.State == VendorAvailabilityState.Available,
            "known allied-society routes with an authoritative rank and satisfied query must remain available");

        var unknownAlliedRouteResult = VendorAvailabilityResolver.Resolve(
            entry,
            new VendorNpc(1u, "Tribal Vendor", 2u, VendorMenuShopType.InclusionShop,
                AlliedRequirementKnown: false),
            new StubAvailabilityQueries());
        Require(unknownAlliedRouteResult.State == VendorAvailabilityState.Unknown,
            "inclusion-shop routes without authoritative allied-society metadata must remain unknown");

        var scripExchangeResult = VendorAvailabilityResolver.Resolve(
            entry with { Group = VendorCurrencyGroup.Scrips },
            new VendorNpc(1u, "Scrip Exchange", 2u, VendorMenuShopType.InclusionShop,
                AlliedRequirementKnown: false),
            new StubAvailabilityQueries());
        Require(scripExchangeResult.State == VendorAvailabilityState.Available,
            "generic scrip exchanges must remain available when their inclusion route has no allied-society gate");

        var contentUnknown = new StubAvailabilityQueries
        {
            ContentResult = new VendorAvailabilityCheck(false, false, "content state unavailable"),
        };
        var contentResult = VendorAvailabilityResolver.Resolve(
            entry with { RequiredContentId = 777u },
            new VendorNpc(1u, "Vendor", 2u),
            contentUnknown);
        Require(contentResult.State == VendorAvailabilityState.Unknown
                && contentResult.Reason.Contains("content state unavailable", StringComparison.Ordinal),
            "unknown content requirements must remain unknown");

        var contentAvailableResult = VendorAvailabilityResolver.Resolve(
            entry with { RequiredContentId = 777u },
            new VendorNpc(1u, "Vendor", 2u),
            new StubAvailabilityQueries
            {
                ContentResult = new VendorAvailabilityCheck(true, true, "content unlocked"),
            });
        Require(contentAvailableResult.State == VendorAvailabilityState.Available,
            "known unlocked content access must permit the vendor route");

        var grandCompanyLocked = new StubAvailabilityQueries
        {
            GrandCompanyResult = new VendorAvailabilityCheck(true, false, "Immortal Flames rank 5"),
        };
        var grandCompanyEntry = entry with
        {
            ShopType = VendorShopType.GrandCompanySeals,
            CurrencyItemId = VendorShopResolver.GetSealCurrencyItemIdForGameGrandCompany(2),
            CurrencyCostVector = null,
        };
        var grandCompanyResult = VendorAvailabilityResolver.Resolve(
            grandCompanyEntry,
            new VendorNpc(1u, "Vendor", 2u, VendorMenuShopType.GrandCompanyShop,
                GcRankIndex: 0, GcCategoryIndex: 0, RequiredGrandCompanyRank: 5u),
            grandCompanyLocked);
        Require(grandCompanyResult.State == VendorAvailabilityState.Locked
                && grandCompanyResult.Reason.Contains("Immortal Flames rank 5", StringComparison.Ordinal),
            "Grand Company gates must preserve the exact availability reason");

        var ranklessGrandCompanyEntry = grandCompanyEntry;
        var wrongGrandCompanyResult = VendorAvailabilityResolver.Resolve(
            ranklessGrandCompanyEntry,
            new VendorNpc(1u, "Vendor", 2u, VendorMenuShopType.GrandCompanyShop,
                GcRankIndex: 0, GcCategoryIndex: 0),
            new StubAvailabilityQueries
            {
                GrandCompanyResult = new VendorAvailabilityCheck(true, false, "membership in Immortal Flames"),
            });
        Require(wrongGrandCompanyResult.State == VendorAvailabilityState.Locked,
            "rankless Grand Company routes must still reject membership in the wrong company");

        var unknownGrandCompanyResult = VendorAvailabilityResolver.Resolve(
            ranklessGrandCompanyEntry,
            new VendorNpc(1u, "Vendor", 2u, VendorMenuShopType.GrandCompanyShop,
                GcRankIndex: 0, GcCategoryIndex: 0),
            new StubAvailabilityQueries
            {
                GrandCompanyResult = new VendorAvailabilityCheck(false, false, "current Grand Company unavailable"),
            });
        Require(unknownGrandCompanyResult.State == VendorAvailabilityState.Unknown,
            "rankless Grand Company routes must remain unknown when current membership is unavailable");

        var invalidGrandCompanyRouteResult = VendorAvailabilityResolver.Resolve(
            ranklessGrandCompanyEntry,
            new VendorNpc(1u, "Vendor", 2u, VendorMenuShopType.GrandCompanyShop,
                GcRankIndex: -1, GcCategoryIndex: 0),
            new StubAvailabilityQueries());
        Require(invalidGrandCompanyRouteResult.State == VendorAvailabilityState.Locked
                && invalidGrandCompanyRouteResult.Reason.Contains("route", StringComparison.OrdinalIgnoreCase),
            "Grand Company availability must reject malformed rank/category routes before purchase");

        var lockedImport = new StubAvailabilityQueries
        {
            QuestResult = new VendorAvailabilityCheck(true, false, "Unlock vendor"),
        };
        var importEntry = entry with { RequiredQuestIds = [42u] };
        var importVendors = new[] { new VendorNpc(1u, "Vendor", 2u) };
        Require(!VendorAvailabilityResolver.TrySelectAvailableVendor(
                    importEntry,
                    importVendors,
                    lockedImport,
                    out var selectedVendor,
                    out var importFailure)
                && selectedVendor == null
                && importFailure.State == VendorAvailabilityState.Locked
                && importFailure.Reason.Contains("Unlock vendor", StringComparison.Ordinal),
            "vendor import selection must exclude locked routes and retain the gate reason");

        var availableImport = new StubAvailabilityQueries();
        Require(VendorAvailabilityResolver.TrySelectAvailableVendor(
                    entry,
                    importVendors,
                    availableImport,
                    out selectedVendor,
                    out _)
                && selectedVendor?.NpcId == 1u,
            "vendor import selection must choose an available route");

        return assertions;
    }

    private sealed class StubAvailabilityQueries : IVendorAvailabilityQueries
    {
        public VendorAvailabilityCheck QuestResult { get; init; } = new(true, true, string.Empty);
        public VendorAvailabilityCheck ContentResult { get; init; } = new(true, true, string.Empty);
        public VendorAvailabilityCheck GrandCompanyResult { get; init; } = new(true, true, string.Empty);
        public VendorAvailabilityCheck AlliedResult { get; init; } = new(true, true, string.Empty);

        public VendorAvailabilityCheck Quest(uint questId)
            => QuestResult;

        public VendorAvailabilityCheck Achievement(uint achievementId)
            => new(true, true, string.Empty);

        public VendorAvailabilityCheck Content(uint contentId, bool mustBeComplete)
            => ContentResult;

        public VendorAvailabilityCheck GrandCompany(uint companyId, uint requiredRank)
            => GrandCompanyResult;

        public VendorAvailabilityCheck AlliedSociety(uint societyId, uint requiredRank)
            => AlliedResult;
    }

    private static void RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
