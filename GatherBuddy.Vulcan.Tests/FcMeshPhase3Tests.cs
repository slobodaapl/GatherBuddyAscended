using System;
using System.Linq;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.Vulcan.Tests;

internal static class FcMeshPhase3Tests
{
    private static readonly Guid ListA = Guid.Parse("00000000-0000-0000-0000-00000000f311");
    private static readonly Guid ListB = Guid.Parse("00000000-0000-0000-0000-00000000f312");
    private static readonly Guid RemoteSession = Guid.Parse("00000000-0000-0000-0000-00000000f313");

    public static void Run(Action<bool, string> require)
    {
        MissingMaterialGatherAndSafeBoundary(require);
        RemoteSupplyWaitsAndExpires(require);
        ChestWithdrawIsAtomic(require);
        ForeignChestObservationCannotTransfer(require);
        StaleLocalChestCannotTransferAgainstFreshForeignObservation(require);
        CraftAndDepositAreExplicit(require);
        DemandReopensAfterChestObservation(require);
        ProjectionScopingAndQuality(require);
    }

    private static void MissingMaterialGatherAndSafeBoundary(Action<bool, string> require)
    {
        var clock = new FcManualClock(1_000_000);
        var driver = NewDriver(clock, ListA);
        require(driver.InjectDemoList(ListA, 100, 100, 1, owner: "list-a").Accepted,
            "synthetic target record enters the ordinary validated world store");

        var gather = driver.Step();
        require(gather.Kind == FcSyntheticActionKind.Gather
                && gather.ItemId == 100
                && gather.Quantity == 100
                && driver.State == FcSyntheticControllerState.Gathering
                && driver.ExecutionPlan?.ExecutionSource == ExecutionSource.FcFulfillment,
            "missing raw material produces a temporary gather target through the FC planning context");

        var complete = driver.CompleteGather();
        require(complete.Kind == FcSyntheticActionKind.Complete
                && driver.LocalWorker.HeldInventoryMap.Get(100, FcItemQuality.Nq) == 100
                && driver.Store.Transfers.Count == 0,
            "synthetic gather completion updates private held state without implying a deposit");

        var planRevision = driver.ExecutionPlan!.WorldRevision!.Number;
        require(planRevision == driver.World.Revision.Number,
            "safe-boundary gather completion refreshes the FC execution plan to the new world revision");
    }

    private static void RemoteSupplyWaitsAndExpires(Action<bool, string> require)
    {
        var clock = new FcManualClock(2_000_000);
        var driver = NewDriver(clock, ListA);
        require(driver.InjectDemoList(ListA, 100, 100, 1, owner: "list-a").Accepted,
            "remote-supply target enters synthetic world");
        require(driver.Step().Kind == FcSyntheticActionKind.Gather,
            "missing supply starts gathering before remote evidence arrives");

        require(driver.InjectDemoRemoteWorker(
                    "remote-a",
                    RemoteSession,
                    ListA,
                    100,
                    TimeSpan.Zero,
                    itemId: 100).Accepted
                && driver.ExecutionPlan!.IsWorldRevisionDirty,
            "new remote held evidence marks the active FC plan dirty without changing the active interaction");
        require(driver.Step(gatheringInteractionActive: true).Kind == FcSyntheticActionKind.Gather
                && driver.LocalWorker.HeldInventoryMap.Get(100) == 0,
            "remote revision cannot switch an active gather interaction mid-interaction");
        require(driver.Step().Kind == FcSyntheticActionKind.WaitRemote
                && driver.LocalWorker.HeldInventoryMap.Get(100) == 0,
            "safe-boundary refresh stops duplicate gathering and waits for represented remote material");

        clock.Advance(TimeSpan.FromMinutes(5));
        require(driver.InjectLocalHeld(FcItemQuantityMap.Empty).Accepted
                && driver.Step().Kind == FcSyntheticActionKind.Gather
                && driver.World.ActiveWorkers.All(worker => worker.Header.OwnerAuthorId != "remote-a"),
            "expired remote HLC evidence contributes zero and reopens local gathering");

        driver.Reset();
        require(driver.InjectDemoList(ListA, 100, 100, 1, owner: "list-a").Accepted
                && driver.InjectDemoRemoteWorker(
                    "remote-a",
                    RemoteSession,
                    ListA,
                    100,
                    TimeSpan.Zero,
                    state: FcWorkerState.Unsubscribed,
                    itemId: 100).Accepted
                && driver.Step().Kind == FcSyntheticActionKind.Gather,
            "unsubscribed remote worker contributes zero despite held inventory");
    }

