using GatherBuddy.Time;

namespace GatherBuddy.AutoGather;

internal static class TimedLegendaryGpPolicy
{
    internal const int GpRegenerationTickSeconds = 3;
    internal const int SafetyMarginSeconds = TimedNodeGpWaitPolicy.GatheringReserveSeconds;

    internal static bool ShouldWaitForFullGp(
        TimeInterval currentWindow,
        TimeInterval? upcomingLegendaryWindow,
        TimeStamp now,
        int currentGp,
        int maxGp,
        int gpRegenPerTick)
    {
        if (currentWindow == TimeInterval.Invalid
         || currentWindow == TimeInterval.Never
         || currentWindow == TimeInterval.Always
         || upcomingLegendaryWindow is not { } upcoming
         || upcoming == TimeInterval.Invalid
         || upcoming == TimeInterval.Never
         || upcoming == TimeInterval.Always
         || now < currentWindow.Start
         || now >= currentWindow.End
         || upcoming.Start <= now
         || upcoming.Start >= currentWindow.End
         || currentGp >= maxGp
         || maxGp <= 0
         || gpRegenPerTick <= 0)
            return false;

        var ticksNeeded = (maxGp - currentGp + gpRegenPerTick - 1) / gpRegenPerTick;
        var restoreMilliseconds = ticksNeeded * GpRegenerationTickSeconds * RealTime.MillisecondsPerSecond;
        var availableMilliseconds = currentWindow.End - now;
        var safetyMilliseconds = SafetyMarginSeconds * RealTime.MillisecondsPerSecond;

        return restoreMilliseconds + safetyMilliseconds < availableMilliseconds;
    }
}
