using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Dalamud.Utility;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatheringType = GatherBuddy.Enums.GatheringType;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting.Acquisition;

/// <summary>
/// Builds the immutable game-state snapshot consumed by <see cref="AcquisitionPlanner"/>.
/// The planner must never inspect inventory, vendor unlocks, or network caches itself.
/// </summary>
public static unsafe class AcquisitionPlanningInputBuilder
{
    private static readonly TimeSpan UniversalisCacheTtl = TimeSpan.FromMinutes(15);

    public sealed class BuildResult
    {
        public AcquisitionPlanningInput Input { get; init; } = new();
        public bool IsLoading { get; init; }
        public string LoadingReason { get; init; } = string.Empty;
        public string ErrorReason { get; init; } = string.Empty;

        public bool IsReady
            => !IsLoading && string.IsNullOrWhiteSpace(ErrorReason);
    }

    public static BuildResult Build(CraftingExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var currentWorldId = Dalamud.Objects.LocalPlayer?.CurrentWorld.RowId ?? 0u;
        var settings = plan.PlanningSnapshot.GetAcquisitionSettings();
        var dependencies = BuildDependencies(plan);
        var requiresAcquisition = dependencies.Any(dependency =>
            !dependency.IsFinalOutput
            && dependency.SelectedPath?.Capability.Status != AcquisitionCapabilityStatus.Usable);

        if (!settings.AutoPurchaseBlockedDependencies || !requiresAcquisition)
        {
            return new BuildResult
            {
                Input = new AcquisitionPlanningInput
                {
                    Dependencies = dependencies,
                    CurrentWorldId = currentWorldId,
                    GilBalance = ReadGilBalance(),
                    CurrencyBalances = new Dictionary<uint, long>(),
                },
                ErrorReason = !settings.AutoPurchaseBlockedDependencies && requiresAcquisition
                    ? BuildCapabilityError(dependencies)
                    : string.Empty,
            };
        }

        if (currentWorldId == 0 && requiresAcquisition)
        {
            return new BuildResult
            {
                Input = new AcquisitionPlanningInput
                {
                    Dependencies = dependencies,
                    CurrentWorldId = currentWorldId,
                },
                ErrorReason = "Current world is unavailable; automatic acquisition cannot be planned safely.",
            };
        }

        VendorShopResolver.InitializeAsync();
        var vendorOffers = BuildVendorOffers(dependencies);
        var market = BuildMarketListings(dependencies, settings, currentWorldId, out var loadingReason);
        if ((!VendorShopResolver.IsInitialized || VendorShopResolver.IsInitializing)
            && string.IsNullOrWhiteSpace(loadingReason))
        {
            loadingReason = "Loading vendor shop and unlock data.";
        }
        else if (VendorShopResolver.IsInitialized
            && (!VendorNpcLocationCache.IsInitialized || VendorNpcLocationCache.IsInitializing))
        {
            VendorNpcLocationCache.InitializeAsync(VendorShopResolver.GetAllVendorNpcIds());
            if (string.IsNullOrWhiteSpace(loadingReason))
                loadingReason = "Loading vendor route data.";
        }
        var balances = BuildCurrencyBalances(vendorOffers);
        var gilBalance = ReadGilBalance();

        var input = new AcquisitionPlanningInput
        {
            Dependencies = dependencies,
            VendorOffers = vendorOffers,
            MarketListings = market,
            CurrencyBalances = balances,
            GilBalance = gilBalance,
            CurrentWorldId = currentWorldId,
        };

        return new BuildResult
        {
            Input = input,
            IsLoading = !string.IsNullOrWhiteSpace(loadingReason),
            LoadingReason = loadingReason,
            ErrorReason = string.Empty,
        };
    }

    private static string BuildCapabilityError(IReadOnlyList<AcquisitionDependency> dependencies)
    {
        var blocked = dependencies
            .Where(dependency => !dependency.IsFinalOutput
                && dependency.SelectedPath?.Capability.Status != AcquisitionCapabilityStatus.Usable)
            .Select(dependency =>
            {
                var reason = dependency.SelectedPath?.Capability.Reason;
                return string.IsNullOrWhiteSpace(reason)
                    ? $"No usable acquisition path is available for {dependency.ItemName}."
                    : $"{dependency.ItemName}: {reason}";
            })
            .ToArray();
        return blocked.Length == 0
            ? string.Empty
            : "A required precraft cannot be acquired or crafted:\n" + string.Join("\n", blocked);
    }

