using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GatherBuddy.Crafting.Acquisition;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Vulcan.Tests;

/// <summary>
/// Pure adapter-bound acceptance fixtures. These exercise externally visible
/// route and purchase outcomes without depending on game memory or UI state.
/// Program.cs may invoke Run() from the native acceptance harness.
/// </summary>
internal static class LiveAcquisitionAcceptanceTests
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

        var route = AcquisitionRoutePlanner.Plan(
            new AcquisitionPlan
            {
                Transactions = new[]
                {
                    Transaction(1, AcquisitionSourceKind.Market, 20, "World B", 10),
                    Transaction(2, AcquisitionSourceKind.Market, 30, "World A", 5),
                },
            },
            new AcquisitionRouteInput
            {
                CurrentWorldId = 10,
                CurrentWorldName = "World A",
                LifestreamAvailable = true,
                CanVisitWorld = _ => true,
                IsGatewayAttuned = id => id == AcquisitionWorldGateways.Gridania,
                GatewayTeleportCost = _ => 0,
            });
        Require(route.IsReady, "same-DC routes must be accepted when Lifestream and an attuned gateway exist");
        Require(route.Routes.Count == 2 && route.Routes[0].WorldName == "World A",
            "route order must prefer the lower planned Gil world");
        Require(route.Routes[1].GatewayId == AcquisitionWorldGateways.Gridania,
            "world hops must use the first attuned major-city gateway");

        var cheapestGateway = AcquisitionRoutePlanner.Plan(
            new AcquisitionPlan
            {
                Transactions = new[] { Transaction(1, AcquisitionSourceKind.Market, 20, "World B", 10) },
            },
            new AcquisitionRouteInput
            {
                CurrentWorldId = 10,
                CurrentWorldName = "World A",
                LifestreamAvailable = true,
                CanVisitWorld = _ => true,
                IsGatewayAttuned = _ => true,
                GatewayTeleportCost = id => id == AcquisitionWorldGateways.Uldah ? 5 : 20,
            });
        Require(cheapestGateway.Routes[0].GatewayId == AcquisitionWorldGateways.Uldah
            && cheapestGateway.Routes[0].TeleportCost == 5,
            "world hops must choose the cheapest attuned gateway with deterministic cost input");

        var unknownGatewayCost = AcquisitionRoutePlanner.Plan(
            new AcquisitionPlan
            {
                Transactions = new[] { Transaction(1, AcquisitionSourceKind.Market, 20, "World B", 10) },
            },
            new AcquisitionRouteInput
            {
                CurrentWorldId = 10,
                CurrentWorldName = "World A",
                LifestreamAvailable = true,
                CanVisitWorld = _ => true,
                IsGatewayAttuned = _ => true,
                GatewayTeleportCost = _ => long.MaxValue,
            });
        Require(!unknownGatewayCost.IsReady,
            "unknown teleport costs must not be selected as a gateway route");

        var negativeGatewayCost = AcquisitionRoutePlanner.Plan(
            new AcquisitionPlan
            {
                Transactions = new[] { Transaction(1, AcquisitionSourceKind.Market, 20, "World B", 10) },
            },
            new AcquisitionRouteInput
            {
                CurrentWorldId = 10,
                CurrentWorldName = "World A",
                LifestreamAvailable = true,
                CanVisitWorld = _ => true,
                IsGatewayAttuned = _ => true,
                GatewayTeleportCost = _ => -1,
            });
        Require(!negativeGatewayCost.IsReady,
            "negative teleport costs must not be treated as free gateway routes");

        var unknownMarketWorld = AcquisitionRoutePlanner.Plan(
            new AcquisitionPlan
            {
                Transactions = new[] { Transaction(1, AcquisitionSourceKind.Market, 0, "", 10) },
            },
            new AcquisitionRouteInput
            {
                CurrentWorldId = 10,
                CurrentWorldName = "World A",
            });
        Require(!unknownMarketWorld.IsReady,
            "market transactions without a resolved world must be rejected before routing");

        var stableId = AcquisitionTransactionIdentity.Create(42, 9001, AcquisitionSourceKind.Market, "12345", false, 0);
        var stableIdRepeat = AcquisitionTransactionIdentity.Create(42, 9001, AcquisitionSourceKind.Market, "12345", false, 0);
        var nextId = AcquisitionTransactionIdentity.Create(42, 9001, AcquisitionSourceKind.Market, "12345", false, 1);
        Require(stableId == stableIdRepeat && stableId != nextId,
            "planner transaction identity must be deterministic and distinguish repeated source transactions");

        var plannerDependency = new AcquisitionDependency
        {
            ItemId = 43,
            ItemName = "Planner item",
            RequiredQuantity = 1,
            SelectedPath = new AcquisitionPath
            {
                RecipeId = 9002,
                Capability = AcquisitionCapability.UnusablePath(AcquisitionPathKind.Craft, "test capability unavailable"),
            },
        };
        var plannerSettings = new AcquisitionPlanningSettings { AutoPurchaseBlockedDependencies = true };
        var plannerInputA = new AcquisitionPlanningInput
        {
            Dependencies = new[] { plannerDependency },
            CurrentWorldId = 10,
            GilBalance = 100,
            MarketListings = new[]
            {
                new AcquisitionMarketListing { ItemId = 43, ListingId = 12346, WorldId = 10, Quantity = 1, PricePerUnit = 6 },
                new AcquisitionMarketListing { ItemId = 43, ListingId = 12345, WorldId = 10, Quantity = 1, PricePerUnit = 5 },
            },
        };
        var plannerInputB = new AcquisitionPlanningInput
        {
            Dependencies = new[] { plannerDependency },
            CurrentWorldId = 10,
            GilBalance = 100,
            MarketListings = plannerInputA.MarketListings.Reverse().ToArray(),
        };
        var plannerA = AcquisitionPlanner.Plan(plannerInputA, plannerSettings);
        var plannerB = AcquisitionPlanner.Plan(plannerInputB, plannerSettings);
        Require(plannerA.SelectedPlan?.Transactions.Single().ExecutionId
                == plannerB.SelectedPlan?.Transactions.Single().ExecutionId,
            "planner ExecutionId must survive source-list reorder");
        var plannerDemand = plannerA.SelectedPlan?.RequiredQuantities;
        Require(plannerDemand != null
                && plannerDemand.TryGetValue(43, out var plannedQuantity)
                && plannedQuantity == 1,
            "planner must retain original dependency demand separately from selected purchase output");

        var authoritativeWallet = new NativeLiveAcquisitionEnvironment(
            currencyAvailability: (group, currencyId, currencyName)
                => new VendorCurrencyAvailability(
                    currencyId,
                    currencyName,
                    25,
                    VendorCurrencyAvailabilitySource.CurrencyManager));
        var currencyPlan = new AcquisitionPlan
        {
            Transactions = new[]
            {
                new AcquisitionTransaction
                {
                    ItemId = 44,
                    Costs = new[]
                    {
                        new AcquisitionCurrencyCost
                        {
                            CurrencyId = 7001,
                            CurrencyName = "Test currency",
                            Group = VendorCurrencyGroup.Tomestones,
                        },
                    },
                },
            },
            Estimate = new AcquisitionEstimate
            {
                Currencies = new[]
                {
                    new AcquisitionCurrencyRequirement
                    {
                        CurrencyId = 7001,
                        CurrencyName = "Test currency",
                        Required = 20,
                    },
                },
            },
        };
        Require(authoritativeWallet.HasSufficientPlannedCurrency(currencyPlan),
            "live preflight must accept a sufficient authoritative wallet balance");

        var insufficientWallet = new NativeLiveAcquisitionEnvironment(
            currencyAvailability: (group, currencyId, currencyName)
                => new VendorCurrencyAvailability(
                    currencyId,
                    currencyName,
                    10,
                    VendorCurrencyAvailabilitySource.CurrencyManager));
        Require(!insufficientWallet.HasSufficientPlannedCurrency(currencyPlan),
            "live preflight must reject an authoritative wallet balance below the planned spend");

        var inventoryFallbackWallet = new NativeLiveAcquisitionEnvironment(
            currencyAvailability: (group, currencyId, currencyName)
                => new VendorCurrencyAvailability(
                    currencyId,
                    currencyName,
                    25,
                    VendorCurrencyAvailabilitySource.InventoryItemCount));
        Require(!inventoryFallbackWallet.HasSufficientPlannedCurrency(currencyPlan),
            "live preflight must reject an inventory-item fallback as unknown currency state");

        var exactVendorCurrency = new FakeEnvironment
        {
            VendorCurrencyBefore = new Dictionary<uint, long> { [VendorShopResolver.GilCurrencyItemId] = 1000, [7001] = 10 },
            VendorCurrencyAfter = new Dictionary<uint, long> { [VendorShopResolver.GilCurrencyItemId] = 900, [7001] = 7 },
            VendorCurrencySources = new Dictionary<uint, VendorCurrencyAvailabilitySource>
            {
                [VendorShopResolver.GilCurrencyItemId] = VendorCurrencyAvailabilitySource.InventoryManagerGil,
                [7001] = VendorCurrencyAvailabilitySource.CurrencyManager,
            },
            VendorCurrencySourcesAfter = new Dictionary<uint, VendorCurrencyAvailabilitySource>
            {
                [VendorShopResolver.GilCurrencyItemId] = VendorCurrencyAvailabilitySource.InventoryManagerGil,
                [7001] = VendorCurrencyAvailabilitySource.CurrencyManager,
            },
        };
        var exactVendorResult = new LiveAcquisitionExecutor(exactVendorCurrency)
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[]
                {
                    new AcquisitionTransaction
                    {
                        ItemId = 45,
                        ItemName = "Multi-currency vendor item",
                        SourceKind = AcquisitionSourceKind.Vendor,
                        SourceId = "vendor-45",
                        Quantity = 1,
                        Costs = new[]
                        {
                            new AcquisitionCurrencyCost
                            {
                                CurrencyId = AcquisitionCurrency.GilId,
                                Amount = 100,
                                IsGil = true,
                                Group = VendorCurrencyGroup.Gil,
                            },
                            new AcquisitionCurrencyCost
                            {
                                CurrencyId = 7001,
                                Amount = 3,
                                CurrencyName = "Test tomestone",
                                Group = VendorCurrencyGroup.Tomestones,
                            },
                        },
                        GilCost = 100,
                    },
                },
            }))
            .GetAwaiter()
            .GetResult();
        Require(exactVendorResult.Status == LiveAcquisitionStatus.Completed,
            "vendor acquisition must verify exact observed deltas for multiple currencies");

        var missingVendorCurrency = new FakeEnvironment
        {
            VendorCurrencyBefore = new Dictionary<uint, long> { [VendorShopResolver.GilCurrencyItemId] = 1000, [7001] = 10 },
            VendorCurrencyAfter = new Dictionary<uint, long> { [VendorShopResolver.GilCurrencyItemId] = 900, [7001] = 7 },
            VendorCurrencySources = new Dictionary<uint, VendorCurrencyAvailabilitySource>
            {
                [VendorShopResolver.GilCurrencyItemId] = VendorCurrencyAvailabilitySource.InventoryManagerGil,
                [7001] = VendorCurrencyAvailabilitySource.InventoryItemCount,
            },
            VendorCurrencySourcesAfter = new Dictionary<uint, VendorCurrencyAvailabilitySource>
            {
                [VendorShopResolver.GilCurrencyItemId] = VendorCurrencyAvailabilitySource.InventoryManagerGil,
                [7001] = VendorCurrencyAvailabilitySource.InventoryItemCount,
            },
        };
        var missingVendorResult = new LiveAcquisitionExecutor(missingVendorCurrency)
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[]
                {
                    new AcquisitionTransaction
                    {
                        ItemId = 46,
                        ItemName = "Unobserved vendor item",
                        SourceKind = AcquisitionSourceKind.Vendor,
                        SourceId = "vendor-46",
                        Quantity = 1,
                        Costs = new[]
                        {
                            new AcquisitionCurrencyCost
                            {
                                CurrencyId = 7001,
                                Amount = 3,
                                CurrencyName = "Test tomestone",
                                Group = VendorCurrencyGroup.Tomestones,
                            },
                        },
                    },
                },
            }))
            .GetAwaiter()
            .GetResult();
        Require(missingVendorResult.Status != LiveAcquisitionStatus.Completed
            && missingVendorResult.FailureKind == LiveAcquisitionFailureKind.VerificationFailed,
            "vendor acquisition must fail closed when a required currency wallet is only an inventory fallback");

        var coProductEnvironment = new FakeEnvironment
        {
            VendorCurrencyBefore = new Dictionary<uint, long> { [VendorShopResolver.GilCurrencyItemId] = 100 },
            VendorCurrencySources = new Dictionary<uint, VendorCurrencyAvailabilitySource>
            {
                [VendorShopResolver.GilCurrencyItemId] = VendorCurrencyAvailabilitySource.InventoryManagerGil,
            },
            VendorCurrencySourcesAfter = new Dictionary<uint, VendorCurrencyAvailabilitySource>
            {
                [VendorShopResolver.GilCurrencyItemId] = VendorCurrencyAvailabilitySource.InventoryManagerGil,
            },
            VendorCurrencySpendPerCall = new Dictionary<uint, long>
            {
                [VendorShopResolver.GilCurrencyItemId] = 1,
            },
        };
        var coProductPlanning = AcquisitionPlanner.Plan(
            new AcquisitionPlanningInput
            {
                Dependencies = new[]
                {
                    new AcquisitionDependency
                    {
                        ItemId = 101,
                        RequiredQuantity = 1,
                        SelectedPath = new AcquisitionPath
                        {
                            Capability = AcquisitionCapability.UnusablePath(AcquisitionPathKind.Craft, "missing A capability"),
                        },
                    },
                    new AcquisitionDependency
                    {
                        ItemId = 103,
                        RequiredQuantity = 2,
                        SelectedPath = new AcquisitionPath
                        {
                            Capability = AcquisitionCapability.UnusablePath(AcquisitionPathKind.Craft, "missing C capability"),
                        },
                    },
                },
                VendorOffers = new[]
                {
                    new AcquisitionVendorOffer
                    {
                        ItemId = 101,
                        OfferId = "co-product-a",
                        ReceiveQuantity = 2,
                        Outputs = new[]
                        {
                            new AcquisitionVendorOutput { ItemId = 101, Quantity = 2 },
                            new AcquisitionVendorOutput { ItemId = 102, Quantity = 1 },
                        },
                        Costs = new[]
                        {
                            new AcquisitionCurrencyCost
                            {
                                CurrencyId = AcquisitionCurrency.GilId,
                                Amount = 1,
                                IsGil = true,
                                Group = VendorCurrencyGroup.Gil,
                            },
                        },
                    },
                    new AcquisitionVendorOffer
                    {
                        ItemId = 102,
                        OfferId = "co-product-b",
                        ReceiveQuantity = 1,
                        Outputs = new[]
                        {
                            new AcquisitionVendorOutput { ItemId = 102, Quantity = 1 },
                            new AcquisitionVendorOutput { ItemId = 103, Quantity = 2 },
                        },
                        Costs = new[]
                        {
                            new AcquisitionCurrencyCost
                            {
                                CurrencyId = AcquisitionCurrency.GilId,
                                Amount = 1,
                                IsGil = true,
                                Group = VendorCurrencyGroup.Gil,
                            },
                        },
                    },
                },
                GilBalance = 100,
            },
            new AcquisitionPlanningSettings { AutoPurchaseBlockedDependencies = true });
        Require(coProductPlanning.IsSuccess
                && coProductPlanning.SelectedPlan?.RequiredQuantities.TryGetValue(103, out var coProductDemand) == true
                && coProductDemand == 2,
            "planner must preserve co-product dependency demand for executor fulfillment");
        var coProductResult = new LiveAcquisitionExecutor(coProductEnvironment)
            .ExecuteAsync(coProductPlanning)
            .GetAwaiter()
            .GetResult();
        Require(coProductResult.Status == LiveAcquisitionStatus.Completed
            && coProductEnvironment.VendorPurchaseCalls == 2
            && coProductResult.PurchasedQuantities[101] == 2
            && coProductResult.PurchasedQuantities[103] == 2,
            "co-product output vectors must execute every atomic vendor transaction needed by another dependency");

        var overlappingOutputEnvironment = new FakeEnvironment
        {
            VendorCurrencyBefore = new Dictionary<uint, long>
            {
                [VendorShopResolver.GilCurrencyItemId] = 1,
            },
            VendorCurrencySources = new Dictionary<uint, VendorCurrencyAvailabilitySource>
            {
                [VendorShopResolver.GilCurrencyItemId] = VendorCurrencyAvailabilitySource.InventoryManagerGil,
            },
            VendorCurrencySourcesAfter = new Dictionary<uint, VendorCurrencyAvailabilitySource>
            {
                [VendorShopResolver.GilCurrencyItemId] = VendorCurrencyAvailabilitySource.InventoryManagerGil,
            },
            VendorCurrencySpendPerCall = new Dictionary<uint, long>
            {
                [VendorShopResolver.GilCurrencyItemId] = 1,
            },
        };
        var overlappingOutputResult = new LiveAcquisitionExecutor(overlappingOutputEnvironment)
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[]
                {
                    new AcquisitionTransaction
                    {
                        ItemId = 201,
                        ItemName = "Primary vendor output",
                        SourceKind = AcquisitionSourceKind.Vendor,
                        SourceId = "overlap-primary",
                        Quantity = 1,
                        PrimaryOutputQuantity = 1,
                        PurchaseUnits = 1,
                        Costs = new[]
                        {
                            new AcquisitionCurrencyCost
                            {
                                CurrencyId = AcquisitionCurrency.GilId,
                                Amount = 1,
                                IsGil = true,
                                Group = VendorCurrencyGroup.Gil,
                            },
                        },
                        GilCost = 1,
                        Outputs = new[]
                        {
                            new AcquisitionVendorOutput { ItemId = 201, Quantity = 1 },
                            new AcquisitionVendorOutput { ItemId = 202, Quantity = 1 },
                        },
                    },
                    new AcquisitionTransaction
                    {
                        ItemId = 202,
                        ItemName = "Overlapping vendor output",
                        SourceKind = AcquisitionSourceKind.Vendor,
                        SourceId = "overlap-secondary",
                        Quantity = 1,
                        PrimaryOutputQuantity = 1,
                        PurchaseUnits = 1,
                        Costs = new[]
                        {
                            new AcquisitionCurrencyCost
                            {
                                CurrencyId = AcquisitionCurrency.GilId,
                                Amount = 1,
                                IsGil = true,
                                Group = VendorCurrencyGroup.Gil,
                            },
                        },
                        GilCost = 1,
                        Outputs = new[]
                        {
                            new AcquisitionVendorOutput { ItemId = 202, Quantity = 1 },
                        },
                    },
                },
                RequiredQuantities = new Dictionary<uint, int>
                {
                    [201] = 1,
                    [202] = 1,
                },
            }))
            .GetAwaiter()
            .GetResult();
        Require(overlappingOutputResult.Status == LiveAcquisitionStatus.Completed
            && overlappingOutputEnvironment.VendorPurchaseCalls == 1
            && overlappingOutputResult.PurchasedQuantities[201] == 1
            && overlappingOutputResult.PurchasedQuantities[202] == 1,
            "an already allocated co-product must satisfy an overlapping required output without a second constrained-currency purchase");

        var blockedRoute = AcquisitionRoutePlanner.Plan(
            new AcquisitionPlan
            {
                Transactions = new[] { Transaction(1, AcquisitionSourceKind.Market, 20, "World B", 10) },
            },
            new AcquisitionRouteInput
            {
                CurrentWorldId = 10,
                CurrentWorldOnly = true,
                LifestreamAvailable = true,
                CanVisitWorld = _ => true,
                IsGatewayAttuned = _ => true,
            });
        Require(!blockedRoute.IsReady && blockedRoute.FailureReason.Contains("Current world only", StringComparison.OrdinalIgnoreCase),
            "current-world-only must hard-stop a plan requiring another world");

        var fake = new FakeEnvironment
        {
            Listings = new[]
            {
                new LiveMarketListing(1, 90, 10, "World A", 1, 1, 0, false, true),
                new LiveMarketListing(1, 91, 10, "World A", 1, 1, 0, false, false, true),
                new LiveMarketListing(1, 100, 10, "World A", 1, 5, 0, false),
            },
        };
        var marketPlan = Ready(new AcquisitionPlan
        {
            Transactions = new[] { Transaction(1, AcquisitionSourceKind.Market, 10, "World A", 5) },
        });
        var complete = new LiveAcquisitionExecutor(fake, new LiveAcquisitionOptions())
            .ExecuteAsync(marketPlan).GetAwaiter().GetResult();
        Require(complete.Status == LiveAcquisitionStatus.Completed && complete.PurchasedQuantities[1] == 1,
            "verified live market purchase must complete the plan");
        Require(fake.MarketPurchaseCalls == 1,
            "one valid live listing must result in exactly one native purchase request");
        Require(fake.LastMarketRoute?.WorldId == 10,
            "market navigation must receive the complete selected world route");

        var underfill = new FakeEnvironment
        {
            Listings = new[] { new LiveMarketListing(2, 200, 10, "World A", 2, 5, 0, false) },
            MarketQuantityOverride = 1,
        };
        var underfillResult = new LiveAcquisitionExecutor(
                underfill,
                new LiveAcquisitionOptions { MaximumReplans = 1 },
                _ =>
                {
                    underfill.ReplanListings = new[]
                    {
                        new LiveMarketListing(2, 201, 10, "World A", 1, 5, 0, false),
                    };
                    return Task.FromResult<AcquisitionPlanningResult?>(Ready(new AcquisitionPlan
                    {
                        Transactions = new[]
                        {
                            Transaction(2, AcquisitionSourceKind.Market, 10, "World A", 10, quantity: 2, sourceId: "201"),
                        },
                    }));
                })
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[] { Transaction(2, AcquisitionSourceKind.Market, 10, "World A", 10, quantity: 2, sourceId: "200") },
            }))
            .GetAwaiter()
            .GetResult();
        Require(underfillResult.Status == LiveAcquisitionStatus.Completed
            && underfillResult.PurchasedQuantities[2] == 2
            && underfill.MarketPurchaseCalls == 2,
            "a deterministic accepted market underfill must trigger global replan and preserve the known partial quantity");

        var indeterminateUnderfill = new FakeEnvironment
        {
            Listings = new[] { new LiveMarketListing(48, 4800, 10, "World A", 2, 5, 0, false) },
            MarketQuantityOverride = 1,
            MarketVerifiedOverride = false,
        };
        var indeterminateUnderfillResult = new LiveAcquisitionExecutor(indeterminateUnderfill)
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[] { Transaction(48, AcquisitionSourceKind.Market, 10, "World A", 10, quantity: 2) },
            }))
            .GetAwaiter()
            .GetResult();
        Require(indeterminateUnderfillResult.Status == LiveAcquisitionStatus.PartiallyCompleted
            && indeterminateUnderfillResult.HasIndeterminatePurchases,
            "an accepted market underfill with unverified inventory state must remain partial and indeterminate");

        var malformedUnderfillReplans = 0;
        var malformedUnderfill = new FakeEnvironment
        {
            Listings = new[] { new LiveMarketListing(49, 4900, 10, "World A", 2, 5, 0, false) },
            MarketQuantityOverride = 1,
            MarketItemIdOverride = 999,
        };
        var malformedUnderfillResult = new LiveAcquisitionExecutor(
                malformedUnderfill,
                new LiveAcquisitionOptions { MaximumReplans = 1 },
                _ =>
                {
                    malformedUnderfillReplans++;
                    return Task.FromResult<AcquisitionPlanningResult?>(null);
                })
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[] { Transaction(49, AcquisitionSourceKind.Market, 10, "World A", 10, quantity: 2) },
            }))
            .GetAwaiter()
            .GetResult();
        Require(malformedUnderfillResult.Status == LiveAcquisitionStatus.Failed
            && malformedUnderfillResult.FailureKind == LiveAcquisitionFailureKind.VerificationFailed
            && malformedUnderfillReplans == 0
            && malformedUnderfillResult.PurchasedQuantities.Count == 0,
            "a malformed accepted underfill must fail common market validation before recording or replanning");

        var replanStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLateReplan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replanRace = new FakeEnvironment
        {
            ListingsFresh = false,
        };
        using var replanCancellation = new CancellationTokenSource();
        var replanRaceExecution = new LiveAcquisitionExecutor(
                replanRace,
                new LiveAcquisitionOptions { MaximumReplans = 1 },
                async _ =>
                {
                    replanStarted.TrySetResult(true);
                    await releaseLateReplan.Task;
                    return Ready(new AcquisitionPlan
                    {
                        Transactions = new[] { Transaction(47, AcquisitionSourceKind.Vendor, 0, string.Empty, 1) },
                    });
                })
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[] { Transaction(47, AcquisitionSourceKind.Market, 10, "World A", 1) },
            }), replanCancellation.Token);
        replanStarted.Task.GetAwaiter().GetResult();
        replanCancellation.Cancel();
        releaseLateReplan.TrySetResult(true);
        var replanRaceResult = replanRaceExecution.GetAwaiter().GetResult();
        Require(replanRaceResult.Status == LiveAcquisitionStatus.Cancelled
            && replanRace.VendorPurchaseCalls == 0,
            $"a replan result that completes after cancellation must not be published into execution "
            + $"(status={replanRaceResult.Status}, vendorCalls={replanRace.VendorPurchaseCalls})");

        var lowerPrice = new FakeEnvironment
        {
            Listings = new[] { new LiveMarketListing(3, 300, 10, "World A", 1, 5, 0, false) },
        };
        var lowerResult = new LiveAcquisitionExecutor(lowerPrice)
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[] { Transaction(3, AcquisitionSourceKind.Market, 10, "World A", 10) },
            }))
            .GetAwaiter()
            .GetResult();
        Require(lowerResult.Status == LiveAcquisitionStatus.Completed,
            "a lower live unit price must remain purchasable");

        var higherPrice = new FakeEnvironment
        {
            Listings = new[] { new LiveMarketListing(4, 400, 10, "World A", 1, 20, 0, false) },
        };
        var higherResult = new LiveAcquisitionExecutor(
                higherPrice,
                new LiveAcquisitionOptions { MaximumReplans = 1 },
                _ => Task.FromResult<AcquisitionPlanningResult?>(Ready(new AcquisitionPlan
                {
                    Transactions = new[] { Transaction(4, AcquisitionSourceKind.Vendor, 0, string.Empty, 5) },
                })))
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[] { Transaction(4, AcquisitionSourceKind.Market, 10, "World A", 10) },
            }))
            .GetAwaiter()
            .GetResult();
        Require(higherResult.Status == LiveAcquisitionStatus.Completed && higherPrice.MarketPurchaseCalls == 0,
            "a higher live unit price must trigger global replan before purchase");

        var globalReservation = new FakeEnvironment
        {
            Listings = new[]
            {
                new LiveMarketListing(5, 500, 10, "World A", 1, 5, 0, false),
                new LiveMarketListing(6, 600, 10, "World A", 1, 5, 0, false),
            },
        };
        var globalReservationResult = new LiveAcquisitionExecutor(
                globalReservation,
                new LiveAcquisitionOptions { MaximumGilSpend = 6 })
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[]
                {
                    Transaction(5, AcquisitionSourceKind.Market, 10, "World A", 5),
                    Transaction(6, AcquisitionSourceKind.Market, 10, "World A", 5),
                },
            }))
            .GetAwaiter()
            .GetResult();
        var globalReservationItemSix = globalReservationResult.PurchasedQuantities.TryGetValue(6, out var purchasedItemSix)
            ? purchasedItemSix
            : 0;
        var globalReservationItemFive = globalReservationResult.PurchasedQuantities.TryGetValue(5, out var purchasedItemFive)
            ? purchasedItemFive
            : 0;
        Require(globalReservationResult.Status == LiveAcquisitionStatus.PartiallyCompleted
            && globalReservationItemFive == 1
            && globalReservationItemSix == 0,
            $"global Gil reservation must stop later purchases without claiming complete fulfillment "
            + $"(status={globalReservationResult.Status}, item5={globalReservationItemFive}, "
            + $"item6={globalReservationItemSix}, marketCalls={globalReservation.MarketPurchaseCalls}, "
            + $"gil={globalReservationResult.GilSpent})");

        var repeatedItem = new FakeEnvironment
        {
            Listings = new[]
            {
                new LiveMarketListing(7, 700, 10, "World A", 1, 5, 0, false),
                new LiveMarketListing(7, 701, 10, "World A", 1, 6, 0, false),
            },
        };
        var repeatedResult = new LiveAcquisitionExecutor(repeatedItem)
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[]
                {
                    Transaction(7, AcquisitionSourceKind.Market, 10, "World A", 5, sourceId: "700"),
                    Transaction(7, AcquisitionSourceKind.Market, 10, "World A", 6, sourceId: "701"),
                },
            }))
            .GetAwaiter()
            .GetResult();
        Require(repeatedResult.Status == LiveAcquisitionStatus.Completed
            && repeatedResult.PurchasedQuantities[7] == 2
            && repeatedItem.MarketPurchaseCalls == 2,
            "repeated same-item transactions must retain independent fulfillment state");

        var replanAllocation = new FakeEnvironment
        {
            Listings = new[]
            {
                new LiveMarketListing(14, 1400, 10, "World A", 1, 5, 0, false),
                new LiveMarketListing(14, 1401, 10, "World A", 1, 5, 0, false),
            },
            StaleOnListingsRequest = 2,
        };
        var replanAllocationResult = new LiveAcquisitionExecutor(
                replanAllocation,
                new LiveAcquisitionOptions { MaximumReplans = 1 },
                _ =>
                {
                    replanAllocation.ReplanListings = new[]
                    {
                        new LiveMarketListing(14, 1402, 10, "World A", 1, 5, 0, false),
                    };
                    return Task.FromResult<AcquisitionPlanningResult?>(Ready(new AcquisitionPlan
                    {
                        // New listing/source ID, but the original required
                        // quantity remains two after the first purchase.
                        Transactions = new[]
                        {
                            Transaction(14, AcquisitionSourceKind.Market, 10, "World A", 10, quantity: 2, sourceId: "1402"),
                        },
                    }));
                })
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[]
                {
                    Transaction(14, AcquisitionSourceKind.Market, 10, "World A", 5, sourceId: "1400"),
                    Transaction(14, AcquisitionSourceKind.Market, 10, "World A", 5, sourceId: "1401"),
                },
            }))
            .GetAwaiter()
            .GetResult();
        Require(replanAllocationResult.Status == LiveAcquisitionStatus.Completed
            && replanAllocationResult.PurchasedQuantities[14] == 2
            && replanAllocation.MarketPurchaseCalls == 2,
            "a replan with a new source ID must allocate prior same-item purchases before buying again");

        var staleAtomicStack = new FakeEnvironment
        {
            Listings = new[]
            {
                new LiveMarketListing(15, 1500, 10, "World A", 50, 1, 0, false),
            },
            StaleOnListingsRequest = 1,
        };
        var staleAtomicResult = new LiveAcquisitionExecutor(
                staleAtomicStack,
                new LiveAcquisitionOptions { MaximumReplans = 1 },
                _ =>
                {
                    staleAtomicStack.ReplanListings = new[]
                    {
                        new LiveMarketListing(15, 1501, 10, "World A", 40, 1, 0, false),
                    };
                    return Task.FromResult<AcquisitionPlanningResult?>(Ready(new AcquisitionPlan
                    {
                        Transactions = new[]
                        {
                            Transaction(15, AcquisitionSourceKind.Market, 10, "World A", 40, quantity: 40, sourceId: "1501"),
                        },
                        RequiredQuantities = new Dictionary<uint, int> { [15] = 40 },
                        PurchasedQuantities = new Dictionary<uint, int> { [15] = 40 },
                    }));
                })
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[]
                {
                    Transaction(15, AcquisitionSourceKind.Market, 10, "World A", 40, quantity: 40, sourceId: "1500"),
                },
                // The selected source is a 50-item atomic stack, but the
                // dependency demand remains exactly 40. The first listing is
                // stale, so only the replanned 40-item listing is purchased.
                RequiredQuantities = new Dictionary<uint, int> { [15] = 40 },
                PurchasedQuantities = new Dictionary<uint, int> { [15] = 50 },
            }))
            .GetAwaiter()
            .GetResult();
        Require(staleAtomicResult.Status == LiveAcquisitionStatus.Completed
            && staleAtomicResult.PurchasedQuantities[15] == 40
            && staleAtomicStack.MarketPurchaseCalls == 1,
            "a stale 50-item atomic selection must not turn a replanned 40-item demand into a required 50");

        var atomicOverbuy = new FakeEnvironment
        {
            Listings = new[]
            {
                new LiveMarketListing(13, 1300, 10, "World A", 50, 1, 0, false),
                new LiveMarketListing(19, 1900, 10, "World A", 1, 5, 0, false),
            },
        };
        var atomicOverbuyResult = new LiveAcquisitionExecutor(
                atomicOverbuy,
                new LiveAcquisitionOptions { MaximumGilSpend = 55 })
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[]
                {
                    Transaction(13, AcquisitionSourceKind.Market, 10, "World A", 40, quantity: 40),
                    Transaction(19, AcquisitionSourceKind.Market, 10, "World A", 5),
                },
            }))
            .GetAwaiter()
            .GetResult();
        Require(atomicOverbuyResult.Status == LiveAcquisitionStatus.Completed
            && atomicOverbuyResult.PurchasedQuantities[13] == 50
            && atomicOverbuyResult.PurchasedQuantities[19] == 1
            && atomicOverbuy.MarketPurchaseCalls == 2,
            "a larger market stack may overbuy only when the complete atomic stack fits its global reservation");

        var atomicReservation = new FakeEnvironment
        {
            Listings = new[] { new LiveMarketListing(16, 1600, 10, "World A", 50, 1, 0, false) },
        };
        var atomicReservationResult = new LiveAcquisitionExecutor(
                atomicReservation,
                new LiveAcquisitionOptions { MaximumGilSpend = 54 })
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[]
                {
                    Transaction(16, AcquisitionSourceKind.Market, 10, "World A", 40, quantity: 40),
                    Transaction(17, AcquisitionSourceKind.Market, 10, "World A", 5),
                },
            }))
            .GetAwaiter()
            .GetResult();
        Require(atomicReservationResult.Status != LiveAcquisitionStatus.Completed
            && atomicReservation.MarketPurchaseCalls == 0,
            "an atomic overbuy must preserve the global reservation for other planned transactions");

        var hqAllocation = new FakeEnvironment
        {
            Listings = new[]
            {
                new LiveMarketListing(18, 1800, 10, "World A", 8, 1, 0, true),
                new LiveMarketListing(18, 1801, 10, "World A", 2, 1, 0, false),
            },
        };
        var hqAllocationResult = new LiveAcquisitionExecutor(
                hqAllocation,
                new LiveAcquisitionOptions { MaximumGilSpend = 13 })
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[]
                {
                    Transaction(18, AcquisitionSourceKind.Market, 10, "World A", 5, quantity: 5, isHq: true),
                    Transaction(18, AcquisitionSourceKind.Market, 10, "World A", 5, quantity: 5),
                },
            }))
            .GetAwaiter()
            .GetResult();
        Require(hqAllocationResult.Status == LiveAcquisitionStatus.Completed
            && hqAllocationResult.PurchasedQuantities[18] == 10
            && hqAllocation.PurchasedListingIds.Contains(1800)
            && hqAllocation.PurchasedListingIds.Contains(1801),
            "prior HQ overbuy must satisfy hard-HQ first and only its remainder may satisfy ordinary quantity");

        var hqFallback = new FakeEnvironment
        {
            Listings = new[]
            {
                new LiveMarketListing(8, 800, 10, "World A", 1, 20, 0, true),
                new LiveMarketListing(8, 801, 10, "World A", 1, 5, 0, false),
            },
        };
        var hqFallbackResult = new LiveAcquisitionExecutor(
                hqFallback,
                new LiveAcquisitionOptions { PreferHQ = true })
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[] { Transaction(8, AcquisitionSourceKind.Market, 10, "World A", 5) },
            }))
            .GetAwaiter()
            .GetResult();
        Require(hqFallbackResult.Status == LiveAcquisitionStatus.Completed
            && hqFallback.PurchasedListingIds.Contains(801),
            "PreferHQ must fall back to an affordable NQ listing when HQ is not required");

        var hqAllowed = new FakeEnvironment
        {
            Listings = new[]
            {
                new LiveMarketListing(12, 1200, 10, "World A", 1, 5, 0, true),
                new LiveMarketListing(12, 1201, 10, "World A", 1, 10, 0, false),
            },
        };
        var hqAllowedResult = new LiveAcquisitionExecutor(hqAllowed)
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[] { Transaction(12, AcquisitionSourceKind.Market, 10, "World A", 5) },
            }))
            .GetAwaiter()
            .GetResult();
        Require(hqAllowedResult.Status == LiveAcquisitionStatus.Completed
            && hqAllowed.PurchasedListingIds.Contains(1200),
            "HQ listings may be purchased without PreferHQ when they fit the transaction and global reservations");

        var hqRequired = new FakeEnvironment
        {
            Listings = new[] { new LiveMarketListing(15, 1500, 10, "World A", 1, 5, 0, false) },
        };
        var hqRequiredResult = new LiveAcquisitionExecutor(hqRequired)
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[]
                {
                    Transaction(15, AcquisitionSourceKind.Market, 10, "World A", 5, isHq: true),
                },
            }))
            .GetAwaiter()
            .GetResult();
        Require(hqRequiredResult.Status != LiveAcquisitionStatus.Completed
            && hqRequired.MarketPurchaseCalls == 0,
            "an explicitly HQ-required transaction must not be fulfilled by an NQ listing");

        var gilMismatch = new FakeEnvironment
        {
            Listings = new[] { new LiveMarketListing(9, 900, 10, "World A", 1, 5, 0, false) },
            MarketGilBefore = 100,
            MarketGilAfter = 90,
            MarketGilReported = 5,
        };
        var gilMismatchResult = new LiveAcquisitionExecutor(gilMismatch)
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[] { Transaction(9, AcquisitionSourceKind.Market, 10, "World A", 5) },
            }))
            .GetAwaiter()
            .GetResult();
        Require(gilMismatchResult.Status != LiveAcquisitionStatus.Completed
            && gilMismatchResult.FailureKind == LiveAcquisitionFailureKind.VerificationFailed,
            "a market Gil delta mismatch must fail verification");

        var vendorGilMismatch = new FakeEnvironment
        {
            VendorGilBefore = 100,
            VendorGilAfter = 90,
            VendorGilReported = 5,
        };
        var vendorGilMismatchResult = new LiveAcquisitionExecutor(vendorGilMismatch)
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[] { Transaction(11, AcquisitionSourceKind.Vendor, 0, string.Empty, 5) },
            }))
            .GetAwaiter()
            .GetResult();
        Require(vendorGilMismatchResult.Status != LiveAcquisitionStatus.Completed
            && vendorGilMismatchResult.FailureKind == LiveAcquisitionFailureKind.VerificationFailed,
            "a vendor Gil delta mismatch must fail verification");

        var cancelled = new FakeEnvironment
        {
            Listings = new[] { new LiveMarketListing(10, 1000, 10, "World A", 1, 5, 0, false) },
            ThrowCancellationAfterSubmit = true,
        };
        var cancelledResult = new LiveAcquisitionExecutor(cancelled)
            .ExecuteAsync(Ready(new AcquisitionPlan
            {
                Transactions = new[] { Transaction(10, AcquisitionSourceKind.Market, 10, "World A", 5) },
            }))
            .GetAwaiter()
            .GetResult();
        Require(cancelledResult.Status == LiveAcquisitionStatus.PartiallyCompleted
            && cancelledResult.HasIndeterminatePurchases
            && cancelled.CleanupCalls == 1,
            "cancellation after submit must preserve indeterminate state and clean up");

        var stale = new FakeEnvironment { ListingsFresh = false };
        var vendorReplacement = Ready(new AcquisitionPlan
        {
            Transactions = new[] { Transaction(1, AcquisitionSourceKind.Vendor, 0, "", 5) },
        });
        var replanned = new LiveAcquisitionExecutor(
                stale,
                new LiveAcquisitionOptions { MaximumReplans = 1 },
                _ => Task.FromResult<AcquisitionPlanningResult?>(vendorReplacement))
            .ExecuteAsync(marketPlan)
            .GetAwaiter()
            .GetResult();
        Require(replanned.Status == LiveAcquisitionStatus.Completed && stale.VendorPurchaseCalls == 1,
            "a stale market listing must trigger a global replan and permit vendor recovery");

        return assertions;
    }

    private static AcquisitionPlanningResult Ready(AcquisitionPlan plan)
        => new()
        {
            Status = AcquisitionPlanStatus.Ready,
            SelectedPlan = plan,
        };

    private static AcquisitionTransaction Transaction(
        uint itemId,
        AcquisitionSourceKind source,
        uint worldId,
        string worldName,
        long gil,
        int quantity = 1,
        string? sourceId = null,
        bool isHq = false)
        => new()
        {
            ItemId = itemId,
            ItemName = $"Item {itemId}",
            SourceKind = source,
            SourceId = sourceId ?? itemId.ToString(),
            SourceName = source == AcquisitionSourceKind.Market ? "Marketboard" : "Vendor",
            WorldId = worldId,
            WorldName = worldName,
            Quantity = quantity,
            IsHq = isHq,
            GilCost = gil,
        };

    private sealed class FakeEnvironment : ILiveAcquisitionEnvironment
    {
        public uint CurrentWorldId => 10;
        public string CurrentWorldName => "World A";
        public bool IsLifestreamAvailable => true;
        public bool IsVNavmeshAvailable => true;
        public bool IsMarketAutomationAvailable => true;
        public bool IsVendorAutomationAvailable => true;
        public bool IsInDuty => false;
        public bool IsInNonCrossWorldParty => false;
        public IReadOnlyList<LiveMarketListing> Listings { get; init; } = Array.Empty<LiveMarketListing>();
        public int? MarketQuantityOverride { get; init; }
        public uint? MarketItemIdOverride { get; init; }
        public long? MarketListingIdOverride { get; init; }
        public bool? MarketIsHqOverride { get; init; }
        public bool MarketVerifiedOverride { get; init; } = true;
        public long? MarketGilBefore { get; init; }
        public long? MarketGilAfter { get; init; }
        public long? MarketGilReported { get; init; }
        public long? VendorGilBefore { get; init; }
        public long? VendorGilAfter { get; init; }
        public long? VendorGilReported { get; init; }
        public IReadOnlyDictionary<uint, long> VendorCurrencyBefore { get; init; }
            = new Dictionary<uint, long>();
        public IReadOnlyDictionary<uint, long> VendorCurrencyAfter { get; init; }
            = new Dictionary<uint, long>();
        public IReadOnlyDictionary<uint, VendorCurrencyAvailabilitySource> VendorCurrencySources { get; init; }
            = new Dictionary<uint, VendorCurrencyAvailabilitySource>();
        public IReadOnlyDictionary<uint, VendorCurrencyAvailabilitySource> VendorCurrencySourcesAfter { get; init; }
            = new Dictionary<uint, VendorCurrencyAvailabilitySource>();
        public IReadOnlyDictionary<uint, long> VendorCurrencySpendPerCall { get; init; }
            = new Dictionary<uint, long>();
        public bool ListingsFresh { get; init; } = true;
        public int StaleOnListingsRequest { get; init; }
        public IReadOnlyList<LiveMarketListing>? ReplanListings { get; set; }
        public int ListingsRequestCalls { get; private set; }
        public bool ThrowCancellationAfterSubmit { get; init; }
        public int MarketPurchaseCalls { get; private set; }
        public int VendorPurchaseCalls { get; private set; }
        public int CleanupCalls { get; private set; }
        public AcquisitionWorldRoute? LastMarketRoute { get; private set; }
        public HashSet<long> PurchasedListingIds { get; } = new();

        public bool CanVisitWorld(uint worldId) => true;
        public bool IsGatewayAttuned(uint gatewayId) => true;
        public long GetGatewayTeleportCost(uint gatewayId) => 0;
        public string ResolveWorldName(uint worldId) => worldId == 10 ? "World A" : $"World {worldId}";

        public LiveAcquisitionPreconditionResult ValidatePlan(AcquisitionPlan plan, LiveAcquisitionOptions options)
            => new(true);

        public Task<bool> TravelToWorldAsync(AcquisitionWorldRoute route, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> NavigateToMarketBoardAsync(AcquisitionWorldRoute route, TimeSpan timeout, CancellationToken cancellationToken)
        {
            LastMarketRoute = route;
            return Task.FromResult(true);
        }

        public Task<LiveMarketListingsResponse> RequestLiveListingsAsync(uint itemId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ListingsRequestCalls++;
            var isFresh = ListingsFresh && ListingsRequestCalls != StaleOnListingsRequest;
            var available = ReplanListings ?? Listings;
            return Task.FromResult(new LiveMarketListingsResponse(
                isFresh,
                available.Where(listing => listing.ItemId == itemId && !PurchasedListingIds.Contains(listing.ListingId)).ToArray()));
        }

        public Task<LiveMarketPurchaseResult> PurchaseMarketListingAsync(LiveMarketListing listing, TimeSpan timeout, CancellationToken cancellationToken)
        {
            MarketPurchaseCalls++;
            PurchasedListingIds.Add(listing.ListingId);
            if (ThrowCancellationAfterSubmit)
                throw new OperationCanceledException("simulated cancellation after submit");
            return Task.FromResult(new LiveMarketPurchaseResult(
                true,
                MarketVerifiedOverride,
                MarketItemIdOverride ?? listing.ItemId,
                MarketListingIdOverride ?? listing.ListingId,
                MarketQuantityOverride ?? listing.Quantity,
                MarketGilReported ?? listing.TotalGil,
                "verified",
                true,
                MarketIsHqOverride ?? listing.IsHq,
                MarketGilBefore,
                MarketGilAfter));
        }

        public Task<LiveVendorPurchaseResult> PurchaseVendorAsync(AcquisitionTransaction transaction, TimeSpan timeout, CancellationToken cancellationToken)
        {
            VendorPurchaseCalls++;
            var beforeSnapshot = VendorCurrencyBefore.ToDictionary(pair => pair.Key, pair => pair.Value);
            var afterSnapshot = VendorCurrencySpendPerCall.Count == 0
                ? VendorCurrencyAfter.ToDictionary(pair => pair.Key, pair => pair.Value)
                : beforeSnapshot.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value - VendorCurrencySpendPerCall.GetValueOrDefault(pair.Key));
            var currencySpent = VendorCurrencyBefore
                .Keys
                .Intersect(afterSnapshot.Keys)
                .Where(currencyId => currencyId != VendorShopResolver.GilCurrencyItemId)
                .ToDictionary(
                    currencyId => currencyId,
                    currencyId => beforeSnapshot[currencyId] - afterSnapshot[currencyId]);
            var gilSpent = beforeSnapshot.TryGetValue(VendorShopResolver.GilCurrencyItemId, out var gilBefore)
                && afterSnapshot.TryGetValue(VendorShopResolver.GilCurrencyItemId, out var gilAfter)
                ? gilBefore - gilAfter
                : VendorGilReported ?? transaction.GilCost;
            var primaryOutputQuantity = transaction.PrimaryOutputQuantity > 0
                ? transaction.PrimaryOutputQuantity
                : transaction.Outputs
                    .Where(output => output is not null && output.ItemId == transaction.ItemId && output.Quantity > 0)
                    .Select(output => output.Quantity)
                    .DefaultIfEmpty(1)
                    .Sum();
            var purchaseUnits = transaction.Outputs is { Count: > 0 }
                ? (transaction.Quantity - 1) / primaryOutputQuantity + 1
                : 1;
            var outputQuantities = transaction.Outputs
                .Where(output => output is not null && output.ItemId != 0 && output.Quantity > 0)
                .GroupBy(output => output.ItemId)
                .ToDictionary(group => group.Key, group => group.Sum(output => output.Quantity) * purchaseUnits);
            if (VendorCurrencySpendPerCall.Count > 0
                && VendorCurrencyBefore is IDictionary<uint, long> mutableBefore)
            {
                mutableBefore.Clear();
                foreach (var balance in afterSnapshot)
                    mutableBefore[balance.Key] = balance.Value;
            }
            return Task.FromResult(new LiveVendorPurchaseResult(
                true,
                true,
                transaction.ItemId,
                transaction.Quantity,
                currencySpent,
                gilSpent,
                "verified",
                true,
                transaction.IsHq,
                VendorGilBefore ?? (beforeSnapshot.TryGetValue(VendorShopResolver.GilCurrencyItemId, out var before) ? before : null),
                VendorGilAfter ?? (afterSnapshot.TryGetValue(VendorShopResolver.GilCurrencyItemId, out var after) ? after : null))
            {
                CurrencyBalancesBefore = beforeSnapshot,
                CurrencyBalancesAfter = afterSnapshot,
                CurrencyBalanceSources = VendorCurrencySources,
                CurrencyBalanceSourcesAfter = VendorCurrencySourcesAfter,
                OutputQuantities = outputQuantities,
            });
        }

        public Task CloseMarketBoardAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CleanupAsync(CancellationToken cancellationToken)
        {
            CleanupCalls++;
            return Task.CompletedTask;
        }
    }
}
