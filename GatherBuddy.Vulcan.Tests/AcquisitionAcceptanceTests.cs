using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GatherBuddy.AutoGather;
using GatherBuddy.Crafting;
using GatherBuddy.Crafting.Acquisition;
using GatherBuddy.Gui;
using GatherBuddy.Vulcan.Vendors;
using LuminaSupplemental.Excel.Model;
using GatheringType = global::GatherBuddy.Enums.GatheringType;

namespace GatherBuddy.Vulcan.Tests;

public static class AcquisitionAcceptanceTests
{
    public static void Run(Action<bool, string> require)
    {
        ManualGatherAssistSettingDefaultsOn(require);
        ReductionSourcesComeFromSupplementalRelations(require);
        FinalOutputsAreNeverPurchased(require);
        FinalOutputAndIntermediateDemandAreDistinct(require);
        DisabledAcquisitionIsAnExplicitNoOp(require);
        SelectedUsablePathRemainsAuthoritative(require);
        SelectedRecipeIdentitySurvivesFallback(require);
        UnavailablePathIsAStartBlocker(require);
        MultiJobGatherPathSelectsUsableConcreteJob(require);
        MarketFallbackSurvivesUnavailableVendor(require);
        CapabilityEvidenceDistinguishesKnownAndUnknown(require);
        UnknownPathKindIsExplicitlyUnknown(require);
        UnknownBalancesAndWorldsBlockSafely(require);
        NonzeroGilIdsAreCanonicalized(require);
        GrandCompanyVendorUsesAuthoritativeSealBalance(require);
        EquivalentVendorOfferPrefersCurrentTerritory(require);
        InvalidVendorCurrencyVectorsAreRejected(require);
        MarketStacksRemainAtomic(require);
        GlobalBudgetReservesCurrencyAcrossItems(require);
        CurrencyVectorsAreRequiredTogether(require);
        SpecialCurrencyMarketReasonUsesPreferenceMetadata(require);
        InsufficientCurrencyMarketReasonIncludesCostAndIcon(require);
        PreferenceRelaxationStaysWithinHardBudget(require);
        SameSourceDifferentItemVendorTransactionsRemainDistinct(require);
        CoProductVendorTransactionIsSharedAcrossDependencies(require);
        ParetoFrontierAvoidsCartesianGlobalExplosion(require);
        FeasibleVendorSurvivesImpossibleMarketplaceBranch(require);
        ExactSearchReportsItsDeterministicLimit(require);
        AcquisitionSettingsRoundTrip(require);
    }

    private static void ReductionSourcesComeFromSupplementalRelations(Action<bool, string> require)
    {
        var index = AetherialReductionSourceResolver.BuildIndex(
        [
            new ItemSupplement(900u, 901u, ItemSupplementSource.Reduction),
            new ItemSupplement(900u, 902u, ItemSupplementSource.Reduction),
            new ItemSupplement(900u, 901u, ItemSupplementSource.Reduction),
            new ItemSupplement(900u, 903u, ItemSupplementSource.Desynth),
            new ItemSupplement(910u, 911u, ItemSupplementSource.Reduction),
        ]);
        require(index.TryGetValue(900u, out var sources) && sources.SequenceEqual([901u, 902u]),
            "reduction source indexing must retain every distinct reduction candidate and exclude other transforms");

        require(AetherialReductionSourceResolver.GetSourceItemIds(46246u)
                .SequenceEqual([46247u, 46248u, 46249u]),
            "the packaged supplemental data must expose all Levinchrome Aethersand reduction candidates");
        require(AetherialReductionSourceResolver.GetOutputItemIds().Contains(46246u),
            "Levinchrome Aethersand must be enumerable as an Aetherial Reduction output for manual auto-gather selection");
        require(AetherialReductionSourceResolver.IsSourceForOutput(46246u, 46247u),
            "Levinchrome Quartz must validate as a source for the selected Aethersand output");
        require(!AetherialReductionSourceResolver.IsSourceForOutput(46246u, 46250u),
            "an unrelated gatherable must not validate as the source for a selected reduction output");

        var usableReduction = new AcquisitionPath
        {
            Kind = AcquisitionPathKind.Reduction,
            SourceItemId = 46247u,
            JobId = 16u,
            Capability = AcquisitionCapability.UsablePath(AcquisitionPathKind.Reduction),
        };
        require(AetherialReductionSourceResolver.TryCreatePresentation(
                    46246u,
                    21,
                    _ => usableReduction,
                    out var presentation)
                && presentation.SourceItemId == 46247u
                && presentation.OutputItemId == 46246u
                && presentation.OutputNeeded == 21
                && presentation.SourceClassJobId == 16u
                && presentation.SourceQuantityUnknown,
            "eligible reduction presentation must resolve demanded output to source while preserving exact output demand and unknown source quantity");

        var syntheticPaths = new Dictionary<uint, AcquisitionPath>
        {
            [900u] = new()
            {
                Kind = AcquisitionPathKind.Reduction,
                SourceItemId = 902u,
                JobId = 16u,
                Capability = AcquisitionCapability.UsablePath(AcquisitionPathKind.Reduction),
            },
            [910u] = new()
            {
                Kind = AcquisitionPathKind.Reduction,
                SourceItemId = 911u,
                JobId = 17u,
                Capability = AcquisitionCapability.UsablePath(AcquisitionPathKind.Reduction),
            },
        };
        require(AetherialReductionSourceResolver.TryCreatePresentation(
                    900u,
                    7,
                    index,
                    outputItemId => syntheticPaths.GetValueOrDefault(outputItemId),
                    out var minerPresentation)
                && minerPresentation.SourceItemId == 902u
                && minerPresentation.SourceClassJobId == 16u,
            "reduction presentation must discover a selected miner source from generic supplemental relations");
        require(AetherialReductionSourceResolver.TryCreatePresentation(
                    910u,
                    5,
                    index,
                    outputItemId => syntheticPaths.GetValueOrDefault(outputItemId),
                    out var botanistPresentation)
                && botanistPresentation.SourceItemId == 911u
                && botanistPresentation.SourceClassJobId == 17u,
            "reduction presentation must preserve the gathering job resolved for a different output and source");
        require(!AetherialReductionSourceResolver.TryCreatePresentation(
                    46247u,
                    21,
                    _ => usableReduction,
                    out _),
            "reduction presentation must not infer output demand by resolving forward from a source item");
        var unknownReduction = new AcquisitionPath
        {
            Kind = AcquisitionPathKind.Reduction,
            SourceItemId = 46247u,
            JobId = 16u,
            Capability = new AcquisitionCapability
            {
                Status = AcquisitionCapabilityStatus.Unknown,
                PathKind = AcquisitionPathKind.Reduction,
            },
        };
        require(!AetherialReductionSourceResolver.TryCreatePresentation(
                    46246u,
                    21,
                    _ => unknownReduction,
                    out _),
            "unknown reduction eligibility must retain the ordinary material source presentation");
    }

