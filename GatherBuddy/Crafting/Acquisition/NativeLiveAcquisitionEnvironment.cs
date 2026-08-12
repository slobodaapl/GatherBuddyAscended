using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using GatherBuddy.Automation;
using GatherBuddy.Helpers;
using GatherBuddy.Plugin;
using GatherBuddy.SeFunctions;
using GatherBuddy.Utility;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel.Sheets;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace GatherBuddy.Crafting.Acquisition;

/// <summary>
/// Default game adapter for live acquisition. Universalis is deliberately not
/// used here: the market-board InfoProxy is the only listing authority used by
/// a purchase.
///
/// Native pointers are confined to synchronous helpers. No pointer remains
/// live across an await and the adapter itself therefore has a safe class
/// context.
/// </summary>
public sealed class NativeLiveAcquisitionEnvironment : ILiveAcquisitionEnvironment
{
    public const uint MarketBoardDataId = 2_000_442;

    private readonly Func<AcquisitionTransaction, TimeSpan, CancellationToken, Task<LiveVendorPurchaseResult>>? _vendorPurchase;
    private readonly Func<VendorCurrencyGroup, uint, string, VendorCurrencyAvailability> _currencyAvailability;
    private int _marketRequestGeneration;
    private byte? _activeMarketRequestId;

    public NativeLiveAcquisitionEnvironment(
        Func<AcquisitionTransaction, TimeSpan, CancellationToken, Task<LiveVendorPurchaseResult>>? vendorPurchase = null,
        Func<VendorCurrencyGroup, uint, string, VendorCurrencyAvailability>? currencyAvailability = null)
    {
        _vendorPurchase = vendorPurchase;
        _currencyAvailability = currencyAvailability ?? ResolveCurrencyAvailability;
    }

    public uint CurrentWorldId
        => RunOnFrameworkThread(GetCurrentWorldIdNative);

    public string CurrentWorldName
        => RunOnFrameworkThread(GetCurrentWorldNameNative);

    public bool IsLifestreamAvailable
        => RunOnFrameworkThread(() => Lifestream.Enabled);

    public bool IsVNavmeshAvailable
        => RunOnFrameworkThread(IsVNavmeshAvailableNative);

    public bool IsMarketAutomationAvailable
        => RunOnFrameworkThread(() => Dalamud.ClientState.IsLoggedIn);

    public bool IsVendorAutomationAvailable
        => _vendorPurchase != null;

    public bool IsInDuty
        => RunOnFrameworkThread(Functions.BoundByDuty);

    public bool IsInNonCrossWorldParty
        => RunOnFrameworkThread(IsInNonCrossWorldPartyNative);

    public bool CanVisitWorld(uint worldId)
    {
        if (worldId == 0)
            return false;
        if (worldId == CurrentWorldId)
            return true;
        var worldName = ResolveWorldName(worldId);
        return RunOnFrameworkThread(() => Lifestream.CanVisitSameDC?.Invoke(worldName) ?? false);
    }

    public bool IsGatewayAttuned(uint gatewayId)
        => gatewayId != 0 && RunOnFrameworkThread(() => Teleporter.IsAttuned(gatewayId));

    public long GetGatewayTeleportCost(uint gatewayId)
        => gatewayId == 0
            ? long.MaxValue
            : RunOnFrameworkThread(() => ReadTeleportCost(gatewayId));

    public string ResolveWorldName(uint worldId)
    {
        if (worldId == 0)
            return string.Empty;

        return RunOnFrameworkThread(() =>
        {
            try
            {
                var worlds = Dalamud.GameData.GetExcelSheet<World>();
                return worlds?.TryGetRow(worldId, out var world) == true
                    ? world.Name.ExtractText()
                    : worldId.ToString();
            }
            catch
            {
                return worldId.ToString();
            }
        });
    }

