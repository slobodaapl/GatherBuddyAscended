using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Crafting.Acquisition;

/// <summary>
/// Deterministic, game-independent source planner for blocked crafting
/// dependencies. Game and network code supplies the immutable input snapshot;
/// this type only decides whether complete source combinations exist.
/// </summary>
public static class AcquisitionPlanner
{
    // These limits are correctness boundaries, not approximation knobs. If a
    // source set is larger than the bounded exact search can prove, the result
    // is an explicit failure instead of a silently greedy plan.
    public const int MaxDependencySearchStates = 100_000;
    public const int MaxGlobalSearchStates = 250_000;

    public static AcquisitionPlanningResult Plan(
        AcquisitionPlanningInput input,
        AcquisitionPlanningSettings settings)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);

        var blockers = new List<AcquisitionBlocker>();
        var skippedFinalOutputs = new List<uint>();
        var blockedDependencies = new List<AcquisitionDependency>();

        foreach (var dependency in input.Dependencies)
        {
            if (dependency.RequiredQuantity <= 0)
                continue;

            if (dependency.IsFinalOutput && !dependency.IsIntermediateDemand)
            {
                skippedFinalOutputs.Add(dependency.ItemId);
                continue;
            }

            var path = dependency.SelectedPath;
            if (path?.Capability.Status == AcquisitionCapabilityStatus.Usable)
                continue;
            blockedDependencies.Add(dependency);
        }

        // Disabled acquisition is an explicit no-op. It must not surface
        // capability/source blockers or accidentally produce a purchase plan.
        if (!settings.AutoPurchaseBlockedDependencies)
        {
            return new AcquisitionPlanningResult
            {
                Status = AcquisitionPlanStatus.NoBlockedDependencies,
                SkippedFinalOutputItemIds = skippedFinalOutputs,
                SelectedPlan = new AcquisitionPlan(),
                PreferredEstimate = new AcquisitionEstimate(),
                MinimumGilEstimate = new AcquisitionEstimate(),
            };
        }

        if (blockedDependencies.Count == 0)
        {
            return new AcquisitionPlanningResult
            {
                Status = AcquisitionPlanStatus.NoBlockedDependencies,
                SkippedFinalOutputItemIds = skippedFinalOutputs,
                SelectedPlan = new AcquisitionPlan(),
                PreferredEstimate = new AcquisitionEstimate(),
                MinimumGilEstimate = new AcquisitionEstimate(),
            };
        }

        if (settings.CurrentWorldOnly && input.CurrentWorldId == 0)
        {
            return new AcquisitionPlanningResult
            {
                Status = AcquisitionPlanStatus.UnknownCurrentWorld,
                Blockers = new[]
                {
                    new AcquisitionBlocker
                    {
                        Kind = AcquisitionBlockerKind.UnknownCurrentWorld,
                        Reason = "Current-world-only acquisition requires a known current world.",
                    },
                },
                SkippedFinalOutputItemIds = skippedFinalOutputs,
            };
        }

        var candidateSets = new List<IReadOnlyList<Candidate>>(blockedDependencies.Count);
        foreach (var dependency in blockedDependencies)
        {
            var candidates = BuildCandidates(dependency, input, settings, out var candidateBlocker, out var limitExceeded);
            if (limitExceeded)
            {
                blockers.Add(new AcquisitionBlocker
                {
                    Kind = AcquisitionBlockerKind.DeterministicLimitExceeded,
                    ItemId = dependency.ItemId,
                    ItemName = dependency.ItemName,
                    Reason = candidateBlocker,
                });
                return new AcquisitionPlanningResult
                {
                    Status = AcquisitionPlanStatus.DeterministicLimitExceeded,
                    Blockers = blockers,
                    SkippedFinalOutputItemIds = skippedFinalOutputs,
                };
            }

            if (candidates.Count == 0)
            {
                var path = dependency.SelectedPath;
                var capabilityKind = path == null
                    ? AcquisitionBlockerKind.MissingSelectedPath
                    : path.Capability.Status == AcquisitionCapabilityStatus.Unknown
                        ? AcquisitionBlockerKind.CapabilityUnknown
                        : AcquisitionBlockerKind.CapabilityUnavailable;
                blockers.Add(new AcquisitionBlocker
                {
                    Kind = candidateBlocker.Contains("quality", StringComparison.OrdinalIgnoreCase)
                        ? AcquisitionBlockerKind.HardQualityUnavailable
                        : capabilityKind,
                    ItemId = dependency.ItemId,
                    ItemName = dependency.ItemName,
                    Reason = BuildCapabilityFailureReason(dependency, candidateBlocker),
                });
            }
            else
            {
                candidateSets.Add(candidates);
            }
        }

        if (blockers.Count > 0)
        {
            return new AcquisitionPlanningResult
            {
                Status = AcquisitionPlanStatus.Blocked,
                Blockers = blockers,
                SkippedFinalOutputItemIds = skippedFinalOutputs,
            };
        }

        var search = new GlobalSearch(input, settings, candidateSets);
        search.Run();

        if (search.LimitExceeded)
        {
            return new AcquisitionPlanningResult
            {
                Status = AcquisitionPlanStatus.DeterministicLimitExceeded,
                Blockers = new[]
                {
                    new AcquisitionBlocker
                    {
                        Kind = AcquisitionBlockerKind.DeterministicLimitExceeded,
                        Reason = "The exact acquisition search exceeded its deterministic state limit.",
                    },
                },
                SkippedFinalOutputItemIds = skippedFinalOutputs,
            };
        }

        if (search.MinimumPlan == null)
        {
            if (search.UnknownCurrencyIds.Count > 0)
            {
                return new AcquisitionPlanningResult
                {
                    Status = AcquisitionPlanStatus.UnknownCurrencyBalance,
                    Blockers = search.UnknownCurrencyIds
                        .OrderBy(currencyId => currencyId)
                        .Select(currencyId => new AcquisitionBlocker
                        {
                            Kind = AcquisitionBlockerKind.UnknownCurrencyBalance,
                            Reason = $"Currency balance is unknown for currency {currencyId}; acquisition was not attempted.",
                        })
                        .ToArray(),
                    SkippedFinalOutputItemIds = skippedFinalOutputs,
                };
            }

            return new AcquisitionPlanningResult
            {
                Status = AcquisitionPlanStatus.InsufficientCurrency,
                Blockers = new[]
                {
                    new AcquisitionBlocker
                    {
                        Kind = AcquisitionBlockerKind.InsufficientCurrency,
                        Reason = "No complete source combination fits the available currency balances.",
                    },
                },
                SkippedFinalOutputItemIds = skippedFinalOutputs,
            };
        }

        if (search.PlanWithinBudget == null)
        {
            return new AcquisitionPlanningResult
            {
                Status = AcquisitionPlanStatus.BudgetExceeded,
                Blockers = new[]
                {
                    new AcquisitionBlocker
                    {
                        Kind = AcquisitionBlockerKind.BudgetExceeded,
                        Reason = "Every complete source combination exceeds the maximum Gil spend.",
                    },
                },
                SkippedFinalOutputItemIds = skippedFinalOutputs,
                PreferredEstimate = search.PreferredPlan == null ? null : BuildEstimate(search.PreferredPlan, input),
                MinimumGilEstimate = BuildEstimate(search.MinimumPlan, input),
            };
        }

        return new AcquisitionPlanningResult
        {
            Status = AcquisitionPlanStatus.Ready,
            Blockers = Array.Empty<AcquisitionBlocker>(),
            SkippedFinalOutputItemIds = skippedFinalOutputs,
            SelectedPlan = BuildPlan(search.PlanWithinBudget, input, blockedDependencies),
            PreferredEstimate = search.PreferredPlan == null ? null : BuildEstimate(search.PreferredPlan, input),
            MinimumGilEstimate = BuildEstimate(search.MinimumPlan, input),
        };
    }

    public static AcquisitionPlanningResult Evaluate(
        AcquisitionPlanningInput input,
        AcquisitionPlanningSettings settings)
        => Plan(input, settings);

    private static string BuildCapabilityFailureReason(
        AcquisitionDependency dependency,
        string sourceFailure)
    {
        var pathReason = dependency.SelectedPath?.Capability.Reason;
        if (string.IsNullOrWhiteSpace(pathReason))
            return sourceFailure;
        return $"{pathReason} {sourceFailure}";
    }

    private static List<Candidate> BuildCandidates(
        AcquisitionDependency dependency,
        AcquisitionPlanningInput input,
        AcquisitionPlanningSettings settings,
        out string failureReason,
        out bool limitExceeded)
    {
        limitExceeded = false;
        var vendors = input.VendorOffers
            .Where(offer => offer.IsAvailable
                && offer.EffectiveOutputs.Any(output => output is not null
                    && output.ItemId == dependency.ItemId
                    && output.Quantity > 0))
            .OrderBy(offer => offer.OfferId, StringComparer.Ordinal)
            .ToList();

        var markets = input.MarketListings
            .Where(listing => listing.ItemId == dependency.ItemId
                && listing.IsAvailable
                && listing.Quantity > 0
                && listing.PricePerUnit >= 0
                && (!settings.CurrentWorldOnly
                    || input.CurrentWorldId == 0
                    || listing.WorldId == input.CurrentWorldId))
            .OrderBy(listing => listing.PricePerUnit)
            .ThenBy(listing => listing.TotalTax)
            .ThenBy(listing => listing.WorldId)
            .ThenBy(listing => listing.ListingId)
            .ToList();

        var sourceChoices = new List<SourceChoice>(vendors.Count + markets.Count);
        var requiredQuantity = dependency.RequiredQuantity;
        var hasSpecialCurrencyVendor = false;

        foreach (var offer in vendors)
        {
            var outputs = offer.EffectiveOutputs;
            if (outputs.Count == 0
                || outputs.Any(output => output == null || output.ItemId == 0 || output.Quantity <= 0))
                continue;

            var targetReceiveQuantity = outputs
                .Where(output => output.ItemId == dependency.ItemId)
                .Select(output => output.Quantity)
                .DefaultIfEmpty()
                .Sum();
            var primaryReceiveQuantity = outputs
                .Where(output => output.ItemId == offer.ItemId)
                .Select(output => output.Quantity)
                .DefaultIfEmpty()
                .Sum();
            if (targetReceiveQuantity <= 0 || primaryReceiveQuantity <= 0)
                continue;

            var maxPurchases = offer.MaximumPurchases
                ?? DivideRoundUp(requiredQuantity, targetReceiveQuantity);
            if (maxPurchases <= 0)
                continue;

            var costs = NormalizeCosts(offer.Costs);
            if (costs == null)
                continue;
            var special = costs.Any(cost => cost.IsSpecialCurrency && !cost.IsGil);
            hasSpecialCurrencyVendor |= special;
            sourceChoices.Add(SourceChoice.ForVendor(
                offer,
                targetReceiveQuantity,
                primaryReceiveQuantity,
                costs,
                maxPurchases,
                special));
        }

        foreach (var listing in markets)
        {
            var gil = checked((long)listing.PricePerUnit * listing.Quantity + Math.Max(0, listing.TotalTax));
            sourceChoices.Add(SourceChoice.ForMarket(listing, gil, hasSpecialCurrencyVendor));
        }

        // Market choices are tagged after vendor inspection. Rebuild them if
        // vendors were encountered after an earlier market in a future source
        // ordering change.
        if (hasSpecialCurrencyVendor)
        {
            for (var i = 0; i < sourceChoices.Count; i++)
            {
                if (sourceChoices[i].Kind == AcquisitionSourceKind.Market)
                    sourceChoices[i] = sourceChoices[i] with { IsSpecialCurrencyAlternative = true };
            }
        }

        if (sourceChoices.Count == 0)
        {
            var unavailable = input.VendorOffers
                .Where(offer => !offer.IsAvailable
                    && offer.EffectiveOutputs.Any(output => output is not null
                        && output.ItemId == dependency.ItemId
                        && output.Quantity > 0))
                .Select(offer => offer.UnavailableReason)
                .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
            failureReason = unavailable == null
                ? "No available vendor offer or market listing can satisfy this dependency."
                : unavailable;
            return new List<Candidate>();
        }

        var results = new List<Candidate>();
        var current = new List<ChosenSource>();
        var states = 0;
        var searchLimitExceeded = false;
        var requiredHq = Math.Clamp(dependency.RequiredHqQuantity, 0, requiredQuantity);
        var requiredNq = Math.Clamp(dependency.RequiredNqQuantity, 0, requiredQuantity);

        void Visit(int index, int acquired, int hq, int nq)
        {
            if (++states > MaxDependencySearchStates)
            {
                searchLimitExceeded = true;
                return;
            }

            if (acquired >= requiredQuantity && hq >= requiredHq && nq >= requiredNq)
            {
                results.Add(Candidate.Create(dependency, current));
                return;
            }

            if (index >= sourceChoices.Count)
                return;

            Visit(index + 1, acquired, hq, nq);
            if (searchLimitExceeded)
                return;

            var choice = sourceChoices[index];
            var maxUses = choice.MaxUses;
            for (var uses = 1; uses <= maxUses; uses++)
            {
                for (var use = 0; use < uses; use++)
                    current.Add(new ChosenSource(choice));

                var nextAcquired = checked(acquired + choice.ReceiveQuantity * uses);
                var nextHq = checked(hq + (choice.IsHq ? choice.ReceiveQuantity * uses : 0));
                var nextNq = checked(nq + (choice.IsHq ? 0 : choice.ReceiveQuantity * uses));
                Visit(index + 1, nextAcquired, nextHq, nextNq);

                for (var use = 0; use < uses; use++)
                    current.RemoveAt(current.Count - 1);
                if (searchLimitExceeded)
                    return;
            }
        }

        Visit(0, 0, 0, 0);
        limitExceeded = searchLimitExceeded;
        if (searchLimitExceeded)
        {
            failureReason = "The exact per-item source search exceeded its deterministic state limit.";
            return new List<Candidate>();
        }

        var unique = results
            .GroupBy(candidate => candidate.Signature, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.GilCost)
            .ThenBy(candidate => candidate.Transactions.Count)
            .ThenBy(candidate => candidate.Overbuy)
            .ThenBy(candidate => candidate.Signature, StringComparer.Ordinal)
            .ToList();

        if (unique.Count == 0)
        {
            failureReason = requiredHq > 0 && requiredNq > 0
                ? "Available sources cannot satisfy the required HQ/NQ quality quantities."
                : requiredHq > 0
                    ? "Available sources cannot satisfy the required HQ quality quantity."
                    : requiredNq > 0
                        ? "Available sources cannot satisfy the required NQ quality quantity."
                        : "Available sources cannot satisfy the required quantity.";
        }
        else
        {
            failureReason = string.Empty;
        }

        return unique;
    }

    private static IReadOnlyList<AcquisitionCurrencyCost>? NormalizeCosts(
        IReadOnlyList<AcquisitionCurrencyCost> costs)
    {
        if (costs is not { Count: > 0 })
            return null;

        var activeCosts = costs
            .Where(cost => cost is null
                || cost.CurrencyId != 0
                || cost.Amount != 0
                || cost.IsGil)
            .ToArray();
        if (activeCosts.Length == 0
            || activeCosts.Any(cost => cost == null
                || cost.Amount <= 0
                || cost.CurrencyId == 0 && !cost.IsGil))
            return null;

        return activeCosts
            .Select(cost =>
            {
                var isGil = cost.IsGil || cost.CurrencyId == AcquisitionCurrency.GilId;
                return new AcquisitionCurrencyCost
                {
                    CurrencyId = isGil ? AcquisitionCurrency.GilId : cost.CurrencyId,
                    IconId = isGil ? 0 : cost.IconId,
                    CurrencyName = isGil ? "Gil" : cost.CurrencyName,
                    Amount = cost.Amount,
                    IsGil = isGil,
                    IsSpecialCurrency = isGil ? false : cost.IsSpecialCurrency,
                    Group = isGil ? global::GatherBuddy.Vulcan.Vendors.VendorCurrencyGroup.Gil : cost.Group,
                };
            })
            .GroupBy(cost => cost.CurrencyId)
            .Select(group =>
            {
                var first = group.First();
                return new AcquisitionCurrencyCost
                {
                    CurrencyId = first.CurrencyId,
                    IconId = first.IconId,
                    CurrencyName = first.CurrencyName,
                    Amount = group.Sum(cost => cost.Amount),
                    IsGil = first.IsGil || first.CurrencyId == AcquisitionCurrency.GilId,
                    IsSpecialCurrency = group.Any(cost => cost.IsSpecialCurrency),
                    Group = first.Group,
                };
            })
            .OrderBy(cost => cost.CurrencyId)
            .ToArray();
    }

    private static int DivideRoundUp(int value, int divisor)
        => divisor <= 0 || value <= 0 ? 0 : (value - 1) / divisor + 1;

    private static AcquisitionPlan BuildPlan(
        CandidatePlan plan,
        AcquisitionPlanningInput input,
        IReadOnlyList<AcquisitionDependency> blockedDependencies)
    {
        var purchased = new Dictionary<uint, int>();
        foreach (var transaction in plan.Transactions)
        {
            if (transaction.Outputs is not { Count: > 0 })
            {
                purchased[transaction.ItemId] = checked(
                    purchased.GetValueOrDefault(transaction.ItemId) + transaction.Quantity);
                continue;
            }

            var units = Math.Max(1, transaction.PurchaseUnits);
            foreach (var output in transaction.Outputs)
                purchased[output.ItemId] = checked(purchased.GetValueOrDefault(output.ItemId) + output.Quantity * units);
        }

        return new()
        {
            Transactions = plan.Transactions,
            Estimate = BuildEstimate(plan, input),
            RequiredQuantities = BuildRequiredQuantities(blockedDependencies),
            RequiredHqQuantities = BuildRequiredHqQuantities(blockedDependencies),
            RequiredNqQuantities = BuildRequiredNqQuantities(blockedDependencies),
            PurchasedQuantities = purchased,
        };
    }

    private static IReadOnlyDictionary<uint, int> BuildRequiredQuantities(
        IReadOnlyList<AcquisitionDependency> dependencies)
        => dependencies
            .Where(dependency => dependency.ItemId != 0 && dependency.RequiredQuantity > 0)
            .GroupBy(dependency => dependency.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group.Aggregate(
                    0,
                    (total, dependency) => checked(total + dependency.RequiredQuantity)));

    private static IReadOnlyDictionary<uint, int> BuildRequiredHqQuantities(
        IReadOnlyList<AcquisitionDependency> dependencies)
        => dependencies
            .Where(dependency => dependency.ItemId != 0 && dependency.RequiredHqQuantity > 0)
            .GroupBy(dependency => dependency.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group.Aggregate(
                    0,
                    (total, dependency) => checked(total + Math.Clamp(
                        dependency.RequiredHqQuantity,
                        0,
                        dependency.RequiredQuantity))));

    private static IReadOnlyDictionary<uint, int> BuildRequiredNqQuantities(
        IReadOnlyList<AcquisitionDependency> dependencies)
        => dependencies
            .Where(dependency => dependency.ItemId != 0 && dependency.RequiredNqQuantity > 0)
            .GroupBy(dependency => dependency.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group.Aggregate(
                    0,
                    (total, dependency) => checked(total + Math.Clamp(
                        dependency.RequiredNqQuantity,
                        0,
                        dependency.RequiredQuantity))));

    private static AcquisitionEstimate BuildEstimate(CandidatePlan plan, AcquisitionPlanningInput input)
    {
        var currencies = plan.CurrencyCosts
            .OrderBy(pair => pair.Key)
            .Select(pair =>
            {
                var available = GetBalance(input, pair.Key);
                var first = plan.Transactions
                    .SelectMany(transaction => transaction.Costs)
                    .FirstOrDefault(cost => cost.CurrencyId == pair.Key);
                return new AcquisitionCurrencyRequirement
                {
                    CurrencyId = pair.Key,
                    IconId = first?.IconId ?? 0,
                    CurrencyName = first?.CurrencyName ?? (pair.Key == AcquisitionCurrency.GilId ? "Gil" : string.Empty),
                    Required = pair.Value,
                    Available = available,
                    Remaining = available == long.MaxValue ? long.MaxValue : available - pair.Value,
                    IsSpecialCurrency = first?.IsSpecialCurrency == true,
                };
            })
            .ToArray();

        var worlds = plan.Transactions
            .Where(transaction => transaction.SourceKind == AcquisitionSourceKind.Market)
            .GroupBy(transaction => (transaction.WorldId, transaction.WorldName))
            .OrderBy(group => group.Key.WorldId)
            .ThenBy(group => group.Key.WorldName, StringComparer.Ordinal)
            .Select(group => new AcquisitionWorldGroup
            {
                WorldId = group.Key.WorldId,
                WorldName = group.Key.WorldName,
                Transactions = group.ToArray(),
            })
            .ToArray();

        var taxGil = plan.TotalTaxGil;
        var purchaseGil = plan.GilCost - taxGil;

        return new AcquisitionEstimate
        {
            TotalGil = plan.GilCost,
            TotalPurchaseGil = purchaseGil,
            TotalTaxGil = taxGil,
            TotalOverbuy = plan.Overbuy,
            Currencies = currencies,
            WorldGroups = worlds,
        };
    }

    private static long GetBalance(AcquisitionPlanningInput input, uint currencyId)
    {
        return TryGetBalance(input, currencyId, out var balance)
            ? balance
            : long.MaxValue;
    }

    private static bool TryGetBalance(
        AcquisitionPlanningInput input,
        uint currencyId,
        out long balance)
    {
        if (input.CurrencyBalances.TryGetValue(currencyId, out balance))
        {
            balance = Math.Max(0, balance);
            return true;
        }
        if (currencyId == AcquisitionCurrency.GilId && input.GilBalance.HasValue)
        {
            balance = Math.Max(0, input.GilBalance.Value);
            return true;
        }
        balance = 0;
        return false;
    }

    private readonly record struct SourceChoice(
        AcquisitionSourceKind Kind,
        string SourceId,
        string SourceName,
        string Location,
        uint WorldId,
        string WorldName,
        int ReceiveQuantity,
        int PrimaryReceiveQuantity,
        int MaxUses,
        bool IsHq,
        bool IsSpecialCurrencySource,
        bool IsSpecialCurrencyAlternative,
        uint OfferItemId,
        string OfferItemName,
        IReadOnlyList<AcquisitionVendorOutput> Outputs,
        IReadOnlyList<AcquisitionCurrencyCost> Costs,
        long GilCost,
        long TaxGilCost)
    {
        public string Identity
            => $"{(int)Kind}:{SourceId}:{WorldId}:{(IsHq ? 1 : 0)}";

        public static SourceChoice ForVendor(
            AcquisitionVendorOffer offer,
            int targetReceiveQuantity,
            int primaryReceiveQuantity,
            IReadOnlyList<AcquisitionCurrencyCost> costs,
            int maxUses,
            bool special)
            => new(
                AcquisitionSourceKind.Vendor,
                offer.OfferId,
                offer.VendorName,
                offer.Location,
                0,
                string.Empty,
                targetReceiveQuantity,
                primaryReceiveQuantity,
                maxUses,
                offer.IsHq,
                special,
                false,
                offer.ItemId,
                offer.VendorName,
                offer.EffectiveOutputs,
                costs,
                costs.Where(cost => cost.IsGil).Sum(cost => cost.Amount),
                0);

        public static SourceChoice ForMarket(
            AcquisitionMarketListing listing,
            long gilCost,
            bool specialAlternative)
            => new(
                AcquisitionSourceKind.Market,
                listing.ListingId.ToString(),
                "Marketboard",
                string.Empty,
                listing.WorldId,
                listing.WorldName,
                listing.Quantity,
                listing.Quantity,
                1,
                listing.IsHq,
                false,
                specialAlternative,
                listing.ItemId,
                "Marketboard",
                new[]
                {
                    new AcquisitionVendorOutput { ItemId = listing.ItemId, Quantity = listing.Quantity },
                },
                new[]
                {
                    new AcquisitionCurrencyCost
                    {
                        CurrencyId = AcquisitionCurrency.GilId,
                        IconId = 0,
                        CurrencyName = "Gil",
                        Amount = gilCost,
                        IsGil = true,
                        Group = global::GatherBuddy.Vulcan.Vendors.VendorCurrencyGroup.Gil,
                    },
                },
                gilCost,
                Math.Max(0, listing.TotalTax));
    }

    private sealed class ChosenSource
    {
        public SourceChoice Choice { get; }

        public ChosenSource(SourceChoice choice)
        {
            Choice = choice;
        }
    }

    private sealed class Candidate
    {
        public List<AcquisitionTransaction> Transactions { get; } = new();
        public Dictionary<uint, long> CurrencyCosts { get; } = new();
        public long GilCost => CurrencyCosts.GetValueOrDefault(AcquisitionCurrency.GilId);
        public int AcquiredQuantity { get; private set; }
        public int Overbuy { get; private set; }
        public int NonHqQuantity { get; private set; }
        public int SpecialCurrencyVendorCount { get; private set; }
        public int SpecialCurrencyMarketCount { get; private set; }
        public string Signature { get; private set; } = string.Empty;

        public static Candidate Create(AcquisitionDependency dependency, IEnumerable<ChosenSource> selected)
        {
            var candidate = new Candidate();
            var sourceOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
            var selectedSources = selected
                .GroupBy(chosen => chosen.Choice.Identity, StringComparer.Ordinal)
                .Select(group => (choice: group.First().Choice, units: group.Count()))
                .ToArray();
            foreach (var (choice, units) in selectedSources)
            {
                var sourceKey = choice.Identity;
                var sourceOrdinal = sourceOrdinals.GetValueOrDefault(sourceKey);
                sourceOrdinals[sourceKey] = checked(sourceOrdinal + 1);
                var transactionCosts = ScaleCosts(choice.Costs, units);
                var transaction = new AcquisitionTransaction
                {
                    ExecutionId = AcquisitionTransactionIdentity.Create(
                        choice.OfferItemId,
                        dependency.SelectedPath?.RecipeId ?? 0,
                        choice.Kind,
                        choice.SourceId,
                        choice.IsHq,
                        sourceOrdinal),
                    ItemId = choice.OfferItemId,
                    ItemName = string.IsNullOrWhiteSpace(dependency.ItemName)
                        ? choice.OfferItemName
                        : dependency.ItemName,
                    SelectedRecipeId = dependency.SelectedPath?.RecipeId ?? 0,
                    SourceKind = choice.Kind,
                    SourceId = choice.SourceId,
                    SourceName = choice.SourceName,
                    Location = choice.Location,
                    WorldId = choice.WorldId,
                    WorldName = choice.WorldName,
                    Quantity = checked(choice.PrimaryReceiveQuantity * units),
                    Outputs = choice.Outputs,
                    PrimaryOutputQuantity = choice.PrimaryReceiveQuantity,
                    PurchaseUnits = units,
                    IsHq = choice.IsHq,
                    IsSpecialCurrencySource = choice.IsSpecialCurrencySource,
                    IsSpecialCurrencyAlternative = choice.IsSpecialCurrencyAlternative,
                    Costs = transactionCosts,
                    GilCost = checked(choice.GilCost * units),
                    TaxGilCost = checked(choice.TaxGilCost * units),
                };
                candidate.Transactions.Add(transaction);
                candidate.AcquiredQuantity = checked(candidate.AcquiredQuantity + choice.ReceiveQuantity * units);
                candidate.NonHqQuantity = checked(candidate.NonHqQuantity + (choice.IsHq ? 0 : choice.ReceiveQuantity * units));
                if (choice.IsSpecialCurrencySource)
                    candidate.SpecialCurrencyVendorCount += units;
                if (choice.IsSpecialCurrencyAlternative)
                    candidate.SpecialCurrencyMarketCount += units;
                foreach (var cost in transactionCosts)
                    candidate.CurrencyCosts[cost.CurrencyId] = checked(candidate.CurrencyCosts.GetValueOrDefault(cost.CurrencyId) + cost.Amount);
            }

            candidate.Overbuy = Math.Max(0, candidate.AcquiredQuantity - dependency.RequiredQuantity);
            candidate.Signature = string.Join("|", candidate.Transactions.Select(transaction =>
                $"{transaction.SourceKind}:{transaction.SourceId}:{transaction.Quantity}:{transaction.PurchaseUnits}:{transaction.IsHq}"));
            return candidate;
        }
    }

    private static IReadOnlyList<AcquisitionCurrencyCost> ScaleCosts(
        IReadOnlyList<AcquisitionCurrencyCost> costs,
        int units)
    {
        if (units <= 0)
            return Array.Empty<AcquisitionCurrencyCost>();

        return costs
            .Select(cost => new AcquisitionCurrencyCost
            {
                CurrencyId = cost.CurrencyId,
                IconId = cost.IconId,
                CurrencyName = cost.CurrencyName,
                Amount = checked(cost.Amount * units),
                IsGil = cost.IsGil,
                IsSpecialCurrency = cost.IsSpecialCurrency,
                Group = cost.Group,
            })
            .ToArray();
    }

    private sealed class CandidatePlan
    {
        public List<AcquisitionTransaction> Transactions { get; } = new();
        public Dictionary<uint, long> CurrencyCosts { get; } = new();
        public long GilCost => CurrencyCosts.GetValueOrDefault(AcquisitionCurrency.GilId);
        public int Overbuy { get; private set; }
        public int NonHqQuantity { get; private set; }
        public int SpecialCurrencyVendorCount { get; private set; }
        public int SpecialCurrencyMarketCount { get; private set; }
        public long TotalTaxGil { get; private set; }
        public int MarketTransactionCount
            => Transactions.Count(transaction => transaction.SourceKind == AcquisitionSourceKind.Market);
        public HashSet<uint> MarketWorldIds
            => Transactions
                .Where(transaction => transaction.SourceKind == AcquisitionSourceKind.Market)
                .Select(transaction => transaction.WorldId)
                .ToHashSet();
        public bool HasCoProductTransactions
            => Transactions.Any(transaction => transaction.Outputs is { Count: > 0 }
                && transaction.Outputs.Any(output => output.ItemId != transaction.ItemId));
        public string TransactionStateSignature
            => string.Join("|", Transactions
                .OrderBy(transaction => transaction.SourceKind)
                .ThenBy(transaction => transaction.SourceId, StringComparer.Ordinal)
                .ThenBy(transaction => transaction.WorldId)
                .ThenBy(transaction => transaction.IsHq)
                .Select(transaction => $"{transaction.SourceKind}:{transaction.SourceId}:{transaction.WorldId}:{transaction.IsHq}:{transaction.PurchaseUnits}"));
        public int WorldCount => Transactions
            .Where(transaction => transaction.SourceKind == AcquisitionSourceKind.Market)
            .Select(transaction => transaction.WorldId)
            .Distinct()
            .Count();
        public string Signature => string.Join("|", Transactions.Select(transaction =>
            $"{transaction.ItemId}:{transaction.SourceKind}:{transaction.SourceId}:{transaction.Quantity}:{transaction.PurchaseUnits}:{transaction.IsHq}"));

        public void Add(Candidate candidate)
        {
            Overbuy = checked(Overbuy + candidate.Overbuy);
            NonHqQuantity = checked(NonHqQuantity + candidate.NonHqQuantity);
            foreach (var transaction in candidate.Transactions)
            {
                var existingIndex = Transactions.FindIndex(existing =>
                    CanMergeTransactions(existing, transaction));
                if (existingIndex < 0)
                    Transactions.Add(transaction);
                else
                    Transactions[existingIndex] = MergeTransactions(Transactions[existingIndex], transaction);
            }

            RecomputeTotals();
        }

        private static bool CanMergeTransactions(
            AcquisitionTransaction existing,
            AcquisitionTransaction incoming)
        {
            if (existing.SourceKind != incoming.SourceKind
                || !string.Equals(existing.SourceId, incoming.SourceId, StringComparison.Ordinal)
                || existing.WorldId != incoming.WorldId
                || existing.IsHq != incoming.IsHq)
            {
                return false;
            }

            // Same-item demand may share one atomic source transaction. A
            // vendor co-product alias may also have a different primary item
            // ID, but only when its complete output and currency vectors are
            // identical; unrelated items from the same source stay separate.
            return existing.ItemId == incoming.ItemId
                || AreEquivalentCoProductTransactions(existing, incoming);
        }

        private static bool AreEquivalentCoProductTransactions(
            AcquisitionTransaction existing,
            AcquisitionTransaction incoming)
        {
            if (existing.Outputs is not { Count: > 0 }
                || incoming.Outputs is not { Count: > 0 }
                || !existing.Outputs.Any(output => output.ItemId != existing.ItemId)
                || !incoming.Outputs.Any(output => output.ItemId != incoming.ItemId))
            {
                return false;
            }

            var existingOutputs = existing.Outputs
                .Select(output => (output.ItemId, output.Quantity))
                .OrderBy(output => output.ItemId)
                .ThenBy(output => output.Quantity);
            var incomingOutputs = incoming.Outputs
                .Select(output => (output.ItemId, output.Quantity))
                .OrderBy(output => output.ItemId)
                .ThenBy(output => output.Quantity);
            if (!existingOutputs.SequenceEqual(incomingOutputs))
                return false;

            var existingUnits = Math.Max(1, existing.PurchaseUnits);
            var incomingUnits = Math.Max(1, incoming.PurchaseUnits);
            var existingCosts = existing.Costs
                .Select(cost => (cost.CurrencyId, Amount: cost.Amount / existingUnits, cost.IsGil, cost.IsSpecialCurrency, cost.Group))
                .OrderBy(cost => cost.CurrencyId)
                .ThenBy(cost => cost.Amount)
                .ThenBy(cost => cost.IsGil)
                .ThenBy(cost => cost.IsSpecialCurrency)
                .ThenBy(cost => cost.Group);
            var incomingCosts = incoming.Costs
                .Select(cost => (cost.CurrencyId, Amount: cost.Amount / incomingUnits, cost.IsGil, cost.IsSpecialCurrency, cost.Group))
                .OrderBy(cost => cost.CurrencyId)
                .ThenBy(cost => cost.Amount)
                .ThenBy(cost => cost.IsGil)
                .ThenBy(cost => cost.IsSpecialCurrency)
                .ThenBy(cost => cost.Group);
            return existingCosts.SequenceEqual(incomingCosts);
        }

        public void Restore(CandidatePlan snapshot)
        {
            Transactions.Clear();
            Transactions.AddRange(snapshot.Transactions);
            Overbuy = snapshot.Overbuy;
            NonHqQuantity = snapshot.NonHqQuantity;
            SpecialCurrencyVendorCount = snapshot.SpecialCurrencyVendorCount;
            SpecialCurrencyMarketCount = snapshot.SpecialCurrencyMarketCount;
            TotalTaxGil = snapshot.TotalTaxGil;
            CurrencyCosts.Clear();
            foreach (var cost in snapshot.CurrencyCosts)
                CurrencyCosts[cost.Key] = cost.Value;
        }

        public CandidatePlan Clone()
        {
            var clone = new CandidatePlan();
            clone.Transactions.AddRange(Transactions);
            foreach (var cost in CurrencyCosts)
                clone.CurrencyCosts[cost.Key] = cost.Value;
            clone.Overbuy = Overbuy;
            clone.NonHqQuantity = NonHqQuantity;
            clone.SpecialCurrencyVendorCount = SpecialCurrencyVendorCount;
            clone.SpecialCurrencyMarketCount = SpecialCurrencyMarketCount;
            clone.TotalTaxGil = TotalTaxGil;
            return clone;
        }

        private void RecomputeTotals()
        {
            CurrencyCosts.Clear();
            TotalTaxGil = 0;
            SpecialCurrencyVendorCount = 0;
            SpecialCurrencyMarketCount = 0;
            foreach (var transaction in Transactions)
            {
                TotalTaxGil = checked(TotalTaxGil + transaction.TaxGilCost);
                var units = Math.Max(1, transaction.PurchaseUnits);
                if (transaction.IsSpecialCurrencySource)
                    SpecialCurrencyVendorCount = checked(SpecialCurrencyVendorCount + units);
                if (transaction.IsSpecialCurrencyAlternative)
                    SpecialCurrencyMarketCount = checked(SpecialCurrencyMarketCount + units);
                foreach (var cost in transaction.Costs)
                    CurrencyCosts[cost.CurrencyId] = checked(CurrencyCosts.GetValueOrDefault(cost.CurrencyId) + cost.Amount);
            }
        }

        private static AcquisitionTransaction MergeTransactions(
            AcquisitionTransaction existing,
            AcquisitionTransaction incoming)
        {
            var existingUnits = Math.Max(1, existing.PurchaseUnits);
            var incomingUnits = Math.Max(1, incoming.PurchaseUnits);
            if (incomingUnits <= existingUnits)
                return existing;

            var additionalUnits = incomingUnits - existingUnits;
            var incomingUnitCosts = incoming.Costs
                .Select(cost => new AcquisitionCurrencyCost
                {
                    CurrencyId = cost.CurrencyId,
                    IconId = cost.IconId,
                    CurrencyName = cost.CurrencyName,
                    Amount = checked(cost.Amount / incomingUnits),
                    IsGil = cost.IsGil,
                    IsSpecialCurrency = cost.IsSpecialCurrency,
                    Group = cost.Group,
                })
                .ToArray();
            var additionalCosts = ScaleCosts(incomingUnitCosts, additionalUnits);
            var mergedCosts = existing.Costs
                .Concat(additionalCosts)
                .GroupBy(cost => cost.CurrencyId)
                .Select(group =>
                {
                    var first = group.First();
                    return new AcquisitionCurrencyCost
                    {
                        CurrencyId = group.Key,
                        IconId = first.IconId,
                        CurrencyName = first.CurrencyName,
                        Amount = checked(group.Sum(cost => cost.Amount)),
                        IsGil = first.IsGil,
                        IsSpecialCurrency = group.Any(cost => cost.IsSpecialCurrency),
                        Group = first.Group,
                    };
                })
                .ToArray();
            var primaryOutputQuantity = existing.PrimaryOutputQuantity > 0
                ? existing.PrimaryOutputQuantity
                : incoming.PrimaryOutputQuantity;
            return new AcquisitionTransaction
            {
                ExecutionId = existing.ExecutionId,
                ItemId = existing.ItemId,
                ItemName = existing.ItemName,
                SelectedRecipeId = existing.SelectedRecipeId,
                SourceKind = existing.SourceKind,
                SourceId = existing.SourceId,
                SourceName = existing.SourceName,
                Location = existing.Location,
                WorldId = existing.WorldId,
                WorldName = existing.WorldName,
                Quantity = checked(existing.Quantity + primaryOutputQuantity * additionalUnits),
                Outputs = existing.Outputs is { Count: > 0 } ? existing.Outputs : incoming.Outputs,
                PrimaryOutputQuantity = primaryOutputQuantity,
                PurchaseUnits = checked(existingUnits + additionalUnits),
                IsHq = existing.IsHq,
                IsSpecialCurrencySource = existing.IsSpecialCurrencySource || incoming.IsSpecialCurrencySource,
                IsSpecialCurrencyAlternative = existing.IsSpecialCurrencyAlternative || incoming.IsSpecialCurrencyAlternative,
                Costs = mergedCosts,
                GilCost = checked(existing.GilCost + incoming.GilCost / incomingUnits * additionalUnits),
                TaxGilCost = checked(existing.TaxGilCost + incoming.TaxGilCost / incomingUnits * additionalUnits),
            };
        }
    }

    private sealed class GlobalSearch
    {
        private readonly AcquisitionPlanningInput _input;
        private readonly AcquisitionPlanningSettings _settings;
        private readonly IReadOnlyList<IReadOnlyList<Candidate>> _candidateSets;
        private readonly HashSet<uint> _unknownCurrencyIds = new();
        private long _states;

        public CandidatePlan? PreferredPlan { get; private set; }
        public CandidatePlan? MinimumPlan { get; private set; }
        public CandidatePlan? PlanWithinBudget { get; private set; }
        public bool LimitExceeded { get; private set; }
        public IReadOnlySet<uint> UnknownCurrencyIds => _unknownCurrencyIds;

        public GlobalSearch(
            AcquisitionPlanningInput input,
            AcquisitionPlanningSettings settings,
            IReadOnlyList<IReadOnlyList<Candidate>> candidateSets)
        {
            _input = input;
            _settings = settings;
            _candidateSets = candidateSets;
        }

        public void Run()
        {
            var frontier = new List<CandidatePlan> { new() };
            foreach (var candidateSet in _candidateSets)
            {
                var next = new List<CandidatePlan>();
                foreach (var partial in frontier)
                foreach (var candidate in candidateSet)
                {
                    if (++_states > MaxGlobalSearchStates)
                    {
                        LimitExceeded = true;
                        return;
                    }

                    var combined = partial.Clone();
                    combined.Add(candidate);
                    if (!FitsPartialBalances(combined))
                        continue;
                    InsertPareto(next, combined);
                }

                if (next.Count == 0)
                    return;
                frontier = next;
            }

            foreach (var complete in frontier)
            {
                if (MinimumPlan == null || CompareMinimum(complete, MinimumPlan) < 0)
                    MinimumPlan = complete;
                if (PreferredPlan == null || ComparePreferred(complete, PreferredPlan) < 0)
                    PreferredPlan = complete;
                if ((_settings.MaximumGilSpend == null || complete.GilCost <= _settings.MaximumGilSpend.Value)
                    && (PlanWithinBudget == null || ComparePreferred(complete, PlanWithinBudget) < 0))
                    PlanWithinBudget = complete;
            }
        }

        private static void InsertPareto(List<CandidatePlan> frontier, CandidatePlan candidate)
        {
            for (var i = frontier.Count - 1; i >= 0; i--)
            {
                var existing = frontier[i];
                if (Dominates(existing, candidate))
                    return;
                if (Dominates(candidate, existing))
                    frontier.RemoveAt(i);
            }

            frontier.Add(candidate);
        }

        private static bool Dominates(CandidatePlan left, CandidatePlan right)
        {
            if ((left.HasCoProductTransactions || right.HasCoProductTransactions)
                && !string.Equals(left.TransactionStateSignature, right.TransactionStateSignature, StringComparison.Ordinal))
                return false;
            if (left.NonHqQuantity > right.NonHqQuantity
                || left.SpecialCurrencyVendorCount > right.SpecialCurrencyVendorCount
                || left.SpecialCurrencyMarketCount > right.SpecialCurrencyMarketCount
                || left.MarketTransactionCount > right.MarketTransactionCount
                || left.Transactions.Count > right.Transactions.Count
                || left.Overbuy > right.Overbuy
                || !left.MarketWorldIds.IsSubsetOf(right.MarketWorldIds))
                return false;

            foreach (var currencyId in left.CurrencyCosts.Keys.Concat(right.CurrencyCosts.Keys).Distinct())
                if (left.CurrencyCosts.GetValueOrDefault(currencyId) > right.CurrencyCosts.GetValueOrDefault(currencyId))
                    return false;

            return left.NonHqQuantity < right.NonHqQuantity
                || left.SpecialCurrencyVendorCount < right.SpecialCurrencyVendorCount
                || left.SpecialCurrencyMarketCount < right.SpecialCurrencyMarketCount
                || left.MarketTransactionCount < right.MarketTransactionCount
                || left.Transactions.Count < right.Transactions.Count
                || left.Overbuy < right.Overbuy
                || left.MarketWorldIds.Count < right.MarketWorldIds.Count
                || left.CurrencyCosts.Keys.Concat(right.CurrencyCosts.Keys).Distinct().Any(currencyId =>
                    left.CurrencyCosts.GetValueOrDefault(currencyId) < right.CurrencyCosts.GetValueOrDefault(currencyId))
                || string.CompareOrdinal(left.Signature, right.Signature) <= 0;
        }

        private bool FitsPartialBalances(CandidatePlan plan)
        {
            foreach (var cost in plan.CurrencyCosts)
            {
                if (!TryGetBalance(_input, cost.Key, out var balance))
                {
                    _unknownCurrencyIds.Add(cost.Key);
                    return false;
                }
                if (cost.Value > balance)
                    return false;
            }
            return true;
        }

        private int ComparePreferred(CandidatePlan left, CandidatePlan right)
        {
            if (_settings.PreferHQ && left.NonHqQuantity != right.NonHqQuantity)
                return left.NonHqQuantity.CompareTo(right.NonHqQuantity);
            if (_settings.PreferVendors)
            {
                var leftMarket = left.Transactions.Count(transaction => transaction.SourceKind == AcquisitionSourceKind.Market);
                var rightMarket = right.Transactions.Count(transaction => transaction.SourceKind == AcquisitionSourceKind.Market);
                if (leftMarket != rightMarket)
                    return leftMarket.CompareTo(rightMarket);
            }

            var leftSpecial = _settings.PreferMarketForSpecialCurrency
                ? left.SpecialCurrencyVendorCount
                : left.SpecialCurrencyMarketCount;
            var rightSpecial = _settings.PreferMarketForSpecialCurrency
                ? right.SpecialCurrencyVendorCount
                : right.SpecialCurrencyMarketCount;
            if (leftSpecial != rightSpecial)
                return leftSpecial.CompareTo(rightSpecial);
            if (left.GilCost != right.GilCost)
                return left.GilCost.CompareTo(right.GilCost);
            if (left.WorldCount != right.WorldCount)
                return left.WorldCount.CompareTo(right.WorldCount);
            if (left.Transactions.Count != right.Transactions.Count)
                return left.Transactions.Count.CompareTo(right.Transactions.Count);
            if (left.Overbuy != right.Overbuy)
                return left.Overbuy.CompareTo(right.Overbuy);
            return string.CompareOrdinal(left.Signature, right.Signature);
        }

        private int CompareMinimum(CandidatePlan left, CandidatePlan right)
        {
            if (left.GilCost != right.GilCost)
                return left.GilCost.CompareTo(right.GilCost);
            if (left.WorldCount != right.WorldCount)
                return left.WorldCount.CompareTo(right.WorldCount);
            if (left.Transactions.Count != right.Transactions.Count)
                return left.Transactions.Count.CompareTo(right.Transactions.Count);
            if (left.Overbuy != right.Overbuy)
                return left.Overbuy.CompareTo(right.Overbuy);
            if (left.NonHqQuantity != right.NonHqQuantity)
                return left.NonHqQuantity.CompareTo(right.NonHqQuantity);
            return string.CompareOrdinal(left.Signature, right.Signature);
        }
    }
}
