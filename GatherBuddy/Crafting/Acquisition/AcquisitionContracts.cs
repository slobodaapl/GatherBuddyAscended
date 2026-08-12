using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Crafting.Acquisition;

public enum AcquisitionPathKind
{
    Craft,
    Gather,
    Fish,
    Unknown,
}

public enum AcquisitionCapabilityStatus
{
    Usable,
    Unusable,
    Unknown,
}

public sealed class AcquisitionCapability
{
    public AcquisitionCapabilityStatus Status { get; init; } = AcquisitionCapabilityStatus.Unknown;
    public string Reason { get; init; } = string.Empty;
    public AcquisitionPathKind PathKind { get; init; } = AcquisitionPathKind.Unknown;
    public uint JobId { get; init; }
    public int RequiredLevel { get; init; }
    public int ActualLevel { get; init; }
    public bool GearsetKnown { get; init; }
    public bool GearsetAvailable { get; init; }
    public bool UnlockKnown { get; init; }
    public bool UnlockAvailable { get; init; }
    public bool FolkloreRequired { get; init; }
    public bool FolkloreKnown { get; init; }
    public bool FolkloreUnlocked { get; init; }
    public bool RouteKnown { get; init; }
    public bool RouteAvailable { get; init; }
    public IReadOnlyDictionary<string, string> Evidence { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static AcquisitionCapability UsablePath(AcquisitionPathKind pathKind, string reason = "")
        => new()
        {
            Status = AcquisitionCapabilityStatus.Usable,
            PathKind = pathKind,
            Reason = reason,
        };

    public static AcquisitionCapability UnusablePath(AcquisitionPathKind pathKind, string reason)
        => new()
        {
            Status = AcquisitionCapabilityStatus.Unusable,
            PathKind = pathKind,
            Reason = reason,
        };
}

public sealed class AcquisitionCapabilityEvidence
{
    public uint JobId { get; init; }
    public int RequiredLevel { get; init; }
    public int ActualLevel { get; init; }
    public bool GearsetKnown { get; init; }
    public bool GearsetAvailable { get; init; }
    public bool UnlockKnown { get; init; }
    public bool UnlockAvailable { get; init; }
    public bool FolkloreRequired { get; init; }
    public bool FolkloreKnown { get; init; }
    public bool FolkloreUnlocked { get; init; }
    public bool RouteKnown { get; init; }
    public bool RouteAvailable { get; init; }
    public IReadOnlyDictionary<string, string> AdditionalEvidence { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class AcquisitionPath
{
    public AcquisitionPathKind Kind { get; init; } = AcquisitionPathKind.Unknown;
    public uint RecipeId { get; init; }
    public uint JobId { get; init; }
    public string JobName { get; init; } = string.Empty;
    public AcquisitionCapability Capability { get; init; } = new();
}

public sealed class AcquisitionDependency
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int RequiredQuantity { get; init; }
    public int RequiredHqQuantity { get; init; }
    /// <summary>Hard NQ demand; HQ never satisfies this quantity.</summary>
    public int RequiredNqQuantity { get; init; }
    /// <summary>
    /// True when this quantity is an intermediate input, even if the same
    /// item ID is also a direct final-list output. Intermediate demand remains
    /// eligible for acquisition; direct final output demand does not.
    /// </summary>
    public bool IsIntermediateDemand { get; init; }
    public bool IsFinalOutput { get; init; }
    public AcquisitionPath? SelectedPath { get; init; }
}

public sealed class AcquisitionPlanningSettings
{
    public bool AutoPurchaseBlockedDependencies { get; init; }
    public bool PreferMarketForSpecialCurrency { get; init; } = true;
    public bool PreferHQ { get; init; }
    public bool PreferVendors { get; init; }
    public bool CurrentWorldOnly { get; init; }
    public long? MaximumGilSpend { get; init; }
}

public static class AcquisitionCurrency
{
    public const uint GilId = 0;
}

public sealed class AcquisitionCurrencyCost
{
    public uint CurrencyId { get; init; }
    public uint IconId { get; init; }
    public string CurrencyName { get; init; } = string.Empty;
    public long Amount { get; init; }
    public bool IsGil { get; init; }
    public bool IsSpecialCurrency { get; init; }
    public VendorCurrencyGroup Group { get; init; } = VendorCurrencyGroup.Other;
}

public sealed class AcquisitionVendorOutput
{
    public uint ItemId { get; init; }
    public int Quantity { get; init; }
}

public sealed class AcquisitionVendorOffer
{
    public uint ItemId { get; init; }
    public string OfferId { get; init; } = string.Empty;
    public string VendorName { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public int ReceiveQuantity { get; init; }
    public IReadOnlyList<AcquisitionVendorOutput> Outputs { get; init; }
        = Array.Empty<AcquisitionVendorOutput>();
    public bool IsHq { get; init; }
    public bool IsAvailable { get; init; } = true;
    public string UnavailableReason { get; init; } = string.Empty;
    public int? MaximumPurchases { get; init; }
    public IReadOnlyList<AcquisitionCurrencyCost> Costs { get; init; }
        = Array.Empty<AcquisitionCurrencyCost>();

    public IReadOnlyList<AcquisitionVendorOutput> EffectiveOutputs
    {
        get
        {
            if (Outputs is not { Count: > 0 })
            {
                return ReceiveQuantity > 0 && ItemId != 0
                    ? new[] { new AcquisitionVendorOutput { ItemId = ItemId, Quantity = ReceiveQuantity } }
                    : Array.Empty<AcquisitionVendorOutput>();
            }

            return Outputs
                .Where(output => output is null || output.ItemId != 0 || output.Quantity != 0)
                .ToArray();
        }
    }
}

public sealed class AcquisitionMarketListing
{
    public uint ItemId { get; init; }
    public long ListingId { get; init; }
    public uint WorldId { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public int PricePerUnit { get; init; }
    public int TotalTax { get; init; }
    public bool IsHq { get; init; }
    public bool IsAvailable { get; init; } = true;
}

public sealed class AcquisitionPlanningInput
{
    public IReadOnlyList<AcquisitionDependency> Dependencies { get; init; }
        = Array.Empty<AcquisitionDependency>();
    public IReadOnlyList<AcquisitionVendorOffer> VendorOffers { get; init; }
        = Array.Empty<AcquisitionVendorOffer>();
    public IReadOnlyList<AcquisitionMarketListing> MarketListings { get; init; }
        = Array.Empty<AcquisitionMarketListing>();
    public IReadOnlyDictionary<uint, long> CurrencyBalances { get; init; }
        = new Dictionary<uint, long>();
    public long? GilBalance { get; init; }
    public uint CurrentWorldId { get; init; }
}

public enum AcquisitionSourceKind
{
    Vendor,
    Market,
}

public sealed class AcquisitionTransaction
{
    /// <summary>
    /// Stable identity for one planned purchase. Distinct transactions for the
    /// same item must not share fulfillment state.
    /// </summary>
    public string ExecutionId { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public uint SelectedRecipeId { get; init; }
    public AcquisitionSourceKind SourceKind { get; init; }
    public string SourceId { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public uint WorldId { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    /// <summary>Output vector per one vendor transaction. Empty for legacy/market callers.</summary>
    public IReadOnlyList<AcquisitionVendorOutput> Outputs { get; init; }
        = Array.Empty<AcquisitionVendorOutput>();
    public int PrimaryOutputQuantity { get; init; }
    public int PurchaseUnits { get; init; }
    public bool IsHq { get; init; }
    public bool IsSpecialCurrencyAlternative { get; init; }
    public bool IsSpecialCurrencySource { get; init; }
    public IReadOnlyList<AcquisitionCurrencyCost> Costs { get; init; }
        = Array.Empty<AcquisitionCurrencyCost>();
    public long GilCost { get; init; }
    public long TaxGilCost { get; init; }
}

public static class AcquisitionTransactionIdentity
{
    public static string Create(
        uint itemId,
        uint recipeId,
        AcquisitionSourceKind sourceKind,
        string sourceId,
        bool isHq,
        int ordinal)
        => $"{sourceKind}:{itemId}:{recipeId}:{sourceId.Length}:{sourceId}:{(isHq ? "hq" : "nq")}:{ordinal}";
}

public sealed class AcquisitionCurrencyRequirement
{
    public uint CurrencyId { get; init; }
    public uint IconId { get; init; }
    public string CurrencyName { get; init; } = string.Empty;
    public long Required { get; init; }
    public long Available { get; init; }
    public long Remaining { get; init; }
    public bool IsSpecialCurrency { get; init; }
}

public sealed class AcquisitionWorldGroup
{
    public uint WorldId { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public IReadOnlyList<AcquisitionTransaction> Transactions { get; init; }
        = Array.Empty<AcquisitionTransaction>();
}

public sealed class AcquisitionEstimate
{
    public long TotalGil { get; init; }
    public long TotalPurchaseGil { get; init; }
    public long TotalTaxGil { get; init; }
    public int TotalOverbuy { get; init; }
    public IReadOnlyList<AcquisitionCurrencyRequirement> Currencies { get; init; }
        = Array.Empty<AcquisitionCurrencyRequirement>();
    public IReadOnlyList<AcquisitionWorldGroup> WorldGroups { get; init; }
        = Array.Empty<AcquisitionWorldGroup>();
}

public sealed class AcquisitionPlan
{
    public IReadOnlyList<AcquisitionTransaction> Transactions { get; init; }
        = Array.Empty<AcquisitionTransaction>();
    public AcquisitionEstimate Estimate { get; init; } = new();
    /// <summary>
    /// Original missing dependency demand. This is intentionally separate from
    /// <see cref="PurchasedQuantities"/> because one atomic listing or vendor
    /// transaction may produce more output than the dependency requires.
    /// </summary>
    public IReadOnlyDictionary<uint, int> RequiredQuantities { get; init; }
        = new Dictionary<uint, int>();
    public IReadOnlyDictionary<uint, int> RequiredHqQuantities { get; init; }
        = new Dictionary<uint, int>();
    /// <summary>Hard NQ demand by item; HQ never satisfies these quantities.</summary>
    public IReadOnlyDictionary<uint, int> RequiredNqQuantities { get; init; }
        = new Dictionary<uint, int>();
    public IReadOnlyDictionary<uint, int> PurchasedQuantities { get; init; }
        = new Dictionary<uint, int>();
}

public enum AcquisitionPlanStatus
{
    Ready,
    NoBlockedDependencies,
    Blocked,
    InsufficientCurrency,
    BudgetExceeded,
    DeterministicLimitExceeded,
    UnknownCurrencyBalance,
    UnknownCurrentWorld,
}

public enum AcquisitionBlockerKind
{
    MissingSelectedPath,
    CapabilityUnavailable,
    CapabilityUnknown,
    NoAvailableSource,
    HardQualityUnavailable,
    InsufficientCurrency,
    BudgetExceeded,
    DeterministicLimitExceeded,
    UnknownCurrencyBalance,
    UnknownCurrentWorld,
}

public sealed class AcquisitionBlocker
{
    public AcquisitionBlockerKind Kind { get; init; }
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class AcquisitionPlanningResult
{
    public AcquisitionPlanStatus Status { get; init; }
    public IReadOnlyList<AcquisitionBlocker> Blockers { get; init; }
        = Array.Empty<AcquisitionBlocker>();
    public IReadOnlyList<uint> SkippedFinalOutputItemIds { get; init; }
        = Array.Empty<uint>();
    public AcquisitionPlan? SelectedPlan { get; init; }
    public AcquisitionEstimate? PreferredEstimate { get; init; }
    public AcquisitionEstimate? MinimumGilEstimate { get; init; }
    public bool IsSuccess => Status is AcquisitionPlanStatus.Ready or AcquisitionPlanStatus.NoBlockedDependencies;
}
