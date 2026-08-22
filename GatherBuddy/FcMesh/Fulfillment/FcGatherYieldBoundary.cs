using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.Fulfillment;

public enum FcGatherYieldBoundaryOutcome : byte
{
    Idle,
    Active,
    Observed,
    NoYield,
    Duplicate,
    Blocked,
}

public sealed record FcGatherYieldBoundaryResult(
    FcGatherYieldBoundaryOutcome Outcome,
    FcItemQuantityMap Quantity,
    string Error)
{
    public bool Succeeded => Outcome == FcGatherYieldBoundaryOutcome.Observed;
}

public sealed record GatherYieldObserved(FcItemQuantityMap Quantity);

/// <summary>
/// Identity captured at the beginning of one indivisible gather interaction.
/// Session and generation prevent a completion event from being attributed to
/// a later worker session; the quality, when supplied by the active target,
/// narrows the accepted quantity key.
/// </summary>
public sealed record FcGatherInteractionToken(
    Guid InteractionId,
    Guid SessionId,
    ulong SessionGeneration,
    uint[] ItemIds,
    FcItemQuality? Quality)
{
    public bool IsValid
        => InteractionId != Guid.Empty
            && ItemIds is { Length: > 0 }
            && ItemIds.All(itemId => itemId != 0)
            && (SessionId != Guid.Empty ? SessionGeneration != 0 : SessionGeneration == 0);
}

/// <summary>
/// One gather interaction's physical inventory boundary. It snapshots the
/// expected item qualities once at interaction start and reads them once at
/// the existing gather-completion boundary. It is deliberately not a timer or
/// inventory poller: a second completion signal is idempotent, and unrelated
/// physical-inventory changes fail closed instead of being attributed. A
/// same-item event cannot identify its causal source from quantity alone; the
/// remaining bounded assumption is that an expected-item event observed inside
/// this token plus the live gather-completion/target checks belongs to the
/// interaction.
/// </summary>
public sealed class FcGatherYieldBoundary
{
    private readonly Func<uint, (int NQ, int HQ)> _inventoryReader;
    private readonly HashSet<uint> _expectedItemIds = new();
    private readonly HashSet<uint> _changedItemIds = new();
    private readonly Dictionary<FcQuantityKey, int> _before = new();
    private bool _active;
    private bool _completed;
    private bool _observedExpectedInventoryEvent;

    public FcGatherYieldBoundary(Func<uint, (int NQ, int HQ)> inventoryReader)
    {
        _inventoryReader = inventoryReader ?? throw new ArgumentNullException(nameof(inventoryReader));
    }

    public FcGatherYieldBoundaryOutcome State { get; private set; }
        = FcGatherYieldBoundaryOutcome.Idle;

    public FcGatherInteractionToken? Token { get; private set; }

    public bool Begin(IEnumerable<uint> itemIds, out string error)
    {
        var ids = (itemIds ?? Array.Empty<uint>()).Distinct().ToArray();
        return Begin(
            ids,
            new FcGatherInteractionToken(Guid.NewGuid(), Guid.Empty, 0, ids, null),
            out error);
    }

