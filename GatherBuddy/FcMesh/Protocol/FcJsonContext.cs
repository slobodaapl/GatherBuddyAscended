using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using GatherBuddy.FcMesh.Chest;
using GatherBuddy.FcMesh.Capabilities;
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
[JsonSerializable(typeof(FcHousingAddress))]
[JsonSerializable(typeof(FcChestLocationEnvironment))]
[JsonSerializable(typeof(FcChestObjectIdentity))]
[JsonSerializable(typeof(FcEstateChestLocationRecord))]
[JsonSerializable(typeof(RequiredCraftCapability))]
[JsonSerializable(typeof(CapabilityRequestRecord))]
[JsonSerializable(typeof(CraftCapabilityResult))]
[JsonSerializable(typeof(CapabilityResponseRecord))]
[JsonSerializable(typeof(FcInventoryTransferRecord))]
[JsonSerializable(typeof(ItemQuantityEntry[]))]
[JsonSerializable(typeof(FcQuantityEntry[]))]
[JsonSerializable(typeof(CrystalQuantityEntry[]))]
[JsonSerializable(typeof(FcEstateChestLocationRecord[]))]
[JsonSerializable(typeof(FcCrystalQuantityEntry[]))]
[JsonSerializable(typeof(PublishedRecipeTarget[]))]
[JsonSerializable(typeof(FcQualityRule[]))]
[JsonSerializable(typeof(FcLogicalQueueEntry[]))]
[JsonSerializable(typeof(RequiredCraftCapability[]))]
[JsonSerializable(typeof(CraftCapabilityResult[]))]
[JsonSerializable(typeof(FcCapabilityFingerprintInput))]
[JsonSerializable(typeof(FcSolverFingerprintInput))]
[JsonSerializable(typeof(FcCapabilityPublicationEntry))]
[JsonSerializable(typeof(FcCapabilityPublicationState))]
[JsonSerializable(typeof(FcCapabilityPublicationEntry[]))]
[JsonSerializable(typeof(uint[]))]
[JsonSerializable(typeof(Guid[]))]
[JsonSerializable(typeof(FcHlcClockState))]
[JsonSerializable(typeof(FcRevisionHighWaterEntry))]
[JsonSerializable(typeof(FcRegisterHlcHighWaterEntry))]
[JsonSerializable(typeof(FcWorkerHighWaterEntry))]
[JsonSerializable(typeof(FcWorldPersistenceState))]
[JsonSerializable(typeof(FcTransferPreOperation))]
[JsonSerializable(typeof(FcTransferPhysicalSnapshot))]
[JsonSerializable(typeof(ItemTransferRequest[]))]
[JsonSerializable(typeof(FcPendingTransferJournalState))]
[JsonSerializable(typeof(FcContributionLedgerState))]
[JsonSerializable(typeof(FcContributionLedgerRecovery))]
[JsonSerializable(typeof(FcPublicationCommandState))]
[JsonSerializable(typeof(FcLocalPublishedListState))]
[JsonSerializable(typeof(FcPublicationState))]
[JsonSerializable(typeof(FcChestLocationPublicationState))]
[JsonSerializable(typeof(FcLocalPublishedListState[]))]
[JsonSerializable(typeof(FcWorkerSessionState))]
[JsonSerializable(typeof(FcObservedAtomicTransfer))]
[JsonSerializable(typeof(FcObservedAtomicTransfer[]))]
public partial class FcJsonContext : JsonSerializerContext
{
}
