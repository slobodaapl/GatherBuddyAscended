using GatherBuddy.Gui;
using GatherBuddy.Crafting;
using Newtonsoft.Json;

namespace GatherBuddy.Vulcan.Tests;

internal static class CraftingMaterialSelectionAcceptanceTests
{
    internal static void Run(Action<bool, string> require)
    {
        var rows = Enumerable.Range(1, 6)
            .Select(itemId => new CraftingMaterialRowKey((uint)itemId, false))
            .ToArray();
        var selection = new CraftingMaterialSelection();

        selection.Click(rows[1], rows, control: false, shift: false);
        require(selection.Count == 1 && selection.Contains(rows[1]),
            "an unmodified material click must replace the selection");

        selection.Click(rows[3], rows, control: true, shift: false);
        require(selection.Count == 2 && selection.Contains(rows[1]) && selection.Contains(rows[3]),
            "Ctrl-click must toggle one material without clearing prior selections");

        selection.Click(rows[5], rows, control: false, shift: true);
        require(selection.Count == 3
                && selection.Contains(rows[3])
                && selection.Contains(rows[4])
                && selection.Contains(rows[5]),
            "Shift-click must replace the selection with the anchored display-order range");

        selection.Click(rows[1], rows, control: true, shift: true);
        require(selection.Count == 5
                && Enumerable.Range(1, 5).All(index => selection.Contains(rows[index])),
            "Ctrl-Shift-click must add the anchored display-order range");

        selection.RightClick(rows[4]);
        require(selection.Count == 5,
            "right-clicking a selected material must preserve the multi-selection");

        selection.RightClick(rows[0]);
        require(selection.Count == 1 && selection.Contains(rows[0]),
            "right-clicking an unselected material must make it the sole context selection");

        selection.RetainVisible(new[] { rows[2], rows[3] });
        require(selection.Count == 0,
            "materials removed from the current sorted view must leave the selection");

        var standaloneComponent = new CraftingMaterialRowKey(42, true, "craft/42");
        var nestedComponent = new CraftingMaterialRowKey(42, true, "craft/10/42");
        selection.Click(standaloneComponent, new[] { standaloneComponent, nestedComponent }, control: false, shift: false);
        selection.Click(nestedComponent, new[] { standaloneComponent, nestedComponent }, control: true, shift: false);
        require(selection.Count == 2
                && selection.Contains(standaloneComponent)
                && selection.Contains(nestedComponent),
            "the same precraft in standalone and nested rows must remain independently selectable");

        var firstFinal = new CraftingMaterialDemandNode(1000, IngredientQualityDemand.FromPreferNQ(1));
        var firstA = new CraftingMaterialDemandNode(10, IngredientQualityDemand.FromPreferNQ(3));
        firstA.Children.Add(new CraftingMaterialDemandNode(20, IngredientQualityDemand.FromPreferNQ(4)));
        firstFinal.Children.Add(firstA);
        firstFinal.Children.Add(new CraftingMaterialDemandNode(20, IngredientQualityDemand.FromPreferNQ(2)));

        var secondFinal = new CraftingMaterialDemandNode(2000, IngredientQualityDemand.FromPreferNQ(1));
        var secondA = new CraftingMaterialDemandNode(10, IngredientQualityDemand.FromPreferNQ(1));
        secondA.Children.Add(new CraftingMaterialDemandNode(20, IngredientQualityDemand.FromPreferNQ(2)));
        secondFinal.Children.Add(secondA);

        var tree = CraftingPrecraftPresentation.FromFinalRoots(
            new CraftingMaterialFinalRoots(new[] { firstFinal, secondFinal }),
            new HashSet<uint> { 10, 20 });
        require(tree.Count == 2
                && tree[0].ItemId == 10
                && tree[0].Demand.Total == 4
                && tree[0].Children.Count == 1
                && tree[0].Children[0].ItemId == 20
                && tree[0].Children[0].Demand.Total == 6
                && tree[1].ItemId == 20
                && tree[1].Demand.Total == 2,
            "hidden final roots must merge direct precrafts while retaining a separate nested demand branch");

        var directBlackStar = new CraftingMaterialDemandNode(30, IngredientQualityDemand.FromPreferNQ(3));
        directBlackStar.Children.Add(new CraftingMaterialDemandNode(40, IngredientQualityDemand.FromPreferNQ(3)));
        var bracelet = new CraftingMaterialDemandNode(3000, IngredientQualityDemand.FromPreferNQ(1));
        var braceletBlackStar = new CraftingMaterialDemandNode(30, IngredientQualityDemand.FromPreferNQ(2));
        braceletBlackStar.Children.Add(new CraftingMaterialDemandNode(40, IngredientQualityDemand.FromPreferNQ(2)));
        bracelet.Children.Add(braceletBlackStar);

        var precraftOnly = CraftingPrecraftPresentation.FromFinalRoots(
            new CraftingMaterialFinalRoots(new[] { directBlackStar, bracelet }),
            new HashSet<uint> { 30, 40, 3000 });
        var standaloneWhetstone = precraftOnly.FirstOrDefault(node => node.ItemId == 40);
        var braceletDependency = precraftOnly.FirstOrDefault(node => node.ItemId == 30);
        require(precraftOnly.Count == 2
                && standaloneWhetstone != null
                && standaloneWhetstone.Demand.Total == 3
                && standaloneWhetstone.Children.Count == 0
                && braceletDependency != null
                && braceletDependency.Demand.Total == 2
                && braceletDependency.Children.Count == 1
                && braceletDependency.Children[0].ItemId == 40
                && braceletDependency.Children[0].Demand.Total == 2
                && precraftOnly.All(node => node.ItemId != 3000),
            "final outputs must stay out of the precraft panel while their dependency branches remain exact");

        require(CraftingMaterialsWindow.FormatMaterialQuantity(0, true) == "?"
                && CraftingMaterialsWindow.FormatMaterialPercent(0f, 1f, false, true) == "?",
            "a reduction-source parent with unknowable required quantity must render question marks for Need and completion");

        var acquisitionSelection = new CraftingMaterialAcquisitionSelection();
        require(acquisitionSelection.ShouldUseReduction(900u, reductionAvailable: true, currencyAvailable: true),
            "an eligible reduction path must be the default presentation before the user chooses another source");
        acquisitionSelection.SelectCurrency(900u, "offer-a");
        require(!acquisitionSelection.ShouldUseReduction(900u, reductionAvailable: true, currencyAvailable: true)
                && acquisitionSelection.IsCurrencySelected(900u)
                && acquisitionSelection.TryGetCurrencyOffer(900u, out var selectedOffer)
                && selectedOffer == "offer-a",
            "choosing a currency must select the currency presentation and retain the exact vendor offer");
        acquisitionSelection.SelectReduction(900u);
        require(acquisitionSelection.ShouldUseReduction(900u, reductionAvailable: true, currencyAvailable: true)
                && !acquisitionSelection.IsCurrencySelected(900u)
                && acquisitionSelection.TryGetCurrencyOffer(900u, out selectedOffer)
                && selectedOffer == "offer-a",
            "switching back to reduction must preserve the selected currency offer for a later switch");
        require(acquisitionSelection.ShouldUseReduction(910u, reductionAvailable: true, currencyAvailable: false)
                && !acquisitionSelection.ShouldUseReduction(910u, reductionAvailable: false, currencyAvailable: true),
            "source selection must use any eligible reduction item generically and never present an unavailable reduction path");

        var craftList = CraftingMaterialCraftListExport.Build(
            "Selected Crafts",
            new[]
            {
                new CraftingMaterialCraftListSource(100, 6, 3),
                new CraftingMaterialCraftListSource(200, 7, 3),
                new CraftingMaterialCraftListSource(0, 9, 3),
                new CraftingMaterialCraftListSource(300, 0, 1),
            });
        require(craftList.Recipes.Count == 2
                && craftList.Recipes[0].RecipeId == 100
                && craftList.Recipes[0].Quantity == 2
                && craftList.Recipes[1].RecipeId == 200
                && craftList.Recipes[1].Quantity == 3,
            "selected craft requirements must preserve display order and round item demand up by recipe yield");

        var payload = CraftingListManager.SerializeExport(craftList);
        var serialized = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        var roundTrip = JsonConvert.DeserializeObject<CraftingListDefinition>(serialized);
        require(roundTrip is { Name: "Selected Crafts", ID: 0, Recipes.Count: 2 }
                && roundTrip.Recipes[1].RecipeId == 200
                && roundTrip.Recipes[1].Quantity == 3,
            "selected craft clipboard output must use the existing import-compatible craft-list serialization contract");
    }
}
