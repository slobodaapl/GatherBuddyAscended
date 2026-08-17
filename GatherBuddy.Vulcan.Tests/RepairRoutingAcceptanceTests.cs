using System.Numerics;
using GatherBuddy.Crafting;

namespace GatherBuddy.Vulcan.Tests;

internal static class RepairRoutingAcceptanceTests
{
    public static void Run(Action<bool, string> require)
    {
        var localLoadedMender = NPC(100, 1319, new Vector3(5, 0, 5));
        var remotePreferredMender = NPC(200, 959, new Vector3(10, 0, 10));
        var loadedRoute = new RepairNPCHelper.RepairNPCRoute(localLoadedMender, 0, 0);
        var preferredRoute = new RepairNPCHelper.RepairNPCRoute(remotePreferredMender, 175, 100);

        var selected = RepairNPCHelper.SelectBestRepairRoute(
            [preferredRoute],
            loadedRoute,
            preferredRoute,
            currentTerritory: 1319,
            playerPosition: Vector3.Zero);
        require(selected?.NPC == localLoadedMender && selected.Value.AetheryteId == 0,
            "a loaded current-zone mender must prevent repair routing from teleporting to a remote preferred mender");

        var duplicateInAuxesia = NPC(300, 1319, Vector3.Zero);
        var duplicateInMare = NPC(300, 959, Vector3.One);
        RepairNPCData[] duplicateLocations = [duplicateInMare, duplicateInAuxesia];
        require(RepairNPCHelper.ResolvePreferredNPC(duplicateLocations, 300, 1319) == duplicateInAuxesia,
            "persisted repair NPC identity must include territory when one NPC data ID has multiple locations");
        require(RepairNPCHelper.ResolvePreferredNPC(duplicateLocations, 300, 0) == null,
            "legacy ambiguous repair NPC identity must fall back to current-zone menders instead of choosing an arbitrary map");
    }

    private static RepairNPCData NPC(uint dataId, uint territoryType, Vector3 position)
        => new()
        {
            DataId = dataId,
            Name = $"Mender {dataId}",
            TerritoryType = territoryType,
            Position = position,
        };
}
