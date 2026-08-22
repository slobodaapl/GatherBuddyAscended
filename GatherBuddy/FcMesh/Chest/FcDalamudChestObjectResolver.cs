using System;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GatherBuddy.Plugin;
using GatherBuddy.FcMesh.Protocol;
using Dalamud.Game.ClientState.Objects.Enums;

namespace GatherBuddy.FcMesh.Chest;

/// <summary>
/// Live framework-thread object-table binding. Stable EObj BaseId/DataId and
/// object kind are the identity; transient GameObjectId and localized names
/// are deliberately ignored. A stable match is still rejected when more than
/// one targetable EventObj is present.
/// </summary>
public unsafe sealed class FcDalamudChestObjectResolver : IFcChestObjectResolver
{
    private readonly FcChestObjectResolver _resolver;

    public FcDalamudChestObjectResolver()
    {
        _resolver = new FcChestObjectResolver(
            () => Dalamud.ClientState.TerritoryType,
            () => Dalamud.Objects
                .Where(value => value.ObjectKind == ObjectKind.EventObj)
                .Select(value => new FcChestLiveObject(
                    value.BaseId,
                    0,
                    value.ObjectKind.ToString(),
                    value.Name.ToString(),
                    value.Position,
                    value.IsTargetable)));
    }

    public FcChestObjectResolution Resolve(FcChestObjectSearch search)
        => _resolver.Resolve(search);

    public FcChestObjectResolution ResolveStable(FcChestObjectIdentity identity, uint territoryId)
    {
        if (identity.BaseId == 0 || string.IsNullOrWhiteSpace(identity.ObjectKind))
            return new(
                FcChestObjectResolutionStatus.InvalidSearch,
                null,
                "Stable Company Chest identity is incomplete.");
        if (Dalamud.ClientState.TerritoryType != territoryId)
            return new(
                FcChestObjectResolutionStatus.WrongTerritory,
                null,
                "Current territory does not match the Company Chest location.");

        var matches = Dalamud.Objects
            .Where(value => value.ObjectKind == ObjectKind.EventObj)
            .Where(value => value.BaseId == identity.BaseId)
            .Where(value => string.Equals(value.ObjectKind.ToString(), identity.ObjectKind, StringComparison.Ordinal))
            .Select(value => new FcChestLiveObject(
                value.BaseId,
                0,
                value.ObjectKind.ToString(),
                value.Name.ToString(),
                value.Position,
                value.IsTargetable))
            .ToArray();
        if (matches.Length == 0)
            return new(FcChestObjectResolutionStatus.Missing, null, "Stable Company Chest object is not present.");
        if (matches.Length > 1)
            return new(FcChestObjectResolutionStatus.Ambiguous, null, "Multiple stable Company Chest objects are present.");
        if (!matches[0].IsTargetable)
            return new(FcChestObjectResolutionStatus.NotTargetable, null, "Stable Company Chest object is not targetable.");
        return new(FcChestObjectResolutionStatus.Resolved, matches[0], string.Empty);
    }

    public bool TryOpenStable(FcChestObjectIdentity identity, uint territoryId, out string error)
    {
        if (Dalamud.ClientState.TerritoryType != territoryId)
        {
            error = "Current territory does not match the Company Chest location.";
            return false;
        }
        var matches = Dalamud.Objects
            .Where(value => value.ObjectKind == ObjectKind.EventObj)
            .Where(value => value.BaseId == identity.BaseId)
            .Where(value => string.Equals(value.ObjectKind.ToString(), identity.ObjectKind, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            error = matches.Length == 0
                ? "Stable Company Chest object is not present."
                : "Multiple stable Company Chest objects are present.";
            return false;
        }
        if (!matches[0].IsTargetable)
        {
            error = "Stable Company Chest object is not targetable.";
            return false;
        }
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
        {
            error = "Game target system is unavailable.";
            return false;
        }
        targetSystem->OpenObjectInteraction(
            (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)matches[0].Address);
        error = string.Empty;
        return true;
    }

    public bool TryOpenSearch(FcChestObjectSearch search, out string error)
    {
        if (!search.IsValid)
        {
            error = "Public Company Chest search identity is incomplete.";
            return false;
        }
        if (Dalamud.ClientState.TerritoryType != search.TerritoryId)
        {
            error = "Current territory does not match the public Company Chest route.";
            return false;
        }
        var matches = Dalamud.Objects
            .Where(value => value.ObjectKind == ObjectKind.EventObj)
            .Where(value => value.BaseId == search.ExpectedBaseId)
            .Where(value => string.Equals(value.ObjectKind.ToString(), search.ObjectKind, StringComparison.Ordinal))
            .Where(value => System.Numerics.Vector3.DistanceSquared(
                    value.Position,
                    search.ApproximateWorldAnchor)
                <= search.SearchRadius * search.SearchRadius)
            .ToArray();
        if (matches.Length != 1)
        {
            error = matches.Length == 0
                ? "Public Company Chest object is not present."
                : "Multiple public Company Chest objects matched; interaction is blocked.";
            return false;
        }
        if (!matches[0].IsTargetable)
        {
            error = "Public Company Chest object is not targetable.";
            return false;
        }
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
        {
            error = "Game target system is unavailable.";
            return false;
        }
        targetSystem->OpenObjectInteraction(
            (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)matches[0].Address);
        error = string.Empty;
        return true;
    }
}