    private static List<AcquisitionDependency> BuildDependencies(CraftingExecutionPlan plan)
    {
        var dependencyQuantities = new Dictionary<uint, int>();
        var finalOutputItemIds = plan.OriginalRecipesView
            .Select(item => RecipeManager.GetRecipe(item.RecipeId)?.ItemResult.RowId ?? 0u)
            .Where(itemId => itemId != 0)
            .ToHashSet();
        var intermediateDemandItemIds = plan.PrecraftsView.Keys
            .Concat(plan.MaterialsView.Keys)
            .ToHashSet();
        foreach (var (itemId, quantity) in plan.PrecraftsView)
            AddQuantity(dependencyQuantities, itemId, quantity);
        foreach (var (itemId, quantity) in plan.MaterialsView)
            AddQuantity(dependencyQuantities, itemId, quantity);

        var result = new List<AcquisitionDependency>(dependencyQuantities.Count);
        foreach (var (itemId, quantity) in dependencyQuantities.OrderBy(pair => pair.Key))
        {
            if (itemId == 0 || quantity <= 0)
                continue;

            var (inventoryNq, inventoryHq) = CraftingInventoryCounter.GetInventorySplitCounts(itemId);
            var demand = plan.IngredientDemandsView.GetValueOrDefault(itemId);
            var (missingQuantity, missingHq, missingNq) = ComputeMissingQuantities(
                quantity,
                demand.RequiredHQ,
                demand.RequiredNQ,
                inventoryNq,
                inventoryHq);
            if (missingQuantity == 0)
                continue;

            var itemName = ResolveItemName(itemId);
            var selectedRecipe = plan.PlanningSnapshot.ResolveRecipeForItem(itemId);
            var selectedPath = ResolvePath(itemId, selectedRecipe);
            result.Add(new AcquisitionDependency
            {
                ItemId = itemId,
                ItemName = itemName,
                RequiredQuantity = missingQuantity,
                RequiredHqQuantity = Math.Clamp(missingHq, 0, missingQuantity),
                RequiredNqQuantity = Math.Clamp(missingNq, 0, missingQuantity),
                IsIntermediateDemand = intermediateDemandItemIds.Contains(itemId),
                IsFinalOutput = finalOutputItemIds.Contains(itemId)
                    && !intermediateDemandItemIds.Contains(itemId),
                SelectedPath = selectedPath,
            });
        }

        return result;
    }

    internal static (int Total, int HQ, int NQ) ComputeMissingQuantities(
        int requiredQuantity,
        int requiredHqQuantity,
        int requiredNqQuantity,
        int inventoryNq,
        int inventoryHq)
    {
        var totalMissing = Math.Max(
            0,
            Math.Max(0, requiredQuantity) - Math.Max(0, inventoryNq) - Math.Max(0, inventoryHq));
        var missingHq = Math.Max(0, Math.Max(0, requiredHqQuantity) - Math.Max(0, inventoryHq));
        var missingNq = Math.Max(0, Math.Max(0, requiredNqQuantity) - Math.Max(0, inventoryNq));
        return (Math.Max(totalMissing, missingHq + missingNq), missingHq, missingNq);
    }

    private static AcquisitionPath? ResolvePath(uint itemId, Recipe? selectedRecipe)
    {
        if (selectedRecipe.HasValue)
        {
            var recipe = selectedRecipe.Value;
            var jobId = recipe.CraftType.RowId + 8;
            var requiredLevel = recipe.RecipeLevelTable.Value.ClassJobLevel;
            var actualLevel = ReadJobLevel(jobId);
            var gearsetKnown = TryHasGearset(jobId, out var gearsetAvailable);
            var capability = AcquisitionCapabilityResolver.Resolve(
                AcquisitionPathKind.Craft,
                new AcquisitionCapabilityEvidence
                {
                    JobId = jobId,
                    RequiredLevel = requiredLevel,
                    ActualLevel = actualLevel,
                    GearsetKnown = gearsetKnown,
                    GearsetAvailable = gearsetAvailable,
                    UnlockKnown = true,
                    UnlockAvailable = true,
                    RouteKnown = true,
                    RouteAvailable = true,
                    AdditionalEvidence = new Dictionary<string, string>
                    {
                        ["recipeId"] = recipe.RowId.ToString(),
                    },
                });
            return new AcquisitionPath
            {
                Kind = AcquisitionPathKind.Craft,
                RecipeId = recipe.RowId,
                JobId = jobId,
                JobName = ResolveJobName(jobId),
                Capability = capability,
            };
        }

        if (GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var gatherable))
            return ResolveGatherPath(itemId, gatherable, AcquisitionPathKind.Gather);

