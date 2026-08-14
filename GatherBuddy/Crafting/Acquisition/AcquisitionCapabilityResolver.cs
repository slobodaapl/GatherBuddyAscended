using System.Collections.Generic;

namespace GatherBuddy.Crafting.Acquisition;

public static class AcquisitionCapabilityResolver
{
    public static AcquisitionCapability Resolve(
        AcquisitionPathKind pathKind,
        AcquisitionCapabilityEvidence evidence)
    {
        if (pathKind == AcquisitionPathKind.Unknown)
        {
            return Create(
                AcquisitionCapabilityStatus.Unknown,
                pathKind,
                evidence,
                "Acquisition path kind is unknown.");
        }

        if (evidence.RequiredLevel > evidence.ActualLevel)
        {
            return Create(
                AcquisitionCapabilityStatus.Unusable,
                pathKind,
                evidence,
                $"Requires level {evidence.RequiredLevel}; current level is {evidence.ActualLevel}.");
        }

        if (!evidence.GearsetKnown)
        {
            return Create(
                AcquisitionCapabilityStatus.Unknown,
                pathKind,
                evidence,
                "Required gearset availability is unknown.");
        }

        if (!evidence.GearsetAvailable)
        {
            return Create(
                AcquisitionCapabilityStatus.Unusable,
                pathKind,
                evidence,
                "No usable gearset is available for the selected job.");
        }

        if (!evidence.UnlockKnown)
        {
            return Create(
                AcquisitionCapabilityStatus.Unknown,
                pathKind,
                evidence,
                "Required unlock state is unknown.");
        }

        if (!evidence.UnlockAvailable)
        {
            return Create(
                AcquisitionCapabilityStatus.Unusable,
                pathKind,
                evidence,
                "Required content or vendor unlock is unavailable.");
        }

        if (evidence.FolkloreRequired)
        {
            if (!evidence.FolkloreKnown)
            {
                return Create(
                    AcquisitionCapabilityStatus.Unknown,
                    pathKind,
                    evidence,
                    "Folklore unlock state is unknown.");
            }

            if (!evidence.FolkloreUnlocked)
            {
                return Create(
                    AcquisitionCapabilityStatus.Unusable,
                    pathKind,
                    evidence,
                    "Required folklore is not unlocked.");
            }
        }

        if (evidence.RequiredPerception > 0)
        {
            if (!evidence.PerceptionKnown)
            {
                return Create(
                    AcquisitionCapabilityStatus.Unknown,
                    pathKind,
                    evidence,
                    "Saved gearset perception is unknown.");
            }

            if (evidence.ActualPerception < evidence.RequiredPerception)
            {
                return Create(
                    AcquisitionCapabilityStatus.Unusable,
                    pathKind,
                    evidence,
                    $"Requires {evidence.RequiredPerception} perception; saved gearset has {evidence.ActualPerception}.");
            }
        }

        if (!evidence.RouteKnown)
        {
            return Create(
                AcquisitionCapabilityStatus.Unknown,
                pathKind,
                evidence,
                "A usable route to the selected source is unknown.");
        }

        if (!evidence.RouteAvailable)
        {
            return Create(
                AcquisitionCapabilityStatus.Unusable,
                pathKind,
                evidence,
                "No usable route to the selected source is available.");
        }

        return Create(AcquisitionCapabilityStatus.Usable, pathKind, evidence, "Selected path is usable.");
    }

    private static AcquisitionCapability Create(
        AcquisitionCapabilityStatus status,
        AcquisitionPathKind pathKind,
        AcquisitionCapabilityEvidence evidence,
        string reason)
        => new()
        {
            Status = status,
            PathKind = pathKind,
            Reason = reason,
            JobId = evidence.JobId,
            RequiredLevel = evidence.RequiredLevel,
            ActualLevel = evidence.ActualLevel,
            GearsetKnown = evidence.GearsetKnown,
            GearsetAvailable = evidence.GearsetAvailable,
            UnlockKnown = evidence.UnlockKnown,
            UnlockAvailable = evidence.UnlockAvailable,
            FolkloreRequired = evidence.FolkloreRequired,
            FolkloreKnown = evidence.FolkloreKnown,
            FolkloreUnlocked = evidence.FolkloreUnlocked,
            RequiredPerception = evidence.RequiredPerception,
            ActualPerception = evidence.ActualPerception,
            PerceptionKnown = evidence.PerceptionKnown,
            RouteKnown = evidence.RouteKnown,
            RouteAvailable = evidence.RouteAvailable,
            Evidence = new Dictionary<string, string>(evidence.AdditionalEvidence),
        };
}
