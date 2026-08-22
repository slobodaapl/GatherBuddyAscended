using System;
using System.Text.Json.Serialization;

namespace GatherBuddy.FcMesh.Protocol;

public sealed record FcQualityRule(
    uint ItemId,
    FcItemQuality Quality,
    int Quantity);

public sealed record FcQualityPolicy(
    FcQualityRule[] Rules)
{
    public static FcQualityPolicy Empty { get; } = new(Array.Empty<FcQualityRule>());
}

// CraftingListItemQuality is not exposed by the current repository build. The
// FC wire equivalent is explicit and can be mapped at the integration seam.
public sealed record PublishedRecipeTarget(
    uint RecipeId,
    uint ItemId,
    int Quantity,
    FcItemQuality Quality);

public sealed record PublishedListRecord(
    FcRecordHeader Header,
    Guid ListId,
    string DisplayName,
    bool Published,
    int PlannerSemanticsVersion,
    string GameVersion,
    PublishedRecipeTarget[] FinalTargets,
    FcQualityPolicy FinalQualityPolicy,
    FcQualityPolicy PrecraftQualityPolicy);

public sealed record CharacterIdentity(
    string CharacterId,
    string DisplayName,
    string World);

public enum FcWorkerState : byte
{
    Active,
    Waiting,
    Unsubscribed,
}

public sealed record FcFulfillmentSelection(
    bool AllPublishedLists,
    Guid[] ListIds)
{
    public static FcFulfillmentSelection All { get; } = new(true, Array.Empty<Guid>());

    public static FcFulfillmentSelection Specific(params Guid[] listIds)
        => new(false, listIds ?? Array.Empty<Guid>());
}

public sealed record FcLogicalTarget(
    Guid? ListId,
    uint? ItemId,
    uint? RecipeId,
    FcItemQuality? Quality);

public sealed record FcLogicalQueueEntry(
    uint RecipeId,
    int Remaining,
    Guid? ListId);

public sealed record WorkerSessionRecord(
    FcRecordHeader Header,
    Guid SessionId,
    ulong SessionGeneration,
    FcWorkerState State,
    CharacterIdentity Character,
    FcFulfillmentSelection Selection,
    bool UseOwnStock,
    ItemQuantityEntry[] HeldInventory,
    FcLogicalTarget? CurrentTarget,
    FcLogicalQueueEntry[] CraftQueue,
    uint[] GatherTargetOrder,
    string WorldFingerprint)
{
    [JsonIgnore]
    public FcItemQuantityMap HeldInventoryMap => new(HeldInventory ?? Array.Empty<ItemQuantityEntry>());
}

public sealed record ChestSnapshotRecord(
    FcRecordHeader Header,
    bool Complete,
    uint LoadedPageMask,
    ItemQuantityEntry[] Items,
    CrystalQuantityMap Crystals)
{
    [JsonIgnore]
    public FcItemQuantityMap ItemMap => new(Items ?? Array.Empty<ItemQuantityEntry>());
}

/// <summary>
/// Stable housing identity captured while the character is physically at an
/// FC estate.  The address is a route selector, not a navigation target.
/// </summary>
public sealed record FcHousingAddress(
    string World,
    string Region,
    uint Ward,
    uint Plot,
    bool IsSubdivision,
    string HousingDistrict = "");

/// <summary>
/// Environment/search information for an FC chest.  Coordinates are an
/// approximate map anchor only; execution must resolve a live object and use
/// that object's current runtime position.
/// </summary>
public sealed record FcChestLocationEnvironment(
    uint TerritoryId,
    uint MapId,
    string TerritoryName,
    float ApproximateMapX,
    float ApproximateMapY);

/// <summary>
/// Stable object identity.  GameObjectId is deliberately absent: it is a
/// transient instance identity and cannot be shared or persisted. DataId is a
/// legacy compatibility field and is non-authoritative; new records set it to
/// zero and all resolution uses BaseId.
/// </summary>
public sealed record FcChestObjectIdentity(
    uint BaseId,
    uint DataId,
    string ObjectKind);

public sealed record FcEstateChestLocationRecord(
    FcRecordHeader Header,
    Guid LocationId,
    bool Published,
    FcHousingAddress Housing,
    FcChestLocationEnvironment Environment,
    FcChestObjectIdentity Chest,
    string CompatibilityFingerprint);

