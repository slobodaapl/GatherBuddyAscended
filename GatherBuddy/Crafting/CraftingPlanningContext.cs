using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.Crafting;

public enum ExecutionSource
{
    PrivateList,
    FcFulfillment,
}

public sealed record FcExecutionContext
{
    public Guid SessionId { get; }
    public IReadOnlyList<Guid> Lists { get; }
    public string WorldFingerprint { get; }
    public FcWorldRevision WorldRevision { get; }

    public FcExecutionContext(
        Guid sessionId,
        IReadOnlyList<Guid> lists,
        string worldFingerprint,
        FcWorldRevision worldRevision)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("FC execution session must be non-empty.", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(lists);
        if (lists.Count == 0)
            throw new ArgumentException("FC execution must subscribe to at least one list.", nameof(lists));
        for (var i = 0; i < lists.Count; i++)
        {
            if (lists[i] == Guid.Empty)
                throw new ArgumentException("FC list identifiers must be non-empty.", nameof(lists));
            if (i > 0 && lists[i - 1].CompareTo(lists[i]) >= 0)
                throw new ArgumentException("FC list identifiers must be distinct and sorted.", nameof(lists));
        }

        WorldFingerprint = ValidateFingerprint(worldFingerprint, nameof(worldFingerprint));
        ArgumentNullException.ThrowIfNull(worldRevision);
        if (worldRevision.Number < 0)
            throw new ArgumentOutOfRangeException(nameof(worldRevision), "World revision cannot be negative.");
        _ = ValidateFingerprint(worldRevision.Fingerprint, nameof(worldRevision));

        SessionId = sessionId;
        Lists = lists.ToArray();
        WorldRevision = worldRevision;
    }

    internal bool MatchesScope(FcExecutionContext other)
        => other != null
        && SessionId == other.SessionId
        && string.Equals(WorldFingerprint, other.WorldFingerprint, StringComparison.Ordinal)
        && Lists.SequenceEqual(other.Lists);

    private static string ValidateFingerprint(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Fingerprint must be non-empty, bounded, and free of control characters.", parameterName);
        return value;
    }
}

public sealed record CraftingPlanningContext
{
    public IItemQuantitySource RepresentedInventory { get; }
    public IItemQuantitySource LocalConsumableInventory { get; }
    public ExecutionSource Source { get; }
    public FcExecutionContext? FcContext { get; }

    public static CraftingPlanningContext DefaultPrivate
        => CreatePrivate();

    public CraftingPlanningContext(
        IItemQuantitySource representedInventory,
        IItemQuantitySource localConsumableInventory,
        ExecutionSource source,
        FcExecutionContext? fcContext)
    {
        RepresentedInventory = representedInventory ?? throw new ArgumentNullException(nameof(representedInventory));
        LocalConsumableInventory = localConsumableInventory ?? throw new ArgumentNullException(nameof(localConsumableInventory));
        if (source is not (ExecutionSource.PrivateList or ExecutionSource.FcFulfillment))
            throw new ArgumentOutOfRangeException(nameof(source));
        if (source == ExecutionSource.PrivateList && fcContext != null)
            throw new ArgumentException("Private planning cannot carry an FC execution context.", nameof(fcContext));
        if (source == ExecutionSource.FcFulfillment && fcContext == null)
            throw new ArgumentException("FC planning requires an FC execution context.", nameof(fcContext));

        Source = source;
        FcContext = fcContext;
    }

    public static CraftingPlanningContext CreatePrivate(IItemQuantitySource? localConsumableInventory = null)
    {
        var physical = new CraftingPhysicalInventorySource();
        return new CraftingPlanningContext(
            physical,
            localConsumableInventory ?? physical,
            ExecutionSource.PrivateList,
            null);
    }

    public static CraftingPlanningContext CreateFc(
        IItemQuantitySource representedInventory,
        IItemQuantitySource localConsumableInventory,
        FcExecutionContext fcContext)
        => new(
            representedInventory,
            localConsumableInventory,
            ExecutionSource.FcFulfillment,
            fcContext);
}

/// <summary>
/// Adapter retaining the planner's historical physical-inventory exception
/// fallback. FC represented inventory must be supplied explicitly.
/// </summary>
public sealed class CraftingPhysicalInventorySource : IItemQuantitySource
{
    private readonly Dictionary<uint, (int NQ, int HQ)> _snapshots = new();

    public int GetNq(uint itemId)
    {
        return GetSnapshot(itemId).NQ;
    }

    public int GetHq(uint itemId)
    {
        return GetSnapshot(itemId).HQ;
    }

    private (int NQ, int HQ) GetSnapshot(uint itemId)
    {
        if (_snapshots.TryGetValue(itemId, out var snapshot))
            return snapshot;

        try
        {
            snapshot = CraftingInventoryCounter.GetInventorySplitCounts(itemId);
        }
        catch
        {
            snapshot = (0, 0);
        }

        _snapshots[itemId] = snapshot;
        return snapshot;
    }
}

public sealed record MaterialDemand
{
    public uint ItemId { get; }
    public FcItemQuality Quality { get; }
    public int RequiredRepresentedTotal { get; }
    public int CurrentRepresented { get; }
    public int Remaining { get; }

    public MaterialDemand(
        uint itemId,
        FcItemQuality quality,
        int requiredRepresentedTotal,
        int currentRepresented)
    {
        if (itemId == 0)
            throw new ArgumentOutOfRangeException(nameof(itemId));
        if (requiredRepresentedTotal < 0)
            throw new ArgumentOutOfRangeException(nameof(requiredRepresentedTotal));
        if (currentRepresented < 0)
            throw new ArgumentOutOfRangeException(nameof(currentRepresented));

        ItemId = itemId;
        Quality = quality;
        RequiredRepresentedTotal = requiredRepresentedTotal;
        CurrentRepresented = currentRepresented;
        Remaining = Math.Max(0, checked(requiredRepresentedTotal - currentRepresented));
    }
}

public static class MaterialDemandBuilder
{
    public static MaterialDemand Build(
        uint itemId,
        FcItemQuality quality,
        int requiredRepresentedTotal,
        IItemQuantitySource representedInventory)
    {
        ArgumentNullException.ThrowIfNull(representedInventory);
        return new MaterialDemand(
            itemId,
            quality,
            requiredRepresentedTotal,
            representedInventory.GetQuantity(itemId, quality));
    }

    public static IReadOnlyList<MaterialDemand> Build(
        IEnumerable<(uint ItemId, FcItemQuality Quality, int RequiredRepresentedTotal)> requirements,
        IItemQuantitySource representedInventory)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(representedInventory);
        return requirements
            .Select(requirement => Build(
                requirement.ItemId,
                requirement.Quality,
                requirement.RequiredRepresentedTotal,
                representedInventory))
            .ToArray();
    }
}
