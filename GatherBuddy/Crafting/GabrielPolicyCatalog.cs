using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public enum GabrielPolicyProfile : byte
{
    ActorV1 = 1,
}

public sealed record GabrielPolicyDescriptor(GabrielPolicyProfile Profile);

public static class GabrielPolicyCatalog
{
    public static readonly GabrielPolicyDescriptor ActorV1 = new(GabrielPolicyProfile.ActorV1);

    public static bool TryResolveRecipe(Recipe recipe, out GabrielPolicyDescriptor policy, out string reason)
    {
        if (!recipe.IsExpert)
        {
            policy = null!;
            reason = "Gabriel is available only for Expert recipes.";
            return false;
        }

        var recipeLevelTableId = (ushort)recipe.RecipeLevelTable.RowId;
        if (!ExpertConditionProfileCatalog.TryGet(recipeLevelTableId, out _))
        {
            policy = null!;
            reason = $"No cataloged Expert condition vector exists for RecipeLevelTable {recipeLevelTableId}.";
            return false;
        }

        policy = ActorV1;
        reason = string.Empty;
        return true;
    }

    public static bool TryResolve(CraftState craft, out GabrielPolicyDescriptor policy, out string reason)
    {
        if (!craft.CraftExpert)
        {
            policy = null!;
            reason = "Gabriel requires an Expert craft.";
            return false;
        }
        if (!ExpertConditionProfileCatalog.TryGet(craft.RecipeLevelTableId, out _))
        {
            policy = null!;
            reason = $"No cataloged Expert condition vector exists for RecipeLevelTable {craft.RecipeLevelTableId}.";
            return false;
        }

        policy = ActorV1;
        reason = string.Empty;
        return true;
    }

    public static bool TryPrepare(
        CraftState craft,
        out CraftState prepared,
        out GabrielPolicyDescriptor policy,
        out string reason)
    {
        prepared = craft;
        return TryResolve(craft, out policy, out reason);
    }
}
