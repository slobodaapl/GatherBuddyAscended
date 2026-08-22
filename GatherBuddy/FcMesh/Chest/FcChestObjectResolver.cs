using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GatherBuddy.FcMesh.Chest;

public readonly record struct FcChestLiveObject(
    uint BaseId,
    // Legacy field retained for wire/test compatibility. BaseId is the sole
    // authoritative live identity; new production evidence sets this to 0.
    uint DataId,
    string ObjectKind,
    string Name,
    Vector3 Position,
    bool IsTargetable);

public readonly record struct FcChestObjectSearch(
    uint ExpectedBaseId,
    uint ExpectedDataId,
    uint TerritoryId,
    Vector3 ApproximateWorldAnchor,
    float SearchRadius,
    string ObjectKind)
{
    public bool IsValid
        => ExpectedBaseId != 0
            && TerritoryId != 0
            && SearchRadius > 0
            && float.IsFinite(SearchRadius)
            && !string.IsNullOrWhiteSpace(ObjectKind);

    public static FcChestObjectSearch FromMapAnchor(
        uint expectedBaseId,
        uint expectedDataId,
        uint territoryId,
        uint mapId,
        float approximateMapX,
        float approximateMapY,
        uint mapSizeFactor,
        int mapOffsetX,
        int mapOffsetY,
        float searchRadius,
        string objectKind)
    {
        if (mapId == 0 || mapSizeFactor == 0
            || !float.IsFinite(approximateMapX) || !float.IsFinite(approximateMapY))
            return default;
        var anchor = FcChestMapCoordinates.ToWorldAnchor(
            approximateMapX,
            approximateMapY,
            mapSizeFactor,
            mapOffsetX,
            mapOffsetY);
        return new(
            expectedBaseId,
            expectedDataId,
            territoryId,
            anchor,
            searchRadius,
            objectKind);
    }
}

/// <summary>
/// Converts the persisted approximate map anchor into a search vicinity using
/// the current Lumina Map row. The result is never a vnav target: execution
/// must resolve a live object and use its runtime Position.
/// </summary>
public static class FcChestMapCoordinates
{
    public static Vector3 ToWorldAnchor(
        float mapX,
        float mapY,
        uint sizeFactor,
        int offsetX,
        int offsetY,
        float worldY = 0f)
    {
        if (sizeFactor == 0)
            throw new ArgumentOutOfRangeException(nameof(sizeFactor));
        const double factor = 0.019999999552965164d;
        var worldX = (mapX - 1d - factor * offsetX - 2048d / sizeFactor) / factor;
        var worldZ = (mapY - 1d - factor * offsetY - 2048d / sizeFactor) / factor;
        return new Vector3((float)worldX, worldY, (float)worldZ);
    }
}

public enum FcChestObjectResolutionStatus : byte
{
    Resolved,
    Missing,
    Ambiguous,
    WrongTerritory,
    NotTargetable,
    InvalidSearch,
}

public readonly record struct FcChestObjectResolution(
    FcChestObjectResolutionStatus Status,
    FcChestLiveObject? Object,
    string Message)
{
    public bool Succeeded => Status == FcChestObjectResolutionStatus.Resolved && Object is not null;
}

public interface IFcChestObjectResolver
{
    FcChestObjectResolution Resolve(FcChestObjectSearch search);
}

/// <summary>
/// Resolves a persisted stable EObj identity against current live objects.
/// Transient GameObjectId is intentionally not part of the comparison.
/// </summary>
public sealed class FcChestObjectResolver : IFcChestObjectResolver
{
    private readonly Func<uint> _territoryProvider;
    private readonly Func<IEnumerable<FcChestLiveObject>> _objectsProvider;

    public FcChestObjectResolver(
        Func<uint> territoryProvider,
        Func<IEnumerable<FcChestLiveObject>> objectsProvider)
    {
        _territoryProvider = territoryProvider ?? throw new ArgumentNullException(nameof(territoryProvider));
        _objectsProvider = objectsProvider ?? throw new ArgumentNullException(nameof(objectsProvider));
    }

    public FcChestObjectResolution Resolve(FcChestObjectSearch search)
    {
        if (!search.IsValid)
            return new(FcChestObjectResolutionStatus.InvalidSearch, null, "Company Chest search identity is incomplete.");
        if (_territoryProvider() != search.TerritoryId)
            return new(FcChestObjectResolutionStatus.WrongTerritory, null, "Current territory does not match the Company Chest route.");

        var matches = (_objectsProvider() ?? Array.Empty<FcChestLiveObject>())
            .Where(value => value.BaseId == search.ExpectedBaseId)
            .Where(value => string.Equals(value.ObjectKind, search.ObjectKind, StringComparison.Ordinal))
            .Where(value => Vector3.DistanceSquared(value.Position, search.ApproximateWorldAnchor)
                <= search.SearchRadius * search.SearchRadius)
            .ToArray();
        if (matches.Length == 0)
            return new(FcChestObjectResolutionStatus.Missing, null, "No current live Company Chest object matched the stable identity.");
        if (matches.Length > 1)
            return new(FcChestObjectResolutionStatus.Ambiguous, null, "Multiple live Company Chest objects matched; interaction is blocked.");
        if (!matches[0].IsTargetable)
            return new(FcChestObjectResolutionStatus.NotTargetable, null, "The resolved Company Chest object is not targetable.");
        return new(FcChestObjectResolutionStatus.Resolved, matches[0], string.Empty);
    }
}
