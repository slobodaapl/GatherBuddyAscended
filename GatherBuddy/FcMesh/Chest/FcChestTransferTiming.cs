using System;

namespace GatherBuddy.FcMesh.Chest;

public static class FcChestTransferTiming
{
    public static readonly TimeSpan PhysicalInterActionDelay = TimeSpan.FromMilliseconds(500);

    public static bool IsDispatchReady(DateTime utcNow, DateTime dispatchNotBefore)
        => utcNow >= dispatchNotBefore;
}