        if (GatherBuddy.GameData.Fishes.TryGetValue(itemId, out var fish))
        {
            // Fish folklore metadata is present, but the client-side folklore
            // bitfield is not exposed through a stable public API. Treat such
            // a route as unknown instead of claiming it is usable.
            var folkloreRequired = !string.IsNullOrWhiteSpace(fish.Folklore);
            var fishLog = GatherBuddy.FishLog;
            var unlockKnown = fishLog != null;
            var unlockAvailable = !fish.InLog
                || unlockKnown && fishLog!.IsUnlocked(fish);
            var capability = AcquisitionCapabilityResolver.Resolve(
                AcquisitionPathKind.Fish,
                new AcquisitionCapabilityEvidence
                {
                    JobId = 18,
                    RequiredLevel = 1,
                    ActualLevel = ReadJobLevel(18),
                    GearsetKnown = TryHasGearset(18, out var gearsetAvailable),
                    GearsetAvailable = gearsetAvailable,
                    UnlockKnown = unlockKnown,
                    UnlockAvailable = unlockAvailable,
                    FolkloreRequired = folkloreRequired,
                    FolkloreKnown = !folkloreRequired,
                    FolkloreUnlocked = !folkloreRequired,
                    RouteKnown = fish.Locations.Count > 0,
                    RouteAvailable = fish.Locations.Count > 0,
                });
            return new AcquisitionPath
            {
                Kind = AcquisitionPathKind.Fish,
                JobId = 18,
                JobName = ResolveJobName(18),
                Capability = capability,
            };
        }

