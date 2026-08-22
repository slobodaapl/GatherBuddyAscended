using System;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.Chest;

/// <summary>
/// Framework-thread evidence supplied by the live game integration while the
/// character is physically at the FC estate chest. The provider must resolve
/// the current targetable EventObj; this boundary never accepts a guessed name
/// or transient GameObjectId.
/// </summary>
public readonly record struct FcCurrentEstateChestEvidence(
    bool CharacterReady,
    bool HousingAddressAvailable,
    FcHousingAddress Housing,
    FcChestLocationEnvironment Environment,
    FcChestLiveObject LiveObject,
    string CompatibilityFingerprint,
    bool ObjectResolutionWasUnique,
    bool NativeOwnershipVerified = false,
    bool SubdivisionKnown = false,
    uint OriginalHouseTerritoryTypeId = 0);

public readonly record struct FcChestLocationCaptureResult(
    bool Succeeded,
    FcChestLocationObservation? Observation,
    string Error)
{
    public static FcChestLocationCaptureResult Blocked(string error)
        => new(false, null, error);
}

public static class FcChestLocationCapture
{
    public static FcChestLocationCaptureResult Capture(FcCurrentEstateChestEvidence evidence)
    {
        if (!evidence.CharacterReady)
            return FcChestLocationCaptureResult.Blocked("Current character/group state is not ready for FC location registration.");
        if (!evidence.HousingAddressAvailable)
            return FcChestLocationCaptureResult.Blocked("Current housing/plot address is unavailable.");
        if (!evidence.NativeOwnershipVerified)
            return FcChestLocationCaptureResult.Blocked("Native FC-estate ownership proof is unavailable.");
        if (!evidence.SubdivisionKnown)
            return FcChestLocationCaptureResult.Blocked("Authoritative housing division proof is unavailable.");
        if (evidence.OriginalHouseTerritoryTypeId == 0)
            return FcChestLocationCaptureResult.Blocked("Original FC house territory proof is unavailable.");
        if (evidence.Housing is null
            || string.IsNullOrWhiteSpace(evidence.Housing.World)
            || string.IsNullOrWhiteSpace(evidence.Housing.Region)
            || evidence.Housing.Ward is < 1 or > 30
            || evidence.Housing.Plot is < 1 or > 60
            || evidence.Housing.HousingDistrict is not (
                "Lavender Beds" or "Mist" or "Goblet" or "Empyreum" or "Shirogane"))
            return FcChestLocationCaptureResult.Blocked("Current FC housing address is incomplete or not route-executable.");
        if (!evidence.ObjectResolutionWasUnique)
            return FcChestLocationCaptureResult.Blocked("Company Chest object resolution was ambiguous.");
        if (evidence.Environment.TerritoryId == 0 || evidence.Environment.MapId == 0
            || string.IsNullOrWhiteSpace(evidence.Environment.TerritoryName)
            || !float.IsFinite(evidence.Environment.ApproximateMapX)
            || !float.IsFinite(evidence.Environment.ApproximateMapY))
            return FcChestLocationCaptureResult.Blocked("Current route environment or approximate map anchor is invalid.");
        if (evidence.LiveObject.BaseId == 0
            || string.IsNullOrWhiteSpace(evidence.LiveObject.ObjectKind)
            || !evidence.LiveObject.IsTargetable)
            return FcChestLocationCaptureResult.Blocked("Current Company Chest object identity is incomplete or not targetable.");
        if (evidence.LiveObject.Position is { X: var x, Y: var y, Z: var z }
            && (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)))
            return FcChestLocationCaptureResult.Blocked("Current Company Chest object position is invalid.");
        if (string.IsNullOrWhiteSpace(evidence.CompatibilityFingerprint))
            return FcChestLocationCaptureResult.Blocked("Game compatibility fingerprint is unavailable.");

        return new(
            true,
            new FcChestLocationObservation(
                evidence.Housing,
                evidence.Environment,
                new FcChestObjectIdentity(
                    evidence.LiveObject.BaseId,
                    0,
                    evidence.LiveObject.ObjectKind),
                evidence.CompatibilityFingerprint),
            string.Empty);
    }
}
