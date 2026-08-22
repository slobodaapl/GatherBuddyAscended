using System;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using GatherBuddy.Plugin;
using GatherBuddy.SeFunctions;
using GatherBuddy.FcMesh.Protocol;
using Lumina.Excel.Sheets;

namespace GatherBuddy.FcMesh.Chest;

public readonly record struct FcLiveChestObjectEvidenceResult(
    bool Succeeded,
    FcChestLiveObject? Object,
    string Error);

/// <summary>
/// Direct current-object-table evidence for registration. Housing address and
/// map-row environment are intentionally separate facts; if their installed
/// API is unavailable this provider returns an explicit block rather than
/// inventing an address or using a localized name.
/// </summary>
public unsafe sealed class FcLiveChestEvidenceProvider
{
    public bool TryResolveReadyChestAddon(out string error)
    {
        if (!FcChestSnapshotReader.TryGetReadyVisibleChestAddon(out _))
        {
            error = "The FreeCompanyChest addon is not visible and ready; open the Company Chest before registration.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public FcLiveChestObjectEvidenceResult ResolveCurrentTarget()
    {
        var target = Dalamud.Targets.Target;
        if (target is null)
            return new(false, null, "Select the live Company Chest object before registration.");
        if (target.ObjectKind != ObjectKind.EventObj)
            return new(false, null, "The selected object is not a Company Chest EventObj.");
        if (target.BaseId == 0 || !target.IsTargetable)
            return new(false, null, "The selected Company Chest lacks a targetable stable EObj identity.");

        var matches = Dalamud.Objects
            .Where(value => value.ObjectKind == ObjectKind.EventObj)
            .Where(value => value.BaseId == target.BaseId)
            .Select(value => new FcChestLiveObject(
                value.BaseId,
                0,
                value.ObjectKind.ToString(),
                value.Name.ToString(),
                value.Position,
                value.IsTargetable))
            .ToArray();
        if (matches.Length != 1)
            return new(
                false,
                null,
                matches.Length == 0
                    ? "The selected stable Company Chest is no longer present."
                    : "Multiple live objects share the selected Company Chest identity; registration is blocked.");
        if (!matches[0].IsTargetable)
            return new(false, null, "The live Company Chest object is not targetable.");
        return new(true, matches[0], string.Empty);
    }

    public bool TryResolveCurrentEnvironment(
        FcChestLiveObject liveObject,
        out FcChestLocationEnvironment environment,
        out string error)
    {
        environment = new FcChestLocationEnvironment(0, 0, string.Empty, 0f, 0f);
        var territoryId = Dalamud.ClientState.TerritoryType;
        var territorySheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
        var mapSheet = Dalamud.GameData.GetExcelSheet<Map>();
        if (territorySheet is null || mapSheet is null
            || !territorySheet.TryGetRow(territoryId, out var territory)
            || territory.Map.RowId == 0
            || !mapSheet.TryGetRow(territory.Map.RowId, out var map)
            || map.SizeFactor == 0)
        {
            error = "Current territory/map Lumina rows are unavailable; environment capture is blocked.";
            return false;
        }

        const double factor = 0.019999999552965164d;
        var mapX = (float)((factor * map.OffsetX) + (2048d / map.SizeFactor) + (factor * liveObject.Position.X) + 1d);
        var mapY = (float)((factor * map.OffsetY) + (2048d / map.SizeFactor) + (factor * liveObject.Position.Z) + 1d);
        var territoryName = territory.PlaceName.RowId != 0
            ? territory.PlaceName.Value.Name.ToString()
            : string.Empty;
        if (string.IsNullOrWhiteSpace(territoryName)
            || !float.IsFinite(mapX) || !float.IsFinite(mapY))
        {
            error = "Current territory/map environment is incomplete.";
            return false;
        }
        environment = new FcChestLocationEnvironment(
            territoryId,
            territory.Map.RowId,
            territoryName,
            mapX,
            mapY);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Captures the current plot address only from authoritative live state:
    /// native HousingManager proves an indoor owned FC estate, while
    /// Lifestream supplies the residential district and zero-based ward/plot
    /// tuple. The persisted address is one-based, matching Lifestream routing.
    /// </summary>
    public bool TryResolveCurrentHousingAddress(
        out FcHousingAddress address,
        out uint originalHouseTerritoryTypeId,
        out string error)
    {
        address = null!;
        originalHouseTerritoryTypeId = 0;
        if (!HousingManager.TryGetCurrentFreeCompanyEstate(
                out _,
                out _,
                out originalHouseTerritoryTypeId,
                out var currentDivision,
                out var identityError))
        {
            error = identityError;
            return false;
        }
        if (!Lifestream.TryGetCurrentPlotInfo(
                out var residentialKind,
                out var zeroBasedWard,
                out var zeroBasedPlot,
                out error))
            return false;
        if (!TryMapHousingDistrict(residentialKind, out var housingDistrict))
        {
            error = $"Lifestream returned unsupported residential district kind {residentialKind}.";
            return false;
        }
        if (!TryMatchOriginalHouseTerritory(originalHouseTerritoryTypeId, housingDistrict, out error))
            return false;

        var player = Dalamud.Objects.LocalPlayer;
        if (player is null || player.CurrentWorld.RowId == 0)
        {
            error = "Current player/world is unavailable for FC housing registration.";
            return false;
        }
        var worlds = Dalamud.GameData.GetExcelSheet<World>();
        if (worlds is null || !worlds.TryGetRow(player.CurrentWorld.RowId, out var world))
        {
            error = "Current world Lumina row is unavailable for FC housing registration.";
            return false;
        }
        var worldName = world.Name.ExtractText();
        var region = world.DataCenter.ValueNullable?.Name.ExtractText() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(worldName) || string.IsNullOrWhiteSpace(region))
        {
            error = "Current world/data-center identity is incomplete.";
            return false;
        }
        if (zeroBasedWard > 29 || zeroBasedPlot > 59)
        {
            error = "Current housing ward/plot tuple is outside the bounded route range.";
            return false;
        }
        address = new FcHousingAddress(
            worldName,
            region,
            checked((uint)zeroBasedWard + 1u),
            checked((uint)zeroBasedPlot + 1u),
            currentDivision == 2,
            housingDistrict);
        error = string.Empty;
        return true;
    }

    private static bool TryMapHousingDistrict(int residentialKind, out string district)
    {
        district = residentialKind switch
        {
            2 => "Lavender Beds",
            8 => "Mist",
            9 => "Goblet",
            70 => "Empyreum",
            111 => "Shirogane",
            _ => string.Empty,
        };
        return district.Length != 0;
    }

    private static bool TryMatchOriginalHouseTerritory(
        uint originalTerritoryId,
        string housingDistrict,
        out string error)
    {
        var territories = Dalamud.GameData.GetExcelSheet<TerritoryType>();
        if (territories is null
            || !territories.TryGetRow(originalTerritoryId, out var territory)
            || territory.PlaceName.RowId == 0)
        {
            error = "Original FC house territory Lumina evidence is unavailable.";
            return false;
        }
        var originalName = territory.PlaceName.Value.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(originalName)
            || !originalName.Contains(housingDistrict, StringComparison.OrdinalIgnoreCase))
        {
            error = "Native original-house territory does not match the current Lifestream housing district.";
            return false;
        }
        error = string.Empty;
        return true;
    }
}
