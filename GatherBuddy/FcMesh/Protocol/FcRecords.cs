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

public sealed record RequiredCraftCapability(
    uint RecipeId,
    FcQualityPolicy QualityPolicy);

public sealed record CapabilityRequestRecord(
    FcRecordHeader Header,
    Guid RequestId,
    string WorldFingerprint,
    FcHlcTimestamp ExpiresAt,
    RequiredCraftCapability[] Recipes);

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
