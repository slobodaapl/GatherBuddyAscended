namespace GatherBuddy.AutoGather;

internal enum CollectableTerminalAction
{
    None,
    Collect,
    WiseToTheWorld,
}

internal static class CollectableTerminalActionPolicy
{
    internal static CollectableTerminalAction Resolve(
        int collectability,
        int integrity,
        bool wiseToTheWorldAvailable)
    {
        if (integrity <= 1)
            return CollectableTerminalAction.Collect;

        if (collectability >= ConfigPreset.MaxCollectability)
            return wiseToTheWorldAvailable
                ? CollectableTerminalAction.WiseToTheWorld
                : CollectableTerminalAction.Collect;

        return CollectableTerminalAction.None;
    }
}
