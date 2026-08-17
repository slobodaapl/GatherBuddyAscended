using System;
using System.Collections.Concurrent;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using static FFXIVClientStructs.FFXIV.Client.Game.InventoryType;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using GatherBuddy.Vulcan;

namespace GatherBuddy.Crafting;

public static unsafe class GearsetStatsReader
{
    private const uint CraftsmanshipParamId = 70;
    private const uint ControlParamId = 71;
    private const uint CpParamId = 11;
    private const uint PerceptionParamId = 73;
    private const int CraftsmanshipIndex = 0;
    private const int ControlIndex = 1;
    private const int CpIndex = 2;
    private const int PerceptionIndex = 3;
    private const int StatCount = 4;
    private const int MateriaSlotCount = 5;
    private const int GearsetItemCount = 14;
    private const int SpecialistSlotIndex = 13;
    private const int BaseCp = 180;
    private const int MaxEquipSlotCategoryId = 22;
    private static readonly ConcurrentDictionary<uint, string> LastReadFailureByJob = new();

    public static string RefreshGearsetFromCurrentEquipped(uint jobId)
    {
        try
        {
            var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
            if (currentJob != jobId)
            {
                var message = "Switch to the selected job before updating its saved gearset.";
                GatherBuddy.Log.Debug($"[GearsetStatsReader] {message} RequestedJob={jobId}, CurrentJob={currentJob}");
                return message;
            }

            var gearsetModule = RaptureGearsetModule.Instance();
            if (gearsetModule == null)
            {
                GatherBuddy.Log.Warning("[GearsetStatsReader] Could not update gearset: gearset module is unavailable.");
                return "Failed to update the saved gearset: gearset module unavailable.";
            }

            if (!TryResolveRefreshTarget(gearsetModule, jobId, out var gearsetIndex, out var usedCurrentGearset))
            {
                var message = "No saved gearset was found for the selected job.";
                GatherBuddy.Log.Debug($"[GearsetStatsReader] {message} JobId={jobId}");
                return message;
            }

            var updateResult = gearsetModule->UpdateGearset(gearsetIndex);
            if (updateResult < 0)
            {
                GatherBuddy.Log.Warning($"[GearsetStatsReader] Failed to update gearset {gearsetIndex} for job {jobId}. Result={updateResult}");
                return $"Failed to update saved gearset {gearsetIndex}.";
            }

            var targetDescription = usedCurrentGearset
                ? $"active saved gearset {gearsetIndex}"
                : $"first matching saved gearset {gearsetIndex}";
            var successMessage = $"Updated {targetDescription} from currently equipped items.";
            GatherBuddy.Log.Information($"[GearsetStatsReader] {successMessage}");
            return successMessage;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[GearsetStatsReader] Failed to update saved gearset for job {jobId}: {ex.Message}");
            return "Failed to update the saved gearset.";
        }
    }

