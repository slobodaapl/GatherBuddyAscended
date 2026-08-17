using GatherBuddy.Time;

namespace GatherBuddy.AutoGather;

internal static class TimedTargetTravelPolicy
{
    public static bool CanStartTravel(TimeInterval uptime, TimeStamp now, int precognitionSeconds, int earlyAbandonmentSeconds)
        => IsAvailable(uptime, now.AddSeconds(precognitionSeconds), now.AddSeconds(earlyAbandonmentSeconds));

    public static bool IsAvailable(TimeInterval uptime, TimeStamp availabilityStart, TimeStamp availabilityEnd)
        => uptime.Start <= availabilityStart && availabilityEnd < uptime.End;
}
