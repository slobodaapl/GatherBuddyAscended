using GatherBuddy.Gui;

namespace GatherBuddy.Vulcan.Tests;

internal static class DevelopmentFeaturePolicyTests
{
    public static void Run(Action<bool, string> require)
    {
        var production = DevelopmentFeaturePolicy.ForPlugin(false);
        var development = DevelopmentFeaturePolicy.ForPlugin(true);
        var gatedFeatures = Enum.GetValues<DevelopmentFeature>()
            .Where(feature => feature is not (DevelopmentFeature.None or DevelopmentFeature.All))
            .ToArray();

        require(production.Enabled == DevelopmentFeature.None,
            "a production plugin load must enable no development feature");
        require(development.Enabled == DevelopmentFeature.All,
            "a dev plugin load must enable every development feature");
        foreach (var feature in gatedFeatures)
        {
            require(!production.Allows(feature),
                $"a production plugin load must reject {feature}");
            require(development.Allows(feature),
                $"a dev plugin load must allow {feature}");
        }

        require(VulcanWindow.ResolveVulcanTabCount(production) == 9,
            "production Vulcan navigation must omit the FC and debug tabs");
        require(VulcanWindow.ResolveVulcanTabCount(development) == 11,
            "dev Vulcan navigation must retain the FC and debug tabs");
        require(VulcanWindow.ResolveVulcanTabCount(
                new DevelopmentFeaturePolicy(DevelopmentFeature.FcUserInterface)) == 10,
            "FC tab promotion must add one navigation slot without requiring the debug tab");
        require(VulcanWindow.ResolveVulcanTabCount(
                new DevelopmentFeaturePolicy(DevelopmentFeature.DebugUserInterface)) == 10,
            "debug tab promotion must add one navigation slot without requiring the FC tab");

        var configureResult = global::GatherBuddy.GatherBuddy.ConfigureFcMeshCharacterAuthor();
        require(configureResult.ErrorCode == global::GatherBuddy.FcMesh.Native.FcNativeErrorCode.InvalidState,
            "the production policy must reject FC native configuration before native handle access");
        require(!global::GatherBuddy.GatherBuddy.QueueRegisterCurrentFcChestLocation()
                && !global::GatherBuddy.GatherBuddy.StartFcFulfillment(),
            "the production policy must reject FC location and fulfillment requests");
        require(global::GatherBuddy.GatherBuddy.UnregisterCurrentFcChestLocation().Message
                == "FC mesh development features are unavailable.",
            "the production policy must reject FC publication commands at the public boundary");
    }
}
