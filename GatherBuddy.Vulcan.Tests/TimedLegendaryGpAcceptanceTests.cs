using GatherBuddy.AutoGather;
using GatherBuddy.Time;
using System;

namespace GatherBuddy.Vulcan.Tests;

public static class TimedLegendaryGpAcceptanceTests
{
    public static void Run(Action<bool, string> require)
    {
        var now = new TimeStamp(1_000_000);
        var current = new TimeInterval(now.AddMinutes(-1), now.AddMinutes(3));
        var upcoming = new TimeInterval(now.AddMinutes(1), now.AddMinutes(2));

        require(TimedLegendaryGpPolicy.ShouldWaitForFullGp(
                    current, upcoming, now, currentGp: 400, maxGp: 1000, gpRegenPerTick: 100),
            "a future legendary window must permit waiting when full GP restoration fits inside the current window");

        require(!TimedLegendaryGpPolicy.ShouldWaitForFullGp(
                    current, null, now, currentGp: 400, maxGp: 1000, gpRegenPerTick: 100),
            "a legendary node without another upcoming window must not defer gathering");

        require(!TimedLegendaryGpPolicy.ShouldWaitForFullGp(
                    new TimeInterval(now.AddMinutes(-1), now.AddSeconds(20)),
                    upcoming, now, currentGp: 400, maxGp: 1000, gpRegenPerTick: 100),
            "waiting must be rejected when restoration would consume the remaining current-node window");

        require(!TimedLegendaryGpPolicy.ShouldWaitForFullGp(
                    current,
                    new TimeInterval(now.AddMinutes(4), now.AddMinutes(5)),
                    now, currentGp: 400, maxGp: 1000, gpRegenPerTick: 100),
            "a future legendary window after the current node expires must not trigger a wait");

        require(!TimedLegendaryGpPolicy.ShouldWaitForFullGp(
                    current, upcoming, now, currentGp: 1000, maxGp: 1000, gpRegenPerTick: 100),
            "full GP must not trigger a wait");

        require(TimedNodeGpWaitPolicy.CanWaitBeforeGathering(TimeInterval.Always, now),
            "untimed nodes must retain configured GP waiting");
        require(TimedNodeGpWaitPolicy.CanWaitBeforeGathering(
                    new TimeInterval(now.AddMinutes(-1), now.AddSeconds(61)), now),
            "a timed node outside the gathering reserve may still wait for GP");
        require(!TimedNodeGpWaitPolicy.CanWaitBeforeGathering(
                    new TimeInterval(now.AddMinutes(-1), now.AddSeconds(60)), now),
            "a timed node entering the gathering reserve must stop waiting for GP");
        require(!TimedNodeGpWaitPolicy.CanWaitBeforeGathering(TimeInterval.Invalid, now)
             && !TimedNodeGpWaitPolicy.CanWaitBeforeGathering(TimeInterval.Never, now),
            "unknown or unreachable windows must not permit indefinite GP waiting");
    }
}
