using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Crafting.Acquisition;

/// <summary>
/// Executes a precomputed acquisition plan against authoritative in-game
/// sources. Vendor purchases run before market purchases. A missing or stale
/// market listing invalidates the complete remaining plan and asks the caller
/// for a fresh global plan; this executor never repairs a stale plan greedily.
/// </summary>
public sealed class LiveAcquisitionExecutor : IDisposable
{
    private readonly ILiveAcquisitionEnvironment _environment;
    private readonly LiveAcquisitionOptions _options;
    private readonly Func<CancellationToken, Task<AcquisitionPlanningResult?>>? _replan;
    private readonly Func<uint, CancellationToken, Task>? _invalidateMarketData;
    private readonly object _replanGate = new();
    private readonly Dictionary<uint, int> _purchasedQuantities = new();
    private readonly Dictionary<uint, int> _purchasedHqQuantities = new();
    private readonly Dictionary<uint, int> _purchasedNqQuantities = new();
    private readonly Dictionary<string, int> _purchasedByTransaction = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _gilSpentByTransaction = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, int> _requiredQuantities = new();
    private readonly Dictionary<uint, int> _requiredHqQuantities = new();
    private readonly Dictionary<uint, int> _requiredNqQuantities = new();
    private readonly Dictionary<uint, long> _currencySpent = new();
    private readonly List<LiveAcquisitionDiagnostic> _diagnostics = new();
    private CancellationTokenSource? _activeCancellation;
    private Task<LiveAcquisitionResult>? _activeExecution;
    private long _gilSpent;
    private bool _hasIndeterminatePurchases;
    private bool _requestSubmitted;
    private LiveAcquisitionResult? _currentResult;
    private bool _disposed;
    private long _replanGeneration;

    public LiveAcquisitionExecutor(
        ILiveAcquisitionEnvironment environment,
        LiveAcquisitionOptions? options = null,
        Func<CancellationToken, Task<AcquisitionPlanningResult?>>? replan = null,
        Func<uint, CancellationToken, Task>? invalidateMarketData = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _options = options ?? new LiveAcquisitionOptions();
        _replan = replan;
        _invalidateMarketData = invalidateMarketData;
    }

    public bool IsRunning
        => _activeExecution != null;

    public LiveAcquisitionStage Stage { get; private set; } = LiveAcquisitionStage.Idle;

    public event Action<LiveAcquisitionDiagnostic>? Diagnostic;

