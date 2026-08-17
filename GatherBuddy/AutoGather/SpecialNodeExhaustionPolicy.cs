using GatherBuddy.Enums;

namespace GatherBuddy.AutoGather;

internal static class SpecialNodeExhaustionPolicy
{
    public static bool ShouldExhaust(AutoGatherConfig config, NodeType nodeType)
        => config.AbandonNodes
        && config.AlwaysExhaustTimedCollectableNodes
        && nodeType is NodeType.Unspoiled or NodeType.Ephemeral or NodeType.Legendary or NodeType.Clouded;

    public static bool ShouldAbandonCompleted(AutoGatherConfig config, NodeType nodeType)
        => config.AbandonNodes && !ShouldExhaust(config, nodeType);
}