    private static GameStateBuilder.PlayerStats? ReadFromCurrentlyEquipped(uint jobId, int jobLevel)
    {
        try
        {
            var craftsmanship = 0;
            var control = 0;
            var cp = BaseCp;
            var splendorCosmic = false;

            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            var materiaSheet = Dalamud.GameData.GetExcelSheet<Materia>();

            if (itemSheet == null || materiaSheet == null)
            {
                GatherBuddy.Log.Debug("[GearsetStatsReader] Item or Materia sheet is null");
                return null;
            }

            var inventoryMgr = InventoryManager.Instance();
            if (inventoryMgr == null)
                return null;

            var equippedContainer = inventoryMgr->GetInventoryContainer(InventoryType.EquippedItems);
            if (equippedContainer == null || equippedContainer->Size == 0)
                return null;
            
            for (int i = 0; i < equippedContainer->Size; i++)
            {
                var inventoryItem = equippedContainer->Items + i;
                if (inventoryItem->ItemId == 0)
                    continue;

                uint actualItemId = inventoryItem->ItemId;
                bool isHQ = inventoryItem->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);

                if (!itemSheet.TryGetRow(actualItemId, out var item))
                    return null;
                if (i == 0)
                    splendorCosmic = CraftingStateBuilder.IsSplendorCosmicTool(item.LevelEquip, item.Rarity);
                var baseStats = new int[StatCount];
                var meldStats = new int[StatCount];

                AccumulateBaseStats(item, isHQ, baseStats);

                for (int m = 0; m < MateriaSlotCount; m++)
                {
                    var materiaId = inventoryItem->Materia[m];
                    if (materiaId == 0)
                        continue;
                    if (!materiaSheet.TryGetRow(materiaId, out var materia))
                        return null;

                    AccumulateMateriaStats(materia, inventoryItem->MateriaGrades[m], meldStats);
                }

                if (!TryCalculateEffectiveSlotStat(i, item, CraftsmanshipParamId, baseStats[CraftsmanshipIndex], meldStats[CraftsmanshipIndex], out var itemCraftsmanship)
                 || !TryCalculateEffectiveSlotStat(i, item, ControlParamId, baseStats[ControlIndex], meldStats[ControlIndex], out var itemControl)
                 || !TryCalculateEffectiveSlotStat(i, item, CpParamId, baseStats[CpIndex], meldStats[CpIndex], out var itemCp))
                    return null;
                craftsmanship += itemCraftsmanship;
                control += itemControl;
                cp += itemCp;
            }

            var manipulation = IsManipulationUnlocked(jobId);
            var isSpecialist = equippedContainer->Size > SpecialistSlotIndex && (equippedContainer->Items + SpecialistSlotIndex)->ItemId != 0;

            return new GameStateBuilder.PlayerStats(
                Craftsmanship: craftsmanship,
                Control: control,
                CP: cp,
                Level: jobLevel,
                Manipulation: manipulation,
                Specialist: isSpecialist,
                SplendorCosmic: splendorCosmic,
                CrafterDelineations: CraftingSpecialistResources.GetCrafterDelineationCount()
            );
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[GearsetStatsReader] Failed to read currently equipped stats: {ex.Message}");
            return null;
        }
    }

    private static bool TryResolveRefreshTarget(RaptureGearsetModule* gearsetModule, uint jobId, out int gearsetIndex, out bool usedCurrentGearset)
    {
        gearsetIndex = -1;
        usedCurrentGearset = false;

        var currentGearsetIndex = gearsetModule->CurrentGearsetIndex;
        if (IsMatchingGearset(gearsetModule, currentGearsetIndex, jobId))
        {
            gearsetIndex = currentGearsetIndex;
            usedCurrentGearset = true;
            return true;
        }

        return TryResolveExistingGearsetIndex(gearsetModule, jobId, out gearsetIndex);
    }

    internal static bool TryResolveExistingGearsetIndex(RaptureGearsetModule* gearsetModule, uint jobId, out int gearsetIndex)
    {
        for (int i = 0; i < 100; i++)
        {
            if (!IsMatchingGearset(gearsetModule, i, jobId))
                continue;

            gearsetIndex = i;
            return true;
        }

        gearsetIndex = -1;
        return false;
    }

    private static bool IsMatchingGearset(RaptureGearsetModule* gearsetModule, int gearsetIndex, uint jobId)
    {
        if (gearsetIndex < 0 || gearsetIndex >= 100)
            return false;

        var gearset = gearsetModule->Entries[gearsetIndex];
        return (gearset.Flags & RaptureGearsetModule.GearsetFlag.Exists) != 0 && gearset.ClassJob == jobId;
    }

    private static void AccumulateBaseStats(Item item, bool isHighQuality, Span<int> baseStats)
    {
        var baseParams = item.BaseParam;
        var baseParamValues = item.BaseParamValue;

        var baseParamIndex = 0;
        foreach (var paramRef in baseParams)
        {
            AddStatValue(baseStats, paramRef.RowId, baseParamValues[baseParamIndex]);
            baseParamIndex++;
        }

        if (!isHighQuality || item.BaseParamSpecial.Count == 0)
            return;

        var hqValues = item.BaseParamValueSpecial;
        var hqParamIndex = 0;
        foreach (var paramRef in item.BaseParamSpecial)
        {
            AddStatValue(baseStats, paramRef.RowId, hqValues[hqParamIndex]);
            hqParamIndex++;
        }
    }

    private static void AccumulateMateriaStats(Materia materia, byte grade, Span<int> meldStats)
    {
        AddStatValue(meldStats, materia.BaseParam.RowId, materia.Value[grade]);
    }

    private static void AddStatValue(Span<int> stats, uint paramId, int value)
    {
        if (value == 0 || !TryGetStatIndex(paramId, out var statIndex))
            return;

        stats[statIndex] += value;
    }

    private static bool TryGetStatIndex(uint paramId, out int statIndex)
    {
        statIndex = paramId switch
        {
            CraftsmanshipParamId => CraftsmanshipIndex,
            ControlParamId => ControlIndex,
            CpParamId => CpIndex,
            PerceptionParamId => PerceptionIndex,
            _ => -1
        };
        return statIndex >= 0;
    }

    private static bool TryCalculateEffectiveItemStat(Item item, uint paramId, int baseValue, int meldedValue, out int value)
    {
        var uncappedValue = baseValue + meldedValue;
        if (uncappedValue == 0)
        {
            value = 0;
            return true;
        }

        if (!TryGetItemStatCap(item, paramId, baseValue, out var cap))
        {
            value = 0;
            return false;
        }

        value = Math.Min(uncappedValue, cap);
        return true;
    }

    private static bool TryCalculateEffectiveSlotStat(
        int slotIndex,
        Item item,
        uint paramId,
        int baseValue,
        int meldedValue,
        out int value)
    {
        if (TryResolveUncappedSpecialistStat(slotIndex, baseValue, meldedValue, out value))
            return true;

        return TryCalculateEffectiveItemStat(item, paramId, baseValue, meldedValue, out value);
    }

    internal static bool TryResolveUncappedSpecialistStat(
        int slotIndex,
        int baseValue,
        int meldedValue,
        out int value)
    {
        value = baseValue + meldedValue;
        return slotIndex == SpecialistSlotIndex;
    }

    private static bool TryGetItemStatCap(Item item, uint paramId, int baseValue, out int cap)
    {
        cap = 0;

        var slotCategoryId = (int)item.EquipSlotCategory.RowId;
        if (slotCategoryId <= 0 || slotCategoryId > MaxEquipSlotCategoryId)
        {
            GatherBuddy.Log.Debug($"[GearsetStatsReader] Missing slot cap data for item {item.RowId} and stat {paramId}: slot category {slotCategoryId} is out of range.");
            return false;
        }

        var levelStatValue = GetItemLevelStat(item, paramId);
        if (levelStatValue <= 0)
        {
            cap = baseValue;
            return true;
        }

        var baseParamSheet = Dalamud.GameData.GetExcelSheet<RawRow>(name: "BaseParam");
        if (baseParamSheet == null)
        {
            GatherBuddy.Log.Debug($"[GearsetStatsReader] Missing BaseParam sheet while calculating cap for item {item.RowId} and stat {paramId}.");
            return false;
        }

        if (!baseParamSheet.TryGetRow(paramId, out var baseParamRow))
        {
            GatherBuddy.Log.Debug($"[GearsetStatsReader] Missing BaseParam row {paramId} while calculating cap for item {item.RowId}.");
            return false;
        }

        var slotModifier = baseParamRow.ReadInt16Column(slotCategoryId + 3);
        if (slotModifier <= 0)
        {
            GatherBuddy.Log.Debug($"[GearsetStatsReader] Missing slot modifier for item {item.RowId}, stat {paramId}, slot category {slotCategoryId}.");
            return false;
        }

        cap = Math.Max(
            baseValue,
            (int)Math.Round(levelStatValue * slotModifier / 1000d, MidpointRounding.AwayFromZero));
        return true;
    }

    private static int GetItemLevelStat(Item item, uint paramId)
    {
        var levelItem = item.LevelItem.Value;
        return paramId switch
        {
            CraftsmanshipParamId => levelItem.Craftsmanship,
            ControlParamId => levelItem.Control,
            CpParamId => levelItem.CP,
            PerceptionParamId => levelItem.Perception,
            _ => 0
        };
    }

    public static bool TryReadGearsetPerception(uint jobId, out int perception)
    {
        perception = 0;
        try
        {
            if (Dalamud.Objects.LocalPlayer?.ClassJob.RowId == jobId)
            {
                var playerState = PlayerState.Instance();
                if (playerState == null)
                    return false;
                perception = playerState->Attributes[(int)PerceptionParamId];
                return true;
            }

            var gearsetModule = RaptureGearsetModule.Instance();
            if (gearsetModule == null
             || !TryResolveExistingGearsetIndex(gearsetModule, jobId, out var gearsetIndex))
                return false;

            fixed (RaptureGearsetModule.GearsetEntry* entries = gearsetModule->Entries)
                return TryCalculatePerceptionFromGearset(&entries[gearsetIndex], out perception);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[GearsetStatsReader] Failed to read perception for job {jobId}: {ex.Message}");
            return false;
        }
    }

    private static bool TryCalculatePerceptionFromGearset(
        RaptureGearsetModule.GearsetEntry* gearset,
        out int perception)
    {
        perception = 0;
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        var materiaSheet = Dalamud.GameData.GetExcelSheet<Materia>();
        if (itemSheet == null || materiaSheet == null)
            return false;

        for (var i = 0; i < GearsetItemCount; i++)
        {
            var gearItem = gearset->Items[i];
            if (gearItem.ItemId == 0)
                continue;

            var actualItemId = gearItem.ItemId % 1_000_000;
            var isHighQuality = gearItem.ItemId >= 1_000_000;
            if (!itemSheet.TryGetRow(actualItemId, out var item))
                return false;

            var baseStats = new int[StatCount];
            var meldStats = new int[StatCount];
            AccumulateBaseStats(item, isHighQuality, baseStats);
            for (var materiaIndex = 0; materiaIndex < MateriaSlotCount; materiaIndex++)
            {
                var materiaId = gearItem.Materia[materiaIndex];
                if (materiaId == 0)
                    continue;
                if (!materiaSheet.TryGetRow(materiaId, out var materia))
                    return false;
                AccumulateMateriaStats(materia, gearItem.MateriaGrades[materiaIndex], meldStats);
            }

            if (!TryCalculateEffectiveItemStat(
                    item,
                    PerceptionParamId,
                    baseStats[PerceptionIndex],
                    meldStats[PerceptionIndex],
                    out var itemPerception))
                return false;
            perception += itemPerception;
        }

        return true;
    }

    public static GameStateBuilder.PlayerStats? ReadGearsetStatsForJob(uint jobId)
    {
        try
        {
            if (!CraftingJobLevelReader.TryRead(jobId, out var jobLevel) || jobLevel <= 0)
            {
                GatherBuddy.Log.Warning($"[GearsetStatsReader] Could not read the current level for job {jobId}.");
                return null;
            }

            var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
            
            if (currentJob == jobId)
            {
                var equippedStats = ReadFromCurrentlyEquipped(jobId, jobLevel);
                
                if (equippedStats != null && equippedStats.Craftsmanship > 0)
                {
                    LastReadFailureByJob.TryRemove(jobId, out _);
                    return equippedStats;
                }
            }

            var gearsetModule = RaptureGearsetModule.Instance();
            if (gearsetModule == null)
                return null;

            fixed (RaptureGearsetModule.GearsetEntry* entries = gearsetModule->Entries)
            {
                var matchingGearsets = 0;
                var failures = new System.Collections.Generic.List<string>();
                for (int i = 0; i < 100; i++)
                {
                    if ((entries[i].Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0)
                        continue;

                    if (entries[i].ClassJob != jobId)
                        continue;

                    matchingGearsets++;
                    var stats = CalculateStatsFromGearset(&entries[i], jobId, jobLevel, out var failureReason);
                    if (stats != null && stats.Craftsmanship > 0)
                    {
                        LastReadFailureByJob.TryRemove(jobId, out _);
                        return stats;
                    }
                    failures.Add($"gearset {i}: {failureReason}");
                }

                ReportReadFailure(
                    jobId,
                    matchingGearsets == 0
                        ? "no saved gearset entry exists"
                        : $"{matchingGearsets} saved gearset(s) found but none were readable ({string.Join("; ", failures)})");
            }
            return null;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[GearsetStatsReader] Failed to read gearset stats for job {jobId}: {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    private static GameStateBuilder.PlayerStats? CalculateStatsFromGearset(
        RaptureGearsetModule.GearsetEntry* gearset,
        uint jobId,
        int jobLevel,
        out string failureReason)
    {
        failureReason = string.Empty;
        try
        {
            var craftsmanship = 0;
            var control = 0;
            var cp = BaseCp;
            var splendorCosmic = false;

            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            var materiaSheet = Dalamud.GameData.GetExcelSheet<Materia>();

            if (itemSheet == null || materiaSheet == null)
            {
                failureReason = "Item or Materia sheet is unavailable";
                return null;
            }

            for (int i = 0; i < GearsetItemCount; i++)
            {
                var gearItem = gearset->Items[i];
                if (gearItem.ItemId == 0)
                    continue;

                uint actualItemId = gearItem.ItemId % 1000000;
                bool isHQ = gearItem.ItemId >= 1000000;
                if (!itemSheet.TryGetRow(actualItemId, out var item))
                {
                    failureReason = $"slot {i} references unknown item {actualItemId}";
                    return null;
                }
                if (i == 0)
                    splendorCosmic = CraftingStateBuilder.IsSplendorCosmicTool(item.LevelEquip, item.Rarity);
                var baseStats = new int[StatCount];
                var meldStats = new int[StatCount];

                AccumulateBaseStats(item, isHQ, baseStats);

                for (int m = 0; m < MateriaSlotCount; m++)
                {
                    var materiaId = gearItem.Materia[m];
                    if (materiaId == 0)
                        continue;
                    if (!materiaSheet.TryGetRow(materiaId, out var materia))
                    {
                        failureReason = $"slot {i} materia {m} references unknown materia {materiaId}";
                        return null;
                    }

                    AccumulateMateriaStats(materia, gearItem.MateriaGrades[m], meldStats);
                }

                if (!TryCalculateEffectiveSlotStat(i, item, CraftsmanshipParamId, baseStats[CraftsmanshipIndex], meldStats[CraftsmanshipIndex], out var itemCraftsmanship)
                 || !TryCalculateEffectiveSlotStat(i, item, ControlParamId, baseStats[ControlIndex], meldStats[ControlIndex], out var itemControl)
                 || !TryCalculateEffectiveSlotStat(i, item, CpParamId, baseStats[CpIndex], meldStats[CpIndex], out var itemCp))
                {
                    failureReason = $"slot {i} item {actualItemId} has unavailable stat-cap data";
                    return null;
                }
                craftsmanship += itemCraftsmanship;
                control += itemControl;
                cp += itemCp;
            }

            var manipulation = IsManipulationUnlocked(jobId);
            var isSpecialist = gearset->Items[SpecialistSlotIndex].ItemId != 0;

            return new GameStateBuilder.PlayerStats(
                Craftsmanship: craftsmanship,
                Control: control,
                CP: cp,
                Level: jobLevel,
                Manipulation: manipulation,
                Specialist: isSpecialist,
                SplendorCosmic: splendorCosmic,
                CrafterDelineations: CraftingSpecialistResources.GetCrafterDelineationCount()
            );
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            return null;
        }
    }

    private static void ReportReadFailure(uint jobId, string reason)
    {
        if (LastReadFailureByJob.TryGetValue(jobId, out var previous) && previous == reason)
            return;
        LastReadFailureByJob[jobId] = reason;
        GatherBuddy.Log.Warning($"[GearsetStatsReader] Failed to read stats for job {jobId}: {reason}");
    }

    private static bool IsManipulationUnlocked(uint jobId)
    {
        try
        {
            var manipulationQuestId = jobId switch
            {
                8 => 67979u,   // CRP
                9 => 68153u,   // BSM
                10 => 68132u,  // ARM
                11 => 68137u,  // GSM
                12 => 68147u,  // LTW
                13 => 67969u,  // WVR
                14 => 67974u,  // ALC
                15 => 68142u,  // CUL
                _ => 0u
            };

            if (manipulationQuestId == 0)
                return false;

            return QuestManager.IsQuestComplete(manipulationQuestId);
        }
        catch
        {
            return false;
        }
    }
    
    private static ItemFood? GetItemConsumableProperties(Item item, bool hq)
    {
        if (!item.ItemAction.IsValid)
            return null;
        var action = item.ItemAction.Value;
        var actionParams = hq ? action.DataHQ : action.Data;
        if (actionParams[0] is not 48 and not 49)
            return null;
        return Dalamud.GameData.GetExcelSheet<ItemFood>()?.GetRow(actionParams[1]);
    }
    
    public static (int craftsmanship, int control, int cp) CalculateConsumableBonus(uint itemId, bool isHQ, int baseCraftsmanship, int baseControl, int baseCP)
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet == null || !itemSheet.TryGetRow(itemId, out var item))
            return (0, 0, 0);
        
        var food = GetItemConsumableProperties(item, isHQ);
        if (food == null)
            return (0, 0, 0);
        
        int craftBonus = 0;
        int controlBonus = 0;
        int cpBonus = 0;
        
        foreach (var p in food.Value.Params)
        {
            if (p.BaseParam.RowId == 70) // Craftsmanship
            {
                var val = isHQ ? p.ValueHQ : p.Value;
                var max = isHQ ? p.MaxHQ : p.Max;
                if (p.IsRelative)
                    craftBonus = Math.Min(max, baseCraftsmanship * val / 100);
                else
                    craftBonus = val;
            }
            else if (p.BaseParam.RowId == 71) // Control
            {
                var val = isHQ ? p.ValueHQ : p.Value;
                var max = isHQ ? p.MaxHQ : p.Max;
                if (p.IsRelative)
                    controlBonus = Math.Min(max, baseControl * val / 100);
                else
                    controlBonus = val;
            }
            else if (p.BaseParam.RowId == 11) // CP
            {
                var val = isHQ ? p.ValueHQ : p.Value;
                var max = isHQ ? p.MaxHQ : p.Max;
                if (p.IsRelative)
                    cpBonus = Math.Min(max, baseCP * val / 100);
                else
                    cpBonus = val;
            }
        }
        
        return (craftBonus, controlBonus, cpBonus);
    }
    
    public static GameStateBuilder.PlayerStats ApplyConsumablesToStats(GameStateBuilder.PlayerStats baseStats, RecipeCraftSettings? settings)
    {
        var foodId     = settings?.FoodItemId;
        var foodHQ     = settings?.FoodHQ ?? false;
        var medicineId = settings?.MedicineItemId;
        var medicineHQ = settings?.MedicineHQ ?? false;
        return ApplyConsumablesToStats(baseStats, foodId, foodHQ, medicineId, medicineHQ);
    }

    public static GameStateBuilder.PlayerStats ApplyConsumablesToStats(GameStateBuilder.PlayerStats baseStats, uint? foodId, bool foodHQ, uint? medicineId, bool medicineHQ)
    {
        var craftsmanship = baseStats.Craftsmanship;
        var control = baseStats.Control;
        var cp = baseStats.CP;

        if (foodId.HasValue)
        {
            var (craftBonus, controlBonus, cpBonus) = CalculateConsumableBonus(foodId.Value, foodHQ, craftsmanship, control, cp);
            craftsmanship += craftBonus;
            control += controlBonus;
            cp += cpBonus;
        }

        if (medicineId.HasValue)
        {
            var (craftBonus, controlBonus, cpBonus) = CalculateConsumableBonus(medicineId.Value, medicineHQ, craftsmanship, control, cp);
            craftsmanship += craftBonus;
            control += controlBonus;
            cp += cpBonus;
        }

        return new GameStateBuilder.PlayerStats(
            Craftsmanship: craftsmanship,
            Control: control,
            CP: cp,
            Level: baseStats.Level,
            Manipulation: baseStats.Manipulation,
            Specialist: baseStats.Specialist,
            SplendorCosmic: baseStats.SplendorCosmic,
            CrafterDelineations: baseStats.CrafterDelineations
        );
    }
}