    private static void ChestWithdrawIsAtomic(Action<bool, string> require)
    {
        var clock = new FcManualClock(3_000_000);
        var driver = NewDriver(clock, ListA);
        require(driver.InjectDemoList(ListA, 100, 100, 1, owner: "list-a").Accepted
                && driver.InjectDemoChest(100, 1, 100).Accepted,
            "fresh complete synthetic chest snapshot enters the local world");
        var revisionBefore = driver.Store.Revision.Number;
        var withdraw = driver.Step();
        require(withdraw.Kind == FcSyntheticActionKind.Withdraw
                && driver.Store.Revision.Number == revisionBefore + 1
                && driver.Store.Transfers.Count == 1
                && driver.World.Chest.Items.Get(100) == 0
                && driver.LocalWorker.HeldInventoryMap.Get(100) == 100,
            "fresh chest material withdraws through one transfer and one world revision with both after-states visible");
        require(driver.ActionLog.Count(action => action.Kind == FcSyntheticActionKind.Withdraw) == 1,
            "one atomic chest withdrawal produces one synthetic withdrawal action");
        require(driver.Step().Kind == FcSyntheticActionKind.Complete,
            "withdrawn local held material fulfills the selected target");
    }

    private static void ForeignChestObservationCannotTransfer(Action<bool, string> require)
    {
        var clock = new FcManualClock(3_500_000);
        var driver = NewDriver(clock, ListA);
        require(driver.InjectDemoList(ListA, 100, 100, 1, owner: "list-a").Accepted
                && driver.InjectDemoChest(100, 1, 100, owner: "foreign-observer").Accepted,
            "foreign complete chest observation enters the projection without becoming a local transfer preimage");
        var revisionBefore = driver.Store.Revision.Number;
        var action = driver.Step();
        require(action.Kind == FcSyntheticActionKind.WaitRemote
                && driver.Store.Transfers.Count == 0
                && driver.Store.Revision.Number == revisionBefore
                && driver.LocalWorker.HeldInventoryMap.Get(100) == 0,
            "foreign-only chest observation cannot silently withdraw without a local actor inspection");
    }

    private static void StaleLocalChestCannotTransferAgainstFreshForeignObservation(Action<bool, string> require)
    {
        var clock = new FcManualClock(3_600_000);
        var driver = NewDriver(clock, ListA);
        require(driver.InjectDemoList(ListA, 100, 100, 1, owner: "list-a").Accepted
                && driver.InjectDemoChest(100, 1, 100).Accepted,
            "local chest preimage enters the synthetic world");
        clock.Advance(TimeSpan.FromMinutes(5));
        require(driver.InjectLocalHeld(FcItemQuantityMap.Empty).Accepted
                && driver.InjectDemoChest(100, 1, 100, owner: "foreign-observer").Accepted,
            "fresh foreign chest observation supersedes the stale local projection");
        require(driver.World.Chest.IsFresh
                && driver.World.Chest.Snapshot is { } projected
                && projected.Header.OwnerAuthorId == "foreign-observer",
            "projection selects the fresh foreign chest observation over the stale local register");
        var revisionBefore = driver.Store.Revision.Number;
        var action = driver.Step();
        require(action.Kind == FcSyntheticActionKind.WaitRemote
                && driver.Store.Transfers.Count == 0
                && driver.Store.Revision.Number == revisionBefore
                && driver.LocalWorker.HeldInventoryMap.Get(100) == 0,
            "stale local chest state cannot authorize transfer against a fresher foreign observation");
    }

