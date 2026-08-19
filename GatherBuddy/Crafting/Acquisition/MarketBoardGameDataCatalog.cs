using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Utility;
using GatherBuddy.Plugin;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting.Acquisition;

internal static class MarketBoardGameDataCatalog
{
    private const string MarketBoardName = "market board";

    private static readonly string[] CandidateLgbFileNames =
    [
        "bg.lgb",
        "planevent.lgb",
        "planlive.lgb",
        "planner.lgb",
    ];

    private static readonly Lazy<DefinitionCatalog> Definitions = new(
        BuildDefinitions,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentDictionary<uint, Lazy<MarketBoardTerritoryData>> Territories = new();

    public static void WarmInBackground()
        => _ = Task.Run(() => _ = Definitions.Value);

    public static bool IsKnownDefinition(uint baseId)
        => Definitions.IsValueCreated
        && Definitions.Value.Failure == null
        && Definitions.Value.Ids.Contains(baseId);

    public static MarketBoardTerritoryData ResolveTerritory(uint territoryId)
    {
        var definitions = Definitions.Value;
        if (definitions.Failure != null)
            return MarketBoardTerritoryData.Unavailable(definitions.Ids, definitions.Failure);
        if (territoryId == 0)
            return MarketBoardTerritoryData.Unavailable(definitions.Ids, "The current territory is unavailable.");

        return Territories.GetOrAdd(
            territoryId,
            id => new Lazy<MarketBoardTerritoryData>(
                () => BuildTerritory(id, definitions.Ids),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static DefinitionCatalog BuildDefinitions()
    {
        try
        {
            var names = Dalamud.GameData.GetExcelSheet<EObjName>(ClientLanguage.English);
            var definitions = Dalamud.GameData.GetExcelSheet<EObj>(ClientLanguage.English);
            if (names == null || definitions == null)
                return DefinitionCatalog.Unavailable("The English EObj/EObjName sheets are unavailable.");

            var namedIds = names
                .Where(row => IsMarketBoardName(row.Singular.ExtractText()))
                .Select(row => row.RowId)
                .ToArray();
            if (namedIds.Length == 0)
                return DefinitionCatalog.Unavailable("No EObjName row identifies a market board.");

            var handlerIds = new HashSet<uint>();
            foreach (var id in namedIds)
            {
                if (!definitions.TryGetRow(id, out var definition) || definition.Data.RowId == 0)
                    return DefinitionCatalog.Unavailable($"Market-board EObjName row {id} has no usable EObj definition.");
                handlerIds.Add(definition.Data.RowId);
            }

            if (handlerIds.Count != 1)
                return DefinitionCatalog.Unavailable(
                    $"Market-board EObj definitions resolve to {handlerIds.Count} distinct handlers; refusing an ambiguous match.");

            var handlerId = handlerIds.Single();
            var ids = definitions
                .Where(definition => definition.Data.RowId == handlerId)
                .Select(definition => definition.RowId)
                .ToHashSet();
            if (ids.Count == 0)
                return DefinitionCatalog.Unavailable("The market-board EObj handler has no definitions.");

            foreach (var id in ids)
            {
                if (!names.TryGetRow(id, out var name) || !IsMarketBoardName(name.Singular.ExtractText()))
                    return DefinitionCatalog.Unavailable(
                        $"Market-board EObj handler also owns non-market definition {id}; refusing an ambiguous match.");
            }

            return new DefinitionCatalog(ids, null);
        }
        catch (Exception exception)
        {
            return DefinitionCatalog.Unavailable($"Could not resolve market-board EObj definitions: {exception.Message}");
        }
    }

    private static MarketBoardTerritoryData BuildTerritory(uint territoryId, IReadOnlySet<uint> definitionIds)
    {
        try
        {
            var territories = Dalamud.GameData.GetExcelSheet<TerritoryType>(ClientLanguage.English);
            if (territories == null || !territories.TryGetRow(territoryId, out var territory))
                return MarketBoardTerritoryData.Unavailable(
                    definitionIds,
                    $"TerritoryType row {territoryId} is unavailable.");

            var bgPath = territory.Bg.ExtractText();
            var levelIndex = bgPath.IndexOf("/level/", StringComparison.Ordinal);
            if (levelIndex < 0)
                return MarketBoardTerritoryData.Unavailable(
                    definitionIds,
                    $"Territory {territoryId} has no resolvable level path.");

            var levelDirectory = $"bg/{bgPath.Substring(0, levelIndex + 1)}level";
            var positions = new HashSet<Vector3>();
            var readableFiles = 0;
            var failedFiles = new List<string>();
            foreach (var fileName in CandidateLgbFileNames)
            {
                var path = $"{levelDirectory}/{fileName}";
                try
                {
                    var lgb = Dalamud.GameData.GetFile<LgbFile>(path);
                    if (lgb == null)
                        continue;

                    readableFiles++;
                    foreach (var layer in lgb.Layers)
                    {
                        foreach (var instance in layer.InstanceObjects)
                        {
                            if (instance.AssetType != LayerEntryType.EventObject)
                                continue;

                            var eventObject = (LayerCommon.EventInstanceObject)instance.Object;
                            if (!definitionIds.Contains(eventObject.ParentData.BaseId))
                                continue;

                            var translation = instance.Transform.Translation;
                            positions.Add(new Vector3(translation.X, translation.Y, translation.Z));
                        }
                    }
                }
                catch (Exception exception)
                {
                    failedFiles.Add($"{fileName}: {exception.Message}");
                }
            }

            var orderedPositions = positions
                .OrderBy(position => position.X)
                .ThenBy(position => position.Y)
                .ThenBy(position => position.Z)
                .ToArray();
            if (orderedPositions.Length > 0)
                return new MarketBoardTerritoryData(definitionIds, orderedPositions, null);

            var detail = readableFiles == 0
                ? "none of the candidate LGB files could be read"
                : $"no market-board EventObjects were present in {readableFiles} readable LGB file(s)";
            if (failedFiles.Count > 0)
                detail += $"; parse failures: {string.Join(" | ", failedFiles)}";
            return MarketBoardTerritoryData.Unavailable(
                definitionIds,
                $"Territory {territoryId}: {detail}.");
        }
        catch (Exception exception)
        {
            return MarketBoardTerritoryData.Unavailable(
                definitionIds,
                $"Could not resolve market-board placements for territory {territoryId}: {exception.Message}");
        }
    }

    private static bool IsMarketBoardName(string name)
        => string.Equals(name.Trim(), MarketBoardName, StringComparison.OrdinalIgnoreCase);

    private sealed record DefinitionCatalog(IReadOnlySet<uint> Ids, string? Failure)
    {
        public static DefinitionCatalog Unavailable(string failure)
            => new(new HashSet<uint>(), failure);
    }
}

internal sealed record MarketBoardTerritoryData(
    IReadOnlySet<uint> DefinitionIds,
    IReadOnlyList<Vector3> Positions,
    string? UnavailableReason)
{
    public static MarketBoardTerritoryData Unavailable(IReadOnlySet<uint> definitionIds, string reason)
        => new(definitionIds, Array.Empty<Vector3>(), reason);

    public bool IsMarketBoardDefinition(uint baseId)
        => DefinitionIds.Contains(baseId);
}
