using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
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

        var loadedNPC = FindNearestLoadedRepairNPC(playerPosition);
        var loadedRoute = loadedNPC == null
            ? (RepairNPCRoute?)null
            : new RepairNPCRoute(loadedNPC, 0, 0);

        var preferredRoute = preferredNPC != null
            && TryBuildRoute(preferredNPC, currentTerritory, out var resolvedPreferredRoute)
                ? resolvedPreferredRoute
                : (RepairNPCRoute?)null;

        var routes = RepairNPCs
            .Select(npc => TryBuildRoute(npc, currentTerritory, out var route) ? route : (RepairNPCRoute?)null)
            .Where(route => route.HasValue)
            .Select(route => route!.Value)
            .ToList();

        return SelectBestRepairRoute(routes, loadedRoute, preferredRoute, currentTerritory, playerPosition);
    }

    internal static RepairNPCRoute? SelectBestRepairRoute(
        IReadOnlyList<RepairNPCRoute> routes,
        RepairNPCRoute? loadedRoute,
        RepairNPCRoute? preferredRoute,
        uint currentTerritory,
        Vector3 playerPosition)
    {
        if (loadedRoute.HasValue)
            return loadedRoute;

        if (preferredRoute.HasValue)
            return preferredRoute;

        var currentTerritoryRoute = routes
            .Where(route => route.NPC.TerritoryType == currentTerritory)
            .OrderBy(route => playerPosition == Vector3.Zero
                ? 0
                : Vector3.DistanceSquared(playerPosition, route.NPC.Position))
            .FirstOrDefault();
        if (currentTerritoryRoute.NPC != null)
            return currentTerritoryRoute;

        var best = routes
            .OrderBy(route => route.TeleportCost)
            .ThenBy(route => route.NPC.TerritoryType)
            .ThenBy(route => route.NPC.DataId)
            .FirstOrDefault();
        return best.NPC != null ? best : null;
    }

    private static RepairNPCData? FindNearestLoadedRepairNPC(Vector3 playerPosition)
    {
        var eNpcBaseSheet = Dalamud.GameData.GetExcelSheet<ENpcBase>();
        if (eNpcBaseSheet == null)
            return null;

        RepairNPCData? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var obj in Dalamud.Objects.Where(obj => obj.ObjectKind == ObjectKind.EventNpc))
        {
            if (!eNpcBaseSheet.TryGetRow(obj.BaseId, out var eNpcBase))
                continue;

            var repairIndex = -1;
            for (var i = 0; i < eNpcBase.ENpcData.Count; ++i)
            {
                if (eNpcBase.ENpcData[i].RowId != 720915)
                    continue;
                repairIndex = i;
                break;
            }

            if (repairIndex < 0)
                continue;

            var distance = playerPosition == Vector3.Zero
                ? 0
                : Vector3.DistanceSquared(playerPosition, obj.Position);
            if (distance >= nearestDistance)
                continue;

            nearestDistance = distance;
            nearest = new RepairNPCData
            {
                DataId = obj.BaseId,
                Name = obj.Name.TextValue,
                TerritoryType = Dalamud.ClientState.TerritoryType,
                Position = obj.Position,
                RepairIndex = repairIndex,
            };
        }

        return nearest;
    }

    public static uint FindCheapestAttunedAetheryte(uint territoryId, out uint teleportCost)
    {
        teleportCost = uint.MaxValue;
        var aetheryteSheet = Dalamud.GameData.GetExcelSheet<Aetheryte>();
        if (aetheryteSheet == null)
            return 0;

        var costs = new Dictionary<uint, uint>();
        var aetherytes = Dalamud.Aetherytes;
        for (var i = 0; i < aetherytes.Length; ++i)
        {
            var entry = aetherytes[i];
            if (entry == null)
                continue;
            costs[entry.AetheryteId] = entry.GilCost;
        }

        var candidate = aetheryteSheet
            .Where(aetheryte => aetheryte.IsAetheryte
                && aetheryte.Territory.RowId == territoryId
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
            config.PreferredRepairNPC = ResolvePreferredNPC(
                RepairNPCs,
                config.PreferredRepairNPCDataId,
                config.PreferredRepairNPCTerritoryType);
            if (config.PreferredRepairNPC != null)
            {
                GatherBuddy.Log.Information($"[RepairNPCHelper] Restored preferred repair NPC: {config.PreferredRepairNPC.Name}");
            }
            else
            {
                GatherBuddy.Log.Warning(
                    $"[RepairNPCHelper] Could not uniquely restore preferred repair NPC " +
                    $"{config.PreferredRepairNPCDataId}/{config.PreferredRepairNPCTerritoryType}; using current-zone menders");
                config.PreferredRepairNPCDataId = 0;
                config.PreferredRepairNPCTerritoryType = 0;
            }
        }
    }

    internal static RepairNPCData? ResolvePreferredNPC(
        IReadOnlyList<RepairNPCData> repairNPCs,
        uint dataId,
        uint territoryType)
    {
        if (dataId == 0)
            return null;

        if (territoryType != 0)
            return repairNPCs.FirstOrDefault(npc => npc.DataId == dataId && npc.TerritoryType == territoryType);

        RepairNPCData? match = null;
        foreach (var npc in repairNPCs.Where(npc => npc.DataId == dataId))
        {
            if (match != null)
                return null;
            match = npc;
        }

        return match;
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
