using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.FcMesh.Chest;

public static class FcChestProbeRules
{
    public static bool IsReconciliationTimedOut(DateTime utcNow, DateTime deadline)
        => utcNow >= deadline;

    public static bool ShouldSuppressFurtherTransfer(
        FcChestProbeState state,
        bool cancellationRequested,
        bool physicalTransferAttempted)
        => cancellationRequested
            || physicalTransferAttempted
            || state is FcChestProbeState.ReconcilingWithdraw
                or FcChestProbeState.ReconcilingDeposit;

    public static bool TryPrepare(
        FcChestSnapshot snapshot,
        out FcChestPreparation preparation,
        out string failureReason)
    {
        preparation = default;
        if (!snapshot.IsComplete)
        {
            failureReason = snapshot.IncompleteReason ?? "FC chest and normal inventory are not complete.";
            return false;
        }

        var source = snapshot.ChestItems.Values
            .Where(static item => item.Address.IsFreeCompanyItemContainer && item.Quantity == 1)
            .OrderBy(static item => item.Address.Container)
            .ThenBy(static item => item.Address.Slot)
            .FirstOrDefault();
        if (source.Address == default && !snapshot.ChestItems.ContainsKey(source.Address))
        {
            failureReason = "No non-crystal FC chest stack with exactly one item is available.";
            return false;
        }

        var destination = snapshot.EmptyPlayerSlots
            .OrderBy(static slot => slot.Container)
            .ThenBy(static slot => slot.Slot)
            .FirstOrDefault();
        if (destination == default && !snapshot.PlayerSlots.Contains(destination))
        {
            failureReason = "No confirmed empty normal player-inventory slot is available.";
            return false;
        }

        preparation = new FcChestPreparation(
            snapshot,
            new FcChestTransferPlan(source.Address, destination, source.Item));
        failureReason = string.Empty;
        return true;
    }

    public static bool TryChooseDepositDestination(
        FcChestSnapshot snapshot,
        FcChestSlotAddress preferredAddress,
        out FcChestSlotAddress destination,
        out string failureReason)
    {
        if (!snapshot.IsComplete)
        {
            destination = default;
            failureReason = snapshot.IncompleteReason ?? "FC chest and normal inventory are not complete.";
            return false;
        }

        if (preferredAddress.IsFreeCompanyItemContainer
            && !snapshot.TryGetChestItem(preferredAddress, out _)
            && snapshot.ChestSlots.Contains(preferredAddress))
        {
            destination = preferredAddress;
            failureReason = string.Empty;
            return true;
        }

        var empty = snapshot.EmptyChestSlots
            .OrderBy(static slot => slot.Container)
            .ThenBy(static slot => slot.Slot)
            .FirstOrDefault();
        if (empty == default && !snapshot.ChestSlots.Contains(empty))
        {
            destination = default;
            failureReason = "No confirmed empty non-crystal FC chest slot is available for deposit.";
            return false;
        }

        destination = empty;
        failureReason = string.Empty;
        return true;
    }

    public static FcChestDeltaValidation ValidatePreparedWithdrawal(
        FcChestPreparation preparation,
        FcChestSnapshot current)
    {
        if (!current.IsComplete)
            return Incomplete(current);

        var plan = preparation.Withdrawal;
        if (!current.TryGetChestItem(plan.Source, out var source)
            || source.Item != plan.Item
            || source.Quantity != plan.Quantity)
        {
            return new(false, FcChestDeltaFailure.SourceChanged,
                "Prepared FC chest source changed before dispatch.");
        }

        if (current.TryGetPlayerItem(plan.Destination, out _))
        {
            return new(false, FcChestDeltaFailure.DestinationOccupied,
                "Prepared player destination is no longer empty.");
        }

        return FcChestDeltaValidation.Success;
    }

    public static FcChestDeltaValidation ValidatePreparedDeposit(
        FcChestTransferPlan deposit,
        FcChestSnapshot current)
    {
        if (!current.IsComplete)
            return Incomplete(current);

        if (!current.TryGetPlayerItem(deposit.Source, out var held)
            || held.Item != deposit.Item
            || held.Quantity != deposit.Quantity)
        {
            return new(false, FcChestDeltaFailure.SourceChanged,
                "The exact withdrawn item is not still held in its confirmed player slot.");
        }

        if (current.TryGetChestItem(deposit.Destination, out _))
        {
            return new(false, FcChestDeltaFailure.DestinationOccupied,
                "Prepared FC chest deposit destination is no longer empty.");
        }

        return FcChestDeltaValidation.Success;
    }

