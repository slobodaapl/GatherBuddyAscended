using System;
using GatherBuddy.AutoGather;
using GatherBuddy.Enums;

namespace GatherBuddy.Vulcan.Tests;

public static class SpecialNodeExhaustionAcceptanceTests
{
    public static void Run(Action<bool, string> require)
    {
        var config = new AutoGatherConfig { AbandonNodes = true };
        foreach (var nodeType in new[] { NodeType.Unspoiled, NodeType.Ephemeral, NodeType.Legendary, NodeType.Clouded })
        {
            require(SpecialNodeExhaustionPolicy.ShouldExhaust(config, nodeType),
                $"{nodeType} nodes must exhaust by default when node abandonment is enabled");
            require(!SpecialNodeExhaustionPolicy.ShouldAbandonCompleted(config, nodeType),
                $"{nodeType} nodes must remain open after satisfying the requested quantity");
        }

        require(!SpecialNodeExhaustionPolicy.ShouldExhaust(config, NodeType.Regular)
             && SpecialNodeExhaustionPolicy.ShouldAbandonCompleted(config, NodeType.Regular),
            "regular nodes must retain early abandonment");
        require(!SpecialNodeExhaustionPolicy.ShouldExhaust(config, NodeType.Unknown)
             && SpecialNodeExhaustionPolicy.ShouldAbandonCompleted(config, NodeType.Unknown),
            "unknown node types must fail safe to early abandonment");

        config.AlwaysExhaustTimedCollectableNodes = false;
        require(SpecialNodeExhaustionPolicy.ShouldAbandonCompleted(config, NodeType.Legendary),
            "disabling special-node exhaustion must restore early abandonment");

        config.AbandonNodes = false;
        require(!SpecialNodeExhaustionPolicy.ShouldAbandonCompleted(config, NodeType.Legendary),
            "disabling node abandonment must prevent early abandonment independently of its child setting");
    }
}