    private static void CraftAndDepositAreExplicit(Action<bool, string> require)
    {
        var clock = new FcManualClock(4_000_000);
        var driver = NewDriver(clock, ListA);
        driver.SetCraftSpec(new FcSyntheticCraftSpec(
            ListA,
            500,
            100,
            FcItemQuality.Nq,
            1,
            200,
            FcItemQuality.Nq,
            1));
        require(driver.InjectDemoList(ListA, 200, 1, 1, owner: "list-a").Accepted
                && driver.InjectDemoChest(0, 1, 100).Accepted,
            "synthetic craft output target and complete empty chest enter the world");
        require(driver.Step().Kind == FcSyntheticActionKind.Gather,
            "crafting cannot start before local consumable preflight succeeds");
        require(driver.CompleteGather().Kind == FcSyntheticActionKind.Craft,
            "craft action is selected after gathered input becomes local consumable inventory");
        require(driver.LocalWorker.HeldInventoryMap.Get(100) == 1
                && driver.Store.Transfers.Count == 0,
            "craft selection does not teleport or deposit inventory");

        var completedCraft = driver.CompleteCraft();
        var deposit = driver.Step();
        require(completedCraft.Kind == FcSyntheticActionKind.Craft
                && deposit.Kind == FcSyntheticActionKind.Deposit
                && driver.Store.Transfers.Count == 1
                && driver.World.Chest.Items.Get(200) == 1
                && driver.LocalWorker.HeldInventoryMap.Get(200) == 0,
            "crafted attributable output deposits only through one explicit atomic transfer");

        driver.Reset();
        require(driver.InjectDemoList(ListA, 200, 1, 1, owner: "list-a").Accepted
                && driver.InjectDemoChest(0, 1, 100).Accepted,
            "cancel fixture enters a fresh synthetic world");
        driver.SetCraftSpec(new FcSyntheticCraftSpec(
            ListA, 500, 100, FcItemQuality.Nq, 1, 200, FcItemQuality.Nq, 1));
        require(driver.Step().Kind == FcSyntheticActionKind.Gather
                && driver.CompleteGather().Kind == FcSyntheticActionKind.Craft
                && driver.CompleteCraft().Kind == FcSyntheticActionKind.Craft,
            "cancel fixture starts a gather interaction");
        var cancel = driver.Cancel();
        require(cancel.Kind == FcSyntheticActionKind.Cancel
                && driver.Store.Transfers.Count == 0
                && driver.LocalWorker.HeldInventoryMap.Get(200) == 1
                && driver.Step().Kind == FcSyntheticActionKind.Cancel,
            "stop/cancel does not imply deposit, travel, or inventory teleport");
    }

    private static void DemandReopensAfterChestObservation(Action<bool, string> require)
    {
        var clock = new FcManualClock(5_000_000);
        var driver = NewDriver(clock, ListA);
        driver.SetCraftSpec(new FcSyntheticCraftSpec(
            ListA, 501, 300, FcItemQuality.Nq, 1, 100, FcItemQuality.Nq, 1));
        require(driver.InjectDemoList(ListA, 100, 1, 1, owner: "list-a").Accepted
                && driver.InjectDemoChest(0, 1, 300).Accepted
                && driver.Step().Kind == FcSyntheticActionKind.Gather
                && driver.CompleteGather().Kind == FcSyntheticActionKind.Craft
                && driver.CompleteCraft().Kind == FcSyntheticActionKind.Craft
                && driver.Step().Kind == FcSyntheticActionKind.Deposit
                && driver.Step().Kind == FcSyntheticActionKind.Complete,
            "initial crafted output is explicitly deposited and satisfies the published target");

        var removal = driver.InjectDemoChest(0, 3, 300);
        require(removal.Accepted,
            $"newer complete chest observation removes the previously available stock ({removal.Status}: {removal.Message})");
        require(driver.Step().Kind == FcSyntheticActionKind.Gather
                && driver.World.Fulfillment.RemainingDemand.Get(100) == 1,
            "demand reopening derives new work after the completed local deposit is observed removed");
        require(driver.LocalWorker.HeldInventoryMap.Get(100) == 0,
            "newer chest observation and local post-deposit state do not retain phantom held supply");
    }

