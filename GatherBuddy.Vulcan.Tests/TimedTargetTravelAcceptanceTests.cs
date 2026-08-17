using System;
using GatherBuddy.AutoGather;
using GatherBuddy.Time;

namespace GatherBuddy.Vulcan.Tests;

public static class TimedTargetTravelAcceptanceTests
{
    public static void Run(Action<bool, string> require)
    {
        var now = new TimeStamp(1_000_000);
        const int precognitionSeconds = 20;
        const int earlyAbandonmentSeconds = 10;

        require(!TimedTargetTravelPolicy.CanStartTravel(
                new TimeInterval(now.AddSeconds(21), now.AddMinutes(5)),
                now,
                precognitionSeconds,
                earlyAbandonmentSeconds),
            "a future timed target outside the configured grace period must not start travel");

        require(TimedTargetTravelPolicy.CanStartTravel(
                new TimeInterval(now.AddSeconds(20), now.AddMinutes(5)),
                now,
                precognitionSeconds,
                earlyAbandonmentSeconds),
            "a timed target at the grace-period boundary must permit travel");

        require(TimedTargetTravelPolicy.CanStartTravel(
                new TimeInterval(now.AddMinutes(-1), now.AddMinutes(1)),
                now,
                precognitionSeconds,
                earlyAbandonmentSeconds),
            "an active timed target with usable uptime remaining must permit travel");

        require(!TimedTargetTravelPolicy.CanStartTravel(
                new TimeInterval(now.AddMinutes(-1), now.AddSeconds(10)),
                now,
                precognitionSeconds,
                earlyAbandonmentSeconds),
            "a timed target at the early-abandonment boundary must not start travel");

        require(!TimedTargetTravelPolicy.CanStartTravel(
                TimeInterval.Invalid,
                now,
                precognitionSeconds,
                earlyAbandonmentSeconds)
             && !TimedTargetTravelPolicy.CanStartTravel(
                TimeInterval.Never,
                now,
                precognitionSeconds,
                earlyAbandonmentSeconds),
            "invalid or impossible timed windows must not start travel");
    }
}
