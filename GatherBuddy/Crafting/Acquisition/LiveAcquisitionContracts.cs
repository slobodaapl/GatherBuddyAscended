using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Crafting.Acquisition;

public enum LiveAcquisitionStage
{
    Idle,
    Preconditions,
    Vendor,
    Market,
    VendorRecovery,
    ReturnToStartWorld,
    Completed,
    Failed,
    Cancelled,
}

public enum LiveAcquisitionStatus
{
    Completed,
    Failed,
    Cancelled,
    PartiallyCompleted,
}

public enum LiveAcquisitionFailureKind
{
    None,
    InvalidPlan,
    MissingPlugin,
    TravelBlocked,
    MarketUnavailable,
    VendorUnavailable,
    ListingUnavailable,
    PurchaseRejected,
    VerificationFailed,
    InventoryCapacity,
    CurrencyUnavailable,
    GilBudgetExceeded,
    DutyOrPartyRestriction,
    Cancelled,
    Unexpected,
}

public sealed class LiveAcquisitionOptions
{
    public bool CurrentWorldOnly { get; init; }
    public bool PreferHQ { get; init; }
    public bool PreferVendors { get; init; }
    public bool PreferMarketForSpecialCurrency { get; init; } = true;
    public long? MaximumGilSpend { get; init; }
    public int MaximumReplans { get; init; } = 3;
    public TimeSpan ReplanTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan TravelTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan MarketBoardTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan PurchaseTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed record LiveAcquisitionDiagnostic(
    DateTime Timestamp,
    LiveAcquisitionStage Stage,
    string Message,
    uint ItemId = 0,
    string ItemName = "",
    string WorldName = "",
    long? EstimatedGil = null,
    long? LiveGil = null,
    long? ListingId = null);

public sealed record LiveMarketListing(
    uint ItemId,
    long ListingId,
    uint WorldId,
    string WorldName,
    int Quantity,
    int PricePerUnit,
    int TotalTax,
    bool IsHq,
    bool IsMannequin = false,
    bool IsSellingAsSet = false)
{
    public long TotalGil
        => checked((long)System.Math.Max(0, PricePerUnit) * System.Math.Max(0, Quantity) + System.Math.Max(0, TotalTax));
}

public sealed record LiveMarketPurchaseResult(
    bool Accepted,
    bool Verified,
    uint ItemId,
    long ListingId,
    int QuantityPurchased,
    long GilSpent,
    string Message,
    bool RequestSubmitted = false,
    bool? IsHq = null,
    long? GilBefore = null,
    long? GilAfter = null,
    bool IsStale = false);

public sealed record LiveVendorPurchaseResult(
    bool Accepted,
    bool Verified,
    uint ItemId,
    int QuantityPurchased,
    IReadOnlyDictionary<uint, long> CurrencySpent,
    long GilSpent,
    string Message,
    bool RequestSubmitted = false,
    bool? IsHq = null,
    long? GilBefore = null,
    long? GilAfter = null)
{
    public IReadOnlyDictionary<uint, int> OutputQuantities { get; init; }
        = new Dictionary<uint, int>();

    /// <summary>
    /// Authoritative wallet snapshots captured around the vendor request.
    /// Keys use the vendor currency item IDs, including
    /// <see cref="VendorShopResolver.GilCurrencyItemId"/> for Gil.
    /// </summary>
    public IReadOnlyDictionary<uint, long> CurrencyBalancesBefore { get; init; }
        = new Dictionary<uint, long>();
    public IReadOnlyDictionary<uint, long> CurrencyBalancesAfter { get; init; }
        = new Dictionary<uint, long>();
    public IReadOnlyDictionary<uint, VendorCurrencyAvailabilitySource> CurrencyBalanceSources { get; init; }
        = new Dictionary<uint, VendorCurrencyAvailabilitySource>();
    public IReadOnlyDictionary<uint, VendorCurrencyAvailabilitySource> CurrencyBalanceSourcesAfter { get; init; }
        = new Dictionary<uint, VendorCurrencyAvailabilitySource>();
}

public sealed record LiveMarketListingsResponse(
    bool IsFresh,
    IReadOnlyList<LiveMarketListing> Listings,
    string FailureReason = "")
{
    public static LiveMarketListingsResponse Failure(string reason)
        => new(false, Array.Empty<LiveMarketListing>(), reason);
}

public sealed record LiveAcquisitionPreconditionResult(
    bool IsReady,
    LiveAcquisitionFailureKind FailureKind = LiveAcquisitionFailureKind.None,
    string Message = "");

public sealed class LiveAcquisitionResult
{
    public LiveAcquisitionStatus Status { get; init; }
    public LiveAcquisitionFailureKind FailureKind { get; init; }
    public LiveAcquisitionStage FinalStage { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool HasIrreversiblePurchases { get; init; }
    public bool HasIndeterminatePurchases { get; init; }
    public long GilSpent { get; init; }
    public IReadOnlyDictionary<uint, int> PurchasedQuantities { get; init; }
        = new Dictionary<uint, int>();
    public IReadOnlyDictionary<uint, long> CurrencySpent { get; init; }
        = new Dictionary<uint, long>();
    public IReadOnlyList<LiveAcquisitionDiagnostic> Diagnostics { get; set; }
        = Array.Empty<LiveAcquisitionDiagnostic>();
}

public sealed record AcquisitionResult(AcquisitionPlanningResult Planning)
{
    public bool IsSuccess => Planning.IsSuccess;
}

/// <summary>
/// Game-facing boundary for live acquisition. Implementations own IPC, UI,
/// native client structs, and inventory/currency verification. The executor
/// never trusts Universalis or a stale estimate as purchase authority.
/// </summary>
public interface ILiveAcquisitionEnvironment
{
    uint CurrentWorldId { get; }
    string CurrentWorldName { get; }
    bool IsLifestreamAvailable { get; }
    bool IsVNavmeshAvailable { get; }
    bool IsMarketAutomationAvailable { get; }
    bool IsVendorAutomationAvailable { get; }
    bool IsInDuty { get; }
    bool IsInNonCrossWorldParty { get; }
    bool CanVisitWorld(uint worldId);
    bool IsGatewayAttuned(uint gatewayId);
    long GetGatewayTeleportCost(uint gatewayId);
    string ResolveWorldName(uint worldId);

    LiveAcquisitionPreconditionResult ValidatePlan(
        AcquisitionPlan plan,
        LiveAcquisitionOptions options);

    Task<bool> TravelToWorldAsync(
        AcquisitionWorldRoute route,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<bool> NavigateToMarketBoardAsync(
        AcquisitionWorldRoute route,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<LiveMarketListingsResponse> RequestLiveListingsAsync(
        uint itemId,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<LiveMarketPurchaseResult> PurchaseMarketListingAsync(
        LiveMarketListing listing,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<LiveVendorPurchaseResult> PurchaseVendorAsync(
        AcquisitionTransaction transaction,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task CloseMarketBoardAsync(CancellationToken cancellationToken);

    Task CleanupAsync(CancellationToken cancellationToken);
}
