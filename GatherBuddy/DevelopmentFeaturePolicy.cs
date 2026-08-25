using System;

namespace GatherBuddy;

[Flags]
internal enum DevelopmentFeature
{
    None = 0,
    FcMeshRuntime = 1 << 0,
    FcUserInterface = 1 << 1,
    DebugUserInterface = 1 << 2,
    VulcanContextMenus = 1 << 3,
    CraftingListPreviewActions = 1 << 4,
    All = FcMeshRuntime
        | FcUserInterface
        | DebugUserInterface
        | VulcanContextMenus
        | CraftingListPreviewActions,
}

internal readonly record struct DevelopmentFeaturePolicy(DevelopmentFeature Enabled)
{
    public static DevelopmentFeaturePolicy ForPlugin(bool isDevPlugin)
        => new(isDevPlugin ? DevelopmentFeature.All : DevelopmentFeature.None);

    public bool Allows(DevelopmentFeature feature)
        => feature != DevelopmentFeature.None && (Enabled & feature) == feature;
}
