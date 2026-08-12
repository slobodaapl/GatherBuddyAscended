using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.SeFunctions;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public class RepairNPCData
{
    public uint DataId { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint TerritoryType { get; set; }
    public Vector3 Position { get; set; }
    public int RepairIndex { get; set; }
}

public static class RepairNPCHelper
{
    public static List<RepairNPCData> RepairNPCs { get; } = new();

    public readonly record struct RepairNPCRoute(RepairNPCData NPC, uint AetheryteId, uint TeleportCost);

    public static RepairNPCRoute? FindBestRepairRoute(RepairNPCData? preferredNPC = null)
    {
        var currentTerritory = Dalamud.ClientState.TerritoryType;
        var playerPosition = Dalamud.Objects.LocalPlayer?.Position ?? Vector3.Zero;

        if (preferredNPC != null && TryBuildRoute(preferredNPC, currentTerritory, out var preferredRoute))
            return preferredRoute;

        var currentTerritoryNPC = RepairNPCs
            .Where(npc => npc.TerritoryType == currentTerritory)
            .OrderBy(npc => playerPosition == Vector3.Zero ? 0 : Vector3.DistanceSquared(playerPosition, npc.Position))
            .FirstOrDefault();
        if (currentTerritoryNPC != null)
            return new RepairNPCRoute(currentTerritoryNPC, 0, 0);

        var best = RepairNPCs
            .Select(npc => TryBuildRoute(npc, currentTerritory, out var route) ? route : (RepairNPCRoute?)null)
            .Where(route => route.HasValue)
            .Select(route => route!.Value)
            .OrderBy(route => route.TeleportCost)
            .ThenBy(route => route.NPC.TerritoryType)
            .ThenBy(route => route.NPC.DataId)
            .FirstOrDefault();
        return best.NPC != null ? best : null;
    }

    public static unsafe uint FindCheapestAttunedAetheryte(uint territoryId, out uint teleportCost)
    {
        teleportCost = uint.MaxValue;
        var telepo = Telepo.Instance();
        var aetheryteSheet = Dalamud.GameData.GetExcelSheet<Aetheryte>();
        if (telepo == null || aetheryteSheet == null)
            return 0;

        telepo->UpdateAetheryteList();
        var costs = new Dictionary<uint, uint>();
        for (var i = 0; i < telepo->TeleportList.Count; ++i)
        {
            var entry = telepo->TeleportList[i];
            costs[entry.AetheryteId] = entry.GilCost;
        }

        var candidate = aetheryteSheet
            .Where(aetheryte => aetheryte.IsAetheryte
                && aetheryte.Territory.RowId == territoryId
                && Teleporter.IsAttuned(aetheryte.RowId)
                && costs.ContainsKey(aetheryte.RowId))
            .Select(aetheryte => (Id: aetheryte.RowId, Cost: costs[aetheryte.RowId]))
            .OrderBy(value => value.Cost)
            .ThenBy(value => value.Id)
            .FirstOrDefault();

        if (candidate.Id == 0)
            return 0;

        teleportCost = candidate.Cost;
        return candidate.Id;
    }

    private static bool TryBuildRoute(RepairNPCData npc, uint currentTerritory, out RepairNPCRoute route)
    {
        if (npc.TerritoryType == currentTerritory)
        {
            route = new RepairNPCRoute(npc, 0, 0);
            return true;
        }

        var aetheryteId = FindCheapestAttunedAetheryte(npc.TerritoryType, out var cost);
        route = new RepairNPCRoute(npc, aetheryteId, cost);
        return aetheryteId != 0;
    }

    public static void PopulateRepairNPCs()
    {
        try
        {
            RepairNPCs.Clear();

            var territorySheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
            var eNpcResidentSheet = Dalamud.GameData.GetExcelSheet<ENpcResident>();
            var eNpcBaseSheet = Dalamud.GameData.GetExcelSheet<ENpcBase>();
            
            if (territorySheet == null || eNpcResidentSheet == null || eNpcBaseSheet == null)
            {
                GatherBuddy.Log.Error("[RepairNPCHelper] Could not get required Excel sheets");
                return;
            }

            var territories = territorySheet.ToList();
            var cityAreaTerritories = territories.Where(x => x.TerritoryIntendedUse.RowId == 0).ToList();
            var excludedTerritories = new HashSet<uint> { 1237, 1291, 573, 574, 575, 654, 985 };
            var territoriesToProcess = territories.Where(t => !excludedTerritories.Contains(t.RowId)).ToList();

            var allNpcInstances = new List<(uint DataId, uint TerritoryId, Vector3 Position)>();
            BuildNPCInstancesFromLgbFiles(territoriesToProcess, allNpcInstances);

            var repairNPCsByDataId = new Dictionary<uint, (string Name, int RepairIndex)>();
            
            foreach (var eNpcResident in eNpcResidentSheet)
            {
                if (eNpcResident.RowId == 0)
                    continue;

                if (!eNpcBaseSheet.TryGetRow(eNpcResident.RowId, out var eNpcBase))
                    continue;

                int repairIndex = -1;
                for (int i = 0; i < eNpcBase.ENpcData.Count; i++)
                {
                    if (eNpcBase.ENpcData[i].RowId == 720915)
                    {
                        repairIndex = i;
                        break;
                    }
                }

                if (repairIndex < 0)
                    continue;

                var name = ToTitleCase(eNpcResident.Singular.ExtractText());
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                repairNPCsByDataId[eNpcResident.RowId] = (name, repairIndex);
            }

            var addedNPCs = new HashSet<(uint DataId, uint TerritoryId)>();
            
            foreach (var instance in allNpcInstances)
            {
                if (!repairNPCsByDataId.TryGetValue(instance.DataId, out var npcData))
                    continue;

                if (!addedNPCs.Add((instance.DataId, instance.TerritoryId)))
                    continue;

                RepairNPCs.Add(new RepairNPCData
                {
                    DataId = instance.DataId,
                    Name = npcData.Name,
                    Position = instance.Position,
                    TerritoryType = instance.TerritoryId,
                    RepairIndex = npcData.RepairIndex
                });
            }

            RepairNPCs.Sort((first, second) =>
            {
                int cityFirst = cityAreaTerritories.FindIndex(t => t.RowId == first.TerritoryType);
                int citySecond = cityAreaTerritories.FindIndex(t => t.RowId == second.TerritoryType);

                long scoreFirst = (cityFirst < 0 ? 5000 : cityFirst) + first.TerritoryType;
                long scoreSecond = (citySecond < 0 ? 5000 : citySecond) + second.TerritoryType;

                return scoreFirst.CompareTo(scoreSecond);
            });

            RestorePreferredNPC();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[RepairNPCHelper] Error populating repair NPCs: {ex.Message}");
        }
    }
    
    public static void RestorePreferredNPC()
    {
        var config = GatherBuddy.Config.VulcanRepairConfig;
        if (config.PreferredRepairNPCDataId != 0)
        {
            config.PreferredRepairNPC = RepairNPCs.FirstOrDefault(npc => npc.DataId == config.PreferredRepairNPCDataId);
            if (config.PreferredRepairNPC != null)
            {
                GatherBuddy.Log.Information($"[RepairNPCHelper] Restored preferred repair NPC: {config.PreferredRepairNPC.Name}");
            }
            else
            {
                GatherBuddy.Log.Warning($"[RepairNPCHelper] Could not find preferred repair NPC with DataId {config.PreferredRepairNPCDataId}");
            }
        }
    }

    private static void BuildNPCInstancesFromLgbFiles(List<TerritoryType> territoryTypes, List<(uint DataId, uint TerritoryId, Vector3 Position)> instances)
    {
        foreach (var territoryType in territoryTypes)
        {
            try
            {
                var lgbFile = GetLgbFile(territoryType, "planevent.lgb");
                if (lgbFile == null)
                    continue;

                foreach (var layer in lgbFile.Layers)
                {
                    foreach (var instanceObject in layer.InstanceObjects)
                    {
                        if (instanceObject.AssetType != LayerEntryType.EventNPC)
                            continue;

                        var eNPCInstanceObject = (LayerCommon.ENPCInstanceObject)instanceObject.Object;
                        var eNpcResidentDataId = eNPCInstanceObject.ParentData.ParentData.BaseId;

                        if (eNpcResidentDataId == 0)
                            continue;

                        var position = new Vector3(
                            instanceObject.Transform.Translation.X,
                            instanceObject.Transform.Translation.Y,
                            instanceObject.Transform.Translation.Z);
                        
                        instances.Add((eNpcResidentDataId, territoryType.RowId, position));
                    }
                }
            }
            catch
            {
            }
        }
    }

    private static LgbFile? GetLgbFile(TerritoryType territoryType, string lgbFileName)
    {
        try
        {
            var bgPath = territoryType.Bg.ExtractText();
            if (string.IsNullOrEmpty(bgPath))
                return null;

            var levelIndex = bgPath.IndexOf("/level/", StringComparison.Ordinal);
            if (levelIndex < 0)
                return null;

            var path = $"bg/{bgPath.Substring(0, levelIndex + 1)}level/{lgbFileName}";
            return Dalamud.GameData.GetFile<LgbFile>(path);
        }
        catch
        {
            return null;
        }
    }
    
    private static string ToTitleCase(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        
        var textInfo = CultureInfo.InvariantCulture.TextInfo;
        return textInfo.ToTitleCase(text.ToLowerInvariant());
    }
}