    public Task<LiveAcquisitionResult> ExecuteAsync(
        AcquisitionResult result,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(result?.Planning ?? throw new ArgumentNullException(nameof(result)), cancellationToken);

    public Task<LiveAcquisitionResult> ExecuteAsync(
        AcquisitionPlanningResult planning,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planning);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_activeExecution != null)
            return Task.FromResult(Failure(LiveAcquisitionFailureKind.Unexpected, "An acquisition run is already active.", Stage));

        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeCancellation = linkedCancellation;
        var execution = ExecuteCoreAsync(planning, linkedCancellation.Token);
        _activeExecution = execution;
        _ = ObserveExecutionAsync(execution, linkedCancellation);
        return execution;
    }

    private async Task<LiveAcquisitionResult> ExecuteCoreAsync(
        AcquisitionPlanningResult planning,
        CancellationToken cancellationToken)
    {
        var startWorldId = 0u;
        var startWorldName = string.Empty;
        try
        {
            ResetRunState();
            startWorldId = _environment.CurrentWorldId;
            startWorldName = _environment.CurrentWorldName;
            var initial = ValidatePlanningResult(planning);
            if (initial != null)
                return ReturnResult(initial);

            var currentPlanning = planning;
            TrackRequirements(currentPlanning.SelectedPlan!);
            var replanCount = 0;
            var vendorRecovery = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selectedPlan = currentPlanning.SelectedPlan ?? new AcquisitionPlan();
                var routePlan = BuildRoutePlan(selectedPlan);
                if (!routePlan.IsReady)
                    return ReturnResult(Failure(LiveAcquisitionFailureKind.TravelBlocked, routePlan.FailureReason, LiveAcquisitionStage.Preconditions));

                var pass = await ExecutePassAsync(selectedPlan, routePlan, vendorRecovery, cancellationToken);
                if (pass.Kind != PassResultKind.Replan)
                {
                    if (pass.Result != null)
                        return ReturnResult(pass.Result);
                    return ReturnResult(await CompleteAsync(startWorldId, startWorldName, cancellationToken));
                }

                if (_replan == null || replanCount++ >= System.Math.Max(0, _options.MaximumReplans))
                {
                    return ReturnResult(Failure(
                        LiveAcquisitionFailureKind.ListingUnavailable,
                        pass.Message,
                        LiveAcquisitionStage.Market,
                        partial: _purchasedQuantities.Count > 0 || _hasIndeterminatePurchases));
                }

                AddDiagnostic(LiveAcquisitionStage.Market, pass.Message, pass.ItemId, pass.ItemName, pass.WorldName, pass.EstimatedGil, pass.LiveGil, pass.ListingId);
                // The failed listing is no longer authoritative. Invalidate it
                // before the framework-side planner reads the market cache;
                // the callback owns the exact current-world/DC scope.
                if (_invalidateMarketData != null)
                    await _invalidateMarketData(pass.ItemId, cancellationToken).ConfigureAwait(false);
                var replanClaim = ClaimReplan(cancellationToken);
                if (replanClaim == 0)
                    throw new OperationCanceledException("Acquisition replan could not claim an active execution generation.");
                var cancellationCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var replanCancellation = cancellationToken.Register(() =>
                {
                    InvalidateReplan(replanClaim);
                    cancellationCompletion.TrySetResult(true);
                });
                var replanTask = _replan(cancellationToken);
                _ = replanTask.ContinueWith(
                    completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                var completedReplan = await Task.WhenAny(
                    replanTask,
                    Task.Delay(_options.ReplanTimeout),
                    cancellationCompletion.Task);
                if (completedReplan != replanTask)
                {
                    InvalidateReplan(replanClaim);
                    if (cancellationToken.IsCancellationRequested || cancellationCompletion.Task.IsCompleted)
                        throw new OperationCanceledException(cancellationToken);
                    throw new TimeoutException("Global acquisition replan did not finish before the acquisition timeout.");
                }

                var refreshedPlanning = await replanTask;
                lock (_replanGate)
                {
                    if (replanClaim == 0
                        || _replanGeneration != replanClaim
                        || cancellationToken.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new OperationCanceledException("A completed acquisition replan lost its execution claim.");
                    }

                    currentPlanning = refreshedPlanning
                        ?? throw new InvalidOperationException("Global acquisition replan returned no result.");
                }
                var replanValidation = ValidatePlanningResult(currentPlanning);
                if (replanValidation != null)
                    return ReturnResult(replanValidation);
                TrackRequirements(currentPlanning.SelectedPlan!);
                AddDiagnostic(LiveAcquisitionStage.Market, $"Global acquisition plan refreshed after stale market data (attempt {replanCount}).");
                vendorRecovery = true;
            }
        }
        catch (OperationCanceledException)
        {
            Stage = LiveAcquisitionStage.Cancelled;
            var cancellationMessage = _requestSubmitted
                ? "Acquisition cancelled after a purchase request was submitted; the final inventory state is indeterminate."
                : "Acquisition cancelled. Any completed purchases are irreversible.";
            return ReturnResult(Failure(
                LiveAcquisitionFailureKind.Cancelled,
                cancellationMessage,
                Stage,
                partial: _purchasedQuantities.Count > 0 || _hasIndeterminatePurchases));
        }
        catch (Exception ex)
        {
            if (_requestSubmitted)
            {
                _hasIndeterminatePurchases = true;
                AddDiagnostic(Stage, "A purchase request may have been submitted before the adapter failed; final inventory and Gil state is indeterminate.");
            }
            AddDiagnostic(Stage, $"Live acquisition failed unexpectedly: {ex.Message}");
            return ReturnResult(Failure(
                LiveAcquisitionFailureKind.Unexpected,
                ex.Message,
                Stage,
                partial: _purchasedQuantities.Count > 0 || _hasIndeterminatePurchases));
        }
        finally
        {
            try
            {
                await _environment.CleanupAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                AddDiagnostic(Stage, $"Acquisition cleanup failed: {ex.Message}");
            }
            await ReturnToStartWorldAfterFailureAsync(startWorldId, startWorldName);
            if (_currentResult != null)
                _currentResult.Diagnostics = _diagnostics.ToArray();
            _activeCancellation = null;
        }
    }

    public void Cancel()
        => _activeCancellation?.Cancel();

    private long ClaimReplan(CancellationToken cancellationToken)
    {
        lock (_replanGate)
        {
            if (cancellationToken.IsCancellationRequested)
                return 0;
            return ++_replanGeneration;
        }
    }

    private void InvalidateReplan(long claim)
    {
        if (claim == 0)
            return;
        lock (_replanGate)
        {
            if (_replanGeneration == claim)
                _replanGeneration++;
        }
    }

    /// <summary>
    /// Requests cancellation and waits until the active acquisition has drained
    /// its environment cleanup. Plugin shutdown must await this before
    /// disposing the vendor adapter or native environment.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _activeCancellation?.Cancel();
        var execution = _activeExecution;
        if (execution == null)
            return;
        await execution.WaitAsync(cancellationToken);
    }

    private async Task ObserveExecutionAsync(
        Task<LiveAcquisitionResult> execution,
        CancellationTokenSource linkedCancellation)
    {
        try
        {
            await execution;
        }
        finally
        {
            if (ReferenceEquals(_activeExecution, execution))
                _activeExecution = null;
            linkedCancellation.Dispose();
        }
    }

    private async Task<PassResult> ExecutePassAsync(
        AcquisitionPlan plan,
        AcquisitionRoutePlan routePlan,
        bool vendorRecovery,
        CancellationToken cancellationToken)
    {
        Stage = LiveAcquisitionStage.Preconditions;
        var precondition = _environment.ValidatePlan(plan, _options);
        if (!precondition.IsReady)
        {
            return PassResult.Finished(Failure(
                precondition.FailureKind == LiveAcquisitionFailureKind.None
                    ? LiveAcquisitionFailureKind.InvalidPlan
                    : precondition.FailureKind,
                precondition.Message,
                Stage,
                partial: _purchasedQuantities.Count > 0 || _hasIndeterminatePurchases));
        }

        // Vendor transactions execute before market transactions regardless of
        // their order in the planner. Keep one allocation ledger for this pass
        // so prior same-item purchases cannot be counted twice after a replan.
        var allocatedItemQuantities = new Dictionary<uint, int>();
        var allocatedHqQuantities = new Dictionary<uint, int>();
        var allocatedNqQuantities = new Dictionary<uint, int>();
        var reservedHqQuantities = plan.Transactions
            .Where(transaction => transaction.IsHq)
            .GroupBy(transaction => transaction.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(transaction => System.Math.Max(0, transaction.Quantity)));
        var reservedNqQuantities = plan.Transactions
            .Where(transaction => !transaction.IsHq)
            .GroupBy(transaction => transaction.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(transaction => System.Math.Max(0, transaction.Quantity)));

        var vendors = plan.Transactions
            .Select((transaction, index) => (transaction, index))
            .Where(entry => entry.transaction.SourceKind == AcquisitionSourceKind.Vendor)
            .ToArray();
        if (vendors.Length > 0)
        {
            Stage = vendorRecovery ? LiveAcquisitionStage.VendorRecovery : LiveAcquisitionStage.Vendor;
            foreach (var (transaction, transactionIndex) in vendors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = Remaining(plan, transaction, transactionIndex, allocatedItemQuantities, allocatedHqQuantities, allocatedNqQuantities, reservedHqQuantities, reservedNqQuantities);
                if (remaining <= 0)
                    continue;

                _requestSubmitted = true;
                LiveVendorPurchaseResult purchase;
                try
                {
                    purchase = await _environment.PurchaseVendorAsync(
                        WithQuantity(transaction, remaining),
                        _options.TravelTimeout,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _hasIndeterminatePurchases = true;
                    AddDiagnostic(Stage, $"Vendor purchase request for {transaction.ItemName} may have been submitted; final state is indeterminate.", transaction.ItemId, transaction.ItemName, transaction.WorldName, transaction.GilCost);
                    throw;
                }
                _requestSubmitted = false;
                if (purchase.RequestSubmitted && !purchase.Verified)
                    _hasIndeterminatePurchases = true;

                var vendorFailure = ValidateVendorPurchase(transaction, transactionIndex, remaining, purchase);
                if (vendorFailure != null)
                {
                    RecordKnownVendorPurchase(transaction, transactionIndex, purchase);
                    return PassResult.Finished(Failure(
                        purchase.Accepted ? LiveAcquisitionFailureKind.VerificationFailed : LiveAcquisitionFailureKind.PurchaseRejected,
                        vendorFailure,
                        Stage,
                        transaction.ItemId,
                        transaction.ItemName,
                        transaction.WorldName,
                        transaction.GilCost,
                        null,
                        null,
                        _purchasedQuantities.Count > 0 || _hasIndeterminatePurchases));
                }

                var vendorGil = VendorGilSpent(purchase);
                RecordPurchase(transaction, transactionIndex, purchase.QuantityPurchased, vendorGil,
                    purchase.OutputQuantities, purchase.CurrencySpent, purchase.IsHq);
                AddDiagnostic(Stage, purchase.Message, transaction.ItemId, transaction.ItemName, transaction.WorldName, transaction.GilCost, purchase.GilSpent);
            }
        }

        foreach (var route in routePlan.Routes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (route.IsWorldHop)
            {
                AddDiagnostic(Stage, $"Traveling to {route.WorldName} through {route.GatewayName}.", worldName: route.WorldName);
                if (!await _environment.TravelToWorldAsync(route, _options.TravelTimeout, cancellationToken))
                {
                    return PassResult.Finished(Failure(
                        LiveAcquisitionFailureKind.TravelBlocked,
                        $"Could not reach marketplace world {route.WorldName}.",
                        Stage,
                        worldName: route.WorldName,
                        partial: _purchasedQuantities.Count > 0 || _hasIndeterminatePurchases));
                }
            }

            Stage = LiveAcquisitionStage.Market;
            if (!await _environment.NavigateToMarketBoardAsync(route, _options.MarketBoardTimeout, cancellationToken))
            {
                return PassResult.Finished(Failure(
                    LiveAcquisitionFailureKind.MarketUnavailable,
                    $"Could not open the market board in {route.WorldName}.",
                    Stage,
                    worldName: route.WorldName,
                    partial: _purchasedQuantities.Count > 0 || _hasIndeterminatePurchases));
            }

            var transactions = plan.Transactions
                .Select((transaction, index) => (transaction, index))
                .Where(entry => entry.transaction.SourceKind == AcquisitionSourceKind.Market
                    && entry.transaction.WorldId == route.WorldId)
                .ToArray();
            foreach (var (transaction, transactionIndex) in transactions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = Remaining(plan, transaction, transactionIndex, allocatedItemQuantities, allocatedHqQuantities, allocatedNqQuantities, reservedHqQuantities, reservedNqQuantities);
                if (remaining <= 0)
                    continue;

                var listingsResponse = await _environment.RequestLiveListingsAsync(
                    transaction.ItemId,
                    _options.MarketBoardTimeout,
                    cancellationToken);
                if (!listingsResponse.IsFresh)
                {
                    await _environment.CloseMarketBoardAsync(cancellationToken);
                    return PassResult.ReplanRequested(
                        listingsResponse.FailureReason,
                        transaction.ItemId,
                        transaction.ItemName,
                        route.WorldName,
                        transaction.GilCost,
                        null,
                        long.TryParse(transaction.SourceId, out var failedListingId) ? failedListingId : null);
                }

                var listings = listingsResponse.Listings;
                var candidates = SelectListings(transaction, remaining, listings);
                var otherReservation = RemainingOtherPlanGilReservation(plan, transactionIndex);
                var listing = candidates.FirstOrDefault(candidate =>
                    CanPurchaseMarketListing(
                        transaction,
                        remaining,
                        candidate,
                        otherReservation));
                if (listing == null)
                {
                    await _environment.CloseMarketBoardAsync(cancellationToken);
                    var plannedListingId = long.TryParse(transaction.SourceId, out var parsedListingId)
                        ? parsedListingId
                        : (long?)null;
                    return PassResult.ReplanRequested(
                        candidates.Count == 0
                            ? $"Live market listing for {transaction.ItemName} no longer satisfies the plan."
                            : $"Live market price for {transaction.ItemName} exceeds its planned unit-price, transaction reservation, or global Gil reservation.",
                        transaction.ItemId,
                        transaction.ItemName,
                        route.WorldName,
                        transaction.GilCost,
                        candidates.MinBy(candidate => candidate.TotalGil)?.TotalGil,
                        plannedListingId);
                }

                _requestSubmitted = true;
                LiveMarketPurchaseResult purchase;
                try
                {
                    purchase = await _environment.PurchaseMarketListingAsync(
                        listing,
                        _options.PurchaseTimeout,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _hasIndeterminatePurchases = true;
                    AddDiagnostic(Stage, $"Market purchase request for {transaction.ItemName} may have been submitted; final state is indeterminate.", transaction.ItemId, transaction.ItemName, route.WorldName, transaction.GilCost, listing.TotalGil, listing.ListingId);
                    throw;
                }
                _requestSubmitted = false;
                if (purchase.RequestSubmitted && !purchase.Verified)
                    _hasIndeterminatePurchases = true;

                if (purchase.IsStale)
                {
                    await _environment.CloseMarketBoardAsync(cancellationToken);
                    return PassResult.ReplanRequested(
                        purchase.Message,
                        transaction.ItemId,
                        transaction.ItemName,
                        route.WorldName,
                        transaction.GilCost,
                        listing.TotalGil,
                        listing.ListingId);
                }

                if (purchase.Accepted
                    && purchase.Verified
                    && purchase.QuantityPurchased < listing.Quantity)
                {
                    var underfillFailure = ValidateMarketPurchase(
                        transaction,
                        transactionIndex,
                        remaining,
                        listing,
                        otherReservation,
                        purchase,
                        allowUnderfill: true);
                    if (underfillFailure != null)
                    {
                        await _environment.CloseMarketBoardAsync(cancellationToken);
                        return PassResult.Finished(Failure(
                            LiveAcquisitionFailureKind.VerificationFailed,
                            underfillFailure,
                            Stage,
                            transaction.ItemId,
                            transaction.ItemName,
                            route.WorldName,
                            transaction.GilCost,
                            purchase.GilSpent,
                            listing.ListingId,
                            _purchasedQuantities.Count > 0 || _hasIndeterminatePurchases));
                    }

                    RecordKnownMarketPurchase(transaction, transactionIndex, listing, purchase);
                    await _environment.CloseMarketBoardAsync(cancellationToken);
                    return PassResult.ReplanRequested(
                        $"Live market purchase for {transaction.ItemName} was accepted but underfilled ({purchase.QuantityPurchased:N0}/{listing.Quantity:N0}); refreshing the global acquisition plan.",
                        transaction.ItemId,
                        transaction.ItemName,
                        route.WorldName,
                        transaction.GilCost,
                        purchase.GilSpent,
                        listing.ListingId);
                }

                var marketFailure = ValidateMarketPurchase(
                    transaction,
                    transactionIndex,
                    remaining,
                    listing,
                    otherReservation,
                    purchase);
                if (marketFailure != null)
                {
                    RecordKnownMarketPurchase(transaction, transactionIndex, listing, purchase);
                    return PassResult.Finished(Failure(
                        purchase.Accepted ? LiveAcquisitionFailureKind.VerificationFailed : LiveAcquisitionFailureKind.PurchaseRejected,
                        marketFailure,
                        Stage,
                        transaction.ItemId,
                        transaction.ItemName,
                        route.WorldName,
                        transaction.GilCost,
                        purchase.GilSpent,
                        listing.ListingId,
                        _purchasedQuantities.Count > 0 || _hasIndeterminatePurchases));
                }

                var marketGil = ActualGilSpent(purchase.GilBefore, purchase.GilAfter, purchase.GilSpent);
                RecordPurchase(transaction, transactionIndex, purchase.QuantityPurchased, marketGil, null, null, listing.IsHq);
                AddDiagnostic(Stage, purchase.Message, transaction.ItemId, transaction.ItemName, route.WorldName, transaction.GilCost, marketGil, listing.ListingId);
            }

            await _environment.CloseMarketBoardAsync(cancellationToken);
        }

        return PassResult.Finished(null);
    }

    private async Task<LiveAcquisitionResult> CompleteAsync(
        uint startWorldId,
        string startWorldName,
        CancellationToken cancellationToken)
    {
        if (_environment.CurrentWorldId != 0
            && startWorldId != 0
            && _environment.CurrentWorldId != startWorldId)
        {
            Stage = LiveAcquisitionStage.ReturnToStartWorld;
            var route = new AcquisitionWorldRoute(startWorldId, startWorldName, 0, string.Empty, true, false);
            if (!await _environment.TravelToWorldAsync(route, _options.TravelTimeout, cancellationToken))
            {
                return Failure(
                    LiveAcquisitionFailureKind.TravelBlocked,
                    $"Purchases completed, but returning to {startWorldName} failed.",
                    Stage,
                    partial: true);
            }
        }

        var missing = _requiredQuantities
            .Where(requirement => _purchasedQuantities.GetValueOrDefault(requirement.Key) < requirement.Value)
            .Select(requirement => $"{requirement.Key:N0}: {_purchasedQuantities.GetValueOrDefault(requirement.Key):N0}/{requirement.Value:N0}")
            .ToArray();
        var missingHq = _requiredHqQuantities
            .Where(requirement => _purchasedHqQuantities.GetValueOrDefault(requirement.Key) < requirement.Value)
            .Select(requirement => $"{requirement.Key:N0} HQ: {_purchasedHqQuantities.GetValueOrDefault(requirement.Key):N0}/{requirement.Value:N0}")
            .ToArray();
        var missingNq = _requiredNqQuantities
            .Where(requirement => _purchasedNqQuantities.GetValueOrDefault(requirement.Key) < requirement.Value)
            .Select(requirement => $"{requirement.Key:N0} NQ: {_purchasedNqQuantities.GetValueOrDefault(requirement.Key):N0}/{requirement.Value:N0}")
            .ToArray();
        if (missing.Length > 0 || missingHq.Length > 0 || missingNq.Length > 0)
        {
            return Failure(
                LiveAcquisitionFailureKind.VerificationFailed,
                $"Acquisition completed without satisfying all required quantities ({string.Join(", ", missing.Concat(missingHq).Concat(missingNq))}).",
                LiveAcquisitionStage.Market,
                partial: _purchasedQuantities.Count > 0 || _hasIndeterminatePurchases);
        }

        Stage = LiveAcquisitionStage.Completed;
        AddDiagnostic(Stage, "All planned acquisition quantities verified.");
        return Success();
    }

    private LiveAcquisitionResult ReturnResult(LiveAcquisitionResult result)
    {
        _currentResult = result;
        return result;
    }

    private async Task ReturnToStartWorldAfterFailureAsync(uint startWorldId, string startWorldName)
    {
        try
        {
            var currentWorldId = _environment.CurrentWorldId;
            if (startWorldId == 0 || currentWorldId == 0 || currentWorldId == startWorldId)
                return;

            Stage = LiveAcquisitionStage.ReturnToStartWorld;
            var route = new AcquisitionWorldRoute(startWorldId, startWorldName, 0, string.Empty, true, false);
            if (await _environment.TravelToWorldAsync(route, _options.TravelTimeout, CancellationToken.None))
                AddDiagnostic(Stage, $"Returned to starting world {startWorldName} after acquisition failure.", worldName: startWorldName);
            else
                AddDiagnostic(Stage, $"Acquisition failed and returning to {startWorldName} was unsuccessful.", worldName: startWorldName);
        }
        catch (Exception ex)
        {
            AddDiagnostic(Stage, $"Acquisition failed and returning to {startWorldName} raised an error: {ex.Message}", worldName: startWorldName);
        }
    }

    private LiveAcquisitionResult? ValidatePlanningResult(AcquisitionPlanningResult planning)
    {
        Stage = LiveAcquisitionStage.Preconditions;
        if (!planning.IsSuccess)
        {
            var reason = planning.Blockers.FirstOrDefault()?.Reason ?? "The acquisition plan is not executable.";
            return Failure(LiveAcquisitionFailureKind.InvalidPlan, reason, Stage, partial: _purchasedQuantities.Count > 0 || _hasIndeterminatePurchases);
        }

        if (planning.SelectedPlan == null)
            return Failure(LiveAcquisitionFailureKind.InvalidPlan, "The acquisition plan contains no selected plan.", Stage, partial: _purchasedQuantities.Count > 0 || _hasIndeterminatePurchases);
        return null;
    }

    private AcquisitionRoutePlan BuildRoutePlan(AcquisitionPlan plan)
        => AcquisitionRoutePlanner.Plan(plan, new AcquisitionRouteInput
        {
            CurrentWorldId = _environment.CurrentWorldId,
            CurrentWorldName = _environment.CurrentWorldName,
            CurrentWorldOnly = _options.CurrentWorldOnly,
            LifestreamAvailable = _environment.IsLifestreamAvailable,
            NonCrossWorldParty = _environment.IsInNonCrossWorldParty,
            TravelProhibited = _environment.IsInDuty,
            CanVisitWorld = _environment.CanVisitWorld,
            IsGatewayAttuned = _environment.IsGatewayAttuned,
            GatewayTeleportCost = _environment.GetGatewayTeleportCost,
            ResolveWorldName = _environment.ResolveWorldName,
        });

    private IReadOnlyList<LiveMarketListing> SelectListings(
        AcquisitionTransaction transaction,
        int remaining,
        IReadOnlyList<LiveMarketListing> listings)
    {
        if (remaining <= 0)
            return Array.Empty<LiveMarketListing>();

        var requiredNqRemaining = _requiredNqQuantities.TryGetValue(transaction.ItemId, out var requiredNq)
            && _purchasedNqQuantities.GetValueOrDefault(transaction.ItemId) < requiredNq;

        return listings
            .Where(listing => listing.ItemId == transaction.ItemId
                && listing.Quantity > 0
                && listing.Quantity >= remaining
                && !listing.IsMannequin
                && !listing.IsSellingAsSet
                && listing.WorldId != 0
                && listing.WorldId == _environment.CurrentWorldId)
            .Where(listing => !_options.CurrentWorldOnly || listing.WorldId == _environment.CurrentWorldId)
            // IsHq and any still-unfulfilled hard NQ demand are hard
            // requirements. PreferHQ only changes ordering; it must not
            // silently reject a cheaper NQ listing when the plan did not
            // require HQ.
            .Where(listing => transaction.IsHq
                ? listing.IsHq
                : !requiredNqRemaining || !listing.IsHq)
            .OrderBy(listing => _options.PreferHQ && !listing.IsHq)
            .ThenBy(listing => listing.TotalGil)
            .ThenBy(listing => listing.ListingId)
            .ToArray();
    }

    private bool WouldExceedBudget(long additionalGil)
        => _options.MaximumGilSpend.HasValue
            && additionalGil > _options.MaximumGilSpend.Value - _gilSpent;

    private bool IsLiveUnitCostHigher(AcquisitionTransaction transaction, LiveMarketListing listing)
    {
        if (transaction.Quantity <= 0 || listing.Quantity <= 0)
            return true;

        var plannedUnitCost = (decimal)transaction.GilCost / transaction.Quantity;
        var liveUnitCost = (decimal)listing.TotalGil / listing.Quantity;
        return liveUnitCost > plannedUnitCost;
    }

    private int Remaining(
        AcquisitionPlan plan,
        AcquisitionTransaction transaction,
        int transactionIndex,
        IDictionary<uint, int> allocatedItemQuantities,
        IDictionary<uint, int> allocatedHqQuantities,
        IDictionary<uint, int> allocatedNqQuantities,
        IReadOnlyDictionary<uint, int> reservedHqQuantities,
        IReadOnlyDictionary<uint, int> reservedNqQuantities)
    {
        int AllocatedItem(uint itemId)
            => allocatedItemQuantities.TryGetValue(itemId, out var value) ? value : 0;

        int AllocatedHq(uint itemId)
            => allocatedHqQuantities.TryGetValue(itemId, out var value) ? value : 0;

        int AllocatedNq(uint itemId)
            => allocatedNqQuantities.TryGetValue(itemId, out var value) ? value : 0;

        var transactionId = TransactionIdentity(transaction, transactionIndex);
        var alreadyPurchasedForTransaction = _purchasedByTransaction.GetValueOrDefault(transactionId);
        var quantity = System.Math.Max(0, transaction.Quantity);
        var allocatedForItem = allocatedItemQuantities.TryGetValue(transaction.ItemId, out var allocatedItem)
            ? allocatedItem
            : 0;

        if (plan.RequiredQuantities.Count > 0 && transaction.Outputs is { Count: > 0 })
        {
            var outputQuantities = transaction.Outputs
                .Where(output => output is not null && output.ItemId != 0 && output.Quantity > 0)
                .GroupBy(output => output.ItemId)
                .ToDictionary(group => group.Key, group => group.Sum(output => output.Quantity));
            if (outputQuantities.Count > 0)
            {
                var requiredOutput = outputQuantities
                    .Select(output =>
                    {
                        var required = plan.RequiredQuantities.TryGetValue(output.Key, out var demand)
                            ? demand
                            : 0;
                        var purchased = _purchasedQuantities.GetValueOrDefault(output.Key);
                        var allocated = AllocatedItem(output.Key);
                        var covered = System.Math.Max(purchased, allocated);
                        var missing = System.Math.Max(0, required - covered);
                        return (Output: output, Missing: missing);
                    })
                    .Where(output => output.Missing > 0)
                    .ToArray();
                var requiredHq = 0;
                var hasRequiredHq = transaction.IsHq
                    && plan.RequiredHqQuantities.TryGetValue(transaction.ItemId, out requiredHq)
                    && _purchasedHqQuantities.GetValueOrDefault(transaction.ItemId)
                        - AllocatedHq(transaction.ItemId)
                        < requiredHq;
                var requiredNq = 0;
                var hasRequiredNq = !transaction.IsHq
                    && plan.RequiredNqQuantities.TryGetValue(transaction.ItemId, out requiredNq)
                    && _purchasedNqQuantities.GetValueOrDefault(transaction.ItemId)
                        - AllocatedNq(transaction.ItemId)
                        < requiredNq;
                if (requiredOutput.Length == 0 && !hasRequiredHq && !hasRequiredNq)
                {
                    return 0;
                }

                var primaryOutputQuantity = transaction.PrimaryOutputQuantity > 0
                    ? transaction.PrimaryOutputQuantity
                    : outputQuantities.GetValueOrDefault(transaction.ItemId, 1);
                var unitsNeeded = requiredOutput
                    .Select(output => (output.Missing - 1) / output.Output.Value + 1)
                    .DefaultIfEmpty(1)
                    .Max();
                if (transaction.PurchaseUnits > 0)
                    unitsNeeded = System.Math.Min(unitsNeeded, transaction.PurchaseUnits);
                if (hasRequiredHq)
                {
                    var hqMissing = System.Math.Max(
                        0,
                        requiredHq
                        - _purchasedHqQuantities.GetValueOrDefault(transaction.ItemId)
                        + AllocatedHq(transaction.ItemId));
                    unitsNeeded = System.Math.Max(
                        unitsNeeded,
                        (hqMissing - 1) / primaryOutputQuantity + 1);
                }
                if (hasRequiredNq)
                {
                    var nqMissing = System.Math.Max(
                        0,
                        requiredNq
                        - _purchasedNqQuantities.GetValueOrDefault(transaction.ItemId)
                        + AllocatedNq(transaction.ItemId));
                    unitsNeeded = System.Math.Max(
                        unitsNeeded,
                        (nqMissing - 1) / primaryOutputQuantity + 1);
                }

                var atomicQuantity = checked(primaryOutputQuantity * unitsNeeded);
                var remainingAtomicQuantity = System.Math.Max(
                    0,
                    atomicQuantity - alreadyPurchasedForTransaction);
                foreach (var output in requiredOutput)
                {
                    var allocatedOutput = System.Math.Min(
                        output.Missing,
                        checked(output.Output.Value * unitsNeeded));
                    allocatedItemQuantities[output.Output.Key] = checked(
                        AllocatedItem(output.Output.Key) + allocatedOutput);
                }
                if (hasRequiredHq)
                {
                    var hqMissing = System.Math.Max(
                        0,
                        requiredHq
                        - _purchasedHqQuantities.GetValueOrDefault(transaction.ItemId)
                        + AllocatedHq(transaction.ItemId));
                    allocatedHqQuantities[transaction.ItemId] = checked(
                        AllocatedHq(transaction.ItemId)
                        + System.Math.Min(hqMissing, atomicQuantity));
                }
                if (hasRequiredNq)
                {
                    var nqMissing = System.Math.Max(
                        0,
                        requiredNq
                        - _purchasedNqQuantities.GetValueOrDefault(transaction.ItemId)
                        + AllocatedNq(transaction.ItemId));
                    allocatedNqQuantities[transaction.ItemId] = checked(
                        AllocatedNq(transaction.ItemId)
                        + System.Math.Min(nqMissing, atomicQuantity));
                }
                return remainingAtomicQuantity;
            }
        }

        var covered = 0;
        if (transaction.IsHq)
        {
            var allocatedHq = allocatedHqQuantities.TryGetValue(transaction.ItemId, out var allocatedHqValue)
                ? allocatedHqValue
                : 0;
            var availableHq = System.Math.Max(
                0,
                _purchasedHqQuantities.GetValueOrDefault(transaction.ItemId) - allocatedHq);
            covered = System.Math.Min(quantity, System.Math.Max(alreadyPurchasedForTransaction, availableHq));
            allocatedHqQuantities[transaction.ItemId] = checked(allocatedHq + quantity);
        }
        else if (plan.RequiredNqQuantities.TryGetValue(transaction.ItemId, out var requiredNq)
            && requiredNq > 0
            && _purchasedNqQuantities.GetValueOrDefault(transaction.ItemId)
                - AllocatedNq(transaction.ItemId)
                < requiredNq)
        {
            var allocatedNq = AllocatedNq(transaction.ItemId);
            var availableNq = System.Math.Max(
                0,
                _purchasedNqQuantities.GetValueOrDefault(transaction.ItemId) - allocatedNq);
            covered = System.Math.Min(quantity, System.Math.Max(alreadyPurchasedForTransaction, availableNq));
            allocatedNqQuantities[transaction.ItemId] = checked(allocatedNq + quantity);
        }
        else
        {
            // Reserve already-purchased quality-specific stock for hard
            // transactions first. Any remaining item stock may satisfy an
            // ordinary transaction through the aggregate item ledger.
            var hqReserved = System.Math.Min(
                reservedHqQuantities.TryGetValue(transaction.ItemId, out var reservedHq)
                    ? reservedHq
                    : 0,
                _purchasedHqQuantities.GetValueOrDefault(transaction.ItemId));
            var allocatedHq = allocatedHqQuantities.TryGetValue(transaction.ItemId, out var allocatedHqValue)
                ? allocatedHqValue
                : 0;
            var heldForHq = System.Math.Max(0, hqReserved - allocatedHq);
            var nqReserved = plan.RequiredNqQuantities.ContainsKey(transaction.ItemId)
                ? System.Math.Min(
                    reservedNqQuantities.TryGetValue(transaction.ItemId, out var reservedNq)
                        ? reservedNq
                        : 0,
                    _purchasedNqQuantities.GetValueOrDefault(transaction.ItemId))
                : 0;
            var allocatedNq = AllocatedNq(transaction.ItemId);
            var heldForNq = System.Math.Max(0, nqReserved - allocatedNq);
            var availableItem = System.Math.Max(
                0,
                _purchasedQuantities.GetValueOrDefault(transaction.ItemId)
                    - allocatedForItem
                    - heldForHq
                    - heldForNq);
            covered = System.Math.Min(quantity, System.Math.Max(alreadyPurchasedForTransaction, availableItem));
        }

        allocatedItemQuantities[transaction.ItemId] = checked(allocatedForItem + quantity);
        return System.Math.Max(0, quantity - covered);
    }

    private long RemainingTransactionReservation(AcquisitionTransaction transaction, int transactionIndex)
        => System.Math.Max(0, transaction.GilCost - _gilSpentByTransaction.GetValueOrDefault(TransactionIdentity(transaction, transactionIndex)));

    private long RemainingOtherPlanGilReservation(AcquisitionPlan plan, int currentTransactionIndex)
        => plan.Transactions
            .Select((transaction, index) => (transaction, index))
            .Where(entry => entry.index != currentTransactionIndex)
            .Sum(entry => RemainingTransactionReservation(entry.transaction, entry.index));

    private bool CanPurchaseMarketListing(
        AcquisitionTransaction transaction,
        int remaining,
        LiveMarketListing listing,
        long otherReservation)
    {
        if (listing.Quantity < remaining
            || (listing.Quantity > remaining && !_options.MaximumGilSpend.HasValue)
            || IsLiveUnitCostHigher(transaction, listing)
            || WouldExceedBudget(listing.TotalGil))
            return false;

        // An overbuy consumes the complete listing. When a global cap exists,
        // keep every other still-planned transaction's reservation intact.
        if (_options.MaximumGilSpend.HasValue
            && listing.Quantity > remaining
            && checked(_gilSpent + listing.TotalGil + otherReservation) > _options.MaximumGilSpend.Value)
            return false;

        return true;
    }

    private string? ValidateVendorPurchase(
        AcquisitionTransaction transaction,
        int transactionIndex,
        int remaining,
        LiveVendorPurchaseResult purchase)
    {
        if (!purchase.Accepted)
            return purchase.Message;
        if (!purchase.Verified)
            return string.IsNullOrWhiteSpace(purchase.Message)
                ? "Vendor purchase was accepted but could not be verified."
                : purchase.Message;
        if (!purchase.InteractionClosed)
            return string.IsNullOrWhiteSpace(purchase.Message)
                ? "Vendor purchase completed, but the vendor interaction did not close."
                : purchase.Message;
        if (purchase.ItemId != transaction.ItemId)
            return $"Vendor purchase returned item {purchase.ItemId:N0}, expected {transaction.ItemId:N0}.";
        if (!purchase.IsHq.HasValue || purchase.IsHq.Value != transaction.IsHq)
            return $"Vendor purchase HQ state did not match the requested transaction for {transaction.ItemName}.";
        if (purchase.QuantityPurchased < remaining)
            return $"Vendor purchase for {transaction.ItemName} completed only {purchase.QuantityPurchased:N0}/{remaining:N0}; unresolved quantity remains.";
        if (transaction.Outputs is { Count: > 0 })
        {
            if (transaction.Outputs.Any(output => output == null || output.ItemId == 0 || output.Quantity <= 0))
                return $"Vendor purchase for {transaction.ItemName} has an invalid output vector.";

            var primaryOutputQuantity = transaction.PrimaryOutputQuantity > 0
                ? transaction.PrimaryOutputQuantity
                : transaction.Outputs
                    .Where(output => output.ItemId == transaction.ItemId)
                    .Select(output => output.Quantity)
                    .DefaultIfEmpty()
                    .Sum();
            var purchaseUnits = primaryOutputQuantity > 0
                ? (remaining - 1) / primaryOutputQuantity + 1
                : 0;
            foreach (var output in transaction.Outputs
                         .Where(output => output.ItemId != 0 && output.Quantity > 0)
                         .GroupBy(output => output.ItemId)
                         .Select(group => new AcquisitionVendorOutput
                         {
                             ItemId = group.Key,
                             Quantity = checked(group.Sum(output => output.Quantity)),
                         }))
            {
                var expected = checked(output.Quantity * purchaseUnits);
                if (purchase.OutputQuantities.GetValueOrDefault(output.ItemId) < expected)
                    return $"Vendor purchase for {transaction.ItemName} did not verify output {output.ItemId:N0} ({purchase.OutputQuantities.GetValueOrDefault(output.ItemId):N0}/{expected:N0}).";
            }
        }
        var currencyFailure = ValidateVendorCurrencyDeltas(transaction, purchase);
        if (currencyFailure != null)
            return currencyFailure;
        if (!TryGetVendorGilSpent(purchase, out var gil, out var gilFailure))
            return gilFailure;
        var reservation = RemainingTransactionReservation(transaction, transactionIndex);
        if (gil > reservation)
            return $"Vendor purchase for {transaction.ItemName} exceeded its remaining Gil reservation ({gil:N0} > {reservation:N0}).";
        if (WouldExceedBudget(gil))
            return $"Vendor purchase for {transaction.ItemName} exceeded the remaining global Gil reservation.";
        return null;
    }

    private static string? ValidateVendorCurrencyDeltas(
        AcquisitionTransaction transaction,
        LiveVendorPurchaseResult purchase)
    {
        var costs = transaction.Costs
            .Where(cost => cost.Amount > 0)
            .GroupBy(cost => cost.IsGil || cost.CurrencyId == AcquisitionCurrency.GilId
                ? VendorShopResolver.GilCurrencyItemId
                : cost.CurrencyId)
            .Select(group => new
            {
                CurrencyId = group.Key,
                Amount = group.Sum(cost => cost.Amount),
                IsGil = group.Key == VendorShopResolver.GilCurrencyItemId,
            })
            .ToArray();
        if (costs.Length == 0)
            return null;

        var completedTransactions = CompletedVendorTransactionCount(transaction, purchase.QuantityPurchased);
        var plannedTransactions = transaction.PurchaseUnits > 0
            ? transaction.PurchaseUnits
            : 1;
        foreach (var cost in costs)
        {
            var reportedDelta = cost.IsGil
                ? purchase.GilSpent
                : purchase.CurrencySpent.TryGetValue(cost.CurrencyId, out var nonGilSpent)
                    ? nonGilSpent
                    : -1;
            if (reportedDelta < 0)
                return $"Vendor purchase for {transaction.ItemName} did not report authoritative {CurrencyLabel(cost.CurrencyId)} spend.";

            if (cost.Amount % plannedTransactions != 0)
                return $"Vendor purchase for {transaction.ItemName} has a non-integral per-transaction {CurrencyLabel(cost.CurrencyId)} cost.";
            var expectedDelta = checked(cost.Amount / plannedTransactions * completedTransactions);
            if (purchase.CurrencySpendIsAuthoritative)
            {
                if (reportedDelta != expectedDelta)
                    return $"Vendor purchase for {transaction.ItemName} spent {reportedDelta:N0} {CurrencyLabel(cost.CurrencyId)}; expected {expectedDelta:N0} for {completedTransactions:N0} completed transaction(s).";
                continue;
            }

            if (!purchase.CurrencyBalancesBefore.TryGetValue(cost.CurrencyId, out var before)
                || !purchase.CurrencyBalancesAfter.TryGetValue(cost.CurrencyId, out var after)
                || !purchase.CurrencyBalanceSources.TryGetValue(cost.CurrencyId, out var beforeSource)
                || !purchase.CurrencyBalanceSourcesAfter.TryGetValue(cost.CurrencyId, out var afterSource))
                return $"Vendor purchase for {transaction.ItemName} did not provide authoritative {CurrencyLabel(cost.CurrencyId)} balances.";
            if (!IsAuthoritativeCurrencySource(beforeSource)
                || !IsAuthoritativeCurrencySource(afterSource))
                return $"Vendor purchase for {transaction.ItemName} reported a non-authoritative {CurrencyLabel(cost.CurrencyId)} balance.";

            var observedDelta = before - after;
            if (observedDelta < 0)
                return $"Vendor purchase for {transaction.ItemName} increased {CurrencyLabel(cost.CurrencyId)} unexpectedly; balance verification failed.";

            if (reportedDelta != observedDelta)
                return $"Vendor purchase for {transaction.ItemName} reported {CurrencyLabel(cost.CurrencyId)} spend {reportedDelta:N0}, but the wallet delta was {observedDelta:N0}.";
            if (observedDelta != expectedDelta)
                return $"Vendor purchase for {transaction.ItemName} spent {observedDelta:N0} {CurrencyLabel(cost.CurrencyId)}; expected {expectedDelta:N0} for {completedTransactions:N0} completed transaction(s).";
        }

        return null;
    }

    private static int CompletedVendorTransactionCount(
        AcquisitionTransaction transaction,
        int quantityPurchased)
    {
        if (quantityPurchased <= 0)
            return 0;
        var primaryOutputQuantity = transaction.PrimaryOutputQuantity > 0
            ? transaction.PrimaryOutputQuantity
            : transaction.Outputs
                .Where(output => output is not null && output.ItemId == transaction.ItemId && output.Quantity > 0)
                .Select(output => output.Quantity)
                .DefaultIfEmpty(1)
                .Sum();
        return checked((quantityPurchased - 1) / primaryOutputQuantity + 1);
    }

    private static bool IsAuthoritativeCurrencySource(VendorCurrencyAvailabilitySource source)
        => VendorCurrencyAvailabilityResolver.IsAuthoritativeSource(source);

    private static string CurrencyLabel(uint currencyId)
        => currencyId == VendorShopResolver.GilCurrencyItemId
            ? "Gil"
            : $"currency {currencyId:N0}";

    private void RecordKnownVendorPurchase(
        AcquisitionTransaction transaction,
        int transactionIndex,
        LiveVendorPurchaseResult purchase)
    {
        if (!purchase.Accepted || purchase.ItemId != transaction.ItemId)
            return;

        var quantity = System.Math.Max(0, purchase.QuantityPurchased);
        var gil = purchase.CurrencySpendIsAuthoritative
            ? System.Math.Max(0, purchase.GilSpent)
            : TryGetObservedGilSpent(purchase.GilBefore, purchase.GilAfter, purchase.GilSpent);
        if (quantity > 0 || gil > 0 || purchase.CurrencySpent.Count > 0 || purchase.OutputQuantities.Count > 0)
            RecordPurchase(transaction, transactionIndex, quantity, gil,
                purchase.OutputQuantities, purchase.CurrencySpent, purchase.IsHq);
    }

    private string? ValidateMarketPurchase(
        AcquisitionTransaction transaction,
        int transactionIndex,
        int remaining,
        LiveMarketListing listing,
        long otherReservation,
        LiveMarketPurchaseResult purchase,
        bool allowUnderfill = false)
    {
        if (!purchase.Accepted)
            return purchase.Message;
        if (!purchase.Verified)
            return string.IsNullOrWhiteSpace(purchase.Message)
                ? "Market purchase was accepted but could not be verified."
                : purchase.Message;
        if (purchase.ItemId != listing.ItemId || purchase.ItemId != transaction.ItemId)
            return $"Market purchase returned item {purchase.ItemId:N0}, expected {transaction.ItemId:N0}.";
        if (purchase.ListingId != listing.ListingId)
            return $"Market purchase returned listing {purchase.ListingId:N0}, expected {listing.ListingId:N0}.";
        if (!purchase.IsHq.HasValue || purchase.IsHq.Value != listing.IsHq)
            return $"Market purchase HQ state did not match listing {listing.ListingId:N0}.";
        if (purchase.QuantityPurchased <= 0 || purchase.QuantityPurchased > listing.Quantity)
            return $"Market purchase for {transaction.ItemName} returned an invalid quantity ({purchase.QuantityPurchased:N0}/{listing.Quantity:N0}).";
        if (!allowUnderfill && (purchase.QuantityPurchased != listing.Quantity || purchase.QuantityPurchased < remaining))
            return $"Market purchase for {transaction.ItemName} completed {purchase.QuantityPurchased:N0}/{listing.Quantity:N0}; unresolved quantity remains if underfilled.";
        if (!TryGetActualGilSpent(purchase.GilBefore, purchase.GilAfter, purchase.GilSpent, out var gil, out var gilFailure))
            return gilFailure;
        if (gil > listing.TotalGil)
            return $"Market purchase Gil delta {gil:N0} exceeded the live listing total {listing.TotalGil:N0}.";
        var transactionReservation = RemainingTransactionReservation(transaction, transactionIndex);
        var isAllowedAtomicOverbuy = listing.Quantity > remaining
            && _options.MaximumGilSpend.HasValue
            && checked(_gilSpent + gil + otherReservation) <= _options.MaximumGilSpend.Value;
        if (gil > transactionReservation && !isAllowedAtomicOverbuy)
            return $"Market purchase for {transaction.ItemName} exceeded its planned transaction reservation.";
        if (WouldExceedBudget(gil))
            return $"Market purchase for {transaction.ItemName} exceeded the remaining global Gil reservation.";
        return null;
    }

    private void RecordKnownMarketPurchase(
        AcquisitionTransaction transaction,
        int transactionIndex,
        LiveMarketListing listing,
        LiveMarketPurchaseResult purchase)
    {
        if (!purchase.Accepted
            || purchase.ItemId != transaction.ItemId
            || purchase.ListingId != listing.ListingId)
            return;

        var quantity = System.Math.Max(0, purchase.QuantityPurchased);
        var gil = TryGetObservedGilSpent(purchase.GilBefore, purchase.GilAfter, purchase.GilSpent);
        if (quantity > 0 || gil > 0)
            RecordPurchase(transaction, transactionIndex, quantity, gil, null, null, listing.IsHq);
    }

    private static bool TryGetActualGilSpent(
        long? gilBefore,
        long? gilAfter,
        long reported,
        out long actual,
        out string failure)
    {
        actual = System.Math.Max(0, reported);
        failure = string.Empty;
        if (reported < 0)
        {
            failure = $"Purchase reported an invalid negative Gil spend ({reported:N0}).";
            return false;
        }
        if (!gilBefore.HasValue || !gilAfter.HasValue)
            return true;
        if (gilAfter.Value > gilBefore.Value)
        {
            failure = $"Gil balance increased during a purchase ({gilBefore.Value:N0} -> {gilAfter.Value:N0}).";
            return false;
        }

        actual = gilBefore.Value - gilAfter.Value;
        if (actual != System.Math.Max(0, reported))
        {
            failure = $"Reported Gil spend {reported:N0} did not match the observed balance delta {actual:N0}.";
            return false;
        }
        return true;
    }

    private static bool TryGetVendorGilSpent(
        LiveVendorPurchaseResult purchase,
        out long actual,
        out string failure)
    {
        if (!purchase.CurrencySpendIsAuthoritative)
            return TryGetActualGilSpent(
                purchase.GilBefore,
                purchase.GilAfter,
                purchase.GilSpent,
                out actual,
                out failure);

        actual = System.Math.Max(0, purchase.GilSpent);
        failure = string.Empty;
        if (purchase.GilSpent >= 0)
            return true;

        failure = $"Purchase reported an invalid negative Gil spend ({purchase.GilSpent:N0}).";
        return false;
    }

    private static long TryGetObservedGilSpent(long? gilBefore, long? gilAfter, long reported)
    {
        if (reported < 0)
            return 0;
        if (gilBefore.HasValue && gilAfter.HasValue && gilBefore.Value >= gilAfter.Value)
            return gilBefore.Value - gilAfter.Value;
        return reported;
    }

    private static AcquisitionTransaction WithQuantity(AcquisitionTransaction transaction, int quantity)
        => new()
        {
            ExecutionId = transaction.ExecutionId,
            ItemId = transaction.ItemId,
            ItemName = transaction.ItemName,
            SelectedRecipeId = transaction.SelectedRecipeId,
            SourceKind = transaction.SourceKind,
            SourceId = transaction.SourceId,
            SourceName = transaction.SourceName,
            Location = transaction.Location,
            WorldId = transaction.WorldId,
            WorldName = transaction.WorldName,
            Quantity = quantity,
            Outputs = transaction.Outputs,
            PrimaryOutputQuantity = transaction.PrimaryOutputQuantity,
            PurchaseUnits = transaction.PurchaseUnits,
            IsHq = transaction.IsHq,
            IsSpecialCurrencySource = transaction.IsSpecialCurrencySource,
            IsSpecialCurrencyAlternative = transaction.IsSpecialCurrencyAlternative,
            Costs = transaction.Costs,
            GilCost = transaction.GilCost,
            TaxGilCost = transaction.TaxGilCost,
        };

    private void RecordPurchase(
        AcquisitionTransaction transaction,
        int transactionIndex,
        int quantity,
        long gil,
        IReadOnlyDictionary<uint, int>? outputs,
        IReadOnlyDictionary<uint, long>? currencies,
        bool? purchasedIsHq)
    {
        var normalizedQuantity = System.Math.Max(0, quantity);
        var transactionId = TransactionIdentity(transaction, transactionIndex);
        _purchasedByTransaction[transactionId] = checked(_purchasedByTransaction.GetValueOrDefault(transactionId) + normalizedQuantity);
        if (outputs is { Count: > 0 })
        {
            foreach (var output in outputs)
            {
                if (output.Key == 0 || output.Value <= 0)
                    continue;
                _purchasedQuantities[output.Key] = checked(
                    _purchasedQuantities.GetValueOrDefault(output.Key) + output.Value);
            }
        }
        else
        {
            _purchasedQuantities[transaction.ItemId] = checked(
                _purchasedQuantities.GetValueOrDefault(transaction.ItemId) + normalizedQuantity);
        }
        var purchasedIsHqValue = purchasedIsHq ?? transaction.IsHq;
        if (outputs is { Count: > 0 })
        {
            foreach (var output in outputs)
            {
                if (output.Key == 0 || output.Value <= 0)
                    continue;
                if (purchasedIsHqValue)
                {
                    _purchasedHqQuantities[output.Key] = checked(
                        _purchasedHqQuantities.GetValueOrDefault(output.Key) + output.Value);
                }
                else
                {
                    _purchasedNqQuantities[output.Key] = checked(
                        _purchasedNqQuantities.GetValueOrDefault(output.Key) + output.Value);
                }
            }
        }
        else if (purchasedIsHqValue)
        {
            _purchasedHqQuantities[transaction.ItemId] = checked(
                _purchasedHqQuantities.GetValueOrDefault(transaction.ItemId) + normalizedQuantity);
        }
        else
        {
            _purchasedNqQuantities[transaction.ItemId] = checked(
                _purchasedNqQuantities.GetValueOrDefault(transaction.ItemId) + normalizedQuantity);
        }
        var normalizedGil = System.Math.Max(0, gil);
        _gilSpentByTransaction[transactionId] = checked(_gilSpentByTransaction.GetValueOrDefault(transactionId) + normalizedGil);
        _gilSpent = checked(_gilSpent + normalizedGil);
        if (currencies == null)
            return;
        foreach (var currency in currencies)
            _currencySpent[currency.Key] = checked(_currencySpent.GetValueOrDefault(currency.Key) + System.Math.Max(0, currency.Value));
    }

    private void ResetRunState()
    {
        Stage = LiveAcquisitionStage.Idle;
        _currentResult = null;
        _gilSpent = 0;
        _hasIndeterminatePurchases = false;
        _requestSubmitted = false;
        _purchasedQuantities.Clear();
        _purchasedHqQuantities.Clear();
        _purchasedNqQuantities.Clear();
        _purchasedByTransaction.Clear();
        _gilSpentByTransaction.Clear();
        _requiredQuantities.Clear();
        _requiredHqQuantities.Clear();
        _requiredNqQuantities.Clear();
        _currencySpent.Clear();
        _diagnostics.Clear();
    }

    private void TrackRequirements(AcquisitionPlan plan)
    {
        if (plan.RequiredQuantities.Count > 0)
        {
            foreach (var requirement in plan.RequiredQuantities)
            {
                _requiredQuantities[requirement.Key] = System.Math.Max(
                    _requiredQuantities.GetValueOrDefault(requirement.Key),
                    System.Math.Max(0, requirement.Value));
            }
        }
        else
        {
            // Legacy/manual plans have no dependency-demand map. Their
            // transaction quantities are the declared demand; never infer it
            // from PurchasedQuantities, which may include an atomic overbuy or
            // a vendor co-product vector.
            foreach (var requirement in plan.Transactions
                         .GroupBy(transaction => transaction.ItemId)
                         .Select(group => new { ItemId = group.Key, Quantity = group.Sum(transaction => transaction.Quantity) }))
            {
                _requiredQuantities[requirement.ItemId] = System.Math.Max(
                    _requiredQuantities.GetValueOrDefault(requirement.ItemId),
                    System.Math.Max(0, requirement.Quantity));
            }
        }

        if (plan.RequiredHqQuantities.Count > 0)
        {
            foreach (var requirement in plan.RequiredHqQuantities)
            {
                _requiredHqQuantities[requirement.Key] = System.Math.Max(
                    _requiredHqQuantities.GetValueOrDefault(requirement.Key),
                    System.Math.Max(0, requirement.Value));
            }
        }
        else
        {
            foreach (var requirement in plan.Transactions
                         .Where(transaction => transaction.IsHq)
                         .GroupBy(transaction => transaction.ItemId)
                         .Select(group => new { ItemId = group.Key, Quantity = group.Sum(transaction => transaction.Quantity) }))
            {
                _requiredHqQuantities[requirement.ItemId] = System.Math.Max(
                    _requiredHqQuantities.GetValueOrDefault(requirement.ItemId),
                    System.Math.Max(0, requirement.Quantity));
            }
        }

        foreach (var requirement in plan.RequiredNqQuantities)
        {
            _requiredNqQuantities[requirement.Key] = System.Math.Max(
                _requiredNqQuantities.GetValueOrDefault(requirement.Key),
                System.Math.Max(0, requirement.Value));
        }
    }

    private static string TransactionIdentity(AcquisitionTransaction transaction, int transactionIndex)
        => string.IsNullOrWhiteSpace(transaction.ExecutionId)
            ? AcquisitionTransactionIdentity.Create(
                transaction.ItemId,
                transaction.SelectedRecipeId,
                transaction.SourceKind,
                transaction.SourceId,
                transaction.IsHq,
                transactionIndex)
            : transaction.ExecutionId;

    private static long ActualGilSpent(long? gilBefore, long? gilAfter, long reported)
    {
        if (gilBefore.HasValue && gilAfter.HasValue && gilBefore.Value >= gilAfter.Value)
            return gilBefore.Value - gilAfter.Value;
        return System.Math.Max(0, reported);
    }

    private static long VendorGilSpent(LiveVendorPurchaseResult purchase)
        => purchase.CurrencySpendIsAuthoritative
            ? System.Math.Max(0, purchase.GilSpent)
            : ActualGilSpent(purchase.GilBefore, purchase.GilAfter, purchase.GilSpent);

    private void AddDiagnostic(
        LiveAcquisitionStage stage,
        string message,
        uint itemId = 0,
        string itemName = "",
        string worldName = "",
        long? estimatedGil = null,
        long? liveGil = null,
        long? listingId = null)
    {
        var diagnostic = new LiveAcquisitionDiagnostic(DateTime.UtcNow, stage, message, itemId, itemName, worldName, estimatedGil, liveGil, listingId);
        _diagnostics.Add(diagnostic);
        Diagnostic?.Invoke(diagnostic);
    }

    private bool HasIrreversiblePurchasesObserved
        => _hasIndeterminatePurchases
            || _gilSpent > 0
            || _purchasedQuantities.Values.Any(quantity => quantity > 0)
            || _currencySpent.Values.Any(amount => amount > 0);

    private LiveAcquisitionResult Success()
        => new()
        {
            Status = LiveAcquisitionStatus.Completed,
            FailureKind = LiveAcquisitionFailureKind.None,
            FinalStage = Stage,
            Message = "Acquisition completed and inventory/currency changes were verified.",
            HasIrreversiblePurchases = HasIrreversiblePurchasesObserved,
            HasIndeterminatePurchases = _hasIndeterminatePurchases,
            GilSpent = _gilSpent,
            PurchasedQuantities = new Dictionary<uint, int>(_purchasedQuantities),
            CurrencySpent = new Dictionary<uint, long>(_currencySpent),
            Diagnostics = _diagnostics.ToArray(),
        };

    private LiveAcquisitionResult Failure(
        LiveAcquisitionFailureKind kind,
        string message,
        LiveAcquisitionStage stage,
        uint itemId = 0,
        string itemName = "",
        string worldName = "",
        long? estimatedGil = null,
        long? liveGil = null,
        long? listingId = null,
        bool partial = false)
        => new()
        {
            Status = partial || HasIrreversiblePurchasesObserved
                ? LiveAcquisitionStatus.PartiallyCompleted
                : kind == LiveAcquisitionFailureKind.Cancelled
                    ? LiveAcquisitionStatus.Cancelled
                    : LiveAcquisitionStatus.Failed,
            FailureKind = kind,
            FinalStage = stage,
            Message = message,
            HasIrreversiblePurchases = HasIrreversiblePurchasesObserved,
            HasIndeterminatePurchases = _hasIndeterminatePurchases,
            GilSpent = _gilSpent,
            PurchasedQuantities = new Dictionary<uint, int>(_purchasedQuantities),
            CurrencySpent = new Dictionary<uint, long>(_currencySpent),
            Diagnostics = _diagnostics
                .Append(new LiveAcquisitionDiagnostic(DateTime.UtcNow, stage, message, itemId, itemName, worldName, estimatedGil, liveGil, listingId))
                .ToArray(),
        };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _activeCancellation?.Cancel();
    }

    private enum PassResultKind
    {
        Finished,
        Replan,
    }

    private sealed record PassResult(
        PassResultKind Kind,
        LiveAcquisitionResult? Result,
        string Message,
        uint ItemId,
        string ItemName,
        string WorldName,
        long? EstimatedGil,
        long? LiveGil,
        long? ListingId)
    {
        public static PassResult Finished(LiveAcquisitionResult? result)
            => new(PassResultKind.Finished, result, string.Empty, 0, string.Empty, string.Empty, null, null, null);

        public static PassResult ReplanRequested(
            string message,
            uint itemId,
            string itemName,
            string worldName,
            long? estimatedGil,
            long? liveGil,
            long? listingId)
            => new(PassResultKind.Replan, null, message, itemId, itemName, worldName, estimatedGil, liveGil, listingId);
    }
}