    public static FcChestDeltaValidation ValidateObservedDelta(
        FcChestSnapshot before,
        FcChestSnapshot after,
        FcChestTransferPlan plan,
        FcChestTransferDirection direction)
    {
        if (!before.IsComplete || !after.IsComplete)
            return new(false, FcChestDeltaFailure.IncompleteSnapshot,
                "A complete FC chest and player-inventory reread was not available.");

        var sourceBefore = GetItem(before, plan.Source, direction);
        var destinationBefore = GetItem(before, plan.Destination, direction);
        if (sourceBefore is null || sourceBefore.Value.Item != plan.Item || sourceBefore.Value.Quantity != plan.Quantity)
        {
            return new(false, FcChestDeltaFailure.SourceChanged,
                "The expected source slot was not the prepared exact one-item stack.");
        }

        if (destinationBefore is not null)
        {
            return new(false, FcChestDeltaFailure.DestinationOccupied,
                "The expected destination was occupied in the pre-transfer snapshot.");
        }

        var expectedDestinationAfter = new FcChestSlotItem(plan.Destination, plan.Item, plan.Quantity);

        var actualSourceAfter = GetItem(after, plan.Source, direction);
        var actualDestinationAfter = GetItem(after, plan.Destination, direction);
        var changes = GetChanges(before, after);

        if (changes.Count == 0)
            return new(false, FcChestDeltaFailure.NoDelta,
                "The native call produced no observed inventory delta before timeout.");

        if (actualSourceAfter is not null || actualDestinationAfter != expectedDestinationAfter)
        {
            return new(false, FcChestDeltaFailure.WrongDelta,
                "Observed transfer delta did not remove the exact source and add it to the exact destination.");
        }

        if (changes.Count != 2
            || !changes.Any(change => change.Address == plan.Source)
            || !changes.Any(change => change.Address == plan.Destination))
        {
            return new(false, FcChestDeltaFailure.UnrelatedMutation,
                "Observed inventory changes included an unrelated or missing slot mutation.");
        }

        return FcChestDeltaValidation.Success;
    }

    public static IReadOnlyList<FcChestSlotChange> GetChanges(
        FcChestSnapshot before,
        FcChestSnapshot after)
    {
        var changes = new List<FcChestSlotChange>();
        AddChanges(changes, before.ChestItems, after.ChestItems);
        AddChanges(changes, before.PlayerItems, after.PlayerItems);
        return changes;
    }

    private static FcChestSlotItem? GetItem(
        FcChestSnapshot snapshot,
        FcChestSlotAddress address,
        FcChestTransferDirection direction)
    {
        var items = direction == FcChestTransferDirection.Withdrawal
            ? address.IsFreeCompanyContainer
                ? snapshot.ChestItems
                : snapshot.PlayerItems
            : address.IsPlayerInventoryContainer
                ? snapshot.PlayerItems
                : snapshot.ChestItems;
        return items.TryGetValue(address, out var item) ? item : null;
    }

    private static void AddChanges(
        ICollection<FcChestSlotChange> changes,
        IReadOnlyDictionary<FcChestSlotAddress, FcChestSlotItem> before,
        IReadOnlyDictionary<FcChestSlotAddress, FcChestSlotItem> after)
    {
        foreach (var address in before.Keys.Union(after.Keys).Distinct().OrderBy(static x => x.Container).ThenBy(static x => x.Slot))
        {
            var oldItem = before.TryGetValue(address, out var oldValue)
                ? oldValue
                : (FcChestSlotItem?)null;
            var newItem = after.TryGetValue(address, out var newValue)
                ? newValue
                : (FcChestSlotItem?)null;
            if (oldItem != newItem)
                changes.Add(new FcChestSlotChange(address, oldItem, newItem));
        }
    }

    private static FcChestDeltaValidation Incomplete(FcChestSnapshot snapshot)
        => new(false, FcChestDeltaFailure.IncompleteSnapshot,
            snapshot.IncompleteReason ?? "A complete FC chest and player-inventory reread was not available.");
}

public readonly record struct FcChestSlotChange(
    FcChestSlotAddress Address,
    FcChestSlotItem? Before,
    FcChestSlotItem? After);
