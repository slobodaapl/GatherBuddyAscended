using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Sessions;

public sealed record FcWorkerPhysicalInventorySnapshotResult(
    bool Succeeded,
    bool Complete,
    string Error,
    FcItemQuantityMap? Snapshot)
{
    public static FcWorkerPhysicalInventorySnapshotResult Invalid(string error)
        => new(false, false, error, null);

    public static FcWorkerPhysicalInventorySnapshotResult Valid(FcItemQuantityMap snapshot)
        => new(true, true, string.Empty, snapshot ?? throw new ArgumentNullException(nameof(snapshot)));
}

/// <summary>
/// Framework-thread adapter for the existing quality-aware inventory counter.
/// It reads every requested quality key and returns an explicit complete or
/// failed result. Missing reads are never represented as zero quantities.
/// </summary>
public static class FcWorkerPhysicalInventorySnapshot
{
    public static FcWorkerPhysicalInventorySnapshotResult Capture(
        IEnumerable<FcQuantityKey> dependencyClosure)
        => Capture(
            dependencyClosure,
            () => Dalamud.PlayerState is not null
                && Dalamud.PlayerState.IsLoaded
                && Dalamud.Objects is not null
                && Dalamud.Objects.LocalPlayer is not null,
            itemId =>
            {
                var split = CraftingInventoryCounter.GetInventorySplitCounts(itemId);
                return (split.NQ, split.HQ);
            });

    /// <summary>
    /// Test/integration seam. The caller must invoke this on the framework
    /// thread; the provider is intentionally synchronous and bounded to the
    /// requested closure.
    /// </summary>
    public static FcWorkerPhysicalInventorySnapshotResult Capture(
        IEnumerable<FcQuantityKey> dependencyClosure,
        Func<bool> availability,
        Func<uint, (long NQ, long HQ)> splitProvider)
    {
        if (dependencyClosure is null)
            return FcWorkerPhysicalInventorySnapshotResult.Invalid("Dependency closure is unavailable.");
        if (availability is null)
            return FcWorkerPhysicalInventorySnapshotResult.Invalid("Inventory availability provider is unavailable.");
        if (splitProvider is null)
            return FcWorkerPhysicalInventorySnapshotResult.Invalid("Inventory split provider is unavailable.");

        FcQuantityKey[] keys;
        try
        {
            var requested = dependencyClosure.ToArray();
            if (requested.Distinct().Count() != requested.Length)
                return FcWorkerPhysicalInventorySnapshotResult.Invalid(
                    "Dependency closure contains duplicate quantity keys.");
            keys = requested
                .OrderBy(key => key)
                .ToArray();
        }
        catch (Exception exception)
        {
            return FcWorkerPhysicalInventorySnapshotResult.Invalid(
                $"Dependency closure could not be checked: {exception.Message}");
        }

        if (keys.Length == 0)
            return FcWorkerPhysicalInventorySnapshotResult.Invalid("Dependency closure is empty.");
        if (keys.Length > FcRecordValidator.MaxArrayEntries)
            return FcWorkerPhysicalInventorySnapshotResult.Invalid("Dependency closure is too large.");
        if (keys.Any(key => key.ItemId == 0 || !Enum.IsDefined(key.Quality)))
            return FcWorkerPhysicalInventorySnapshotResult.Invalid("Dependency closure contains an invalid quantity key.");

        try
        {
            if (!availability())
                return FcWorkerPhysicalInventorySnapshotResult.Invalid(
                    "Player inventory is unavailable; physical snapshot is incomplete.");

            var splits = new Dictionary<uint, (long NQ, long HQ)>();
            foreach (var itemId in keys.Select(key => key.ItemId).Distinct())
            {
                var split = splitProvider(itemId);
                if (split.NQ < 0 || split.HQ < 0
                    || split.NQ > int.MaxValue || split.HQ > int.MaxValue
                    || checked(split.NQ + split.HQ) > int.MaxValue)
                    return FcWorkerPhysicalInventorySnapshotResult.Invalid(
                        $"Inventory quantity for item {itemId} is outside the checked protocol range.");
                splits[itemId] = split;
            }

            var entries = keys
                .Select(key =>
                {
                    var split = splits[key.ItemId];
                    var quantity = key.Quality == FcItemQuality.Hq ? split.HQ : split.NQ;
                    return new ItemQuantityEntry(key.ItemId, key.Quality, checked((int)quantity));
                })
                .ToArray();
            return FcWorkerPhysicalInventorySnapshotResult.Valid(new FcItemQuantityMap(entries));
        }
        catch (OverflowException exception)
        {
            return FcWorkerPhysicalInventorySnapshotResult.Invalid(
                $"Inventory quantity overflowed the checked protocol range: {exception.Message}");
        }
        catch (Exception exception)
        {
            return FcWorkerPhysicalInventorySnapshotResult.Invalid(
                $"Physical inventory read failed: {exception.Message}");
        }
    }
}