        return new AcquisitionPath
        {
            Kind = AcquisitionPathKind.Unknown,
            Capability = AcquisitionCapability.UnusablePath(
                AcquisitionPathKind.Unknown,
                "No craft, gather, or fish path is known for this dependency."),
        };
    }

    private static AcquisitionPath ResolveGatherPath(uint itemId, Gatherable gatherable, AcquisitionPathKind kind)
    {
        var jobId = gatherable.GatheringType.ToGroup() switch
        {
            GatheringType.Miner => 16u,
            GatheringType.Botanist => 17u,
            _ => 0u,
        };
        var requiredLevel = gatherable.Level;
        var actualLevel = ReadJobLevel(jobId);
        var gearsetAvailable = false;
        var gearsetKnown = jobId != 0 && TryHasGearset(jobId, out gearsetAvailable);
        var folkloreRequired = gatherable.NodeList.Any(node =>
            node.FolkloreId != 0 || !string.IsNullOrWhiteSpace(node.Folklore));
        // Gatherable unlock state is not exposed by a stable client API. A
        // normal node is safe to treat as unlocked; a gated/folklore node is
        // explicitly unknown so acquisition can fail closed or choose a
        // purchase source instead of starting an impossible gather route.
        var unlockKnown = !folkloreRequired;
        var capability = AcquisitionCapabilityResolver.Resolve(
            kind,
            new AcquisitionCapabilityEvidence
            {
                JobId = jobId,
                RequiredLevel = requiredLevel,
                ActualLevel = actualLevel,
                GearsetKnown = gearsetKnown,
                GearsetAvailable = gearsetAvailable,
                UnlockKnown = unlockKnown,
                UnlockAvailable = unlockKnown,
                FolkloreRequired = folkloreRequired,
                FolkloreKnown = !folkloreRequired,
                FolkloreUnlocked = !folkloreRequired,
                RouteKnown = gatherable.Locations.Count > 0,
                RouteAvailable = gatherable.Locations.Count > 0,
            });
        return new AcquisitionPath
        {
            Kind = kind,
            JobId = jobId,
            JobName = ResolveJobName(jobId),
            Capability = capability,
        };
    }

    private static List<AcquisitionVendorOffer> BuildVendorOffers(
        IReadOnlyList<AcquisitionDependency> dependencies)
    {
        if (!VendorShopResolver.IsInitialized || VendorShopResolver.IsInitializing)
            return new List<AcquisitionVendorOffer>();

        var itemIds = dependencies.Select(dependency => dependency.ItemId).ToHashSet();
        var entries = VendorShopResolver.GilShopEntries
            .Concat(VendorShopResolver.SpecialShopEntries)
            .Concat(VendorShopResolver.GcShopEntries)
            .Where(entry => itemIds.Contains(entry.ItemId)
                || entry.ReceivedItems.Any(output => output is not null
                    && itemIds.Contains(output.ItemId)))
            .ToList();
        var result = new List<AcquisitionVendorOffer>();

        foreach (var entry in entries)
        {
            foreach (var vendor in entry.Npcs)
            {
                var offerId = $"{entry.TransactionSignature}:{VendorPreferenceHelper.GetRouteKey(vendor)}";
                var availability = VendorAvailabilityResolver.Resolve(entry, vendor);
                if (!availability.IsAvailable)
                {
                    result.Add(new AcquisitionVendorOffer
                    {
                        ItemId = entry.ItemId,
                        OfferId = offerId,
                        VendorName = vendor.Name,
                        ReceiveQuantity = checked((int)entry.ReceivedQuantity),
                        Outputs = entry.ReceivedItems
                            .Select(output => new AcquisitionVendorOutput
                            {
                                ItemId = output?.ItemId ?? 0,
                                Quantity = checked((int)(output?.Quantity ?? 0)),
                            })
                            .ToArray(),
                        IsAvailable = false,
                        UnavailableReason = availability.Reason,
                    });
                    continue;
                }
                if (!VendorPurchaseManager.IsPurchaseSupported(entry, vendor))
                {
                    result.Add(new AcquisitionVendorOffer
                    {
                        ItemId = entry.ItemId,
                        OfferId = offerId,
                        VendorName = vendor.Name,
                        ReceiveQuantity = checked((int)entry.ReceivedQuantity),
                        Outputs = entry.ReceivedItems
                            .Select(output => new AcquisitionVendorOutput
                            {
                                ItemId = output?.ItemId ?? 0,
                                Quantity = checked((int)(output?.Quantity ?? 0)),
                            })
                            .ToArray(),
                        IsAvailable = false,
                        UnavailableReason = "Vendor automation does not support this offer.",
                    });
                    continue;
                }
                var location = VendorNpcLocationCache.TryGetFirstLocation(vendor.NpcId);
                if (location == null)
                {
                    result.Add(new AcquisitionVendorOffer
                    {
                        ItemId = entry.ItemId,
                        OfferId = offerId,
                        VendorName = vendor.Name,
                        ReceiveQuantity = checked((int)entry.ReceivedQuantity),
                        Outputs = entry.ReceivedItems
                            .Select(output => new AcquisitionVendorOutput
                            {
                                ItemId = output?.ItemId ?? 0,
                                Quantity = checked((int)(output?.Quantity ?? 0)),
                            })
                            .ToArray(),
                        IsAvailable = false,
                        UnavailableReason = $"No route to vendor {vendor.Name} is available.",
                    });
                    continue;
                }

                var costs = entry.CurrencyCosts
                    .Where(cost => cost.Amount > 0)
                    .Select(cost => new AcquisitionCurrencyCost
                    {
                        CurrencyId = cost.CurrencyItemId == VendorShopResolver.GilCurrencyItemId
                            ? AcquisitionCurrency.GilId
                            : cost.CurrencyItemId,
                        IconId = ResolveItemIcon(cost.CurrencyItemId),
                        CurrencyName = cost.CurrencyItemId == VendorShopResolver.GilCurrencyItemId
                            ? "Gil"
                            : cost.CurrencyName,
                        Amount = cost.Amount,
                        IsGil = cost.CurrencyItemId == VendorShopResolver.GilCurrencyItemId,
                        IsSpecialCurrency = cost.CurrencyItemId != VendorShopResolver.GilCurrencyItemId,
                        Group = cost.Group,
                    })
                    .ToArray();
                if (costs.Length == 0 || entry.ReceivedQuantity == 0)
                    continue;

                result.Add(new AcquisitionVendorOffer
                {
                    ItemId = entry.ItemId,
                    OfferId = offerId,
                    VendorName = vendor.Name,
                    Location = $"{location.NpcName} ({location.TerritoryId})",
                    ReceiveQuantity = checked((int)entry.ReceivedQuantity),
                    Outputs = entry.ReceivedItems
                        .Select(output => new AcquisitionVendorOutput
                        {
                            ItemId = output?.ItemId ?? 0,
                            Quantity = checked((int)(output?.Quantity ?? 0)),
                        })
                        .ToArray(),
                    IsHq = false,
                    IsAvailable = true,
                    MaximumPurchases = null,
                    Costs = costs,
                });
            }
        }

        return result;
    }

    private static List<AcquisitionMarketListing> BuildMarketListings(
        IReadOnlyList<AcquisitionDependency> dependencies,
        AcquisitionPlanningSettings settings,
        uint currentWorldId,
        out string loadingReason)
    {
        loadingReason = string.Empty;
        var service = GatherBuddy.MarketboardService;
        if (service == null)
        {
            loadingReason = "Marketboard service is not initialized.";
            return new List<AcquisitionMarketListing>();
        }

        var scope = settings.CurrentWorldOnly
            ? service.GetCurrentWorld()
            : service.GetDataCenter();
        var result = new List<AcquisitionMarketListing>();
        foreach (var dependency in dependencies)
        {
            var cached = service.GetCached(dependency.ItemId, scope);
            var fetchedAt = service.GetFetchTime(dependency.ItemId, scope);
            if (cached == null || DateTime.UtcNow - fetchedAt > UniversalisCacheTtl)
            {
                service.QueueLookup(dependency.ItemId, dependency.ItemName, ResolveItemIcon(dependency.ItemId), scope);
                if (!service.HasError(dependency.ItemId, scope))
                    loadingReason = $"Loading Universalis data for {dependency.ItemName}.";
                continue;
            }

            foreach (var listing in cached.Listings)
            {
                if (listing.Quantity <= 0 || listing.PricePerUnit < 0 || listing.ListingId > long.MaxValue
                    || listing.IsMannequin == true || listing.IsSellingAsSet == true)
                    continue;
                if (settings.CurrentWorldOnly && listing.WorldId != currentWorldId)
                    continue;
                result.Add(new AcquisitionMarketListing
                {
                    ItemId = dependency.ItemId,
                    ListingId = checked((long)listing.ListingId),
                    WorldId = listing.WorldId,
                    WorldName = listing.WorldName,
                    Quantity = listing.Quantity,
                    PricePerUnit = listing.PricePerUnit,
                    TotalTax = checked((int)Math.Max(0, listing.TotalTax)),
                    IsHq = listing.IsHq,
                    IsAvailable = true,
                });
            }
        }

        return result;
    }

    private static Dictionary<uint, long> BuildCurrencyBalances(
        IReadOnlyList<AcquisitionVendorOffer> offers)
    {
        var result = new Dictionary<uint, long>();
        foreach (var cost in offers
                     .SelectMany(offer => offer.Costs)
                     .Where(cost => !cost.IsGil && cost.CurrencyId != 0)
                     .GroupBy(cost => cost.CurrencyId)
                     .Select(group => group.First()))
        {
            var availability = VendorCurrencyAvailabilityResolver.Resolve(
                cost.Group,
                cost.CurrencyId,
                cost.CurrencyName);
            if (availability.Source is VendorCurrencyAvailabilitySource.Unknown
                or VendorCurrencyAvailabilitySource.InventoryItemCount)
            {
                // A generic inventory count is not an authoritative currency
                // wallet. Omit it so the planner reports the balance as unknown
                // instead of risking an unaffordable purchase plan.
                continue;
            }

            result[cost.CurrencyId] = availability.AvailableAmount;
        }
        return result;
    }

    private static bool TryHasGearset(uint jobId, out bool available)
    {
        available = false;
        if (jobId == 0)
            return false;
        var module = RaptureGearsetModule.Instance();
        if (module == null)
            return false;
        available = GearsetStatsReader.TryResolveExistingGearsetIndex(module, jobId, out _);
        return true;
    }

    private static int ReadJobLevel(uint jobId)
    {
        if (jobId == 0)
            return 0;
        var playerState = PlayerState.Instance();
        return playerState == null ? 0 : playerState->ClassJobLevels[(int)jobId];
    }

    private static long? ReadGilBalance()
    {
        var availability = VendorCurrencyAvailabilityResolver.Resolve(
            VendorCurrencyGroup.Gil,
            VendorShopResolver.GilCurrencyItemId,
            "Gil");
        return availability.Source is VendorCurrencyAvailabilitySource.Unknown
            or VendorCurrencyAvailabilitySource.InventoryItemCount
            ? null
            : availability.AvailableAmount;
    }

    private static string ResolveItemName(uint itemId)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<Item>();
        return sheet?.TryGetRow(itemId, out var item) == true
            ? item.Name.ExtractText()
            : $"Item {itemId}";
    }

    private static uint ResolveItemIcon(uint itemId)
    {
        if (itemId == VendorShopResolver.GilCurrencyItemId)
            return 0;
        var sheet = Dalamud.GameData.GetExcelSheet<Item>();
        return sheet?.TryGetRow(itemId, out var item) == true ? (uint)item.Icon : 0;
    }

    private static void AddQuantity(Dictionary<uint, int> quantities, uint itemId, int quantity)
    {
        if (itemId == 0 || quantity <= 0)
            return;
        quantities[itemId] = checked(quantities.GetValueOrDefault(itemId) + quantity);
    }

    private static string ResolveJobName(uint jobId)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<ClassJob>();
        return sheet?.TryGetRow(jobId, out var job) == true
            ? job.Name.ExtractText()
            : jobId == 0 ? string.Empty : $"Job {jobId}";
    }
}
