using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.FcMesh.State;
using GatherBuddy.FcMesh.Sessions;

namespace GatherBuddy.FcMesh.Protocol;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    WriteIndented = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(FcRecordHeader))]
[JsonSerializable(typeof(FcMeshRecord))]
[JsonSerializable(typeof(FcNativeEnvelopeWire))]
[JsonSerializable(typeof(FcNativeHlcWire))]
[JsonSerializable(typeof(FcVerifiedMeshContext))]
[JsonSerializable(typeof(FcHlcTimestamp))]
[JsonSerializable(typeof(ItemQuantityKey))]
[JsonSerializable(typeof(FcQuantityKey))]
[JsonSerializable(typeof(ItemQuantityEntry))]
[JsonSerializable(typeof(FcQuantityEntry))]
[JsonSerializable(typeof(CrystalQuantityEntry))]
[JsonSerializable(typeof(FcCrystalQuantityEntry))]
[JsonSerializable(typeof(FcItemQuantityMap))]
[JsonSerializable(typeof(CrystalQuantityMap))]
[JsonSerializable(typeof(FcCrystalQuantityMap))]
[JsonSerializable(typeof(FcQualityRule))]
[JsonSerializable(typeof(FcQualityPolicy))]
[JsonSerializable(typeof(PublishedRecipeTarget))]
[JsonSerializable(typeof(PublishedListRecord))]
[JsonSerializable(typeof(CharacterIdentity))]
[JsonSerializable(typeof(FcFulfillmentSelection))]
[JsonSerializable(typeof(FcLogicalTarget))]
[JsonSerializable(typeof(FcLogicalQueueEntry))]
[JsonSerializable(typeof(WorkerSessionRecord))]
[JsonSerializable(typeof(ChestSnapshotRecord))]
[JsonSerializable(typeof(RequiredCraftCapability))]
[JsonSerializable(typeof(CapabilityRequestRecord))]
[JsonSerializable(typeof(CraftCapabilityResult))]
[JsonSerializable(typeof(CapabilityResponseRecord))]
[JsonSerializable(typeof(FcInventoryTransferRecord))]
[JsonSerializable(typeof(ItemQuantityEntry[]))]
[JsonSerializable(typeof(FcQuantityEntry[]))]
[JsonSerializable(typeof(CrystalQuantityEntry[]))]
[JsonSerializable(typeof(FcCrystalQuantityEntry[]))]
[JsonSerializable(typeof(PublishedRecipeTarget[]))]
[JsonSerializable(typeof(FcQualityRule[]))]
[JsonSerializable(typeof(FcLogicalQueueEntry[]))]
[JsonSerializable(typeof(RequiredCraftCapability[]))]
[JsonSerializable(typeof(CraftCapabilityResult[]))]
[JsonSerializable(typeof(uint[]))]
[JsonSerializable(typeof(Guid[]))]
[JsonSerializable(typeof(FcHlcClockState))]
[JsonSerializable(typeof(FcRevisionHighWaterEntry))]
[JsonSerializable(typeof(FcRegisterHlcHighWaterEntry))]
[JsonSerializable(typeof(FcWorkerHighWaterEntry))]
[JsonSerializable(typeof(FcWorldPersistenceState))]
[JsonSerializable(typeof(FcTransferPreOperation))]
[JsonSerializable(typeof(FcPendingTransferJournalState))]
[JsonSerializable(typeof(FcContributionLedgerState))]
[JsonSerializable(typeof(FcContributionLedgerRecovery))]
[JsonSerializable(typeof(FcPublicationCommandState))]
[JsonSerializable(typeof(FcLocalPublishedListState))]
[JsonSerializable(typeof(FcPublicationState))]
[JsonSerializable(typeof(FcLocalPublishedListState[]))]
[JsonSerializable(typeof(FcWorkerSessionState))]
public partial class FcJsonContext : JsonSerializerContext
{
}
