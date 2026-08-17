using GatherBuddy.AutoGather;
using System;

namespace GatherBuddy.Vulcan.Tests;

public static class CollectableTerminalActionAcceptanceTests
{
    public static void Run(Action<bool, string> require)
    {
        require(CollectableTerminalActionPolicy.Resolve(1000, 3, true)
                    == CollectableTerminalAction.WiseToTheWorld,
            "1000 collectability with Wise to the World available must bypass solving");

        require(CollectableTerminalActionPolicy.Resolve(1000, 3, false)
                    == CollectableTerminalAction.Collect,
            "1000 collectability without Wise to the World must collect immediately");

        require(CollectableTerminalActionPolicy.Resolve(999, 3, true)
                    == CollectableTerminalAction.None,
            "sub-cap collectability must retain the solver path");

        require(CollectableTerminalActionPolicy.Resolve(0, 1, true)
                    == CollectableTerminalAction.Collect
             && CollectableTerminalActionPolicy.Resolve(1000, 1, true)
                    == CollectableTerminalAction.Collect,
            "one remaining integrity must collect regardless of collectability or Wise availability");
    }
}