    public LiveAcquisitionPreconditionResult ValidatePlan(
        AcquisitionPlan plan,
        LiveAcquisitionOptions options)
    {
        if (plan.Transactions.Count == 0)
            return new LiveAcquisitionPreconditionResult(true);
        if (CurrentWorldId == 0)
            return new LiveAcquisitionPreconditionResult(false, LiveAcquisitionFailureKind.TravelBlocked, "The current world is unknown; acquisition cannot start safely.");
        if (plan.Transactions.Any(transaction => transaction.SourceKind == AcquisitionSourceKind.Market
                && transaction.WorldId == 0))
            return new LiveAcquisitionPreconditionResult(false, LiveAcquisitionFailureKind.TravelBlocked, "A market transaction has no resolved world; acquisition cannot start safely.");

        var hasMarket = plan.Transactions.Any(transaction => transaction.SourceKind == AcquisitionSourceKind.Market);
        var hasVendor = plan.Transactions.Any(transaction => transaction.SourceKind == AcquisitionSourceKind.Vendor);
        if (hasMarket && !IsMarketAutomationAvailable)
            return new LiveAcquisitionPreconditionResult(false, LiveAcquisitionFailureKind.MarketUnavailable, "The game is not ready for market-board automation.");
        if (hasVendor && !IsVendorAutomationAvailable)
            return new LiveAcquisitionPreconditionResult(false, LiveAcquisitionFailureKind.VendorUnavailable, "Vendor purchase automation is unavailable.");
        if ((hasMarket || hasVendor) && IsInDuty)
            return new LiveAcquisitionPreconditionResult(false, LiveAcquisitionFailureKind.DutyOrPartyRestriction, "Purchasing is unavailable while bound by a duty.");
        if (hasMarket && !IsVNavmeshAvailable)
            return new LiveAcquisitionPreconditionResult(false, LiveAcquisitionFailureKind.MissingPlugin, "vnavmesh is required to navigate to a market board.");
        if (hasMarket && plan.Transactions.Any(transaction => transaction.WorldId != 0 && transaction.WorldId != CurrentWorldId)
            && !IsLifestreamAvailable)
            return new LiveAcquisitionPreconditionResult(false, LiveAcquisitionFailureKind.MissingPlugin, "Lifestream is required for marketplace world travel.");
        if (options.CurrentWorldOnly
            && plan.Transactions.Any(transaction => transaction.SourceKind == AcquisitionSourceKind.Market
                && transaction.WorldId != 0
                && transaction.WorldId != CurrentWorldId))
            return new LiveAcquisitionPreconditionResult(false, LiveAcquisitionFailureKind.TravelBlocked, "The plan requires another world while Current world only is enabled.");

        if (options.MaximumGilSpend.HasValue && plan.Estimate.TotalGil > options.MaximumGilSpend.Value)
            return new LiveAcquisitionPreconditionResult(false, LiveAcquisitionFailureKind.GilBudgetExceeded, "The selected acquisition plan exceeds the maximum Gil spend.");

        if (GetEmptyBagSlots() == 0)
            return new LiveAcquisitionPreconditionResult(false, LiveAcquisitionFailureKind.InventoryCapacity, "Inventory has no free slot for an acquired item.");

        if (!HasSufficientPlannedCurrency(plan))
            return new LiveAcquisitionPreconditionResult(false, LiveAcquisitionFailureKind.CurrencyUnavailable, "Current currency balances cannot satisfy the selected acquisition plan.");

        return new LiveAcquisitionPreconditionResult(true);
    }

    internal bool HasSufficientPlannedCurrency(AcquisitionPlan plan)
        => plan.Estimate.Currencies.All(requirement => HasSufficientCurrency(plan, requirement));

    private bool HasSufficientCurrency(AcquisitionPlan plan, AcquisitionCurrencyRequirement requirement)
    {
        var cost = plan.Transactions
            .SelectMany(transaction => transaction.Costs)
            .FirstOrDefault(candidate => candidate.CurrencyId == requirement.CurrencyId);
        var group = cost?.Group
            ?? (requirement.CurrencyId == AcquisitionCurrency.GilId
                ? VendorCurrencyGroup.Gil
                : VendorCurrencyGroup.Other);
        var availability = _currencyAvailability(group, requirement.CurrencyId, requirement.CurrencyName);
        return IsAuthoritativeCurrencySufficient(availability, requirement.Required);
    }

    internal static bool IsAuthoritativeCurrencySufficient(
        VendorCurrencyAvailability availability,
        long required)
        => required >= 0
            && VendorCurrencyAvailabilityResolver.IsAuthoritativeSource(availability.Source)
            && availability.AvailableAmount >= required;