    private static void ManualGatherAssistSettingDefaultsOn(Action<bool, string> require)
    {
        var config = new AutoGatherConfig();
        require(ManualGatherAssistPolicy.IsEnabled(config),
            "manual gathering assistance for normal items and collectables must default on");

        config.AssistManualGathering = false;
        require(!ManualGatherAssistPolicy.IsEnabled(config),
            "one manual gathering setting must control both normal items and collectables");

        config.AssistManualGathering = true;
        config.DoGathering = false;
        require(!ManualGatherAssistPolicy.IsEnabled(config),
            "the global gathering-window interaction setting must disable manual assistance");

        require(!ManualGatherAssistPolicy.ShouldAutoGatherOwnSession(0, [], 100, [200]),
            "AutoGather must not claim a manually opened node when no active target exists");
        require(!ManualGatherAssistPolicy.ShouldAutoGatherOwnSession(200, [300], 100, [201]),
            "AutoGather must not claim a manually opened node that does not contain its active target");
        require(ManualGatherAssistPolicy.ShouldAutoGatherOwnSession(200, [100], 100, [201]),
            "matching gathering-point identity must authorize AutoGather ownership");
        require(ManualGatherAssistPolicy.ShouldAutoGatherOwnSession(200, [300], 100, [200]),
            "a visible active-target item must authorize AutoGather ownership when node identity is unavailable");
    }

