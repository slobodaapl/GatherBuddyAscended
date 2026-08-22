using System;
using System.Linq;
using Dalamud;
using Dalamud.Game;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.FcMesh.Chest;

public readonly record struct FcPublicChestDestinationResolution(
    bool Succeeded,
    FcPublicChestDestination Destination,
    FcChestObjectSearch Search,
    string Error)
{
    public static FcPublicChestDestinationResolution Blocked(
        FcPublicChestDestination destination,
        string error)
        => new(false, destination, default, error);
}

/// <summary>
/// Resolves the immutable public catalog through current English Lumina
/// Territory/Map/EObj rows. Catalog coordinates only produce a search
/// vicinity; callers must resolve a unique live EventObj and navigate to its
/// current runtime position.
/// </summary>
public sealed class FcPublicChestDestinationResolver
{
    private readonly FcDalamudChestObjectResolver _objects;

    public FcPublicChestDestinationResolver(FcDalamudChestObjectResolver? objects = null)
        => _objects = objects ?? new FcDalamudChestObjectResolver();

    public FcPublicChestDestinationResolution BuildSearch(FcPublicChestDestination destination)
    {
        if (destination is null)
            return FcPublicChestDestinationResolution.Blocked(destination!, "Public Company Chest destination is unavailable.");
        if (!IsValidDestination(destination))
            return FcPublicChestDestinationResolution.Blocked(
                destination,
                "Public Company Chest destination contains an invalid route or map anchor.");

        try
        {
            var territories = Dalamud.GameData.GetExcelSheet<TerritoryType>(ClientLanguage.English);
            var maps = Dalamud.GameData.GetExcelSheet<Map>(ClientLanguage.English);
            var names = Dalamud.GameData.GetExcelSheet<EObjName>(ClientLanguage.English);
            var definitions = Dalamud.GameData.GetExcelSheet<EObj>(ClientLanguage.English);
            if (territories is null || maps is null || names is null || definitions is null)
                return FcPublicChestDestinationResolution.Blocked(destination, "Lumina public destination rows are unavailable.");

            var territoryMatches = territories
                .Where(row => string.Equals(
                    row.PlaceName.RowId == 0 ? string.Empty : row.PlaceName.Value.Name.ExtractText(),
                    destination.Zone,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (territoryMatches.Length != 1)
                return FcPublicChestDestinationResolution.Blocked(
                    destination,
                    territoryMatches.Length == 0
                        ? "Public destination zone has no unique Lumina TerritoryType row."
                        : "Public destination zone maps to multiple TerritoryType rows.");

            var territory = territoryMatches[0];
            if (territory.Map.RowId == 0 || !maps.TryGetRow(territory.Map.RowId, out var map) || map.SizeFactor == 0)
                return FcPublicChestDestinationResolution.Blocked(destination, "Public destination Map row is unavailable.");

            var matchingNames = names
                .Where(row => string.Equals(
                    row.Singular.ExtractText(),
                    destination.ObjectHint,
                    StringComparison.OrdinalIgnoreCase))
                .Select(row => row.RowId)
                .ToArray();
            if (matchingNames.Length == 0)
                return FcPublicChestDestinationResolution.Blocked(
                    destination,
                    "Public destination object hint has no exact Lumina EObjName row.");

            var handlers = matchingNames
                .Select(id => definitions.TryGetRow(id, out var definition)
                    ? definition.Data.RowId
                    : 0u)
                .Where(id => id != 0)
                .Distinct()
                .ToArray();
            if (handlers.Length != 1)
                return FcPublicChestDestinationResolution.Blocked(
                    destination,
                    handlers.Length == 0
                        ? "Public destination object hint has no Lumina EObj definition."
                        : "Public destination object hint resolves to multiple Lumina EObj handlers.");

            var definitionIds = definitions
                .Where(definition => definition.Data.RowId == handlers[0])
                .Where(definition => names.TryGetRow(definition.RowId, out var name)
                    && string.Equals(
                        name.Singular.ExtractText(),
                        destination.ObjectHint,
                        StringComparison.OrdinalIgnoreCase))
                .Select(definition => definition.RowId)
                .Distinct()
                .ToArray();
            if (definitionIds.Length != 1 || matchingNames.Length != 1)
                return FcPublicChestDestinationResolution.Blocked(
                    destination,
                    definitionIds.Length == 0
                        ? "Public destination object hint has no stable EObj BaseId."
                        : "Public destination object hint resolves to multiple stable EObj BaseIds.");

            var anchor = FcChestMapCoordinates.ToWorldAnchor(
                destination.ApproximateMapX,
                destination.ApproximateMapY,
                map.SizeFactor,
                map.OffsetX,
                map.OffsetY);
            var search = new FcChestObjectSearch(
                definitionIds[0],
                matchingNames[0],
                territory.RowId,
                anchor,
                30f,
                "EventObj");
            return new(true, destination, search, string.Empty);
        }
        catch (Exception exception)
        {
            return FcPublicChestDestinationResolution.Blocked(
                destination,
                $"Public destination Lumina resolution failed: {exception.Message}");
        }
    }

    public FcChestObjectResolution ResolveLive(FcPublicChestDestination destination)
    {
        var search = BuildSearch(destination);
        return search.Succeeded
            ? _objects.Resolve(search.Search)
            : new(FcChestObjectResolutionStatus.InvalidSearch, null, search.Error);
    }

    public bool TryOpenLive(FcPublicChestDestination destination, out string error)
    {
        var search = BuildSearch(destination);
        if (!search.Succeeded || !search.Search.IsValid)
        {
            error = search.Error.Length == 0
                ? "Public Company Chest stable object resolution is unavailable."
                : search.Error;
            return false;
        }
        if (!_objects.TryOpenSearch(search.Search, out error))
            return false;
        return true;
    }

    private static bool IsValidDestination(FcPublicChestDestination destination)
        => IsSafe(destination.Region)
            && IsSafe(destination.Zone)
            && IsSafe(destination.AethernetShard)
            && IsSafe(destination.ObjectHint)
            && float.IsFinite(destination.ApproximateMapX)
            && float.IsFinite(destination.ApproximateMapY)
            && destination.ApproximateMapX is >= 0f and <= 1000f
            && destination.ApproximateMapY is >= 0f and <= 1000f;

    private static bool IsSafe(string value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= 256
            && !value.Contains('\0')
            && !value.Contains('\r')
            && !value.Contains('\n');
}