    private static VendorCurrencyAvailability ResolveCurrencyAvailability(
        VendorCurrencyGroup group,
        uint currencyId,
        string currencyName)
        => RunOnFrameworkThread(() => VendorCurrencyAvailabilityResolver.Resolve(group, currencyId, currencyName));

    public async Task<bool> TravelToWorldAsync(
        AcquisitionWorldRoute route,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (route.WorldId == 0 || string.IsNullOrWhiteSpace(route.WorldName))
            return false;
        if (route.WorldId == CurrentWorldId)
            return true;
        if (!IsLifestreamAvailable || !CanVisitWorld(route.WorldId))
            return false;

        try
        {
            RunOnFrameworkThread(() => Lifestream.TPAndChangeWorld?.Invoke(
                route.WorldName,
                false,
                string.Empty,
                false,
                route.GatewayId == 0 ? null : (int)route.GatewayId,
                true,
                true), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[Acquisition] Lifestream world travel failed to start: {ex.Message}");
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RunOnFrameworkThread(() => GetCurrentWorldIdNative() == route.WorldId
                    && !(Lifestream.IsBusy?.Invoke() ?? false), cancellationToken))
                return true;
            await Task.Delay(250, cancellationToken);
        }
        return RunOnFrameworkThread(() => GetCurrentWorldIdNative() == route.WorldId
            && !(Lifestream.IsBusy?.Invoke() ?? false), cancellationToken);
    }

    public async Task<bool> NavigateToMarketBoardAsync(
        AcquisitionWorldRoute route,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (route.WorldId == 0
            || string.IsNullOrWhiteSpace(route.WorldName)
            || CurrentWorldId != route.WorldId)
            return false;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RunOnFrameworkThread(() => GetCurrentWorldIdNative() != route.WorldId, cancellationToken))
                return false;
            if (RunOnFrameworkThread(() => Lifestream.IsBusy?.Invoke() ?? false, cancellationToken))
            {
                await Task.Delay(250, cancellationToken);
                continue;
            }