    private static void FinalOutputsAreNeverPurchased(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[]
                {
                    Blocked(100, 1, "Final output", isFinalOutput: true),
                    Blocked(101, 1, "Missing precraft"),
                },
                MarketListings = new[]
                {
                    Listing(100, 1000, 1),
                    Listing(101, 1001, 1),
                },
                GilBalance = 10_000,
            },
            Enabled());

        require(result.IsSuccess, "final-output filtering should not block an otherwise purchasable dependency");
        require(result.SkippedFinalOutputItemIds.SequenceEqual(new[] { 100u }),
            "final output must be reported as skipped");
        require(result.SelectedPlan?.Transactions.Count == 1
            && result.SelectedPlan.Transactions[0].ItemId == 101,
            "only the missing dependency may be purchased");
    }

    private static void FinalOutputAndIntermediateDemandAreDistinct(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[]
                {
                    Blocked(150, 1, "Direct final output", isFinalOutput: true),
                    new AcquisitionDependency
                    {
                        ItemId = 150,
                        ItemName = "Intermediate use of final output ID",
                        RequiredQuantity = 2,
                        IsIntermediateDemand = true,
                        SelectedPath = new AcquisitionPath
                        {
                            Kind = AcquisitionPathKind.Craft,
                            Capability = AcquisitionCapability.UnusablePath(
                                AcquisitionPathKind.Craft,
                                "The selected intermediate path is unavailable."),
                        },
                    },
                },
                MarketListings = new[] { Listing(150, 1500, 2) },
                GilBalance = 10_000,
            },
            Enabled());

        require(result.IsSuccess
                && result.SkippedFinalOutputItemIds.SequenceEqual(new[] { 150u })
                && result.SelectedPlan?.Transactions.Count == 1
                && result.SelectedPlan.Transactions[0].ItemId == 150
                && result.SelectedPlan.Transactions[0].Quantity == 2,
            "an item ID used by both a direct final output and an intermediate demand must skip only the final demand and acquire the intermediate quantity");
    }

    private static void SelectedUsablePathRemainsAuthoritative(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[]
                {
                    new AcquisitionDependency
                    {
                        ItemId = 200,
                        RequiredQuantity = 1,
                        SelectedPath = new AcquisitionPath
                        {
                            RecipeId = 777,
                            JobId = 13,
                            Kind = AcquisitionPathKind.Craft,
                            Capability = AcquisitionCapability.UsablePath(AcquisitionPathKind.Craft),
                        },
                    },
                },
                MarketListings = new[] { Listing(200, 2000, 1) },
                GilBalance = 10_000,
            },
            Enabled());

        require(result.Status == AcquisitionPlanStatus.NoBlockedDependencies,
            "a usable selected craft path must not become a purchase target");
        require(result.SelectedPlan?.Transactions.Count == 0,
            "planner must not replace a usable selected multi-recipe path");
    }

    private static void DisabledAcquisitionIsAnExplicitNoOp(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(225, 1) },
                MarketListings = new[] { Listing(225, 2250, 1) },
                GilBalance = 10_000,
            },
            new AcquisitionPlanningSettings());

        require(result.Status == AcquisitionPlanStatus.NoBlockedDependencies
            && result.SelectedPlan?.Transactions.Count == 0,
            "disabled automatic acquisition must be a no-op with no purchase transactions");
    }

    private static void UnavailablePathIsAStartBlocker(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(300, 1, "Gather-only material") },
                VendorOffers = Array.Empty<AcquisitionVendorOffer>(),
                MarketListings = Array.Empty<AcquisitionMarketListing>(),
            },
            Enabled());

        require(result.Status == AcquisitionPlanStatus.Blocked,
            "an unusable path without a source must block the craft before execution");
        require(result.Blockers.Any(blocker => blocker.Kind == AcquisitionBlockerKind.CapabilityUnavailable),
            "missing source plus an unusable path must expose the capability blocker");
    }

    private static void MultiJobGatherPathSelectsUsableConcreteJob(Action<bool, string> require)
    {
        var jobIds = AcquisitionPlanningInputBuilder.ResolveGatheringJobIds(
            [GatheringType.Mining, GatheringType.Logging],
            GatheringType.Multiple);
        var selected = AcquisitionPlanningInputBuilder.SelectBestGatherPath(
        [
            new AcquisitionPath
            {
                JobId = 16,
                Kind = AcquisitionPathKind.Gather,
                Capability = new AcquisitionCapability
                {
                    JobId = 16,
                    Status = AcquisitionCapabilityStatus.Unusable,
                    ActualLevel = 0,
                },
            },
            new AcquisitionPath
            {
                JobId = 17,
                Kind = AcquisitionPathKind.Gather,
                Capability = new AcquisitionCapability
                {
                    JobId = 17,
                    Status = AcquisitionCapabilityStatus.Usable,
                    ActualLevel = 90,
                },
            },
        ]);

        require(jobIds.SequenceEqual([16u, 17u])
            && selected.JobId == 17
            && selected.Capability.Status == AcquisitionCapabilityStatus.Usable,
            "multi-job gatherables must select the usable concrete gathering job instead of job zero or an unavailable alternative");
    }

    private static void MarketFallbackSurvivesUnavailableVendor(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(325, 1, "Marketable material") },
                VendorOffers = new[]
                {
                    new AcquisitionVendorOffer
                    {
                        ItemId = 325,
                        OfferId = "unsupported-shop",
                        ReceiveQuantity = 1,
                        IsAvailable = false,
                        UnavailableReason = "Vendor automation does not support this offer.",
                    },
                },
                MarketListings = new[] { Listing(325, 3250, 1) },
                GilBalance = 10_000,
            },
            Enabled());

        require(result.Status == AcquisitionPlanStatus.Ready
            && result.SelectedPlan?.Transactions.SingleOrDefault()?.SourceKind == AcquisitionSourceKind.Market,
            "an unavailable vendor route must not block a valid market-board fallback");
    }

    private static void GrandCompanyVendorUsesAuthoritativeSealBalance(Action<bool, string> require)
    {
        const uint potash = 5501;
        const uint maelstromSeals = 20;
        var vendor = new AcquisitionVendorOffer
        {
            ItemId = potash,
            OfferId = "maelstrom-quartermaster-potash",
            ReceiveQuantity = 1,
            Costs = new[]
            {
                new AcquisitionCurrencyCost
                {
                    CurrencyId = maelstromSeals,
                    CurrencyName = "Storm Seals",
                    Amount = 200,
                    IsSpecialCurrency = true,
                    Group = VendorCurrencyGroup.GrandCompanySeals,
                },
            },
        };

        var affordable = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(potash, 1, "Potash") },
                VendorOffers = new[] { vendor },
                MarketListings = new[] { Listing(potash, 5501, 1, price: 400) },
                CurrencyBalances = new Dictionary<uint, long> { [maelstromSeals] = 200 },
                GilBalance = 10_000,
            },
            new AcquisitionPlanningSettings
            {
                AutoPurchaseBlockedDependencies = true,
                PreferMarketForSpecialCurrency = false,
                PreferVendors = true,
            });
        require(affordable.SelectedPlan?.Transactions.SingleOrDefault()?.SourceKind == AcquisitionSourceKind.Vendor,
            "an eligible current-GC quartermaster offer must beat the market when its seal wallet covers the cost");

        var insufficient = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(potash, 1, "Potash") },
                VendorOffers = new[] { vendor },
                MarketListings = new[] { Listing(potash, 5501, 1, price: 400) },
                CurrencyBalances = new Dictionary<uint, long> { [maelstromSeals] = 199 },
                GilBalance = 10_000,
            },
            new AcquisitionPlanningSettings
            {
                AutoPurchaseBlockedDependencies = true,
                PreferMarketForSpecialCurrency = false,
                PreferVendors = true,
            });
        require(insufficient.SelectedPlan?.Transactions.SingleOrDefault()?.SourceKind == AcquisitionSourceKind.Market,
            "an ineligible quartermaster offer must fall back to the market when the seal wallet is short");
    }

    private static void EquivalentVendorOfferPrefersCurrentTerritory(Action<bool, string> require)
    {
        const uint itemId = 5502;
        const uint purpleScrips = 33913;
        AcquisitionVendorOffer Offer(string id, uint territoryId)
            => new()
            {
                ItemId = itemId,
                OfferId = id,
                VendorName = "Scrip Exchange",
                VendorTerritoryId = territoryId,
                ReceiveQuantity = 1,
                Costs = new[]
                {
                    new AcquisitionCurrencyCost
                    {
                        CurrencyId = purpleScrips,
                        CurrencyName = "Purple Crafters' Scrip",
                        Amount = 100,
                        IsSpecialCurrency = true,
                        Group = VendorCurrencyGroup.Other,
                    },
                },
            };

        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(itemId, 1, "Scrip material") },
                VendorOffers = new[]
                {
                    Offer("a-remote-ul-dah", 130),
                    Offer("z-local-tuliyollal", 1185),
                },
                CurrencyBalances = new Dictionary<uint, long> { [purpleScrips] = 100 },
                CurrentTerritoryId = 1185,
            },
            new AcquisitionPlanningSettings
            {
                AutoPurchaseBlockedDependencies = true,
                PreferMarketForSpecialCurrency = false,
                PreferVendors = true,
            });

        require(result.SelectedPlan?.Transactions.SingleOrDefault()?.VendorTerritoryId == 1185,
            "equivalent vendor offers must prefer the current-territory route over lexical source ordering");
    }

    private static void SelectedRecipeIdentitySurvivesFallback(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[]
                {
                    new AcquisitionDependency
                    {
                        ItemId = 250,
                        RequiredQuantity = 1,
                        SelectedPath = new AcquisitionPath
                        {
                            RecipeId = 8_888,
                            Kind = AcquisitionPathKind.Craft,
                            Capability = AcquisitionCapability.UnusablePath(AcquisitionPathKind.Craft, "Selected job unavailable."),
                        },
                    },
                },
                MarketListings = new[] { Listing(250, 2500, 1) },
                GilBalance = 10_000,
            },
            Enabled());

        require(result.SelectedPlan?.Transactions.SingleOrDefault()?.SelectedRecipeId == 8_888,
            "automatic purchasing must retain the exact selected recipe identity instead of resolving another class");
    }

    private static void CapabilityEvidenceDistinguishesKnownAndUnknown(Action<bool, string> require)
    {
        var missingGearset = AcquisitionCapabilityResolver.Resolve(
            AcquisitionPathKind.Craft,
            new AcquisitionCapabilityEvidence
            {
                JobId = 13,
                RequiredLevel = 100,
                ActualLevel = 100,
                GearsetKnown = true,
                GearsetAvailable = false,
                UnlockKnown = true,
                UnlockAvailable = true,
                RouteKnown = true,
                RouteAvailable = true,
            });
        var unknownFolklore = AcquisitionCapabilityResolver.Resolve(
            AcquisitionPathKind.Gather,
            new AcquisitionCapabilityEvidence
            {
                RequiredLevel = 100,
                ActualLevel = 100,
                GearsetKnown = true,
                GearsetAvailable = true,
                UnlockKnown = true,
                UnlockAvailable = true,
                FolkloreRequired = false,
                FolkloreKnown = false,
                RouteKnown = true,
                RouteAvailable = true,
            });

        require(missingGearset.Status == AcquisitionCapabilityStatus.Unusable
            && missingGearset.Reason.Contains("gearset", StringComparison.OrdinalIgnoreCase),
            "known missing gearset must produce an unusable capability with a concrete reason");
        require(unknownFolklore.Status == AcquisitionCapabilityStatus.Usable,
            "non-folklore gathering paths must not require a folklore lookup");

        var unknownGatherUnlock = AcquisitionCapabilityResolver.Resolve(
            AcquisitionPathKind.Gather,
            new AcquisitionCapabilityEvidence
            {
                RequiredLevel = 100,
                ActualLevel = 100,
                GearsetKnown = true,
                GearsetAvailable = true,
                UnlockKnown = false,
                UnlockAvailable = false,
                RouteKnown = true,
                RouteAvailable = true,
            });
        require(unknownGatherUnlock.Status == AcquisitionCapabilityStatus.Unknown
            && unknownGatherUnlock.Reason.Contains("unlock", StringComparison.OrdinalIgnoreCase),
            "unknown gather unlock state must fail closed instead of starting an unverified route");

        var requiredUnknownFolklore = AcquisitionCapabilityResolver.Resolve(
            AcquisitionPathKind.Gather,
            new AcquisitionCapabilityEvidence
            {
                RequiredLevel = 100,
                ActualLevel = 100,
                GearsetKnown = true,
                GearsetAvailable = true,
                UnlockKnown = true,
                UnlockAvailable = true,
                FolkloreRequired = true,
                FolkloreKnown = false,
                RouteKnown = true,
                RouteAvailable = true,
            });
        require(requiredUnknownFolklore.Status == AcquisitionCapabilityStatus.Unknown
            && requiredUnknownFolklore.Reason.Contains("folklore", StringComparison.OrdinalIgnoreCase),
            "unknown required folklore state must remain unknown instead of being guessed usable");

        var unknownPerception = AcquisitionCapabilityResolver.Resolve(
            AcquisitionPathKind.Gather,
            new AcquisitionCapabilityEvidence
            {
                RequiredLevel = 100,
                ActualLevel = 100,
                GearsetKnown = true,
                GearsetAvailable = true,
                UnlockKnown = true,
                UnlockAvailable = true,
                RequiredPerception = 4900,
                PerceptionKnown = false,
                RouteKnown = true,
                RouteAvailable = true,
            });
        require(unknownPerception.Status == AcquisitionCapabilityStatus.Unknown
            && unknownPerception.Reason.Contains("perception", StringComparison.OrdinalIgnoreCase),
            "unknown saved-gearset perception must fail closed");

        var insufficientPerception = AcquisitionCapabilityResolver.Resolve(
            AcquisitionPathKind.Gather,
            new AcquisitionCapabilityEvidence
            {
                RequiredLevel = 100,
                ActualLevel = 100,
                GearsetKnown = true,
                GearsetAvailable = true,
                UnlockKnown = true,
                UnlockAvailable = true,
                RequiredPerception = 4900,
                ActualPerception = 4800,
                PerceptionKnown = true,
                RouteKnown = true,
                RouteAvailable = true,
            });
        require(insufficientPerception.Status == AcquisitionCapabilityStatus.Unusable
            && insufficientPerception.Reason.Contains("4900", StringComparison.Ordinal)
            && insufficientPerception.Reason.Contains("4800", StringComparison.Ordinal),
            "insufficient saved-gearset perception must reject gathering with required and actual values");

        var sufficientPerception = AcquisitionCapabilityResolver.Resolve(
            AcquisitionPathKind.Gather,
            new AcquisitionCapabilityEvidence
            {
                RequiredLevel = 100,
                ActualLevel = 100,
                GearsetKnown = true,
                GearsetAvailable = true,
                UnlockKnown = true,
                UnlockAvailable = true,
                RequiredPerception = 4900,
                ActualPerception = 4900,
                PerceptionKnown = true,
                RouteKnown = true,
                RouteAvailable = true,
            });
        require(sufficientPerception.Status == AcquisitionCapabilityStatus.Usable,
            "meeting the exact perception requirement must preserve gathering as usable");
    }

    private static void UnknownPathKindIsExplicitlyUnknown(Action<bool, string> require)
    {
        var capability = AcquisitionCapabilityResolver.Resolve(
            AcquisitionPathKind.Unknown,
            new AcquisitionCapabilityEvidence
            {
                RequiredLevel = 1,
                ActualLevel = 100,
                GearsetKnown = true,
                GearsetAvailable = true,
                UnlockKnown = true,
                UnlockAvailable = true,
                RouteKnown = true,
                RouteAvailable = true,
            });

        require(capability.Status == AcquisitionCapabilityStatus.Unknown
            && capability.PathKind == AcquisitionPathKind.Unknown
            && capability.Reason.Contains("path kind", StringComparison.OrdinalIgnoreCase),
            "unknown acquisition path kind must remain explicitly unknown and unusable");
    }

    private static void UnknownBalancesAndWorldsBlockSafely(Action<bool, string> require)
    {
        var unknownCurrency = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(350, 1) },
                VendorOffers = new[]
                {
                    new AcquisitionVendorOffer
                    {
                        ItemId = 350,
                        OfferId = "unknown-currency",
                        ReceiveQuantity = 1,
                        Costs = new[] { Currency(9350, 1, true) },
                    },
                },
            },
            Enabled());
        var unknownWorld = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(351, 1) },
                MarketListings = new[] { Listing(351, 3510, 1) },
            },
            new AcquisitionPlanningSettings
            {
                AutoPurchaseBlockedDependencies = true,
                CurrentWorldOnly = true,
            });

        require(unknownCurrency.Status == AcquisitionPlanStatus.UnknownCurrencyBalance
            && unknownCurrency.Blockers.Any(blocker => blocker.Kind == AcquisitionBlockerKind.UnknownCurrencyBalance),
            "missing currency balances must block instead of being treated as unlimited");
        require(unknownWorld.Status == AcquisitionPlanStatus.UnknownCurrentWorld
            && unknownWorld.Blockers.Any(blocker => blocker.Kind == AcquisitionBlockerKind.UnknownCurrentWorld),
            "current-world-only planning must block when current world is unknown");

        var noBlockedUnknownWorld = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput(),
            new AcquisitionPlanningSettings
            {
                AutoPurchaseBlockedDependencies = true,
                CurrentWorldOnly = true,
            });
        require(noBlockedUnknownWorld.Status == AcquisitionPlanStatus.NoBlockedDependencies,
            "no blocked dependencies must short-circuit before current-world validation");
    }

    private static void NonzeroGilIdsAreCanonicalized(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(352, 1) },
                VendorOffers = new[]
                {
                    new AcquisitionVendorOffer
                    {
                        ItemId = 352,
                        OfferId = "malformed-gil-id",
                        ReceiveQuantity = 1,
                        Costs = new[]
                        {
                            new AcquisitionCurrencyCost
                            {
                                CurrencyId = 9_352,
                                CurrencyName = "Gil",
                                Amount = 25,
                                IsGil = true,
                            },
                        },
                    },
                },
                GilBalance = 25,
            },
            new AcquisitionPlanningSettings
            {
                AutoPurchaseBlockedDependencies = true,
                MaximumGilSpend = 25,
            });

        require(result.IsSuccess && result.SelectedPlan?.Estimate.TotalGil == 25,
            "nonzero-ID IsGil costs must count toward Gil budgets and estimates");
        require(result.SelectedPlan?.Estimate.Currencies.Count(currency => currency.CurrencyId == 0) == 1,
            "canonicalized Gil must occupy the Gil currency row instead of an unknown currency row");
    }

    private static void InvalidVendorCurrencyVectorsAreRejected(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(353, 1) },
                VendorOffers = new[]
                {
                    new AcquisitionVendorOffer
                    {
                        ItemId = 353,
                        OfferId = "empty-currency-slot",
                        ReceiveQuantity = 1,
                        Costs = new[] { Currency(0, 0, false) },
                    },
                },
            },
            Enabled());

        require(result.Status == AcquisitionPlanStatus.Blocked,
            "an unresolved or zero-cost vendor currency vector must not become a free purchase");
    }

    private static void MarketStacksRemainAtomic(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(400, 3, "Stacked material") },
                MarketListings = new[] { Listing(400, 4000, 5) },
                GilBalance = 10_000,
            },
            Enabled());

        var transaction = result.SelectedPlan?.Transactions.SingleOrDefault();
        require(transaction != null && transaction.Quantity == 5,
            "market purchases must consume the complete listing stack");
        require(result.SelectedPlan?.Estimate.TotalOverbuy == 2,
            "whole-stack overbuy must be visible in the estimate");
    }

    private static void GlobalBudgetReservesCurrencyAcrossItems(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(500, 1), Blocked(501, 1) },
                VendorOffers = new[]
                {
                    Vendor(500, "cheap-vendor", 4),
                    Vendor(501, "unusable-vendor", 100),
                },
                MarketListings = new[]
                {
                    Listing(500, 5000, 6),
                    Listing(501, 5001, 5),
                },
                GilBalance = 10_000,
            },
            new AcquisitionPlanningSettings { AutoPurchaseBlockedDependencies = true, MaximumGilSpend = 10 });

        require(result.IsSuccess && result.SelectedPlan != null,
            "the planner must reserve the shared budget across all dependencies");
        require(result.SelectedPlan!.Estimate.TotalGil == 9,
            "global budget reservation must choose the only complete under-cap combination");
        require(result.SelectedPlan.Transactions.Any(transaction => transaction.ItemId == 500 && transaction.SourceKind == AcquisitionSourceKind.Vendor)
            && result.SelectedPlan.Transactions.Any(transaction => transaction.ItemId == 501 && transaction.SourceKind == AcquisitionSourceKind.Market),
            "one item must not consume budget needed to satisfy another item");
    }

    private static void CurrencyVectorsAreRequiredTogether(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(600, 1) },
                VendorOffers = new[]
                {
                    new AcquisitionVendorOffer
                    {
                        ItemId = 600,
                        OfferId = "two-currency-offer",
                        ReceiveQuantity = 1,
                        Costs = new[]
                        {
                            Currency(9001, 1, true),
                            Currency(9002, 2, true),
                        },
                    },
                },
                CurrencyBalances = new Dictionary<uint, long> { [9001] = 1, [9002] = 2 },
            },
            new AcquisitionPlanningSettings { AutoPurchaseBlockedDependencies = true, PreferMarketForSpecialCurrency = false });

        require(result.IsSuccess && result.SelectedPlan?.Transactions.SingleOrDefault()?.SourceKind == AcquisitionSourceKind.Vendor,
            "a vendor offer with a currency vector must be selectable when every component is available");
        require(result.SelectedPlan!.Estimate.Currencies.Count(currency => currency.IsSpecialCurrency) == 2,
            "each special currency component must remain a separate estimate row");
    }

    private static void PreferenceRelaxationStaysWithinHardBudget(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(700, 1) },
                VendorOffers = new[]
                {
                    new AcquisitionVendorOffer
                    {
                        ItemId = 700,
                        OfferId = "special-token-offer",
                        ReceiveQuantity = 1,
                        Costs = new[] { Currency(9003, 1, true) },
                    },
                },
                MarketListings = new[] { Listing(700, 7000, 1, price: 100) },
                CurrencyBalances = new Dictionary<uint, long> { [9003] = 1 },
                GilBalance = 1_000,
            },
            new AcquisitionPlanningSettings
            {
                AutoPurchaseBlockedDependencies = true,
                PreferMarketForSpecialCurrency = true,
                MaximumGilSpend = 50,
            });

        require(result.IsSuccess && result.SelectedPlan != null,
            "budget filtering must relax a soft source preference when a valid fallback exists");
        require(result.PreferredEstimate?.TotalGil == 100,
            "preferred estimate must preserve the requested market preference");
        require(result.SelectedPlan!.Estimate.TotalGil == 0
            && result.SelectedPlan.Transactions.Single().SourceKind == AcquisitionSourceKind.Vendor,
            "hard Gil cap must select the special-currency fallback instead of exceeding budget");
    }

    private static void SpecialCurrencyMarketReasonUsesPreferenceMetadata(Action<bool, string> require)
    {
        var input = new AcquisitionPlanningInput
        {
            Dependencies = new[] { Blocked(701, 1) },
            VendorOffers = new[]
            {
                new AcquisitionVendorOffer
                {
                    ItemId = 701,
                    OfferId = "special-token-offer",
                    ReceiveQuantity = 1,
                    Costs = new[] { Currency(9004, 1, true) },
                },
            },
            MarketListings = new[] { Listing(701, 7001, 1, price: 100) },
            CurrencyBalances = new Dictionary<uint, long> { [9004] = 1 },
            GilBalance = 1_000,
        };
        var planning = AcquisitionPlanner.Plan(
            input,
            new AcquisitionPlanningSettings
            {
                AutoPurchaseBlockedDependencies = true,
                PreferMarketForSpecialCurrency = true,
            });
        var reasons = CraftingListEditor.BuildMarketplacePurchaseReasons(
            new CraftingAcquisitionService.Evaluation
            {
                Snapshot = new AcquisitionPlanningInputBuilder.BuildResult { Input = input },
                Planning = planning,
            },
            preferMarketForSpecialCurrency: true);

        require(reasons.TryGetValue(701u, out var reason)
                && reason.Text == "market selected by special-currency preference",
            "special-currency market purchases must identify the preference that selected them");
    }

    private static void InsufficientCurrencyMarketReasonIncludesCostAndIcon(Action<bool, string> require)
    {
        const uint currencyId = 9005;
        const uint currencyIconId = 12345;
        var input = new AcquisitionPlanningInput
        {
            Dependencies = new[] { Blocked(702, 2) },
            VendorOffers = new[]
            {
                new AcquisitionVendorOffer
                {
                    ItemId = 702,
                    OfferId = "purple-scrip-offer",
                    ReceiveQuantity = 1,
                    Costs = new[]
                    {
                        new AcquisitionCurrencyCost
                        {
                            CurrencyId = currencyId,
                            IconId = currencyIconId,
                            CurrencyName = "Purple Scrip",
                            Amount = 500,
                            IsSpecialCurrency = true,
                        },
                    },
                },
            },
            MarketListings = new[] { Listing(702, 7002, 2, price: 100) },
            CurrencyBalances = new Dictionary<uint, long> { [currencyId] = 302 },
            GilBalance = 1_000,
        };
        var planning = AcquisitionPlanner.Plan(
            input,
            new AcquisitionPlanningSettings
            {
                AutoPurchaseBlockedDependencies = true,
                PreferMarketForSpecialCurrency = false,
            });
        require(planning.SelectedPlan?.Transactions.SingleOrDefault()?.SourceKind == AcquisitionSourceKind.Market,
            "insufficient special currency must select the market fallback");
        var reasons = CraftingListEditor.BuildMarketplacePurchaseReasons(
            new CraftingAcquisitionService.Evaluation
            {
                Snapshot = new AcquisitionPlanningInputBuilder.BuildResult { Input = input },
                Planning = planning,
            },
            preferMarketForSpecialCurrency: false);

        require(reasons.TryGetValue(702u, out var reason)
                && reason.Text == "not enough currency"
                && reason.CurrencyAmount == 1_000
                && reason.CurrencyIconId == currencyIconId
                && reason.CurrencyTooltip == "Purple Scrip",
            "market fallback must identify the total missing special-currency cost and its icon");
    }

    private static void ExactSearchReportsItsDeterministicLimit(Action<bool, string> require)
    {
        var listings = Enumerable.Range(0, 20)
            .Select(index => Listing(800, 8000 + index, 1, price: index + 1))
            .ToArray();
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(800, 10) },
                MarketListings = listings,
                GilBalance = 1_000_000,
            },
            Enabled());

        require(result.Status == AcquisitionPlanStatus.DeterministicLimitExceeded,
            "bounded exact search must fail explicitly instead of returning a partial greedy plan");
    }

    private static void FeasibleVendorSurvivesImpossibleMarketplaceBranch(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(801, 24) },
                VendorOffers = new[] { Vendor(801, "cheap-gil-vendor", 4) },
                MarketListings = Enumerable.Range(0, 20)
                    .Select(index => Listing(801, 8010 + index, 1, price: 100 + index))
                    .ToArray(),
                GilBalance = 1_000,
            },
            Enabled());

        require(result.IsSuccess,
            $"a feasible cheap vendor must survive an impossible marketplace-only branch without hitting the per-item search limit (status={result.Status}, blockers={string.Join(" | ", result.Blockers.Select(blocker => blocker.Reason))})");
        var selectedPlan = result.SelectedPlan;
        require(selectedPlan != null,
            "a successful acquisition plan must include its selected plan");
        require(selectedPlan!.Transactions.SingleOrDefault()?.SourceKind == AcquisitionSourceKind.Vendor,
            "the feasible cheap vendor must be selected over an incomplete marketplace-only branch");
        require(selectedPlan.Estimate.TotalGil == 96,
            "the feasible cheap vendor must cost 4 Gil per required item");
    }

    private static void ParetoFrontierAvoidsCartesianGlobalExplosion(Action<bool, string> require)
    {
        var dependencies = Enumerable.Range(0, 10)
            .Select(index => Blocked((uint)(850 + index), 1))
            .ToArray();
        var listings = dependencies
            .SelectMany((dependency, dependencyIndex) => Enumerable.Range(0, 4)
                .Select(choice => Listing(
                    dependency.ItemId,
                    85_000 + dependencyIndex * 10 + choice,
                    1,
                    price: choice + 1)))
            .ToArray();

        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = dependencies,
                MarketListings = listings,
                GilBalance = 1_000_000,
            },
            Enabled());

        require(result.IsSuccess
                && result.SelectedPlan?.Transactions.Count == dependencies.Length
                && result.SelectedPlan.Estimate.TotalGil == dependencies.Length,
            "exact Pareto search must collapse dominated choices instead of enumerating the Cartesian product");
    }

    private static void CoProductVendorTransactionIsSharedAcrossDependencies(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(901, 1), Blocked(902, 1) },
                VendorOffers = new[]
                {
                    new AcquisitionVendorOffer
                    {
                        ItemId = 901,
                        OfferId = "co-product-offer",
                        ReceiveQuantity = 1,
                        Outputs = new[]
                        {
                            new AcquisitionVendorOutput { ItemId = 901, Quantity = 1 },
                            new AcquisitionVendorOutput { ItemId = 902, Quantity = 1 },
                        },
                        Costs = new[] { Currency(0, 5, false) },
                    },
                    new AcquisitionVendorOffer
                    {
                        ItemId = 902,
                        OfferId = "co-product-offer",
                        ReceiveQuantity = 1,
                        Outputs = new[]
                        {
                            new AcquisitionVendorOutput { ItemId = 901, Quantity = 1 },
                            new AcquisitionVendorOutput { ItemId = 902, Quantity = 1 },
                        },
                        Costs = new[] { Currency(0, 5, false) },
                    },
                },
                GilBalance = 5,
            },
            Enabled());

        var transaction = result.SelectedPlan?.Transactions.SingleOrDefault();
        require(result.IsSuccess
                && transaction != null
                && transaction.PurchaseUnits == 1
                && transaction.Costs.Count(cost => cost.CurrencyId == AcquisitionCurrency.GilId) == 1
                && transaction.Costs.Single(cost => cost.CurrencyId == AcquisitionCurrency.GilId).Amount == 5
                && transaction.Outputs.Count == 2
                && result.SelectedPlan!.Estimate.TotalGil == 5
                && result.SelectedPlan.PurchasedQuantities[901] == 1
                && result.SelectedPlan.PurchasedQuantities[902] == 1,
            "one vendor transaction must jointly satisfy A+B co-product dependencies and charge once");
    }

    private static void SameSourceDifferentItemVendorTransactionsRemainDistinct(Action<bool, string> require)
    {
        var result = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[] { Blocked(910, 10), Blocked(911, 8) },
                VendorOffers = new[]
                {
                    new AcquisitionVendorOffer
                    {
                        ItemId = 910,
                        OfferId = "shared-vendor-source",
                        ReceiveQuantity = 1,
                        Costs = new[] { Currency(9_100, 20, true) },
                    },
                    new AcquisitionVendorOffer
                    {
                        ItemId = 911,
                        OfferId = "shared-vendor-source",
                        ReceiveQuantity = 1,
                        Costs = new[] { Currency(9_100, 20, true) },
                    },
                },
                CurrencyBalances = new Dictionary<uint, long> { [9_100] = 360 },
            },
            new AcquisitionPlanningSettings
            {
                AutoPurchaseBlockedDependencies = true,
                PreferMarketForSpecialCurrency = false,
            });

        var selected = result.SelectedPlan;
        var preferred = result.PreferredEstimate?.Currencies.SingleOrDefault(currency => currency.CurrencyId == 9_100);
        var minimum = result.MinimumGilEstimate?.Currencies.SingleOrDefault(currency => currency.CurrencyId == 9_100);
        require(result.IsSuccess
                && selected != null
                && selected.Transactions.Count == 2
                && selected.Transactions.Select(transaction => transaction.ItemId).OrderBy(itemId => itemId)
                    .SequenceEqual(new[] { 910u, 911u })
                && selected.Transactions.Single(transaction => transaction.ItemId == 910).Quantity == 10
                && selected.Transactions.Single(transaction => transaction.ItemId == 910).PurchaseUnits == 10
                && selected.Transactions.Single(transaction => transaction.ItemId == 910).Costs
                    .Single(cost => cost.CurrencyId == 9_100).Amount == 200
                && selected.Transactions.Single(transaction => transaction.ItemId == 911).Quantity == 8
                && selected.Transactions.Single(transaction => transaction.ItemId == 911).PurchaseUnits == 8
                && selected.Transactions.Single(transaction => transaction.ItemId == 911).Costs
                    .Single(cost => cost.CurrencyId == 9_100).Amount == 160
                && selected.Estimate.Currencies.Single(currency => currency.CurrencyId == 9_100).Required == 360
                && preferred?.Required == 360
                && minimum?.Required == 360,
            "different vendor items sharing a source ID must retain both atomic purchases and aggregate their currency cost");
    }

    private static AcquisitionDependency Blocked(
        uint itemId,
        int quantity,
        string itemName = "",
        int requiredHq = 0,
        bool isFinalOutput = false)
        => new()
        {
            ItemId = itemId,
            ItemName = itemName,
            RequiredQuantity = quantity,
            RequiredHqQuantity = requiredHq,
            IsFinalOutput = isFinalOutput,
            SelectedPath = new AcquisitionPath
            {
                Kind = AcquisitionPathKind.Craft,
                Capability = AcquisitionCapability.UnusablePath(
                    AcquisitionPathKind.Craft,
                    "The selected class or route is unavailable."),
            },
        };

    private static AcquisitionPlanningSettings Enabled()
        => new() { AutoPurchaseBlockedDependencies = true };

    private static void AcquisitionSettingsRoundTrip(Action<bool, string> require)
    {
        var source = new CraftingListDefinition
        {
            PreferBestClassForMultiRecipeItems = true,
            AutoPurchaseBlockedDependencies = true,
            PreferMarketForSpecialCurrency = false,
            PreferHQ = true,
            PreferVendors = true,
            CurrentWorldOnly = true,
            MaximumGilSpend = 123_456,
            ReturnToHomeWorldBeforeCrafting = true,
        };
        var serialized = JsonSerializer.Serialize(source);
        var roundTrip = JsonSerializer.Deserialize<CraftingListDefinition>(serialized);

        require(roundTrip != null
            && roundTrip.PreferBestClassForMultiRecipeItems
            && roundTrip.AutoPurchaseBlockedDependencies
            && !roundTrip.PreferMarketForSpecialCurrency
            && roundTrip.PreferHQ
            && roundTrip.PreferVendors
            && roundTrip.CurrentWorldOnly
            && roundTrip.MaximumGilSpend == 123_456
            && roundTrip.ReturnToHomeWorldBeforeCrafting,
            "acquisition settings and enabled best-class preference must survive list persistence round-trip");
    }

    private static AcquisitionVendorOffer Vendor(uint itemId, string offerId, long gil)
        => new()
        {
            ItemId = itemId,
            OfferId = offerId,
            ReceiveQuantity = 1,
            Costs = new[] { Currency(0, gil, false) },
        };

    private static AcquisitionMarketListing Listing(uint itemId, long listingId, int quantity, int price = 1)
        => new()
        {
            ItemId = itemId,
            ListingId = listingId,
            WorldId = 10,
            WorldName = "Test World",
            Quantity = quantity,
            PricePerUnit = price,
        };

    private static AcquisitionCurrencyCost Currency(uint currencyId, long amount, bool special)
        => new()
        {
            CurrencyId = currencyId,
            CurrencyName = currencyId == 0 ? "Gil" : $"Currency {currencyId}",
            Amount = amount,
            IsGil = currencyId == 0,
            IsSpecialCurrency = special,
        };
}
