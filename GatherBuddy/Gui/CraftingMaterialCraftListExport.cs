using System.Collections.Generic;
using GatherBuddy.Crafting;

namespace GatherBuddy.Gui;

internal readonly record struct CraftingMaterialCraftListSource(
    uint RecipeId,
    int RequiredItems,
    int Yield);

internal static class CraftingMaterialCraftListExport
{
    internal static CraftingListDefinition Build(
        string name,
        IEnumerable<CraftingMaterialCraftListSource> sources)
    {
        var list = new CraftingListDefinition { Name = name };
        foreach (var source in sources)
        {
            if (source.RecipeId == 0 || source.RequiredItems <= 0 || source.Yield <= 0)
                continue;

            var craftCount = (source.RequiredItems - 1) / source.Yield + 1;
            list.AddRecipe(source.RecipeId, craftCount);
        }

        return list;
    }
}