public sealed record RequiredCraftCapability(
    uint RecipeId,
    FcQualityPolicy QualityPolicy)
{
    /// <summary>
    /// The policy used when this recipe is a final product.  QualityPolicy is
    /// retained as the compact/legacy alias so old records remain readable.
    /// </summary>
    public FcQualityPolicy FinalQualityPolicy { get; init; } = QualityPolicy ?? FcQualityPolicy.Empty;

    /// <summary>Policy used when this recipe is a precraft dependency.</summary>
    public FcQualityPolicy PrecraftQualityPolicy { get; init; } = FcQualityPolicy.Empty;

    /// <summary>Distinguishes a precraft capability from a final capability.</summary>
    public bool IsPrecraft { get; init; }

    [JsonIgnore]
    public FcQualityPolicy EffectiveQualityPolicy
        => IsPrecraft ? PrecraftQualityPolicy : FinalQualityPolicy;
}

public sealed record CapabilityRequestRecord(
    FcRecordHeader Header,
    Guid RequestId,
    string WorldFingerprint,
    FcHlcTimestamp ExpiresAt,
    RequiredCraftCapability[] Recipes)
{
    /// <summary>
    /// Optional explicit compatibility metadata. Header ownership remains the
    /// requester identity; these fields make the compatibility contract
    /// inspectable without trusting an arrival-time observation.
    /// </summary>
    public string RequesterAuthorId { get; init; } = string.Empty;
    public string GameVersion { get; init; } = string.Empty;
    public string PlannerFingerprint { get; init; } = string.Empty;
}

public enum FcRaphaelAssessmentOutcome : byte
{
    None,
    SimulationFailed,
    Incomplete,
    FailedDurability,
    FailedQualityRequirement,
    NoQualityRequired,
    MinimumQualityMet,
    CollectibleTier1,
    CollectibleTier2,
    CollectibleTier3,
    PartialQuality,
    FullQuality,
}

public sealed record CraftCapabilityResult(
    uint RecipeId,
    bool CanCraft,
    uint? SelectedJobId,
    bool GuaranteesRequiredQuality,
    FcRaphaelAssessmentOutcome Assessment);

public sealed record CapabilityResponseRecord(
    FcRecordHeader Header,
    Guid RequestId,
    FcHlcTimestamp ExpiresAt,
    string WorldFingerprint,
    string GameVersion,
    string PlannerFingerprint,
    string GearsetFingerprint,
    string SolverFingerprint,
    CraftCapabilityResult[] Results)
{
    /// <summary>
    /// The exact requested recipe set assessed by this response. Older
    /// records may omit it; new responses always carry it and validators
    /// compare it against the request, including separate quality policies.
    /// </summary>
    public RequiredCraftCapability[]? RequestedRecipes { get; init; }

    /// <summary>Responder session proof used for liveness-bound eligibility.</summary>
    public Guid SessionId { get; init; }
    public ulong SessionGeneration { get; init; }
    public string ResponderAuthorId { get; init; } = string.Empty;

    [JsonIgnore]
    public string PlannerSemanticsFingerprint
    {
        get => PlannerFingerprint;
        init => PlannerFingerprint = value;
    }

    [JsonIgnore]
    public string GameFingerprint
    {
        get => GameVersion;
        init => GameVersion = value;
    }
}

public enum FcInventoryTransferKind : byte
{
    Withdraw,
    Deposit,
}

public enum FcInventoryTransferOutcome : byte
{
    Committed,
    ReconciledFailure,
}

public sealed record FcInventoryTransferRecord(
    FcRecordHeader Header,
    Guid OperationId,
    Guid SessionId,
    ulong SessionGeneration,
    Guid? PurposeListId,
    FcInventoryTransferKind Kind,
    FcInventoryTransferOutcome Outcome,
    ItemQuantityEntry[] ActualTransferred,
    ChestSnapshotRecord ChestAfter,
    WorkerSessionRecord WorkerAfter)
{
    [JsonIgnore]
    public FcItemQuantityMap ActualTransferredMap
        => new(ActualTransferred ?? Array.Empty<ItemQuantityEntry>());

    // Crystals have their own physical container. Keep an explicit local-only
    // value for reconciliation adapters without changing the wire record.
    [JsonIgnore]
    public CrystalQuantityMap ActualCrystals { get; init; } = new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>());
}
