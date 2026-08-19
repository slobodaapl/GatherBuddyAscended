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
using FFXIVClientStructs.FFXIV.Component.GUI;
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
    private static readonly TimeSpan WorldTravelRetryCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MarketBoardNavigationPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MarketBoardInteractionRetryCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MarketRequestStartRetryInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<AcquisitionTransaction, TimeSpan, CancellationToken, Task<LiveVendorPurchaseResult>>? _vendorPurchase;
    private readonly Func<VendorCurrencyGroup, uint, string, VendorCurrencyAvailability> _currencyAvailability;
    private int _marketRequestGeneration;
    private byte? _activeMarketRequestId;
    private uint _marketBoardGatewayId;
    private bool _marketBoardAethernetRequested;
    private bool _marketBoardDetectedLogged;
    private bool _marketBoardInteractionRequested;
    private DateTime _marketBoardInteractionRequestedUtc;
    private bool _marketBoardPathRequested;
    private uint _marketBoardTeleportTerritoryId;
    private bool _marketBoardTeleportRequested;
    private bool _marketBoardShortcutRequested;

    public NativeLiveAcquisitionEnvironment(
        Func<AcquisitionTransaction, TimeSpan, CancellationToken, Task<LiveVendorPurchaseResult>>? vendorPurchase = null,
        Func<VendorCurrencyGroup, uint, string, VendorCurrencyAvailability>? currencyAvailability = null)
    {
        _vendorPurchase = vendorPurchase;
        _currencyAvailability = currencyAvailability ?? ResolveCurrencyAvailability;
        MarketBoardGameDataCatalog.WarmInBackground();
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

    public bool IsAtMarketBoard
        => RunOnFrameworkThread(IsAtMarketBoardNative);

    public bool IsInDuty
        => RunOnFrameworkThread(Functions.BoundByDuty);

    public bool IsInNonCrossWorldParty
        => RunOnFrameworkThread(IsInNonCrossWorldPartyNative);

    public uint CurrentGatewayId
        => RunOnFrameworkThread(GetCurrentGatewayIdNative);

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
        if (!IsLifestreamAvailable)
        {
            GatherBuddy.Log.Warning($"[Acquisition] Cannot travel to {route.WorldName}: Lifestream is unavailable.");
            return false;
        }
        if (!CanVisitWorld(route.WorldId))
        {
            GatherBuddy.Log.Warning($"[Acquisition] Cannot travel to {route.WorldName}: Lifestream reports that the world is unreachable.");
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;
        var nextAttemptUtc = DateTime.MinValue;
        var attemptCount = 0;
        string? lastWaitReason = null;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = DateTime.UtcNow;
                var snapshot = RunOnFrameworkThread(() =>
                {
                    var lifestreamReady = Lifestream.Enabled
                        && Lifestream.IsBusy != null;
                    var isBusy = lifestreamReady && Lifestream.IsBusy!();
                    var currentWorldId = GetCurrentWorldIdNative();
                    var betweenAreas = Functions.BetweenAreas();
                    var screenReady = GenericHelpers.IsScreenReady();
                    var canAttempt = currentWorldId != route.WorldId
                        && lifestreamReady
                        && !isBusy
                        && !betweenAreas
                        && screenReady
                        && now >= nextAttemptUtc;
                    if (canAttempt)
                    {
                        if (!Lifestream.TryTpAndChangeWorld(
                                route.WorldName,
                                false,
                                string.Empty,
                                false,
                                route.GatewayId == 0 ? null : (int)route.GatewayId,
                                true,
                                true,
                                out var error))
                        {
                            throw new InvalidOperationException(
                                $"Lifestream TPAndChangeWorld IPC failed: {error}");
                        }
                    }

                    return new WorldTravelSnapshot(
                        currentWorldId,
                        isBusy,
                        canAttempt,
                        lifestreamReady,
                        betweenAreas,
                        screenReady);
                }, cancellationToken);

                if (snapshot.CurrentWorldId == route.WorldId && !snapshot.IsBusy)
                    return true;

                if (snapshot.Attempted)
                {
                    attemptCount++;
                    nextAttemptUtc = DateTime.UtcNow + WorldTravelRetryCooldown;
                    if (attemptCount == 1)
                    {
                        GatherBuddy.Log.Information(
                            $"[Acquisition] Requested Lifestream travel to {route.WorldName} via {route.GatewayName}; current world {snapshot.CurrentWorldId}.");
                    }
                    else
                    {
                        GatherBuddy.Log.Debug(
                            $"[Acquisition] Retrying Lifestream travel to {route.WorldName} via {route.GatewayName} (attempt {attemptCount}, current world {snapshot.CurrentWorldId}).");
                    }
                }
                else if (attemptCount == 0)
                {
                    var waitReason = DescribeWorldTravelWait(snapshot);
                    if (!string.Equals(lastWaitReason, waitReason, StringComparison.Ordinal))
                    {
                        lastWaitReason = waitReason;
                        GatherBuddy.Log.Information(
                            $"[Acquisition] Waiting to start Lifestream travel to {route.WorldName}: {waitReason}.");
                    }
                }

                await Task.Delay(250, cancellationToken);
            }

            var completed = RunOnFrameworkThread(() => GetCurrentWorldIdNative() == route.WorldId
                && !(Lifestream.IsBusy?.Invoke() ?? false), cancellationToken);
            if (!completed)
            {
                GatherBuddy.Log.Warning(attemptCount == 0
                    ? $"[Acquisition] Lifestream travel to {route.WorldName} never started before timeout. Last blocker: {lastWaitReason ?? "unknown"}."
                    : $"[Acquisition] Lifestream travel to {route.WorldName} did not complete before timeout after {attemptCount} request(s).");
            }
            return completed;
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
    }

    private static string DescribeWorldTravelWait(WorldTravelSnapshot snapshot)
    {
        if (!snapshot.LifestreamReady)
            return "Lifestream IPC is unavailable";
        if (snapshot.IsBusy)
            return "Lifestream is busy";
        if (snapshot.BetweenAreas)
            return "the character is between areas";
        if (!snapshot.ScreenReady)
            return "the game screen is loading or fading";
        if (snapshot.CurrentWorldId == 0)
            return "the current world is unavailable";
        return "the world-travel request gate is not ready";
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

        RunOnFrameworkThread(() =>
        {
            StopNativeNavigation();
            _marketBoardGatewayId = route.GatewayId;
            _marketBoardAethernetRequested = false;
            _marketBoardDetectedLogged = false;
            _marketBoardInteractionRequested = false;
            _marketBoardInteractionRequestedUtc = DateTime.MinValue;
            _marketBoardPathRequested = false;
            _marketBoardTeleportRequested = false;
            _marketBoardTeleportTerritoryId = 0;
            _marketBoardShortcutRequested = false;
        }, cancellationToken);

        var deadline = DateTime.UtcNow + timeout;
        var aethernetBusyObserved = false;
        var aethernetCompleted = false;
        var resolvedTerritoryId = 0u;
        MarketBoardTerritoryData? territoryData = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RunOnFrameworkThread(() => GetCurrentWorldIdNative() != route.WorldId, cancellationToken))
                return false;
            if (RunOnFrameworkThread(() => Lifestream.IsBusy?.Invoke() ?? false, cancellationToken))
            {
                aethernetBusyObserved |= _marketBoardAethernetRequested;
                await Task.Delay(MarketBoardNavigationPollInterval, cancellationToken);
                continue;
            }
            if (aethernetBusyObserved && !aethernetCompleted)
            {
                aethernetCompleted = true;
                deadline = DateTime.UtcNow + timeout;
                var completedTerritoryId = RunOnFrameworkThread(() => Dalamud.ClientState.TerritoryType, cancellationToken);
                GatherBuddy.Log.Information(
                    $"[Acquisition] Market-board aethernet completed in territory {completedTerritoryId}; starting the {timeout.TotalSeconds:N0}-second interaction window.");
            }
            if (!RunOnFrameworkThread(
                    () => GenericHelpers.IsScreenReady() && Dalamud.Objects.LocalPlayer != null,
                    cancellationToken))
            {
                await Task.Delay(MarketBoardNavigationPollInterval, cancellationToken);
                continue;
            }

            var territoryId = RunOnFrameworkThread(() => Dalamud.ClientState.TerritoryType, cancellationToken);
            if (territoryId == 0)
            {
                await Task.Delay(MarketBoardNavigationPollInterval, cancellationToken);
                continue;
            }
            if (territoryId != resolvedTerritoryId)
            {
                territoryData = await Task.Run(
                    () => MarketBoardGameDataCatalog.ResolveTerritory(territoryId),
                    cancellationToken);
                resolvedTerritoryId = territoryId;
                _marketBoardDetectedLogged = false;
                _marketBoardPathRequested = false;
                if (territoryData.Positions.Count > 0)
                {
                    GatherBuddy.Log.Information(
                        $"[Acquisition] Resolved {territoryData.Positions.Count} market-board placement(s) "
                        + $"and {territoryData.DefinitionIds.Count} definition(s) from game data for territory {territoryId}.");
                }
                else
                {
                    GatherBuddy.Log.Warning(
                        $"[Acquisition] No game-data market-board placement is available in territory {territoryId}; "
                        + $"using gateway fallback. {territoryData.UnavailableReason}");
                }
            }

            var step = RunOnFrameworkThread(
                () => TryNavigateToMarketBoardNative(route, territoryData!),
                cancellationToken);
            if (step == MarketBoardNavigationStep.Open)
                return true;
            if (step == MarketBoardNavigationStep.Unavailable)
                return false;
            await Task.Delay(MarketBoardNavigationPollInterval, cancellationToken);
        }
        RunOnFrameworkThread(() => LogMarketBoardNavigationTimeout(territoryData), cancellationToken);
        return false;
    }

    public async Task<LiveMarketListingsResponse> RequestLiveListingsAsync(
        uint itemId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (itemId == 0)
            return LiveMarketListingsResponse.Failure("Cannot request market-board listings for world item 0.");

        var itemName = RunOnFrameworkThread(() => ResolveMarketItemName(itemId), cancellationToken);
        if (string.IsNullOrWhiteSpace(itemName))
            return LiveMarketListingsResponse.Failure($"Could not resolve market-board item {itemId:N0} in the current client language.");

        var generation = checked(++_marketRequestGeneration);
        var deadline = DateTime.UtcNow + timeout;
        var startState = new NativeMarketRequestStartState();
        NativeMarketRequestEvidence? requestEvidence = null;
        var lastStartFailure = "the market-board search interface is not ready";
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestStart = RunOnFrameworkThread(() =>
            {
                var started = TryAdvanceMarketRequest(
                    itemId,
                    itemName,
                    generation,
                    startState,
                    out var evidence,
                    out var failure);
                return (Started: started, Evidence: evidence, Failure: failure);
            }, cancellationToken);
            if (requestStart.Started)
            {
                requestEvidence = requestStart.Evidence;
                break;
            }

            lastStartFailure = requestStart.Failure;
            await Task.Delay(MarketRequestStartRetryInterval, cancellationToken);
        }

        if (requestEvidence is not { } evidence)
        {
            return LiveMarketListingsResponse.Failure(
                $"Could not start a fresh market-board listing request for item {itemId:N0} before timeout: {lastStartFailure}.");
        }
        _activeMarketRequestId = evidence.RequestId;

        var sawWaiting = false;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_marketRequestGeneration != generation
                || evidence.Generation != generation
                || evidence.ItemId != itemId)
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
                && state.CurrentRequestId != evidence.PreviousCurrentRequestId
                && state.CurrentRequestId != evidence.RequestId)
                return LiveMarketListingsResponse.Failure($"Market-board listing request generation {generation} did not produce a correlated InfoProxy result; stale proxy data was discarded.");

            sawWaiting |= state.WaitingForListings;
            var correlatedCompletion = state.CurrentRequestId == evidence.RequestId
                && state.CurrentRequestId != evidence.PreviousCurrentRequestId;
            var sawFreshResult = DateTime.UtcNow > evidence.StartedAtUtc
                && (sawWaiting || correlatedCompletion || state.ListingCount > evidence.InitialListingCount);
            if (!state.WaitingForListings && sawFreshResult)
            {
                if (_marketRequestGeneration != generation)
                    return LiveMarketListingsResponse.Failure($"Market-board listing request generation {generation} was superseded before result capture; stale proxy data was discarded.");
                var listings = RunOnFrameworkThread(() =>
                    ReadCurrentListings(
                        itemId,
                        evidence.RequestId,
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
        TimeSpan navigationTimeout,
        CancellationToken cancellationToken)
        => _vendorPurchase?.Invoke(transaction, navigationTimeout, cancellationToken)
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
                if (Lifestream.Enabled && !Lifestream.TryAbort(out var abortError))
                    GatherBuddy.Log.Debug($"[Acquisition] Lifestream cleanup warning: {abortError}");
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

    private static unsafe bool TryAdvanceMarketRequest(
        uint itemId,
        string itemName,
        int generation,
        NativeMarketRequestStartState startState,
        out NativeMarketRequestEvidence evidence,
        out string failure)
    {
        evidence = default;
        var agent = AgentItemSearch.Instance();
        var proxy = GetItemSearchProxy();
        if (agent == null || proxy == null)
        {
            failure = "AgentItemSearch or InfoProxyItemSearch is unavailable";
            return false;
        }

        if (startState.ResultSelected)
        {
            if (proxy->SearchItemId != itemId)
            {
                failure = $"the UI selected item {proxy->SearchItemId:N0} instead of {itemId:N0}";
                return false;
            }
            if (proxy->NextRequestId == startState.PreviousNextRequestId)
            {
                failure = "waiting for the UI selection to assign a fresh request id";
                return false;
            }

            evidence = new NativeMarketRequestEvidence(
                generation,
                itemId,
                startState.InitialListingCount,
                startState.SelectedAtUtc,
                startState.PreviousCurrentRequestId,
                proxy->NextRequestId);
            failure = string.Empty;
            return true;
        }

        if (!GenericHelpers.TryGetAddonByName<AddonItemSearch>("ItemSearch", out var addon)
            || addon == null
            || !addon->AtkUnitBase.IsVisible)
        {
            failure = "the ItemSearch addon is not visible";
            return false;
        }

        if (!string.IsNullOrEmpty(startState.SearchSubmissionFailure))
        {
            failure = startState.SearchSubmissionFailure;
            return false;
        }

        if (addon->SearchTextInput == null
            || addon->SearchButton == null
            || addon->ResultsList == null)
        {
            failure = "the ItemSearch text input, search button, or results list is unavailable";
            return false;
        }

        if (!startState.SearchIssued)
        {
            addon->SearchTextInput->SetText(itemName);
            var enteredText = addon->SearchTextInput->RawString.ToString();
            if (!string.Equals(enteredText, itemName, StringComparison.Ordinal))
            {
                failure = $"the ItemSearch text input contains '{enteredText}' instead of '{itemName}'";
                return false;
            }

            // RunSearch(true) emits callback opcode 1 without the search text,
            // mode, or filter; in the live ItemSearch addon that opens the
            // Wishlist. False emits the normal-search opcode 0 and complete
            // search arguments.
            addon->RunSearch(false);
            startState.SearchIssued = true;
            var submittedText = addon->SearchText.ToString();
            if (string.Equals(submittedText, itemName, StringComparison.Ordinal))
            {
                GatherBuddy.Log.Information(
                    $"[Acquisition] Submitted market-board text search for {itemName} ({itemId:N0}); waiting for the exact game result.");
            }
            else
            {
                startState.SearchSubmissionFailure = $"the ItemSearch callback did not retain search text '{itemName}' "
                    + $"(mode={addon->Mode}, filter={addon->SelectedFilter}, text='{submittedText}')";
                failure = startState.SearchSubmissionFailure;
                return false;
            }
            failure = "waiting for the ItemSearch results";
            return false;
        }

        if (addon->ResultsList == null)
        {
            failure = "the ItemSearch result list is unavailable";
            return false;
        }

        var resultCount = System.Math.Max(0, addon->ResultsList->GetItemCount());
        var resultIndex = -1;
        for (var index = 0; index < resultCount; index++)
        {
            var resultName = addon->ResultsList->GetItemLabel(index).ToString();
            if (string.Equals(resultName, itemName, StringComparison.Ordinal))
            {
                resultIndex = index;
                break;
            }
        }
        if (resultIndex < 0 && resultCount == 1)
            resultIndex = 0;
        if (resultIndex < 0)
        {
            failure = resultCount == 0
                ? "waiting for the ItemSearch results"
                : $"the ItemSearch results do not contain exact name '{itemName}'";
            return false;
        }

        startState.InitialListingCount = System.Math.Max(0, (int)proxy->ListingCount);
        startState.PreviousCurrentRequestId = proxy->CurrentRequestId;
        startState.PreviousNextRequestId = proxy->NextRequestId;
        startState.SelectedAtUtc = DateTime.UtcNow;
        addon->ResultsList->DispatchItemEvent(resultIndex, AtkEventType.ListItemClick);
        startState.ResultSelected = true;
        GatherBuddy.Log.Information(
            $"[Acquisition] Activated market-board result candidate for {itemName} ({itemId:N0}) at index {resultIndex}; "
            + "waiting for the game to confirm the exact item and start its listing request.");
        failure = "waiting for the activated UI result to start the exact-item listing request";
        return false;
    }

    private static string ResolveMarketItemName(uint itemId)
    {
        var items = Dalamud.GameData.GetExcelSheet<Item>();
        return items?.TryGetRow(itemId, out var item) == true
            ? item.Name.ExtractText().Trim()
            : string.Empty;
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
        var currentWorldId = GetCurrentWorldIdNative();
        if (currentWorldId == 0)
        {
            stale = true;
            failure = "The current world is unknown; market-board purchase was rejected safely.";
            return false;
        }
        if (requested.WorldId == 0 || currentWorldId != requested.WorldId)
        {
            stale = true;
            failure = $"The current world {currentWorldId} does not match listing world {requested.WorldId}; market-board purchase was rejected safely.";
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
            GatherBuddy.Log.Information(
                $"[Acquisition] Submitting exact market listing {requested.ListingId:N0}: "
                + $"{requested.Quantity:N0}x item {requested.ItemId:N0} at {requested.PricePerUnit:N0} each "
                + $"+ {requested.TotalTax:N0} tax = {requested.TotalGil:N0} Gil on world {requested.WorldId}.");
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

    private unsafe MarketBoardNavigationStep TryNavigateToMarketBoardNative(
        AcquisitionWorldRoute route,
        MarketBoardTerritoryData territoryData)
    {
        if (route.WorldId == 0
            || string.IsNullOrWhiteSpace(route.WorldName)
            || (route.IsWorldHop
                && (route.GatewayId == 0 || string.IsNullOrWhiteSpace(route.GatewayName))))
            return MarketBoardNavigationStep.Unavailable;

        if (IsMarketBoardAddonVisibleNative())
            return GetItemSearchProxy() != null
                ? MarketBoardNavigationStep.Open
                : MarketBoardNavigationStep.Continue;

        var player = Dalamud.Objects.LocalPlayer;
        var board = player == null
            ? Dalamud.Objects.FirstOrDefault(obj => territoryData.IsMarketBoardDefinition(obj.BaseId))
            : Dalamud.Objects
                .Where(obj => territoryData.IsMarketBoardDefinition(obj.BaseId))
                .MinBy(obj => Vector3.DistanceSquared(player.Position, obj.Position));
        if (board != null)
        {
            var distance = player == null
                ? 0f
                : MathF.Sqrt(Vector3.DistanceSquared(player.Position, board.Position));
            if (!_marketBoardDetectedLogged)
            {
                _marketBoardDetectedLogged = true;
                GatherBuddy.Log.Information(
                    $"[Acquisition] Detected market board {board.BaseId} at {distance:N1} yalms; targetable={board.IsTargetable}.");
            }
            if (player != null && Vector3.DistanceSquared(player.Position, board.Position) > 16f)
            {
                if (!IsVNavmeshAvailableNative())
                    return MarketBoardNavigationStep.Unavailable;
                if (VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress())
                    return MarketBoardNavigationStep.Continue;
                VNavmesh.SimpleMove.PathfindAndMoveCloseTo?.Invoke(board.Position, false, 3f);
                if (!_marketBoardPathRequested)
                {
                    _marketBoardPathRequested = true;
                    GatherBuddy.Log.Information("[Acquisition] Requested vnavmesh path to the detected market board.");
                }
                return MarketBoardNavigationStep.Continue;
            }
            else
            {
                var now = DateTime.UtcNow;
                if (now - _marketBoardInteractionRequestedUtc >= MarketBoardInteractionRetryCooldown)
                {
                    _marketBoardInteractionRequestedUtc = now;
                    if (OpenMarketBoardObject(board.Address) && !_marketBoardInteractionRequested)
                    {
                        _marketBoardInteractionRequested = true;
                        GatherBuddy.Log.Information(
                            "[Acquisition] Requested market-board interaction; waiting for ItemSearch and InfoProxyItemSearch readiness.");
                    }
                }
                return MarketBoardNavigationStep.Continue;
            }
        }

        if (player == null)
            return MarketBoardNavigationStep.Continue;

        if (territoryData.Positions.Count > 0)
        {
            var position = territoryData.Positions.MinBy(candidate =>
                Vector3.DistanceSquared(player.Position, candidate));
            var distanceSquared = Vector3.DistanceSquared(player.Position, position);
            if (distanceSquared <= 16f)
                return MarketBoardNavigationStep.Continue;
            if (!IsVNavmeshAvailableNative())
                return MarketBoardNavigationStep.Unavailable;
            if (VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress())
                return MarketBoardNavigationStep.Continue;

            VNavmesh.SimpleMove.PathfindAndMoveCloseTo?.Invoke(position, false, 3f);
            if (!_marketBoardPathRequested)
            {
                _marketBoardPathRequested = true;
                GatherBuddy.Log.Information(
                    $"[Acquisition] Requested vnavmesh path to the nearest game-data market board "
                    + $"({MathF.Sqrt(distanceSquared):N1} yalms away).");
            }
            return MarketBoardNavigationStep.Continue;
        }

        if (Lifestream.Enabled && !(Lifestream.IsBusy?.Invoke() ?? false))
        {
            if (_marketBoardTeleportRequested)
            {
                if (_marketBoardTeleportTerritoryId == Dalamud.ClientState.TerritoryType)
                {
                    _marketBoardTeleportRequested = false;
                    _marketBoardTeleportTerritoryId = 0;
                }
                else
                {
                    return MarketBoardNavigationStep.Continue;
                }
            }

            if (_marketBoardGatewayId == 0)
            {
                var activeAetheryteId = Lifestream.ActiveAetheryteId;
                if (GetMarketBoardAethernetId(activeAetheryteId) != 0)
                {
                    _marketBoardGatewayId = activeAetheryteId;
                }
                else
                {
                    var destination = FindCheapestMarketBoardGateway();
                    if (destination.Id != 0)
                    {
                        _marketBoardGatewayId = destination.Id;
                        if (destination.TerritoryId != Dalamud.ClientState.TerritoryType)
                        {
                            if (!Teleporter.Teleport(destination.Id))
                                return MarketBoardNavigationStep.Unavailable;
                            _marketBoardTeleportRequested = true;
                            _marketBoardTeleportTerritoryId = destination.TerritoryId;
                            GatherBuddy.Log.Information(
                                $"[Acquisition] Teleporting to {destination.Name} for its market-board aethernet route ({destination.Cost:N0} Gil).");
                            return MarketBoardNavigationStep.Continue;
                        }
                    }
                }
            }

            var marketBoardAethernetId = GetMarketBoardAethernetId(_marketBoardGatewayId);
            if (marketBoardAethernetId != 0 && !_marketBoardAethernetRequested)
            {
                if (!Lifestream.TryAethernetTeleportById(marketBoardAethernetId, out var error))
                {
                    GatherBuddy.Log.Warning(
                        $"[Acquisition] Could not request market-board aethernet {marketBoardAethernetId}: {error}");
                    return MarketBoardNavigationStep.Unavailable;
                }

                _marketBoardAethernetRequested = true;
                GatherBuddy.Log.Information(
                    $"[Acquisition] Requested market-board aethernet {marketBoardAethernetId} from gateway {_marketBoardGatewayId}.");
                return MarketBoardNavigationStep.Continue;
            }

            if (marketBoardAethernetId == 0 && !_marketBoardShortcutRequested)
            {
                Lifestream.ExecuteCommand?.Invoke("mb");
                _marketBoardShortcutRequested = true;
                GatherBuddy.Log.Information(
                    "[Acquisition] No supported market-board gateway was available; requested Lifestream's market-board shortcut.");
            }
        }

        return MarketBoardNavigationStep.Continue;
    }

    private static void LogMarketBoardNavigationTimeout(MarketBoardTerritoryData? territoryData)
    {
        var player = Dalamud.Objects.LocalPlayer;
        var nearby = player == null
            ? Array.Empty<string>()
            : Dalamud.Objects
                .Where(obj => obj.BaseId != 0)
                .Select(obj => new
                {
                    obj.BaseId,
                    obj.ObjectKind,
                    obj.IsTargetable,
                    Distance = MathF.Sqrt(Vector3.DistanceSquared(player.Position, obj.Position)),
                })
                .Where(obj => obj.Distance <= 50f)
                .OrderBy(obj => obj.Distance)
                .Take(16)
                .Select(obj => $"{obj.ObjectKind}/{obj.BaseId}@{obj.Distance:N1}y/targetable={obj.IsTargetable}")
                .ToArray();
        GatherBuddy.Log.Warning(
            $"[Acquisition] Market-board navigation timed out in territory {Dalamud.ClientState.TerritoryType}; "
            + $"gameDataPlacements={territoryData?.Positions.Count ?? 0}; "
            + $"activeAetheryte={Lifestream.ActiveAetheryteId}; nearby=[{string.Join(", ", nearby)}].");
    }

    private static bool IsAtMarketBoardNative()
    {
        if (IsMarketBoardAddonVisibleNative())
            return true;

        var board = Dalamud.Objects.FirstOrDefault(obj => MarketBoardGameDataCatalog.IsKnownDefinition(obj.BaseId));
        var player = Dalamud.Objects.LocalPlayer;
        return board != null
            && player != null
            && Vector3.DistanceSquared(player.Position, board.Position) <= 16f;
    }

    private static uint GetMarketBoardAethernetId(uint gatewayId)
        => gatewayId switch
        {
            AcquisitionWorldGateways.Gridania => 26,
            AcquisitionWorldGateways.LimsaLominsa => 49,
            AcquisitionWorldGateways.Uldah => 125,
            _ => 0,
        };

    private static unsafe (uint Id, uint TerritoryId, string Name, long Cost) FindCheapestMarketBoardGateway()
    {
        var sheet = Dalamud.GameData.GetExcelSheet<Aetheryte>();
        if (sheet == null)
            return default;

        var candidates = new List<(uint Id, uint TerritoryId, string Name, long Cost)>();
        foreach (var gatewayId in AcquisitionWorldGateways.Preferred)
        {
            if (!sheet.TryGetRow(gatewayId, out var aetheryte)
                || !Teleporter.IsAttuned(gatewayId))
                continue;

            var cost = ReadTeleportCost(gatewayId);
            if (cost < 0 || cost == long.MaxValue)
                continue;
            var placeName = aetheryte.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
            candidates.Add((gatewayId, aetheryte.Territory.RowId, placeName, cost));
        }

        return candidates
            .OrderBy(candidate => candidate.Cost)
            .ThenBy(candidate => candidate.TerritoryId)
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefault();
    }

    private static bool IsVNavmeshAvailableNative()
        => VNavmesh.Enabled && (VNavmesh.Nav.IsReady?.Invoke() ?? false);

    private static unsafe bool OpenMarketBoardObject(nint address)
    {
        var target = TargetSystem.Instance();
        if (target == null)
            return false;

        target->OpenObjectInteraction((GameObjectStruct*)address);
        return true;
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

    private static long ReadTeleportCost(uint gatewayId)
        => Teleporter.TryGetTeleportCost(gatewayId, out var gilCost)
            ? gilCost
            : long.MaxValue;

    private static uint GetCurrentGatewayIdNative()
    {
        var currentTerritoryId = (uint)Dalamud.ClientState.TerritoryType;
        if (currentTerritoryId == 0)
            return 0;

        var aetherytes = Dalamud.GameData.GetExcelSheet<Aetheryte>();
        if (aetherytes == null)
            return 0;

        var currentAethernetGroups = aetherytes
            .Where(aetheryte => aetheryte.Territory.RowId == currentTerritoryId)
            .Select(aetheryte => aetheryte.AethernetGroup)
            .Where(group => group != 0)
            .ToHashSet();
        foreach (var gatewayId in AcquisitionWorldGateways.Preferred)
        {
            if (!aetherytes.TryGetRow(gatewayId, out var gateway))
                continue;
            if (gateway.Territory.RowId == currentTerritoryId
                || gateway.AethernetGroup != 0 && currentAethernetGroups.Contains(gateway.AethernetGroup))
                return gatewayId;
        }

        return 0;
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

    private sealed class NativeMarketRequestStartState
    {
        public bool SearchIssued { get; set; }
        public string SearchSubmissionFailure { get; set; } = string.Empty;
        public bool ResultSelected { get; set; }
        public int InitialListingCount { get; set; }
        public DateTime SelectedAtUtc { get; set; }
        public byte PreviousCurrentRequestId { get; set; }
        public byte PreviousNextRequestId { get; set; }
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

    private readonly record struct WorldTravelSnapshot(
        uint CurrentWorldId,
        bool IsBusy,
        bool Attempted,
        bool LifestreamReady,
        bool BetweenAreas,
        bool ScreenReady);
}
