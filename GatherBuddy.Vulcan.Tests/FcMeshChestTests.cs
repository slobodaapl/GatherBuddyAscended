using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.FcMesh.Chest;

namespace GatherBuddy.Vulcan.Tests;

internal static class FcMeshChestTests
{
    public static void Run(Action<bool, string> require)
    {
        var initial = MakeSnapshot();
        require(initial.IsComplete,
            "a complete fixture requires the ready addon, all six FC containers, and all four player pages");

        var totals = initial.GetChestTotals();
        var crystalTotals = initial.GetCrystalTotals();
        require(totals[new FcChestItemKey(500, false)] == 3
                && totals[new FcChestItemKey(500, true)] == 3
                && !totals.ContainsKey(new FcChestItemKey(2, false))
                && crystalTotals[2] == 100,
            "FC snapshot aggregation must keep normal NQ/HQ totals separate from crystal quantities");

        var hqCrystalRejected = false;
        try
        {
            _ = MakeSnapshot(hqCrystal: true);
        }
        catch (ArgumentException)
        {
            hqCrystalRejected = true;
        }
        require(hqCrystalRejected,
            "FC crystal slots must reject an HQ quality flag");

        require(FcChestProbeRules.TryPrepare(initial, out var preparation, out _)
                && preparation.Withdrawal.Source == new FcChestSlotAddress(FcChestContainer.FreeCompanyPage1, 0)
                && preparation.Withdrawal.Destination == new FcChestSlotAddress(FcChestContainer.Inventory1, 0)
                && preparation.Withdrawal.Item == new FcChestItemKey(500, false),
            "preparation must choose a deterministic one-item non-crystal source and confirmed empty player slot");

        var withdrawn = Replace(
            initial,
            initial.ChestItems.Values.Where(item => item.Address != preparation.Withdrawal.Source),
            initial.PlayerItems.Values.Append(new FcChestSlotItem(
                preparation.Withdrawal.Destination,
                preparation.Withdrawal.Item,
                1)));
        var withdrawalDelta = FcChestProbeRules.ValidateObservedDelta(
            initial,
            withdrawn,
            preparation.Withdrawal,
            FcChestTransferDirection.Withdrawal);
        require(withdrawalDelta.Accepted,
            "an exact chest-minus-one/player-plus-one delta must validate as the withdrawal");

        var deposit = new FcChestTransferPlan(
            preparation.Withdrawal.Destination,
            preparation.Withdrawal.Source,
            preparation.Withdrawal.Item);
        var redeposited = Replace(
            withdrawn,
            withdrawn.ChestItems.Values.Append(new FcChestSlotItem(
                deposit.Destination,
                deposit.Item,
                1)),
            withdrawn.PlayerItems.Values.Where(item => item.Address != deposit.Source));
        var depositDelta = FcChestProbeRules.ValidateObservedDelta(
            withdrawn,
            redeposited,
            deposit,
            FcChestTransferDirection.Deposit);
        require(depositDelta.Accepted,
            "an exact player-minus-one/chest-plus-one delta must validate as the deposit");

        var unrelated = Replace(
            withdrawn,
            withdrawn.ChestItems.Values,
            withdrawn.PlayerItems.Values.Append(new FcChestSlotItem(
                new FcChestSlotAddress(FcChestContainer.Inventory2, 0),
                new FcChestItemKey(900, false),
                1)));
        var unrelatedDelta = FcChestProbeRules.ValidateObservedDelta(
            initial,
            unrelated,
            preparation.Withdrawal,
            FcChestTransferDirection.Withdrawal);
        require(!unrelatedDelta.Accepted && unrelatedDelta.Failure == FcChestDeltaFailure.UnrelatedMutation,
            "an unrelated inventory mutation must reject an otherwise plausible transfer delta");

        var partial = Replace(
            initial,
            initial.ChestItems.Values.Where(item => item.Address != preparation.Withdrawal.Source),
            initial.PlayerItems.Values);
        var partialDelta = FcChestProbeRules.ValidateObservedDelta(
            initial,
            partial,
            preparation.Withdrawal,
            FcChestTransferDirection.Withdrawal);
        require(!partialDelta.Accepted && partialDelta.Failure == FcChestDeltaFailure.WrongDelta,
            "a one-sided partial withdrawal delta must be rejected");

        var wrongQuantity = Replace(
            initial,
            initial.ChestItems.Values.Where(item => item.Address != preparation.Withdrawal.Source),
            initial.PlayerItems.Values.Append(new FcChestSlotItem(
                preparation.Withdrawal.Destination,
                preparation.Withdrawal.Item,
                2)));
        var wrongQuantityDelta = FcChestProbeRules.ValidateObservedDelta(
            initial,
            wrongQuantity,
            preparation.Withdrawal,
            FcChestTransferDirection.Withdrawal);
        require(!wrongQuantityDelta.Accepted && wrongQuantityDelta.Failure == FcChestDeltaFailure.WrongDelta,
            "a destination quantity other than the exact one-item transfer must be rejected");

        var crystalChanged = Replace(
            initial,
            initial.ChestItems.Values.Select(item => item.Address.Container == FcChestContainer.FreeCompanyCrystals
                ? item with { Quantity = item.Quantity - 1 }
                : item),
            initial.PlayerItems.Values);
        var changes = FcChestProbeRules.GetChanges(initial, crystalChanged);
        require(changes.Any(change => change.Address.Container == FcChestContainer.FreeCompanyCrystals),
            "absolute snapshot diffing must observe crystal-container changes");
        var crystalMutationDelta = FcChestProbeRules.ValidateObservedDelta(
            initial,
            Replace(
                crystalChanged,
                crystalChanged.ChestItems.Values.Where(item => item.Address != preparation.Withdrawal.Source),
                crystalChanged.PlayerItems.Values.Append(new FcChestSlotItem(
                    preparation.Withdrawal.Destination,
                    preparation.Withdrawal.Item,
                    1))),
            preparation.Withdrawal,
            FcChestTransferDirection.Withdrawal);
        require(!crystalMutationDelta.Accepted
                && crystalMutationDelta.Failure == FcChestDeltaFailure.UnrelatedMutation,
            "a crystal mutation alongside a transfer must reject the paired-delta claim");

        var missingFcPage = MakeSnapshot(page5Loaded: false,
            incompleteReason: "FC page 5 is not loaded.");
        require(!missingFcPage.IsComplete
                && !FcChestProbeRules.TryPrepare(missingFcPage, out _, out var missingReason)
                && missingReason.Contains("page 5", StringComparison.Ordinal),
            "missing or unloaded FC page must block preparation");

        var zeroCapacity = MakeSnapshot(zeroCapacityPage5: true);
        require(!zeroCapacity.IsComplete
                && !FcChestProbeRules.TryPrepare(zeroCapacity, out _, out _),
            "a loaded FC page with zero slot capacity must not become actionable");

        var missingCoverage = MakeSnapshot();
        var missingCoverageSnapshot = new FcChestSnapshot(
            missingCoverage.AddonVisible,
            missingCoverage.AddonReady,
            missingCoverage.ContainerStates.Values,
            missingCoverage.ChestSlots.Where(slot => slot.Container != FcChestContainer.FreeCompanyPage5),
            missingCoverage.PlayerSlots,
            missingCoverage.ChestItems.Values,
            missingCoverage.PlayerItems.Values);
        require(!missingCoverageSnapshot.IsComplete
                && !FcChestProbeRules.TryPrepare(missingCoverageSnapshot, out _, out _),
            "a snapshot missing expected container-slot coverage must not become actionable");

        var hiddenAddon = MakeSnapshot(addonVisible: false);
        require(!hiddenAddon.IsComplete
                && !FcChestProbeRules.TryPrepare(hiddenAddon, out _, out _),
            "a hidden FC chest addon must block preparation even when containers look complete");
        var notReadyAddon = MakeSnapshot(addonReady: false);
        require(!notReadyAddon.IsComplete
                && !FcChestProbeRules.TryPrepare(notReadyAddon, out _, out _),
            "an FC chest addon that is visible but not ready must block preparation");

        var sourceChanged = Replace(
            initial,
            initial.ChestItems.Values.Select(item => item.Address == preparation.Withdrawal.Source
                ? item with { Quantity = 2 }
                : item),
            initial.PlayerItems.Values);
        var sourceValidation = FcChestProbeRules.ValidatePreparedWithdrawal(preparation, sourceChanged);
        require(!sourceValidation.Accepted && sourceValidation.Failure == FcChestDeltaFailure.SourceChanged,
            "a source quantity or quality change before dispatch must block the native call");

        var occupiedDestination = Replace(
            initial,
            initial.ChestItems.Values,
            initial.PlayerItems.Values.Append(new FcChestSlotItem(
                preparation.Withdrawal.Destination,
                new FcChestItemKey(901, false),
                1)));
        var destinationValidation = FcChestProbeRules.ValidatePreparedWithdrawal(preparation, occupiedDestination);
        require(!destinationValidation.Accepted
                && destinationValidation.Failure == FcChestDeltaFailure.DestinationOccupied,
            "a destination occupied after preparation must block the native call");

        var noDelta = FcChestProbeRules.ValidateObservedDelta(
            initial,
            initial,
            preparation.Withdrawal,
            FcChestTransferDirection.Withdrawal);
        require(!noDelta.Accepted && noDelta.Failure == FcChestDeltaFailure.NoDelta,
            "a transfer timeout path must treat an unchanged complete snapshot as no delta");
        var deadline = DateTime.UtcNow.AddSeconds(-1);
        require(FcChestProbeRules.IsReconciliationTimedOut(DateTime.UtcNow, deadline),
            "reconciliation must become timed out at or after its bounded deadline");

        var withdrawalAt = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var depositNotBefore = withdrawalAt.AddMilliseconds(500);
        require(!FcChestTransferTiming.IsDispatchReady(
                    withdrawalAt.AddMilliseconds(499),
                    depositNotBefore),
            "deposit dispatch must remain blocked before the fixed 500 ms inter-action delay");
        require(FcChestTransferTiming.IsDispatchReady(
                    withdrawalAt.AddMilliseconds(500),
                    depositNotBefore),
            "deposit dispatch must become ready at the fixed 500 ms inter-action boundary");

        require(FcChestProbeRules.ShouldSuppressFurtherTransfer(
                    FcChestProbeState.ReconcilingWithdraw,
                    cancellationRequested: true,
                    physicalTransferAttempted: true),
            "cancellation after withdrawal must suppress the assumed deposit");
        require(FcChestProbeRules.ShouldSuppressFurtherTransfer(
                    FcChestProbeState.Depositing,
                    cancellationRequested: true,
                    physicalTransferAttempted: false),
            "cancellation during the inter-action delay must suppress deposit dispatch");
        require(!FcChestProbeRules.ShouldSuppressFurtherTransfer(
                    FcChestProbeState.Prepared,
                    cancellationRequested: false,
                    physicalTransferAttempted: false),
            "a prepared, armed-free probe must not be treated as already cancelled");

        require(FcChestProbeRules.ShouldSuppressFurtherTransfer(
                    FcChestProbeState.ReconcilingDeposit,
                    cancellationRequested: false,
                    physicalTransferAttempted: true),
            "a second execution after a physical transfer attempt must be suppressed");
    }

