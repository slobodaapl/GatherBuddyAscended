using GatherBuddy.Crafting;

namespace GatherBuddy.Vulcan.Tests;

internal static class NativeRecipeCraftingTests
{
    internal static void Run(Action<bool, string> require)
    {
        var amount = new NativeCraftAmountState();
        amount.Refresh(100, 12);
        require(amount.RecipeId == 100 && amount.Maximum == 12 && amount.Value == 12,
            "native craft amount must default to the selected recipe's maximum");
        require(amount.ApplyText("7") && amount.Value == 7,
            "native craft amount must accept a user-entered value");
        amount.Refresh(100, 15);
        require(amount.Value == 7,
            "same-recipe refreshes must preserve a manual amount when the maximum increases");
        amount.Refresh(100, 5);
        require(amount.Value == 5,
            "same-recipe refreshes must clamp a manual amount when the maximum decreases");
        require(amount.ApplyText(string.Empty) && amount.Value == 0,
            "clearing the native amount input must disable crafting with a zero value");
        amount.Refresh(101, 9);
        require(amount.Value == 9,
            "selecting a different recipe must reset the native amount to its maximum");

        require(NativeRecipeCraftingUi.TryParseDisplayedCount("Craftable: 1,234", out var parsed)
                && parsed == 1234,
            "native craftable-count parsing must tolerate localized labels and separators");
        require(!NativeRecipeCraftingUi.TryParseDisplayedCount("", out _),
            "an empty native craftable-count label must fail closed");

        var sourceSettings = new RecipeCraftSettings
        {
            SolverOverride = SolverOverrideMode.DonatelloSolver,
            MaximizeQualityAtCostOfTime = true,
            DonatelloImprovementQuietSecondsOverride = 11,
            SpecialistActionOverride = SpecialistActionOverrideMode.Allow,
            IngredientPreferences = new Dictionary<uint, int> { [500] = 2 },
        };
        var list = new CraftingListDefinition { ID = 42, Name = "native test" };
        NativeRecipeCraftingLauncher.ConfigureDefinition(list, 777, 6, sourceSettings);

        require(list.Ephemeral
                && !list.SkipIfEnough
                && !list.SkipFinalIfEnough
                && !list.QuickSynthAll
                && !list.RetainerRestock
                && !list.AutoPurchaseBlockedDependencies
                && !list.ReturnToHomeWorldBeforeCrafting,
            "native crafting must create an ephemeral exact-count list without skip, quick-synth, acquisition, or return-home behavior");
        require(list.Recipes is [{ RecipeId: 777, Quantity: 6, IsOriginalRecipe: true }],
            "native crafting must preserve the exact selected recipe and synthesis-attempt count");

        var copiedSettings = list.Recipes[0].CraftSettings;
        var copiedIngredient = copiedSettings?.IngredientPreferences.GetValueOrDefault(500u);
        require(copiedSettings != null
                && !ReferenceEquals(copiedSettings, sourceSettings)
                && copiedSettings.SolverOverride == SolverOverrideMode.DonatelloSolver
                && copiedSettings.MaximizeQualityAtCostOfTime
                && copiedSettings.DonatelloImprovementQuietSecondsOverride == 11
                && copiedSettings.SpecialistActionOverride == SpecialistActionOverrideMode.Allow
                && copiedIngredient == 2,
            "native crafting must clone every recipe-level solver and quality override into the ephemeral list");
        sourceSettings.IngredientPreferences[500] = 9;
        require(copiedSettings!.IngredientPreferences[500] == 2,
            "the ephemeral recipe settings must not alias mutable browser settings");
    }
}
