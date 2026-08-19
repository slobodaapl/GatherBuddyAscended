using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
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
        var currentTerritoryId = (uint)Dalamud.ClientState.TerritoryType;
        var settings = plan.PlanningSnapshot.GetAcquisitionSettings();
        var boundaryPlan = plan.CreateAcquisitionBoundaryPlan(IsCraftPrecraftUsable);
        var dependencies = BuildDependencies(plan, boundaryPlan);
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
                    CurrentTerritoryId = currentTerritoryId,
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
                    CurrentTerritoryId = currentTerritoryId,
                },
                ErrorReason = "Current world is unavailable; automatic acquisition cannot be planned safely.",
            };
        }

        return BuildAcquisitionInput(dependencies, settings, currentWorldId, currentTerritoryId);
    }

    /// <summary>
    /// Builds a live acquisition snapshot for persisted marketplace targets.
    /// These targets have no craft/gather path by design; the planner chooses
    /// among currently available vendor and market sources.
    /// </summary>
    public static BuildResult BuildMarketplaceTargets(
        IReadOnlyList<(uint ItemId, string ItemName, uint IconId, int TargetQuantity)> targets,
        AcquisitionPlanningSettings settings)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(settings);

        var dependencies = new List<AcquisitionDependency>(targets.Count);
        foreach (var target in targets)
        {
            if (target.ItemId == 0 || target.TargetQuantity <= 0)
                continue;

            var (inventoryNq, inventoryHq) = CraftingInventoryCounter.GetInventorySplitCounts(target.ItemId);
            var inventory = settings.PreferHQ
                ? Math.Max(0, inventoryHq)
                : Math.Max(0, inventoryNq + inventoryHq);
            var missing = Math.Max(0, target.TargetQuantity - inventory);
            if (missing == 0)
                continue;

            dependencies.Add(new AcquisitionDependency
            {
                ItemId = target.ItemId,
                ItemName = string.IsNullOrWhiteSpace(target.ItemName)
                    ? ResolveItemName(target.ItemId)
                    : target.ItemName,
                RequiredQuantity = missing,
                RequiredHqQuantity = settings.PreferHQ ? missing : 0,
                RequiredNqQuantity = 0,
                IsIntermediateDemand = true,
                SelectedPath = null,
            });
        }

        return BuildAcquisitionInput(
            dependencies,
            settings,
            Dalamud.Objects.LocalPlayer?.CurrentWorld.RowId ?? 0u,
            (uint)Dalamud.ClientState.TerritoryType);
    }

    private static BuildResult BuildAcquisitionInput(
        IReadOnlyList<AcquisitionDependency> dependencies,
        AcquisitionPlanningSettings settings,
        uint currentWorldId,
        uint currentTerritoryId)
    {
        var requiresAcquisition = dependencies.Any(dependency => dependency.RequiredQuantity > 0);
        if (!requiresAcquisition)
        {
            return new BuildResult
            {
                Input = new AcquisitionPlanningInput
                {
                    Dependencies = dependencies,
                    CurrentWorldId = currentWorldId,
                    CurrentTerritoryId = currentTerritoryId,
                    GilBalance = ReadGilBalance(),
                    CurrencyBalances = new Dictionary<uint, long>(),
                },
            };
        }

        if (currentWorldId == 0)
        {
            return new BuildResult
            {
                Input = new AcquisitionPlanningInput
                {
                    Dependencies = dependencies,
                    CurrentWorldId = currentWorldId,
                    CurrentTerritoryId = currentTerritoryId,
                },
                ErrorReason = "Current world is unavailable; automatic acquisition cannot be planned safely.",
            };
        }

        VendorShopResolver.InitializeAsync();
        var vendorOffers = BuildVendorOffers(dependencies, currentTerritoryId);
        var market = BuildMarketListings(dependencies, settings, currentWorldId, out var loadingReason);
        if (VendorShopResolver.IsInitialized
            && !VendorShopResolver.TryGetCurrentGrandCompanyId(out _)
            && dependencies.Any(dependency => VendorShopResolver.GcShopEntries.Any(entry =>
                entry.ItemId == dependency.ItemId
                || entry.ReceivedItems.Any(output => output is not null && output.ItemId == dependency.ItemId))))
        {
            loadingReason = string.IsNullOrWhiteSpace(loadingReason)
                ? "Loading current Grand Company state for seal purchases."
                : loadingReason;
        }
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
            CurrentTerritoryId = currentTerritoryId,
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

    private static List<AcquisitionDependency> BuildDependencies(
        CraftingExecutionPlan plan,
        CraftingListPlan boundaryPlan)
    {
        var dependencyQuantities = new Dictionary<uint, int>();
        var finalOutputItemIds = plan.OriginalRecipesView
            .Select(item => RecipeManager.GetRecipe(item.RecipeId)?.ItemResult.RowId ?? 0u)
            .Where(itemId => itemId != 0)
            .ToHashSet();
        var intermediateDemandItemIds = boundaryPlan.Precrafts.Keys
            .Concat(boundaryPlan.Materials.Keys)
            .ToHashSet();
        foreach (var (itemId, quantity) in boundaryPlan.Precrafts)
            AddQuantity(dependencyQuantities, itemId, quantity);
        foreach (var (itemId, quantity) in boundaryPlan.Materials)
            AddQuantity(dependencyQuantities, itemId, quantity);

        var result = new List<AcquisitionDependency>(dependencyQuantities.Count);
        foreach (var (itemId, quantity) in dependencyQuantities.OrderBy(pair => pair.Key))
        {
            if (itemId == 0 || quantity <= 0)
                continue;

            var (inventoryNq, inventoryHq) = CraftingInventoryCounter.GetInventorySplitCounts(itemId);
            var demand = boundaryPlan.IngredientDemands.GetValueOrDefault(itemId);
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

    private static bool IsCraftPrecraftUsable(Recipe recipe)
        => ResolvePath(recipe.ItemResult.RowId, recipe)?.Capability.Status == AcquisitionCapabilityStatus.Usable;

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

    internal static AcquisitionPath? ResolvePath(uint itemId, Recipe? selectedRecipe)
    {
        if (selectedRecipe.HasValue)
        {
            var recipe = selectedRecipe.Value;
            var jobId = recipe.CraftType.RowId + 8;
            var requiredLevel = recipe.MinimumJobLevel();
            var actualLevel = ReadJobLevel(jobId);
            var gearsetKnown = TryHasGearset(jobId, out var gearsetAvailable);
            var recipeUnlocked = recipe.Number == 0 || Dalamud.UnlockState.IsRecipeUnlocked(recipe);
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
                    UnlockAvailable = recipeUnlocked,
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
            return ResolveFishPath(fish);

        if (ResolveReductionPath(itemId) is { } reductionPath)
            return reductionPath;

        return new AcquisitionPath
        {
            Kind = AcquisitionPathKind.Unknown,
            Capability = AcquisitionCapability.UnusablePath(
                AcquisitionPathKind.Unknown,
                "No craft, gather, or fish path is known for this dependency."),
        };
    }

    private static AcquisitionPath ResolveFishPath(Fish fish)
    {
        var folkloreRequired = !string.IsNullOrWhiteSpace(fish.Folklore);
        var folkloreDivision = fish.FolkloreUnlockId == 0
            ? null
            : Dalamud.GameData.GetExcelSheet<NotebookDivision>().GetRowOrDefault(fish.FolkloreUnlockId);
        var folkloreKnown = !folkloreRequired || folkloreDivision is not null;
        var folkloreUnlocked = !folkloreRequired
            || folkloreDivision is { } division && Dalamud.UnlockState.IsNotebookDivisionUnlocked(division);
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
                FolkloreKnown = folkloreKnown,
                FolkloreUnlocked = folkloreUnlocked,
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

    private static AcquisitionPath? ResolveReductionPath(uint outputItemId)
    {
        var sourceItemIds = AetherialReductionSourceResolver.GetSourceItemIds(outputItemId);
        if (sourceItemIds.Count == 0)
            return null;

        var sourcePaths = sourceItemIds
            .Select(sourceItemId => (SourceItemId: sourceItemId, Path: ResolveDirectGatherPath(sourceItemId)))
            .Where(candidate => candidate.Path != null)
            .Select(candidate => (candidate.SourceItemId, Path: candidate.Path!))
            .ToArray();
        if (sourcePaths.Length == 0)
            return new AcquisitionPath
            {
                Kind = AcquisitionPathKind.Reduction,
                AlternativeSourceItemIds = sourceItemIds.ToArray(),
                Capability = AcquisitionCapability.UnusablePath(
                    AcquisitionPathKind.Reduction,
                    "Aetherial reduction sources are known, but none has a gather or fish route."),
            };

        var selected = sourcePaths
            .OrderBy(candidate => candidate.Path.Capability.Status switch
            {
                AcquisitionCapabilityStatus.Usable => 0,
                AcquisitionCapabilityStatus.Unknown => 1,
                _ => 2,
            })
            .ThenBy(candidate => candidate.SourceItemId)
            .First();
        var sourceCapability = selected.Path.Capability;
        var reductionUnlocked = QuestManager.IsQuestComplete(67633);
        var evidence = new AcquisitionCapabilityEvidence
        {
            JobId = sourceCapability.JobId,
            RequiredLevel = sourceCapability.RequiredLevel,
            ActualLevel = sourceCapability.ActualLevel,
            GearsetKnown = sourceCapability.GearsetKnown,
            GearsetAvailable = sourceCapability.GearsetAvailable,
            UnlockKnown = sourceCapability.UnlockKnown,
            UnlockAvailable = sourceCapability.UnlockAvailable && reductionUnlocked,
            FolkloreRequired = sourceCapability.FolkloreRequired,
            FolkloreKnown = sourceCapability.FolkloreKnown,
            FolkloreUnlocked = sourceCapability.FolkloreUnlocked,
            RequiredPerception = sourceCapability.RequiredPerception,
            ActualPerception = sourceCapability.ActualPerception,
            PerceptionKnown = sourceCapability.PerceptionKnown,
            RouteKnown = sourceCapability.RouteKnown,
            RouteAvailable = sourceCapability.RouteAvailable,
            AdditionalEvidence = new Dictionary<string, string>
            {
                ["reductionOutputItemId"] = outputItemId.ToString(),
                ["reductionSourceItemIds"] = string.Join(",", sourceItemIds),
                ["selectedReductionSourceItemId"] = selected.SourceItemId.ToString(),
                ["aetherialReductionUnlocked"] = reductionUnlocked.ToString(),
            },
        };
        return new AcquisitionPath
        {
            Kind = AcquisitionPathKind.Reduction,
            JobId = selected.Path.JobId,
            JobName = selected.Path.JobName,
            SourceItemId = selected.SourceItemId,
            AlternativeSourceItemIds = sourceItemIds.ToArray(),
            Capability = AcquisitionCapabilityResolver.Resolve(AcquisitionPathKind.Reduction, evidence),
        };
    }

    private static AcquisitionPath? ResolveDirectGatherPath(uint itemId)
    {
        if (GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var gatherable))
            return ResolveGatherPath(itemId, gatherable, AcquisitionPathKind.Gather);
        return GatherBuddy.GameData.Fishes.TryGetValue(itemId, out var fish)
            ? ResolveFishPath(fish)
            : null;
    }

    private static AcquisitionPath ResolveGatherPath(uint itemId, Gatherable gatherable, AcquisitionPathKind kind)
    {
        var jobIds = ResolveGatheringJobIds(
            gatherable.NodeList.Select(node => node.GatheringType),
            gatherable.GatheringType);
        if (jobIds.Count == 0)
            jobIds = [0];

        return SelectBestGatherPath(jobIds
            .Select(jobId => ResolveGatherPathForJob(gatherable, kind, jobId))
            .ToArray());
    }

    internal static IReadOnlyList<uint> ResolveGatheringJobIds(
        IEnumerable<GatheringType> nodeTypes,
        GatheringType aggregateType)
        => nodeTypes
            .Select(GatheringJobId)
            .Append(GatheringJobId(aggregateType))
            .Where(jobId => jobId != 0)
            .Distinct()
            .ToArray();

    private static uint GatheringJobId(GatheringType gatheringType)
        => gatheringType.ToGroup() switch
        {
            GatheringType.Miner => 16u,
            GatheringType.Botanist => 17u,
            _ => 0u,
        };

    private static AcquisitionPath ResolveGatherPathForJob(
        Gatherable gatherable,
        AcquisitionPathKind kind,
        uint jobId)
    {
        var relevantNodes = gatherable.NodeList
            .Where(node => GatheringJobId(node.GatheringType) == jobId)
            .ToList();
        var requiredLevel = gatherable.Level;
        var actualLevel = ReadJobLevel(jobId);
        var gearsetAvailable = false;
        var gearsetKnown = jobId != 0 && TryHasGearset(jobId, out gearsetAvailable);
        var requiredPerception = (int)gatherable.GatheringData.PerceptionReq;
        var actualPerception = 0;
        var perceptionKnown = requiredPerception == 0
            || gearsetAvailable && GearsetStatsReader.TryReadGearsetPerception(jobId, out actualPerception);
        var folkloreNodes = relevantNodes
            .Where(node => node.FolkloreId != 0 || !string.IsNullOrWhiteSpace(node.Folklore))
            .ToList();
        var folkloreRequired = relevantNodes.Count > 0
            && folkloreNodes.Count == relevantNodes.Count;
        var folkloreKnown = !folkloreRequired;
        var folkloreUnlocked = !folkloreRequired;
        if (folkloreRequired)
        {
            var notebookDivisions = Dalamud.GameData.GetExcelSheet<NotebookDivision>();
            folkloreUnlocked = folkloreNodes.Any(node =>
                node.FolkloreUnlockId != 0
             && notebookDivisions.GetRowOrDefault(node.FolkloreUnlockId) is { } division
             && Dalamud.UnlockState.IsNotebookDivisionUnlocked(division));
            folkloreKnown = folkloreUnlocked || folkloreNodes.All(node =>
                node.FolkloreUnlockId != 0
             && notebookDivisions.GetRowOrDefault(node.FolkloreUnlockId) is not null);
        }
        var capability = AcquisitionCapabilityResolver.Resolve(
            kind,
            new AcquisitionCapabilityEvidence
            {
                JobId = jobId,
                RequiredLevel = requiredLevel,
                ActualLevel = actualLevel,
                GearsetKnown = gearsetKnown,
                GearsetAvailable = gearsetAvailable,
                UnlockKnown = true,
                UnlockAvailable = true,
                FolkloreRequired = folkloreRequired,
                FolkloreKnown = folkloreKnown,
                FolkloreUnlocked = folkloreUnlocked,
                RequiredPerception = requiredPerception,
                ActualPerception = actualPerception,
                PerceptionKnown = perceptionKnown,
                RouteKnown = relevantNodes.Count > 0,
                RouteAvailable = relevantNodes.Count > 0,
            });
        return new AcquisitionPath
        {
            Kind = kind,
            JobId = jobId,
            JobName = ResolveJobName(jobId),
            Capability = capability,
        };
    }

    internal static AcquisitionPath SelectBestGatherPath(IReadOnlyList<AcquisitionPath> candidates)
        => candidates
            .OrderBy(candidate => candidate.Capability.Status switch
            {
                AcquisitionCapabilityStatus.Usable => 0,
                AcquisitionCapabilityStatus.Unknown => 1,
                _ => 2,
            })
            .ThenByDescending(candidate => candidate.Capability.ActualLevel)
            .ThenBy(candidate => candidate.JobId)
            .First();

    private static List<AcquisitionVendorOffer> BuildVendorOffers(
        IReadOnlyList<AcquisitionDependency> dependencies,
        uint currentTerritoryId)
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
            if (entry.ShopType == VendorShopType.GrandCompanySeals
                && VendorShopResolver.TryGetCurrentGrandCompanyId(out var currentGrandCompanyId)
                && !VendorShopResolver.MatchesGrandCompany(entry, currentGrandCompanyId))
                continue;

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
                var location = VendorNpcLocationCache.TryGetPreferredLocation(vendor.NpcId, currentTerritoryId);
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
                    VendorTerritoryId = location.TerritoryId,
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
            if (service.IsKnownUnmarketable(dependency.ItemId))
                continue;

            var cached = service.GetCached(dependency.ItemId, scope);
            var fetchedAt = service.GetFetchTime(dependency.ItemId, scope);
            if (cached == null)
            {
                if (service.HasError(dependency.ItemId, scope))
                    continue;
                service.QueueLookup(dependency.ItemId, dependency.ItemName, ResolveItemIcon(dependency.ItemId), scope);
                loadingReason = $"Loading Universalis data for {dependency.ItemName}.";
                continue;
            }

            if (DateTime.UtcNow - fetchedAt > UniversalisCacheTtl
                && !service.HasError(dependency.ItemId, scope))
            {
                service.QueueLookup(dependency.ItemId, dependency.ItemName, ResolveItemIcon(dependency.ItemId), scope);
                loadingReason = $"Refreshing Universalis data for {dependency.ItemName}.";
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
        => CraftingJobLevelReader.ReadOrDefault(jobId);

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