    private static FcChestSnapshot MakeSnapshot(
        bool addonVisible = true,
        bool addonReady = true,
        bool page5Loaded = true,
        bool zeroCapacityPage5 = false,
        bool hqCrystal = false,
        string? incompleteReason = null)
    {
        var chestSlots = new List<FcChestSlotAddress>();
        foreach (var container in new[]
                 {
                     FcChestContainer.FreeCompanyPage1,
                     FcChestContainer.FreeCompanyPage2,
                     FcChestContainer.FreeCompanyPage3,
                     FcChestContainer.FreeCompanyPage4,
                     FcChestContainer.FreeCompanyPage5,
                     FcChestContainer.FreeCompanyCrystals,
                 })
        {
            chestSlots.Add(new FcChestSlotAddress(container, 0));
            if (container == FcChestContainer.FreeCompanyPage1)
                chestSlots.Add(new FcChestSlotAddress(container, 1));
        }

        var playerSlots = new List<FcChestSlotAddress>
        {
            new(FcChestContainer.Inventory1, 0),
            new(FcChestContainer.Inventory1, 1),
            new(FcChestContainer.Inventory2, 0),
            new(FcChestContainer.Inventory3, 0),
            new(FcChestContainer.Inventory4, 0),
        };
        var chestItems = new List<FcChestSlotItem>
        {
            new(new FcChestSlotAddress(FcChestContainer.FreeCompanyPage1, 0), new FcChestItemKey(500, false), 1),
            new(new FcChestSlotAddress(FcChestContainer.FreeCompanyPage1, 1), new FcChestItemKey(500, true), 3),
            new(new FcChestSlotAddress(FcChestContainer.FreeCompanyPage2, 0), new FcChestItemKey(500, false), 2),
            new(new FcChestSlotAddress(FcChestContainer.FreeCompanyCrystals, 0), new FcChestItemKey(2, hqCrystal), 100),
        };
        var playerItems = new List<FcChestSlotItem>
        {
            new(new FcChestSlotAddress(FcChestContainer.Inventory1, 1), new FcChestItemKey(700, false), 4),
        };
        return new FcChestSnapshot(
            addonVisible,
            addonReady,
            ContainerStates(page5Loaded, zeroCapacityPage5),
            chestSlots,
            playerSlots,
            chestItems,
            playerItems,
            incompleteReason);
    }