            var step = RunOnFrameworkThread(() => TryNavigateToMarketBoardNative(route), cancellationToken);
            if (step == MarketBoardNavigationStep.Open)
                return true;
            if (step == MarketBoardNavigationStep.Unavailable)
                return false;
            await Task.Delay(250, cancellationToken);
        }
        return false;
    }

    public async Task<LiveMarketListingsResponse> RequestLiveListingsAsync(
        uint itemId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (itemId == 0)
            return LiveMarketListingsResponse.Failure("Cannot request market-board listings for world item 0.");

        var generation = checked(++_marketRequestGeneration);
        var requestStart = RunOnFrameworkThread(() =>
        {
            var started = TryBeginMarketRequest(itemId, generation, out var evidence);
            return (Started: started, Evidence: evidence);
        }, cancellationToken);
        if (!requestStart.Started)
            return LiveMarketListingsResponse.Failure($"Could not start a fresh market-board listing request for item {itemId:N0}.");
        _activeMarketRequestId = requestStart.Evidence.RequestId;

        var sawWaiting = false;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_marketRequestGeneration != generation
                || requestStart.Evidence.Generation != generation
                || requestStart.Evidence.ItemId != itemId)
                return LiveMarketListingsResponse.Failure($"Market-board listing request generation {generation} was superseded; stale proxy data was discarded.");
            var proxyState = RunOnFrameworkThread(() =>
            {
                var available = TryReadMarketProxyState(out var state);
                return (Available: available, State: state);
            }, cancellationToken);
            if (!proxyState.Available)
                return LiveMarketListingsResponse.Failure($"Market-board listing request generation {generation} lost its InfoProxy.");
            var state = proxyState.State;
            if (state.SearchItemId != itemId)
                return LiveMarketListingsResponse.Failure($"Market-board listing request generation {generation} returned item {state.SearchItemId:N0} instead of {itemId:N0}.");
            if (!state.WaitingForListings
                && (state.CurrentRequestId == requestStart.Evidence.PreviousCurrentRequestId
                    || state.CurrentRequestId != requestStart.Evidence.RequestId))
                return LiveMarketListingsResponse.Failure($"Market-board listing request generation {generation} did not produce a correlated InfoProxy result; stale proxy data was discarded.");

            sawWaiting |= state.WaitingForListings;
            var sawFreshResult = DateTime.UtcNow > requestStart.Evidence.StartedAtUtc
                && (sawWaiting || state.ListingCount > requestStart.Evidence.InitialListingCount);
            if (!state.WaitingForListings && sawFreshResult)
            {
                if (_marketRequestGeneration != generation)
                    return LiveMarketListingsResponse.Failure($"Market-board listing request generation {generation} was superseded before result capture; stale proxy data was discarded.");
                var listings = RunOnFrameworkThread(() =>
                    ReadCurrentListings(
                        itemId,
                        requestStart.Evidence.RequestId,
                        GetCurrentWorldIdNative(),
                        GetCurrentWorldNameNative()), cancellationToken);
                if (listings.Any(listing => listing.WorldId == 0))
                    return LiveMarketListingsResponse.Failure("The current world became unknown while capturing market listings; stale data was discarded.");
                return new LiveMarketListingsResponse(true, listings);
            }
            await Task.Delay(100, cancellationToken);
        }

        return LiveMarketListingsResponse.Failure(
            $"Market-board listing request generation {generation} did not produce a fresh ready result for item {itemId:N0}; stale proxy data was discarded.");
    }

    public async Task<LiveMarketPurchaseResult> PurchaseMarketListingAsync(
        LiveMarketListing listing,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var submit = RunOnFrameworkThread(() =>
        {
            var accepted = TrySubmitNativePurchase(
                listing,
                _activeMarketRequestId,
                out var submission,
                out var failure,
                out var stale);
            return (Accepted: accepted, Submission: submission, Failure: failure, Stale: stale);
        }, cancellationToken);
        if (!submit.Accepted)
            return new LiveMarketPurchaseResult(false, false, listing.ItemId, listing.ListingId, 0, 0, submit.Failure, false, listing.IsHq, null, null, submit.Stale);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var purchaseState = RunOnFrameworkThread(() => new NativePurchaseState(
                GetInventoryCountNative(listing.ItemId, listing.IsHq),
                GetGilBalanceNative()), cancellationToken);
            if (purchaseState.InventoryCount >= submit.Submission.InventoryBefore + listing.Quantity)
            {
                var spent = System.Math.Max(0, submit.Submission.GilBefore - purchaseState.GilBalance);
                return new LiveMarketPurchaseResult(
                    true,
                    true,
                    listing.ItemId,
                    listing.ListingId,
                    listing.Quantity,
                    spent,
                    $"Purchased {listing.Quantity:N0}x {listing.ItemId:N0} from the market board.",
                    true,
                    listing.IsHq,
                    submit.Submission.GilBefore,
                    purchaseState.GilBalance);
            }
            await Task.Delay(100, cancellationToken);
        }

        var finalState = RunOnFrameworkThread(() => new NativePurchaseState(
            GetInventoryCountNative(listing.ItemId, listing.IsHq),
            GetGilBalanceNative()), cancellationToken);
        var completed = System.Math.Max(0, finalState.InventoryCount - submit.Submission.InventoryBefore);
        return new LiveMarketPurchaseResult(
            true,
            completed >= listing.Quantity,
            listing.ItemId,
            listing.ListingId,
            completed,
            System.Math.Max(0, submit.Submission.GilBefore - finalState.GilBalance),
            completed >= listing.Quantity
                ? "Market-board purchase verified."
                : "Market-board purchase was accepted but inventory verification timed out; final state is indeterminate.",
            true,
            listing.IsHq,
            submit.Submission.GilBefore,
            finalState.GilBalance);
    }

    public Task<LiveVendorPurchaseResult> PurchaseVendorAsync(
        AcquisitionTransaction transaction,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => _vendorPurchase?.Invoke(transaction, timeout, cancellationToken)
            ?? Task.FromResult(new LiveVendorPurchaseResult(
                false,
                false,
                transaction.ItemId,
                0,
                new Dictionary<uint, long>(),
                0,
                "Vendor acquisition adapter is not configured.",
                false,
                transaction.IsHq,
                null,
                null));

    public Task CloseMarketBoardAsync(CancellationToken cancellationToken)
    {
        RunOnFrameworkThread(HideMarketBoard, cancellationToken);
        return Task.CompletedTask;
    }

    public Task CleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            RunOnFrameworkThread(() =>
            {
                StopNativeNavigation();
                HideMarketBoard();
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[Acquisition] Native cleanup warning: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    private static unsafe bool TryBeginMarketRequest(
        uint itemId,
        int generation,
        out NativeMarketRequestEvidence evidence)
    {
        evidence = default;
        var proxy = GetItemSearchProxy();
        if (proxy == null)
            return false;

        proxy->SearchItemId = itemId;
        proxy->ClearListData();
        var initialCount = System.Math.Max(0, (int)proxy->ListingCount);
        var previousCurrentRequestId = proxy->CurrentRequestId;
        var previousNextRequestId = proxy->NextRequestId;
        var startedAtUtc = DateTime.UtcNow;
        if (!proxy->RequestData())
            return false;
        var requestId = proxy->NextRequestId;
        if (requestId == previousNextRequestId)
            return false;
        evidence = new NativeMarketRequestEvidence(
            generation,
            itemId,
            initialCount,
            startedAtUtc,
            previousCurrentRequestId,
            requestId);
        return true;
    }

    private static unsafe bool TryReadMarketProxyState(out NativeMarketProxyState state)
    {
        var proxy = GetItemSearchProxy();
        if (proxy == null)
        {
            state = default;
            return false;
        }
        state = new NativeMarketProxyState(
            proxy->SearchItemId,
            proxy->WaitingForListings,
            System.Math.Max(0, (int)proxy->ListingCount),
            proxy->CurrentRequestId,
            proxy->NextRequestId);
        return true;
    }

    private static unsafe IReadOnlyList<LiveMarketListing> ReadCurrentListings(
        uint itemId,
        byte requestId,
        uint worldId,
        string worldName)
    {
        var proxy = GetItemSearchProxy();
        if (proxy == null
            || proxy->SearchItemId != itemId
            || proxy->CurrentRequestId != requestId)
            return Array.Empty<LiveMarketListing>();

        var count = System.Math.Min(System.Math.Max(0, (int)proxy->ListingCount), proxy->Listings.Length);
        var result = new List<LiveMarketListing>(count);
        for (var i = 0; i < count; i++)
        {
            var listing = proxy->Listings[i];
            result.Add(new LiveMarketListing(
                listing.ItemId,
                checked((long)listing.ListingId),
                worldId,
                worldName,
                checked((int)listing.Quantity),
                checked((int)listing.UnitPrice),
                checked((int)listing.TotalTax),
                listing.IsHqItem,
                listing.IsMannequin,
                listing.IsSellingAsSet));
        }
        return result;
    }

    private static unsafe bool TrySubmitNativePurchase(
        LiveMarketListing requested,
        byte? expectedRequestId,
        out NativePurchaseSubmission submission,
        out string failure,
        out bool stale)
    {
        submission = default;
        stale = false;
        var proxy = GetItemSearchProxy();
        if (proxy == null)
        {
            stale = true;
            failure = "Market-board InfoProxy is unavailable.";
            return false;
        }
        if (GetCurrentWorldIdNative() == 0)
        {
            stale = true;
            failure = "The current world is unknown; market-board purchase was rejected safely.";
            return false;
        }
        if (!expectedRequestId.HasValue || proxy->CurrentRequestId != expectedRequestId.Value)
        {
            stale = true;
            failure = "Market-board InfoProxy request identity no longer matches the fresh listing result.";
            return false;
        }
        if (proxy->SearchItemId != requested.ItemId)
        {
            stale = true;
            failure = $"Market-board search item {proxy->SearchItemId:N0} did not match requested item {requested.ItemId:N0}.";
            return false;
        }

        var count = System.Math.Min(System.Math.Max(0, (int)proxy->ListingCount), proxy->Listings.Length);
        for (var i = 0; i < count; i++)
        {
            ref var nativeListing = ref proxy->Listings[i];
            if ((long)nativeListing.ListingId != requested.ListingId
                || nativeListing.ItemId != requested.ItemId
                || checked((int)nativeListing.Quantity) != requested.Quantity
                || checked((int)nativeListing.UnitPrice) != requested.PricePerUnit
                || checked((int)nativeListing.TotalTax) != requested.TotalTax
                || nativeListing.IsHqItem != requested.IsHq
                || nativeListing.IsMannequin
                || nativeListing.IsSellingAsSet)
                continue;

            var inventoryBefore = GetInventoryCountNative(requested.ItemId, requested.IsHq);
            var gilBefore = GetGilBalanceNative();
            fixed (MarketBoardListing* listingPointer = &nativeListing)
            {
                if (!proxy->SetLastPurchasedItem(listingPointer) || !proxy->SendPurchaseRequestPacket())
                {
                    failure = "The game rejected the market-board purchase request.";
                    return false;
                }
            }

            submission = new NativePurchaseSubmission(inventoryBefore, gilBefore);
            failure = string.Empty;
            return true;
        }

        stale = true;
        failure = $"The requested market-board listing {requested.ListingId:N0} is no longer present or changed.";
        return false;
    }

    private static unsafe InfoProxyItemSearch* GetItemSearchProxy()
    {
        var agent = AgentItemSearch.Instance();
        return agent == null ? null : agent->InfoProxyItemSearch;
    }

    private static unsafe bool IsMarketBoardAddonVisibleNative()
    {
        return GenericHelpers.TryGetAddonByName<AddonItemSearch>("ItemSearch", out var addon)
            && addon != null
            && addon->AtkUnitBase.IsVisible;
    }

    private static unsafe MarketBoardNavigationStep TryNavigateToMarketBoardNative(AcquisitionWorldRoute route)
    {
        if (route.WorldId == 0
            || string.IsNullOrWhiteSpace(route.WorldName)
            || (route.IsWorldHop
                && (route.GatewayId == 0 || string.IsNullOrWhiteSpace(route.GatewayName))))
            return MarketBoardNavigationStep.Unavailable;

        if (IsMarketBoardAddonVisibleNative())
            return MarketBoardNavigationStep.Open;

        var board = Dalamud.Objects.FirstOrDefault(obj => obj.BaseId == MarketBoardDataId);
        if (board != null)
        {
            var player = Dalamud.Objects.LocalPlayer;
            if (player != null && Vector3.DistanceSquared(player.Position, board.Position) > 16f)
            {
                if (!IsVNavmeshAvailableNative())
                    return MarketBoardNavigationStep.Unavailable;
                VNavmesh.SimpleMove.PathfindAndMoveCloseTo?.Invoke(board.Position, false, 3f);
            }
            else
            {
                OpenMarketBoardObject(board.Address);
            }
        }
        else if (Lifestream.Enabled && !(Lifestream.IsBusy?.Invoke() ?? false))
        {
            // A world-hop route arrives in its selected GatewayName city. The
            // unqualified /li mb command therefore stays in that city/current
            // world instead of introducing another teleport.
            Lifestream.ExecuteCommand?.Invoke("mb");
        }

        return MarketBoardNavigationStep.Continue;
    }

    private static bool IsVNavmeshAvailableNative()
        => VNavmesh.Enabled && (VNavmesh.Nav.IsReady?.Invoke() ?? false);

    private static unsafe void OpenMarketBoardObject(nint address)
    {
        var target = TargetSystem.Instance();
        if (target != null)
            target->OpenObjectInteraction((GameObjectStruct*)address);
    }

    private static unsafe void HideMarketBoard()
    {
        var agent = AgentItemSearch.Instance();
        if (agent != null)
            agent->Hide();
    }

    private static unsafe void StopNativeNavigation()
        => VNavmesh.Path.Stop?.Invoke();

    private static int GetEmptyBagSlots()
        => RunOnFrameworkThread(GetEmptyBagSlotsNative);

    private static unsafe int GetEmptyBagSlotsNative()
        => System.Math.Max(0, checked((int)InventoryManager.Instance()->GetEmptySlotsInBag()));

    private static unsafe int GetInventoryCountNative(uint itemId, bool hq)
        => System.Math.Max(0, InventoryManager.Instance()->GetInventoryItemCount(itemId, hq, false, false));

    private static unsafe long GetGilBalanceNative()
        => InventoryManager.Instance()->GetGil();

    private static unsafe long ReadTeleportCost(uint gatewayId)
    {
        var telepo = Telepo.Instance();
        if (telepo == null)
            return long.MaxValue;

        telepo->UpdateAetheryteList();
        for (var i = 0; i < telepo->TeleportList.Count; i++)
        {
            var entry = telepo->TeleportList[i];
            if (entry.AetheryteId == gatewayId)
                return entry.GilCost;
        }
        return long.MaxValue;
    }

    private static unsafe bool IsInNonCrossWorldPartyNative()
    {
        if (Dalamud.Conditions[ConditionFlag.ParticipatingInCrossWorldPartyOrAlliance])
            return false;

        var groupManager = GroupManager.Instance();
        if (groupManager == null)
            return false;

        // A normal party is the explicit blocker: changing world would
        // disband it and must fail before any purchase starts.
        return groupManager->MainGroup.MemberCount > 1;
    }

    private static uint GetCurrentWorldIdNative()
        => Dalamud.Objects.LocalPlayer?.CurrentWorld.RowId ?? 0;

    private static string GetCurrentWorldNameNative()
        => Dalamud.Objects.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ExtractText()
            ?? string.Empty;

    private static T RunOnFrameworkThread<T>(Func<T> callback)
        => RunOnFrameworkThread(callback, CancellationToken.None);

    private static T RunOnFrameworkThread<T>(Func<T> callback, CancellationToken cancellationToken)
    {
        if (Dalamud.Framework.IsInFrameworkUpdateThread)
            return callback();
        return RunOnFrameworkThreadAsync(callback, cancellationToken).GetAwaiter().GetResult();
    }

    private static void RunOnFrameworkThread(System.Action callback)
        => RunOnFrameworkThread(callback, CancellationToken.None);

    private static void RunOnFrameworkThread(System.Action callback, CancellationToken cancellationToken)
    {
        if (Dalamud.Framework.IsInFrameworkUpdateThread)
        {
            callback();
            return;
        }

        RunOnFrameworkThreadAsync(() =>
        {
            callback();
            return true;
        }, cancellationToken).GetAwaiter().GetResult();
    }

    private static Task<T> RunOnFrameworkThreadAsync<T>(Func<T> callback, CancellationToken cancellationToken = default)
    {
        if (Dalamud.Framework.IsInFrameworkUpdateThread)
            return Task.FromResult(callback());
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackState = 0;
        CancellationTokenRegistration cancellationRegistration = default;
        void Run(IFramework _)
        {
            if (Interlocked.Exchange(ref callbackState, 1) != 0)
                return;
            try
            {
                completion.TrySetResult(callback());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                Dalamud.Framework.Update -= Run;
            }
        }

        Dalamud.Framework.Update += Run;
        if (cancellationToken.CanBeCanceled)
        {
            cancellationRegistration = cancellationToken.Register(() =>
            {
                if (Interlocked.Exchange(ref callbackState, 1) == 0)
                    Dalamud.Framework.Update -= Run;
                completion.TrySetCanceled(cancellationToken);
            });
        }
        return AwaitFrameworkCallbackAsync();

        async Task<T> AwaitFrameworkCallbackAsync()
        {
            try
            {
                var completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                if (completed != completion.Task)
                {
                    if (Interlocked.Exchange(ref callbackState, 1) == 0)
                        Dalamud.Framework.Update -= Run;
                    throw new TimeoutException("Dalamud framework callback did not run before the acquisition timeout.");
                }

                return await completion.Task;
            }
            finally
            {
                cancellationRegistration.Dispose();
            }
        }
    }

    private enum MarketBoardNavigationStep
    {
        Continue,
        Open,
        Unavailable,
    }

    private readonly record struct NativeMarketRequestEvidence(
        int Generation,
        uint ItemId,
        int InitialListingCount,
        DateTime StartedAtUtc,
        byte PreviousCurrentRequestId,
        byte RequestId);

    private readonly record struct NativeMarketProxyState(
        uint SearchItemId,
        bool WaitingForListings,
        int ListingCount,
        byte CurrentRequestId,
        byte NextRequestId);

    private readonly record struct NativePurchaseSubmission(
        int InventoryBefore,
        long GilBefore);

    private readonly record struct NativePurchaseState(
        int InventoryCount,
        long GilBalance);
}
