using System;
using System.Collections.Generic;

namespace GatherBuddy.Crafting;

internal static class CraftingJobTransitionValidator
{
    internal static List<uint> FindMissingGearsets(
        IReadOnlyList<uint> requiredJobs,
        uint currentJob,
        Func<uint, bool> hasGearset)
    {
        var missing = new List<uint>();
        var activeJob = currentJob;
        foreach (var requiredJob in requiredJobs)
        {
            if (requiredJob == activeJob)
                continue;
            if (!hasGearset(requiredJob) && !missing.Contains(requiredJob))
                missing.Add(requiredJob);
            activeJob = requiredJob;
        }

        return missing;
    }
}
