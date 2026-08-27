using GatherBuddy.Config;
using GatherBuddy.Crafting;
using GatherBuddy.Helpers;
using Newtonsoft.Json;

namespace GatherBuddy.Vulcan.Tests;

internal static class CraftingReturnNavigationTests
{
    public static void Run(Action<bool, string> require)
    {
        var defaults = new Configuration();
        require(defaults.ReturnBeforeCrafting
                && defaults.ReturnBeforeCraftingDestination == CraftingReturnDestination.CheapestAetheryte,
            "crafting return must default on with the cheapest aetheryte destination");

        var deserializedDefaults = JsonConvert.DeserializeObject<Configuration>("{}")!;
        require(deserializedDefaults.ReturnBeforeCrafting
                && deserializedDefaults.ReturnBeforeCraftingDestination == CraftingReturnDestination.CheapestAetheryte,
            "missing crafting return fields must retain their enabled cheapest-aetheryte defaults");

        var legacyInn = JsonConvert.DeserializeObject<Configuration>(
            "{\"Version\":20,\"GoToInnBeforeCrafting\":true}")!;
        legacyInn.Migrate20To21();
        require(legacyInn.Version == 21
                && legacyInn.ReturnBeforeCrafting
                && legacyInn.ReturnBeforeCraftingDestination == CraftingReturnDestination.Inn,
            "an enabled legacy inn return must migrate to the enabled inn destination");
        require(!JsonConvert.SerializeObject(legacyInn).Contains("GoToInnBeforeCrafting", StringComparison.Ordinal),
            "the legacy inn-return field must not survive v21 serialization");

        var legacyDisabled = JsonConvert.DeserializeObject<Configuration>(
            "{\"Version\":20,\"GoToInnBeforeCrafting\":false}")!;
        legacyDisabled.Migrate20To21();
        require(legacyDisabled.Version == 21
                && legacyDisabled.ReturnBeforeCrafting
                && legacyDisabled.ReturnBeforeCraftingDestination == CraftingReturnDestination.CheapestAetheryte,
            "a disabled legacy inn return must migrate to the new enabled cheapest-aetheryte default");

        var cheapest = HomeNavigationHelper.SelectCheapestAetheryte(
        [
            (AetheryteId: 40, GilCost: 100),
            (AetheryteId: 30, GilCost: 25),
            (AetheryteId: 20, GilCost: 25),
            (AetheryteId: 0, GilCost: 0),
        ]);
        require(cheapest == 20,
            "crafting return must select the cheapest valid aetheryte and break price ties by id");
        require(HomeNavigationHelper.SelectCheapestAetheryte([]) == 0,
            "crafting return must report no destination when no attuned aetheryte is available");

        require(!CraftingQueueProcessor.ShouldUseConfiguredReturnBeforeCrafting(
                hadGatheringSteps: false,
                enabled: true),
            "craft-only queues must not use the configured return destination");
        require(CraftingQueueProcessor.ShouldUseConfiguredReturnBeforeCrafting(
                hadGatheringSteps: true,
                enabled: true),
            "queues that ran gathering steps must use the enabled return destination");
        require(!CraftingQueueProcessor.ShouldUseConfiguredReturnBeforeCrafting(
                hadGatheringSteps: true,
                enabled: false),
            "disabled crafting return must remain disabled after gathering");
    }
}