    private static IReadOnlyList<FcChestContainerState> ContainerStates(
        bool page5Loaded,
        bool zeroCapacityPage5)
        =>
        [
            new(FcChestContainer.FreeCompanyPage1, true, 2),
            new(FcChestContainer.FreeCompanyPage2, true, 1),
            new(FcChestContainer.FreeCompanyPage3, true, 1),
            new(FcChestContainer.FreeCompanyPage4, true, 1),
            new(FcChestContainer.FreeCompanyPage5, page5Loaded, zeroCapacityPage5 ? 0 : 1),
            new(FcChestContainer.FreeCompanyCrystals, true, 1),
            new(FcChestContainer.Inventory1, true, 2),
            new(FcChestContainer.Inventory2, true, 1),
            new(FcChestContainer.Inventory3, true, 1),
            new(FcChestContainer.Inventory4, true, 1),
        ];

    private static FcChestSnapshot Replace(
        FcChestSnapshot original,
        IEnumerable<FcChestSlotItem> chestItems,
        IEnumerable<FcChestSlotItem> playerItems)
        => new(
            original.AddonVisible,
            original.AddonReady,
            original.ContainerStates.Values,
            original.ChestSlots,
            original.PlayerSlots,
            chestItems,
            playerItems,
            original.IncompleteReason);
}
