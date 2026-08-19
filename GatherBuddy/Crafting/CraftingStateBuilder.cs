using Dalamud.Plugin.Services;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using System;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace GatherBuddy.Crafting;

public static class CraftingStateBuilder
{
    public static GameStateBuilder.PlayerStats? GetCurrentPlayerStats()
    {
        try
        {
            var player = Dalamud.Objects.LocalPlayer;
            if (player == null)
                return null;

            var level = player.Level;
            var jobId = player.ClassJob.RowId;
            var isCrafter = jobId is >= 8 and <= 15;
            if (!isCrafter)
                return null;

            var craftsmanship = GetCraftsmanshipStat();
            var control = GetControlStat();
            var cp = GetMaxCPStat();
            if (craftsmanship == null || control == null || cp == null)
                return null;

            var stats = new GameStateBuilder.PlayerStats(
                Craftsmanship: craftsmanship.Value,
                Control: control.Value,
                CP: cp.Value,
                Level: level,
                Manipulation: GetManipulationUnlocked(jobId),
                Specialist: GetIsSpecialist(jobId),
                SplendorCosmic: GetSplendorCosmic(),
                CrafterDelineations: CraftingSpecialistResources.GetCrafterDelineationCount()
            );

            return stats;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingStateBuilder] Failed to get current player stats: {ex.Message}");
            return null;
        }
    }

    private static int? GetCraftsmanshipStat()
    {
        try
        {
            unsafe
            {
                var playerState = PlayerState.Instance();
                if (playerState == null)
                    return null;
                return playerState->Attributes[70];
            }
        }
        catch
        {
            return null;
        }
    }

    private static int? GetControlStat()
    {
        try
        {
            unsafe
            {
                var playerState = PlayerState.Instance();
                if (playerState == null)
                    return null;
                return playerState->Attributes[71];
            }
        }
        catch
        {
            return null;
        }
    }

    private static int? GetMaxCPStat()
    {
        try
        {
            unsafe
            {
                var playerState = PlayerState.Instance();
                if (playerState == null)
                    return null;
                return playerState->Attributes[11];
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool GetManipulationUnlocked(uint jobId)
    {
        try
        {
            unsafe
            {
                var manipulationQuestId = jobId switch
                {
                    8 => 67979u,  // CRP
                    9 => 68153u,  // BSM
                    10 => 68132u, // ARM
                    11 => 68137u, // GSM
                    12 => 68147u, // LTW
                    13 => 67969u, // WVR
                    14 => 67974u, // ALC
                    15 => 68142u, // CUL
                    _ => 0u
                };

                if (manipulationQuestId == 0)
                    return false;

                return QuestManager.IsQuestComplete(manipulationQuestId);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool GetIsSpecialist(uint jobId)
    {
        try
        {
            var player = Dalamud.Objects.LocalPlayer;
            if (player == null)
                return false;

            unsafe
            {
                var inventoryManager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                if (inventoryManager == null)
                    return false;

                var jobCrystal = inventoryManager->GetInventorySlot(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.EquippedItems, 13);
                return jobCrystal != null && jobCrystal->ItemId != 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool GetSplendorCosmic()
    {
        try
        {
            unsafe
            {
                var inventory = InventoryManager.Instance();
                var mainHand = inventory == null ? null : inventory->GetInventorySlot(InventoryType.EquippedItems, 0);
                if (mainHand == null || mainHand->ItemId == 0)
                    return false;

                return Dalamud.GameData.GetExcelSheet<Item>()?.TryGetRow(mainHand->ItemId, out var item) == true
                    && IsSplendorCosmicTool(item.LevelEquip, item.Rarity);
            }
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsSplendorCosmicTool(int levelEquip, int rarity)
        => levelEquip is 90 or 100 && rarity >= 4;

    public static GameStateBuilder.RecipeInfo BuildRecipeInfo(
        Recipe recipe,
        int? playerLevel = null,
        bool readDutyActionCharges = true)
    {
        var nativeLevelTable = recipe.RecipeLevelTable.Value;
        var lt = nativeLevelTable;
        if (recipe.Number == 0 && playerLevel is > 0 and < 100)
        {
            var levelTableSheet = Dalamud.GameData.GetExcelSheet<RecipeLevelTable>();
            if (levelTableSheet != null)
            {
                var levelTable = levelTableSheet.FirstOrDefault(row => row.ClassJobLevel == playerLevel.Value);
                if (levelTable.RowId != 0)
                    lt = levelTable;
            }
        }
        var difficulty = (int)(lt.Difficulty * recipe.DifficultyFactor / 100);
        var qualityMax = (int)(lt.Quality * recipe.QualityFactor / 100);
        var durability = (int)(nativeLevelTable.Durability * recipe.DurabilityFactor / 100);
        var qualityMin1 = 0;
        var qualityMin2 = 0;
        var qualityMin3 = 0;
        var isCollectible = recipe.ItemResult.Value.AlwaysCollectable;
        var collectableMetadataKey = recipe.CollectableMetadataKey;
        var temporaryAction = ResolveCosmicTemporaryAction(recipe);
        var hasMaterialMiracle = temporaryAction == (uint)VulcanSkill.MaterialMiracle;
        var materialMiracleCharges = hasMaterialMiracle && readDutyActionCharges
            ? GetDutyActionCharges((uint)VulcanSkill.MaterialMiracle)
            : 0;
        var hasStellarSteadyHand = temporaryAction == (uint)VulcanSkill.StellarSteadyHand;
        var stellarSteadyHandCharges = hasStellarSteadyHand && readDutyActionCharges
            ? GetDutyActionCharges((uint)VulcanSkill.StellarSteadyHand)
            : 0;

        if (isCollectible)
        {
            var itemId = recipe.ItemResult.RowId;
            var found = false;

            if (collectableMetadataKey == 7)
            {
                var cosmicRefineSheet = Dalamud.GameData.GetExcelSheet<WKSMissionToDoEvalutionRefin>();
                if (cosmicRefineSheet != null
                    && cosmicRefineSheet.TryGetRow(recipe.CollectableMetadata.RowId, out var refine))
                {
                    var scale = lt.Quality * ((double)recipe.QualityFactor / 100) / 1000;
                    qualityMin1 = (int)Math.Floor(refine.Unknown0 * scale) * 10;
                    qualityMin2 = (int)Math.Floor(refine.Unknown1 * scale) * 10;
                    qualityMin3 = (int)Math.Floor(refine.Unknown2 * scale) * 10;
                    found = true;
                }
            }

            var hwdSheet = Dalamud.GameData.GetExcelSheet<HWDCrafterSupply>();
            if (!found && hwdSheet != null)
            {
                foreach (var row in hwdSheet)
                {
                    foreach (var param in row.HWDCrafterSupplyParams)
                    {
                        if (param.ItemTradeIn.RowId != itemId)
                            continue;

                        qualityMin1 = param.BaseCollectableRating * 10;
                        qualityMin2 = param.MidCollectableRating * 10;
                        qualityMin3 = param.HighCollectableRating * 10;
                        found = true;
                        break;
                    }

                    if (found)
                        break;
                }
            }

            if (!found)
            {
                var satisfactionSheet = Dalamud.GameData.GetSubrowExcelSheet<SatisfactionSupply>();
                if (satisfactionSheet != null)
                {
                    foreach (var row in satisfactionSheet.SelectMany(sheet => sheet))
                    {
                        if (row.Item.RowId != itemId)
                            continue;

                        qualityMin1 = row.CollectabilityLow * 10;
                        qualityMin2 = row.CollectabilityMid * 10;
                        qualityMin3 = row.CollectabilityHigh * 10;
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                var sharlayanSheet = Dalamud.GameData.GetExcelSheet<SharlayanCraftWorksSupply>();
                if (sharlayanSheet != null)
                {
                    foreach (var row in sharlayanSheet)
                    {
                        foreach (var entry in row.Item)
                        {
                            if (entry.ItemId.RowId != itemId)
                                continue;

                            qualityMin1 = entry.CollectabilityMid * 10;
                            qualityMin2 = entry.CollectabilityHigh * 10;
                            found = true;
                            break;
                        }

                        if (found)
                            break;
                    }
                }
            }

            if (!found)
            {
                var bankaSheet = Dalamud.GameData.GetExcelSheet<BankaCraftWorksSupply>();
                if (bankaSheet != null)
                {
                    foreach (var row in bankaSheet)
                    {
                        foreach (var entry in row.Item)
                        {
                            if (entry.ItemId.RowId != itemId)
                                continue;

                            var breakpoints = entry.Collectability.Value;
                            qualityMin1 = breakpoints.CollectabilityLow * 10;
                            qualityMin2 = breakpoints.CollectabilityMid * 10;
                            qualityMin3 = breakpoints.CollectabilityHigh * 10;
                            found = true;
                            break;
                        }

                        if (found)
                            break;
                    }
                }
            }

            if (!found)
            {
                var collectableSheet = Dalamud.GameData.GetSubrowExcelSheet<CollectablesShopItem>();
                if (collectableSheet != null)
                {
                    foreach (var row in collectableSheet.SelectMany(sheet => sheet))
                    {
                        if (row.Item.RowId != itemId || row.CollectablesShopRefine.RowId == 0)
                            continue;

                        var breakpoints = row.CollectablesShopRefine.Value;
                        qualityMin1 = breakpoints.LowCollectability * 10;
                        qualityMin2 = breakpoints.MidCollectability * 10;
                        qualityMin3 = breakpoints.HighCollectability * 10;
                        found = true;
                        break;
                    }
                }
            }

            if (qualityMin3 == 0)
            {
                qualityMin3 = qualityMin2;
                qualityMin2 = qualityMin1;
            }
        }
        else if (recipe.RequiredQuality > 0)
        {
            qualityMax = (int)recipe.RequiredQuality;
            qualityMin1 = qualityMax;
            qualityMin2 = qualityMax;
            qualityMin3 = qualityMax;
        }
        else if (recipe.CanHq)
        {
            qualityMin3 = qualityMax;
        }

        return new GameStateBuilder.RecipeInfo(
            RecipeId: recipe.RowId,
            RecipeLevelTableId: (ushort)nativeLevelTable.RowId,
            Level: lt.ClassJobLevel,
            Difficulty: difficulty,
            QualityMax: qualityMax,
            RequiredQuality: (int)recipe.RequiredQuality,
            Durability: durability,
            ProgressDivider: lt.ProgressDivider,
            ProgressModifier: lt.ProgressModifier,
            QualityDivider: lt.QualityDivider,
            QualityModifier: lt.QualityModifier,
            CanHQ: recipe.CanHq,
            IsExpert: recipe.IsExpert,
            Stars: lt.Stars,
            IsCollectible: isCollectible,
            QualityMin1: qualityMin1,
            QualityMin2: qualityMin2,
            QualityMin3: qualityMin3,
            ConditionFlags: (ConditionFlags)lt.ConditionsFlag,
            HasMaterialMiracle: hasMaterialMiracle,
            CurrentMaterialMiracleCharges: materialMiracleCharges,
            HasStellarSteadyHand: hasStellarSteadyHand,
            CurrentStellarSteadyHandCharges: stellarSteadyHandCharges,
            CollectableMetadataKey: collectableMetadataKey,
            IsCosmic: recipe.Number == 0
        );
    }

    private static uint ResolveCosmicTemporaryAction(Recipe recipe)
    {
        if (recipe.Number != 0)
            return 0;

        try
        {
            var missionRecipeSheet = Dalamud.GameData.GetExcelSheet<WKSMissionRecipe>();
            var missionUnitSheet = Dalamud.GameData.GetExcelSheet<WKSMissionUnit>();
            var missionToDoSheet = Dalamud.GameData.GetExcelSheet<WKSMissionToDo>();
            if (missionRecipeSheet == null || missionUnitSheet == null || missionToDoSheet == null)
                return 0;

            var missionRecipe = missionRecipeSheet.FirstOrDefault(row => row.Recipe.Any(entry => entry.RowId == recipe.RowId));
            if (missionRecipe.RowId == 0)
                return 0;
            var missionUnit = missionUnitSheet.FirstOrDefault(row => row.WKSMissionRecipe.RowId == missionRecipe.RowId);
            if (missionUnit.RowId == 0 || missionUnit.MissionToDo.Count == 0)
                return 0;
            return missionToDoSheet.TryGetRow(missionUnit.MissionToDo[0].RowId, out var missionToDo)
                ? missionToDo.TemporaryAction.RowId
                : 0;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[CraftingStateBuilder] Failed to resolve Cosmic temporary action for recipe {recipe.RowId}: {ex.Message}");
            return 0;
        }
    }

    internal static unsafe uint GetDutyActionCharges(uint actionId)
    {
        var dutyActions = DutyActionManager.GetInstanceIfReady();
        if (dutyActions == null)
            return 0;
        for (var index = 0; index < 2; index++)
        {
            if (dutyActions->ActionId[index] == actionId)
                return dutyActions->CurCharges[index];
        }
        return 0;
    }

    public static CraftState? BuildCraftState(Recipe recipe)
    {
        var playerStats = GetCurrentPlayerStats();
        if (playerStats == null)
            return null;
        var recipeInfo = BuildRecipeInfo(recipe, playerStats.Level);
        return GameStateBuilder.BuildCraftState(recipeInfo, playerStats);
    }

    public static StepState BuildInitialStepState(CraftState craft)
    {
        return GameStateBuilder.BuildInitialStepState(craft, craft.InitialQuality);
    }
}