    private static void ProjectionScopingAndQuality(Action<bool, string> require)
    {
        var clock = new FcManualClock(6_000_000);
        var driver = new FcSyntheticFulfillmentDriver(
            clock,
            selectedListIds: [ListA, ListA],
            localAuthorId: "local",
            hlcNodeId: "local-node");
        require(driver.SelectedListIds.SequenceEqual([ListA]),
            "duplicate local list selection is normalized to one active list");
        require(driver.InjectDemoList(ListA, 100, 100, 1, owner: "list-a").Accepted
                && driver.InjectDemoList(ListB, 100, 100, 1, owner: "list-b").Accepted
                && driver.InjectDemoRemoteWorker(
                    "remote-b",
                    RemoteSession,
                    ListB,
                    0,
                    TimeSpan.Zero,
                    itemId: 100).Accepted,
            "distinct published list demand and a separate selected-list worker enter the world");
        require(driver.World.ActiveListIds.SequenceEqual([ListA, ListB])
                && driver.World.Fulfillment.TotalDemand == 200,
            "distinct published list demands aggregate while duplicate selection does not duplicate one list demand");

        require(driver.InjectDemoRemoteWorker(
                    "remote-a",
                    Guid.Parse("00000000-0000-0000-0000-00000000f314"),
                    ListA,
                    100,
                    TimeSpan.Zero,
                    itemId: 100).Accepted
                && driver.World.ActiveWorkers.Any(worker => worker.Header.OwnerAuthorId == "remote-a")
                && driver.World.ActiveWorkers.Any(worker => driver.Store.GetWorkerHlc(worker.Header.OwnerAuthorId)!.Value.NodeId == "remote-a-hlc"),
            "fresh worker HLC physical age controls liveness while HLC node remains independent from author identity");

        var remoteHlc = driver.Store.GetWorkerHlc("remote-a");
        var localObservation = driver.InjectDemoList(ListA, 100, 1, 2, owner: "local");
        var localHlc = driver.Store.GetListHlc("local", ListA);
        require(localObservation.Accepted
                && remoteHlc is { } remoteStamp
                && localHlc is { } localStamp
                && localStamp > remoteStamp,
            "accepted remote HLC is observed before a causally subsequent local synthetic record");

        var hqRejected = false;
        try
        {
            _ = driver.InjectDemoList(ListA, 100, 1, 2, FcItemQuality.Hq, owner: "list-a");
        }
        catch (NotSupportedException)
        {
            hqRejected = true;
        }
        require(hqRejected,
            "unsupported HQ synthetic targets are rejected instead of silently flattened to NQ");

        var matching = FcFulfillmentMatcher.Match(
            [
                new FcListDemand(ListA, 100, FcItemQuality.Nq, 100),
                new FcListDemand(ListB, 100, FcItemQuality.Nq, 100),
            ],
            new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 50)]),
            [new FcWorkerSupply(
                "remote-a",
                new FcItemQuantityMap([new ItemQuantityEntry(100, FcItemQuality.Nq, 100)]),
                new[] { ListA, ListB }.ToHashSet())]);
        require(matching.TotalMatched == 150
                && matching.Contributions.Sum(contribution => contribution.Quantity) == 150,
            "deterministic capacity matching allocates each physical quality-keyed unit once across lists");
    }

    private static FcSyntheticFulfillmentDriver NewDriver(FcManualClock clock, Guid listId)
        => new(
            clock,
            selectedListIds: [listId],
            localAuthorId: "local",
            hlcNodeId: "local-node");
}
