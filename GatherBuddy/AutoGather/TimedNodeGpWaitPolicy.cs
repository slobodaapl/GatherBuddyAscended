using GatherBuddy.Time;

namespace GatherBuddy.AutoGather;

internal static class TimedNodeGpWaitPolicy
{
    internal const int GatheringReserveSeconds = 60;

    internal static bool CanWaitBeforeGathering(TimeInterval window, TimeStamp now)
        => window == TimeInterval.Always
        || (window != TimeInterval.Invalid
         && window != TimeInterval.Never
         && window.End > now.AddSeconds(GatheringReserveSeconds));
}