    public bool Begin(
        IEnumerable<uint> itemIds,
        FcGatherInteractionToken token,
        out string error)
    {
        if (_active)
        {
            _active = false;
            _completed = true;
            error = "A gather-yield boundary is already active.";
            State = FcGatherYieldBoundaryOutcome.Blocked;
            return false;
        }

        _expectedItemIds.Clear();
        _changedItemIds.Clear();
        _before.Clear();
        _observedExpectedInventoryEvent = false;
        _completed = false;

        if (itemIds is null)
        {
            error = "Gather-yield boundary item IDs are unavailable.";
            State = FcGatherYieldBoundaryOutcome.Blocked;
            return false;
        }

        if (token is null || !token.IsValid)
        {
            error = "Gather-yield boundary token is invalid.";
            State = FcGatherYieldBoundaryOutcome.Blocked;
            return false;
        }

        foreach (var itemId in itemIds)
        {
            if (itemId == 0)
            {
                error = "Gather-yield boundary contains an invalid item ID.";
                State = FcGatherYieldBoundaryOutcome.Blocked;
                return false;
            }
            _expectedItemIds.Add(itemId);
        }

        if (_expectedItemIds.Count == 0)
        {
            error = "Gather-yield boundary has no expected item IDs.";
            State = FcGatherYieldBoundaryOutcome.Blocked;
            return false;
        }

        if (!token.ItemIds.OrderBy(itemId => itemId).SequenceEqual(_expectedItemIds.OrderBy(itemId => itemId)))
        {
            error = "Gather-yield boundary token does not match its expected item IDs.";
            State = FcGatherYieldBoundaryOutcome.Blocked;
            return false;
        }

        Token = token with { ItemIds = token.ItemIds.Distinct().OrderBy(itemId => itemId).ToArray() };

        try
        {
            foreach (var itemId in _expectedItemIds)
            {
                var counts = _inventoryReader(itemId);
                if (counts.NQ < 0 || counts.HQ < 0)
                {
                    error = $"Inventory snapshot for item {itemId} is negative.";
                    State = FcGatherYieldBoundaryOutcome.Blocked;
                    return false;
                }
                _before[new FcQuantityKey(itemId, FcItemQuality.Nq)] = counts.NQ;
                _before[new FcQuantityKey(itemId, FcItemQuality.Hq)] = counts.HQ;
            }
        }
        catch (Exception exception)
        {
            error = $"Gather-yield inventory snapshot failed: {exception.Message}";
            State = FcGatherYieldBoundaryOutcome.Blocked;
            return false;
        }

        _active = true;
        State = FcGatherYieldBoundaryOutcome.Active;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Records an item ID from the scoped Dalamud inventory-change event batch.
    /// The completion read remains authoritative; the event IDs only prove
    /// that an unrelated physical item must not be silently attributed.
    /// </summary>
    public void ObserveInventoryItem(uint itemId)
    {
        if (_active && itemId != 0)
        {
            _changedItemIds.Add(itemId);
            if (_expectedItemIds.Contains(itemId))
                _observedExpectedInventoryEvent = true;
        }
    }

    public FcGatherYieldBoundaryResult Complete()
    {
        if (_completed)
            return Result(FcGatherYieldBoundaryOutcome.Duplicate, FcItemQuantityMap.Empty, "Gather completion was already reconciled.");
        if (!_active)
            return Result(FcGatherYieldBoundaryOutcome.Blocked, FcItemQuantityMap.Empty, "No gather-yield boundary is active.");

        _active = false;
        _completed = true;

        var unrelated = _changedItemIds.FirstOrDefault(itemId => !_expectedItemIds.Contains(itemId));
        if (unrelated != 0)
        {
            State = FcGatherYieldBoundaryOutcome.Blocked;
            return Result(
                FcGatherYieldBoundaryOutcome.Blocked,
                FcItemQuantityMap.Empty,
                $"Inventory item {unrelated} changed during the gather interaction but was not an expected yield.");
        }

        var yields = new Dictionary<FcQuantityKey, int>();
        try
        {
            foreach (var itemId in _expectedItemIds)
            {
                var counts = _inventoryReader(itemId);
                if (counts.NQ < 0 || counts.HQ < 0)
                {
                    State = FcGatherYieldBoundaryOutcome.Blocked;
                    return Result(
                        FcGatherYieldBoundaryOutcome.Blocked,
                        FcItemQuantityMap.Empty,
                        $"Inventory completion snapshot for item {itemId} is negative.");
                }

                AddDelta(yields, new FcQuantityKey(itemId, FcItemQuality.Nq), counts.NQ);
                AddDelta(yields, new FcQuantityKey(itemId, FcItemQuality.Hq), counts.HQ);
            }
        }
        catch (Exception exception)
        {
            State = FcGatherYieldBoundaryOutcome.Blocked;
            return Result(
                FcGatherYieldBoundaryOutcome.Blocked,
                FcItemQuantityMap.Empty,
                $"Gather-yield completion snapshot failed: {exception.Message}");
        }

        if (yields.Count == 0)
        {
            State = FcGatherYieldBoundaryOutcome.NoYield;
            return Result(FcGatherYieldBoundaryOutcome.NoYield, FcItemQuantityMap.Empty, string.Empty);
        }

        if (!_observedExpectedInventoryEvent)
        {
            State = FcGatherYieldBoundaryOutcome.Blocked;
            return Result(
                FcGatherYieldBoundaryOutcome.Blocked,
                FcItemQuantityMap.Empty,
                "Positive gather quantity has no expected-item inventory event within its interaction token.");
        }

        State = FcGatherYieldBoundaryOutcome.Observed;
        return Result(
            FcGatherYieldBoundaryOutcome.Observed,
            FcItemQuantityMap.FromDictionary(yields),
            string.Empty);
    }

    public void Abort()
    {
        _active = false;
        _completed = false;
        _expectedItemIds.Clear();
        _changedItemIds.Clear();
        _before.Clear();
        _observedExpectedInventoryEvent = false;
        Token = null;
        State = FcGatherYieldBoundaryOutcome.Idle;
    }

    private FcGatherYieldBoundaryResult Result(
        FcGatherYieldBoundaryOutcome outcome,
        FcItemQuantityMap quantity,
        string error)
        => new(outcome, quantity, error);

    private void AddDelta(
        IDictionary<FcQuantityKey, int> yields,
        FcQuantityKey key,
        int after)
    {
        var before = _before[key];
        var delta = after - before;
        if (delta < 0)
            throw new InvalidOperationException($"Inventory item {key.ItemId}/{key.Quality} decreased during gather reconciliation.");
        if (delta > 0 && (Token?.Quality is null || Token.Quality == key.Quality))
            yields[key] = delta;
    }
}
