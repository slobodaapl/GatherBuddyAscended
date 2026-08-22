using GatherBuddy.Time;
using System;

namespace GatherBuddy.AutoGather;

internal static class CollectableGpWaitPolicy
{
    internal static int ResolveRequiredGp(int configuredGp, int solverGp, int maximumGp)
        => Math.Clamp(Math.Max(configuredGp, solverGp), 0, maximumGp);

    internal static bool ShouldWait(
        int currentGp,
        int requiredGp,
        TimeInterval window,
        TimeStamp now)
        => currentGp < requiredGp
        && TimedNodeGpWaitPolicy.CanWaitBeforeGathering(window, now);
}
