using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.Fulfillment;

public sealed record FcContributionLedgerState
{
    public Guid SessionId { get; init; }
    public FcItemQuantityMap StartingInventory { get; init; }
    public FcItemQuantityMap ProtectedInventory { get; init; }
    public FcItemQuantityMap AttributedInventory { get; init; }
    public bool UseOwnStock { get; init; }

    // Recovery metadata is local persistence state, not replicated domain data.
    public ulong SessionGeneration { get; init; }
    public FcFulfillmentSelection? Selection { get; init; }
    public FcLogicalQueueEntry[] QueueReferences { get; init; } = Array.Empty<FcLogicalQueueEntry>();

    [System.Text.Json.Serialization.JsonConstructor]
    public FcContributionLedgerState(
        Guid sessionId,
        FcItemQuantityMap startingInventory,
        FcItemQuantityMap protectedInventory,
        FcItemQuantityMap attributedInventory,
        bool useOwnStock)
    {
        SessionId = sessionId;
        StartingInventory = startingInventory ?? throw new ArgumentNullException(nameof(startingInventory));
        ProtectedInventory = protectedInventory ?? throw new ArgumentNullException(nameof(protectedInventory));
        AttributedInventory = attributedInventory ?? throw new ArgumentNullException(nameof(attributedInventory));
        UseOwnStock = useOwnStock;
    }
}

public sealed record FcContributionLedgerRecovery
{
    public Guid SessionId { get; init; }
    public ulong SessionGeneration { get; init; }
    public FcFulfillmentSelection Selection { get; init; }
    public FcItemQuantityMap StartingInventory { get; init; }
    public FcItemQuantityMap ProtectedInventory { get; init; }
    public FcItemQuantityMap AttributedInventory { get; init; }
    public bool UseOwnStock { get; init; }
    public FcLogicalQueueEntry[] QueueReferences { get; init; }

    [System.Text.Json.Serialization.JsonConstructor]
    public FcContributionLedgerRecovery(
        Guid sessionId,
        ulong sessionGeneration,
        FcFulfillmentSelection selection,
        FcItemQuantityMap startingInventory,
        FcItemQuantityMap protectedInventory,
        FcItemQuantityMap attributedInventory,
        bool useOwnStock,
        FcLogicalQueueEntry[] queueReferences)
    {
        SessionId = sessionId;
        SessionGeneration = sessionGeneration;
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));
        StartingInventory = startingInventory ?? throw new ArgumentNullException(nameof(startingInventory));
        ProtectedInventory = protectedInventory ?? throw new ArgumentNullException(nameof(protectedInventory));
        AttributedInventory = attributedInventory ?? throw new ArgumentNullException(nameof(attributedInventory));
        UseOwnStock = useOwnStock;
        QueueReferences = queueReferences ?? throw new ArgumentNullException(nameof(queueReferences));
    }
}

public sealed class FcContributionLedger
{
    private FcItemQuantityMap _currentPhysical;

    public FcContributionLedger(FcContributionLedgerState initial, FcItemQuantityMap? currentPhysical = null)
    {
        State = initial ?? throw new ArgumentNullException(nameof(initial));
        if (State.SessionId == Guid.Empty || State.StartingInventory is null
            || State.ProtectedInventory is null || State.AttributedInventory is null)
            throw new ArgumentException("Ledger state is incomplete.", nameof(initial));
        _currentPhysical = currentPhysical ?? initial.StartingInventory;
        ReconcilePhysical(_currentPhysical);
    }

    public FcContributionLedgerState State { get; private set; }
    public FcItemQuantityMap CurrentPhysical => _currentPhysical;

    public void RecordGatherYield(FcItemQuantityMap quantity) => AddAttributed(quantity, addPhysical: true);
    public void RecordCraftOutput(FcItemQuantityMap quantity) => AddAttributed(quantity, addPhysical: true);
    public void RecordCraftMaterialConsumption(FcItemQuantityMap quantity) => RemoveAttributed(quantity, removePhysical: true);
    public void RecordChestWithdrawal(FcItemQuantityMap quantity) => AddAttributed(quantity, addPhysical: true);
    public void RecordChestDeposit(FcItemQuantityMap quantity) => RemoveAttributed(quantity, removePhysical: true);
    public void RecordDiscardOrTransfer(FcItemQuantityMap quantity) => RemoveAttributed(quantity, removePhysical: true);

    public bool CanConsumeLocally(FcItemQuantityMap quantity)
    {
        if (quantity is null)
            throw new ArgumentNullException(nameof(quantity));
        if (State.UseOwnStock)
            return IsAvailable(_currentPhysical, quantity);

        foreach (var entry in quantity.Entries)
        {
            var remaining = _currentPhysical.Get(entry.Key) - entry.Quantity;
            if (remaining < State.ProtectedInventory.Get(entry.Key))
                return false;
        }
        return true;
    }

    public void ReconcilePhysical(FcItemQuantityMap currentPhysical)
    {
        _currentPhysical = currentPhysical ?? throw new ArgumentNullException(nameof(currentPhysical));
        var protectedInventory = Min(State.StartingInventory, _currentPhysical);
        var attributed = Min(State.AttributedInventory, _currentPhysical);
        State = State with { ProtectedInventory = protectedInventory, AttributedInventory = attributed };
    }

