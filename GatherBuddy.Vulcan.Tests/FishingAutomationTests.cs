using GatherBuddy.AutoGather;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AutoGatherRuntime = GatherBuddy.AutoGather.AutoGather;

namespace GatherBuddy.Vulcan.Tests;

internal static class FishingAutomationTests
{
    public static void Run(Action<bool, string> require)
    {
        require(AutoGatherRuntime.FishingAutomationUnavailableReason(autoHookAvailable: true) == null,
            "fish automation must be available when AutoHook is available");
        require(AutoGatherRuntime.FishingAutomationUnavailableReason(autoHookAvailable: false) is { } reason
                && reason.Contains("AutoHook", StringComparison.Ordinal),
            "fish automation must report AutoHook as its only external prerequisite");

        var legacy = JsonConvert.DeserializeObject<AutoGatherConfig>("{\"FishDataCollection\":false}")!;
        var serialized = JObject.FromObject(legacy);
        require(legacy.UseAutoHook && !serialized.ContainsKey("FishDataCollection"),
            "legacy telemetry settings must be ignored and omitted from saved configuration");
    }
}