    public FcItemQuantityMap GetPublishableHeld(IEnumerable<FcQuantityKey>? dependencyClosure = null)
    {
        var allowed = dependencyClosure?.ToHashSet();
        if (State.UseOwnStock)
            return FilterRelevant(_currentPhysical, allowed);

        var result = new List<ItemQuantityEntry>();
        var keys = State.StartingInventory.Entries.Select(entry => (FcQuantityKey)entry.Key)
            .Concat(State.AttributedInventory.Entries.Select(entry => (FcQuantityKey)entry.Key))
            .Concat(_currentPhysical.Entries.Select(entry => (FcQuantityKey)entry.Key))
            .Distinct()
            .OrderBy(key => key);
        foreach (var key in keys)
        {
            if (allowed is not null && !allowed.Contains(key))
                continue;
            var protectedRemaining = Math.Min(State.StartingInventory.Get(key), _currentPhysical.Get(key));
            var maximumNonOwnedPhysical = Math.Max(_currentPhysical.Get(key) - protectedRemaining, 0);
            var publishable = Math.Min(State.AttributedInventory.Get(key), maximumNonOwnedPhysical);
            if (publishable > 0)
                result.Add(new ItemQuantityEntry(key.ItemId, key.Quality, publishable));
        }
        return new FcItemQuantityMap(result);
    }

    public FcContributionLedgerRecovery ExportRecovery(FcFulfillmentSelection selection)
        => new(
            State.SessionId,
            State.SessionGeneration,
            selection ?? throw new ArgumentNullException(nameof(selection)),
            State.StartingInventory,
            State.ProtectedInventory,
            State.AttributedInventory,
            State.UseOwnStock,
            State.QueueReferences);

    public static FcContributionLedger Recover(FcContributionLedgerRecovery recovery, FcItemQuantityMap currentPhysical)
    {
        if (recovery is null)
            throw new ArgumentNullException(nameof(recovery));
        if (currentPhysical is null)
            throw new ArgumentNullException(nameof(currentPhysical));
        ValidateRecovery(recovery);
        var state = new FcContributionLedgerState(
            recovery.SessionId,
            recovery.StartingInventory,
            recovery.ProtectedInventory,
            recovery.AttributedInventory,
            recovery.UseOwnStock)
        {
            SessionGeneration = recovery.SessionGeneration,
            Selection = recovery.Selection,
            QueueReferences = recovery.QueueReferences ?? Array.Empty<FcLogicalQueueEntry>(),
        };
        return new FcContributionLedger(state, currentPhysical);
    }

    public void Recover(FcItemQuantityMap currentPhysical)
        => ReconcilePhysical(currentPhysical);

    private void AddAttributed(FcItemQuantityMap quantity, bool addPhysical)
    {
        if (quantity is null)
            throw new ArgumentNullException(nameof(quantity));
        var attributed = State.AttributedInventory.Add(quantity);
        var physical = addPhysical ? _currentPhysical.Add(quantity) : _currentPhysical;
        State = State with { AttributedInventory = attributed };
        ReconcilePhysical(physical);
    }

    private void RemoveAttributed(FcItemQuantityMap quantity, bool removePhysical)
    {
        if (quantity is null)
            throw new ArgumentNullException(nameof(quantity));
        var attributed = State.AttributedInventory.SubtractClamped(quantity);
        var physical = removePhysical ? _currentPhysical.SubtractClamped(quantity) : _currentPhysical;
        State = State with { AttributedInventory = attributed };
        ReconcilePhysical(physical);
    }

    private static FcItemQuantityMap Min(FcItemQuantityMap left, FcItemQuantityMap right)
        => left.Min(right);

    private static FcItemQuantityMap FilterRelevant(FcItemQuantityMap map, HashSet<FcQuantityKey>? allowed)
        => allowed is null ? map : map.Filter(allowed);

    private static bool IsAvailable(FcItemQuantityMap physical, FcItemQuantityMap requested)
        => requested.Entries.All(entry => physical.Get(entry.Key) >= entry.Quantity);

    private static void ValidateRecovery(FcContributionLedgerRecovery recovery)
    {
        if (recovery.SessionId == Guid.Empty || recovery.SessionGeneration == 0
            || recovery.Selection is null
            || recovery.StartingInventory is null
            || recovery.ProtectedInventory is null
            || recovery.AttributedInventory is null
            || recovery.QueueReferences is null)
            throw new ArgumentException("Ledger recovery state is incomplete.", nameof(recovery));

        if (recovery.Selection.ListIds is null
            || recovery.Selection.ListIds.Any(listId => listId == Guid.Empty)
            || recovery.Selection.ListIds.Distinct().Count() != recovery.Selection.ListIds.Length
            || (recovery.Selection.AllPublishedLists && recovery.Selection.ListIds.Length != 0))
            throw new ArgumentException("Ledger recovery selection is invalid.", nameof(recovery));

        foreach (var entry in recovery.QueueReferences)
        {
            if (entry is null || entry.RecipeId == 0 || entry.Remaining < 0
                || (entry.ListId is { } listId && listId == Guid.Empty))
                throw new ArgumentException("Ledger recovery queue references are invalid.", nameof(recovery));
        }
    }
}
