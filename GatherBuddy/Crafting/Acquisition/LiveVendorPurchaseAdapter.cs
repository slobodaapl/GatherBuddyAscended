using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.Crafting.Acquisition;

/// <summary>
/// Adapts the existing vendor UI automation to the live acquisition ABI.
/// VendorPurchaseManager remains the sole owner of interaction and inventory
/// verification; this adapter only resolves the immutable transaction back to
/// its persisted offer and waits for its completion event.
/// </summary>
public sealed class LiveVendorPurchaseAdapter
{
    private static readonly TimeSpan FrameworkDispatchTimeout = TimeSpan.FromSeconds(5);

    private readonly VendorPurchaseManager _manager;
    private readonly object _requestGate = new();
    private long _requestGeneration;

    public LiveVendorPurchaseAdapter(VendorPurchaseManager manager)
        => _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    public async Task<LiveVendorPurchaseResult> PurchaseAsync(
        AcquisitionTransaction transaction,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        VendorShopEntry entry = null!;
        VendorNpc vendor = null!;
        VendorNpcLocation location = null!;
        string failure = string.Empty;
        var resolved = await OnFrameworkThreadAsync(
            () => TryResolve(transaction, out entry, out vendor, out location, out failure),
            cancellationToken);
        if (!resolved)
            return Failure(transaction, failure);
        if (await OnFrameworkThreadAsync(() => _manager.IsRunning, cancellationToken))
            return Failure(transaction, "Another vendor purchase is already in progress.");

        var generation = BeginRequestGeneration();
        using var generationCancellation = cancellationToken.Register(
            () => InvalidateRequestGeneration(generation));
        var completion = new TaskCompletionSource<VendorPurchaseManager.PurchaseResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptResults = 0;
        void Handle(VendorPurchaseManager.PurchaseResult result)
        {
            lock (_requestGate)
            {
                if (Volatile.Read(ref acceptResults) == 0
                    || _requestGeneration != generation
                    || result.ItemId != transaction.ItemId)
                    return;
                completion.TrySetResult(result);
            }
        }

        _manager.PurchaseFinished += Handle;
        var requestStarted = false;
        var requestSubmitted = false;
        var cleanupAttempted = false;
        try
        {
            var currencyBefore = await OnFrameworkThreadAsync(
                () => CaptureCurrencySnapshot(entry.CurrencyCosts),
                cancellationToken);
            var start = await OnFrameworkThreadAsync(
                () => TryStartPurchase(
                    generation,
                    cancellationToken,
                    entry,
                    vendor,
                    location,
                    checked((uint)transaction.Quantity)),
                cancellationToken);
            requestSubmitted = start.Submitted;
            requestStarted = start.Running;
            Volatile.Write(ref acceptResults, requestStarted ? 1 : 0);
            if (!requestStarted)
            {
                var managerFailure = await OnFrameworkThreadAsync(
                    () => string.IsNullOrWhiteSpace(_manager.StatusText)
                        ? $"Vendor purchase manager rejected the request for {transaction.ItemName}."
                        : _manager.StatusText,
                    cancellationToken);
                return Failure(
                    transaction,
                    managerFailure,
                    requestSubmitted);
            }
            var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (finished != completion.Task)
            {
                await StopOnFrameworkThreadAsync(cancellationToken);
                cleanupAttempted = true;
                var currencyAfter = await OnFrameworkThreadAsync(
                    () => CaptureCurrencySnapshot(entry.CurrencyCosts),
                    cancellationToken);
                var timedOutObserved = ObserveCurrencySpend(currencyBefore, currencyAfter);
                var stoppedResult = completion.Task.IsCompletedSuccessfully
                    ? completion.Task.Result
                    : null;
                var timedOutCompletedQuantity = stoppedResult == null
                    ? 0
                    : checked((int)stoppedResult.CompletedQuantity);
                var timedOutResult = new LiveVendorPurchaseResult(
                    true,
                    false,
                    transaction.ItemId,
                    timedOutCompletedQuantity,
                    timedOutObserved.NonGilSpent,
                    timedOutObserved.GilSpent,
                    stoppedResult == null
                        ? $"Vendor purchase timed out for {transaction.ItemName}; final state is indeterminate."
                        : $"Vendor purchase timed out for {transaction.ItemName} after {timedOutCompletedQuantity:N0} item(s); final state is indeterminate.",
                    RequestSubmitted: requestSubmitted,
                    IsHq: transaction.IsHq,
                    GilBefore: currencyBefore.Balances.GetValueOrDefault(VendorShopResolver.GilCurrencyItemId),
                    GilAfter: currencyAfter.Balances.GetValueOrDefault(VendorShopResolver.GilCurrencyItemId))
                {
                    OutputQuantities = stoppedResult?.OutputQuantities
                        ?? new Dictionary<uint, int>(),
                    CurrencyBalancesBefore = currencyBefore.Balances,
                    CurrencyBalancesAfter = currencyAfter.Balances,
                    CurrencyBalanceSources = currencyBefore.Sources,
                    CurrencyBalanceSourcesAfter = currencyAfter.Sources,
                };
                return timedOutResult;
            }

            var result = await completion.Task;
            var currencyAfterResult = await OnFrameworkThreadAsync(
                () => CaptureCurrencySnapshot(entry.CurrencyCosts),
                cancellationToken);
            var observed = ObserveCurrencySpend(currencyBefore, currencyAfterResult);
            var completedQuantity = checked((int)result.CompletedQuantity);
            var verified = result.State == VendorPurchaseManager.CompletionState.Completed
                && completedQuantity >= transaction.Quantity
                && observed.IsAuthoritative;
            var purchaseResult = new LiveVendorPurchaseResult(
                result.State is not VendorPurchaseManager.CompletionState.Failed
                    and not VendorPurchaseManager.CompletionState.Cancelled
                    and not VendorPurchaseManager.CompletionState.Skipped,
                verified,
                transaction.ItemId,
                completedQuantity,
                observed.NonGilSpent,
                observed.GilSpent,
                observed.IsAuthoritative
                    ? result.Message
                    : $"{result.Message} {observed.FailureReason}",
                RequestSubmitted: requestSubmitted,
                IsHq: transaction.IsHq,
                GilBefore: currencyBefore.Balances.GetValueOrDefault(VendorShopResolver.GilCurrencyItemId),
                GilAfter: currencyAfterResult.Balances.GetValueOrDefault(VendorShopResolver.GilCurrencyItemId))
            {
                OutputQuantities = result.OutputQuantities,
                CurrencyBalancesBefore = currencyBefore.Balances,
                CurrencyBalancesAfter = currencyAfterResult.Balances,
                CurrencyBalanceSources = currencyBefore.Sources,
                CurrencyBalanceSourcesAfter = currencyAfterResult.Sources,
            };
            return purchaseResult;
        }
        catch (OperationCanceledException)
        {
            InvalidateRequestGeneration(generation);
            Volatile.Write(ref acceptResults, 0);
            await TryStopOnFrameworkThreadAsync();
            cleanupAttempted = true;
            throw;
        }
        catch (Exception ex)
        {
            InvalidateRequestGeneration(generation);
            Volatile.Write(ref acceptResults, 0);
            var cleanupFailure = await TryStopOnFrameworkThreadAsync();
            cleanupAttempted = true;
            var message = cleanupFailure == null
                ? ex.Message
                : $"{ex.Message} Cleanup also failed: {cleanupFailure}";
            return Failure(transaction, message, requestSubmitted);
        }
        finally
        {
            InvalidateRequestGeneration(generation);
            Volatile.Write(ref acceptResults, 0);
            if (!cleanupAttempted)
                await TryStopOnFrameworkThreadAsync();
            _manager.PurchaseFinished -= Handle;
        }
    }

    private long BeginRequestGeneration()
    {
        lock (_requestGate)
            return ++_requestGeneration;
    }

    private void InvalidateRequestGeneration(long generation)
    {
        lock (_requestGate)
        {
            if (_requestGeneration == generation)
                _requestGeneration++;
        }
    }

    private PurchaseStartResult TryStartPurchase(
        long generation,
        CancellationToken cancellationToken,
        VendorShopEntry entry,
        VendorNpc vendor,
        VendorNpcLocation location,
        uint quantity)
    {
        lock (_requestGate)
        {
            if (_requestGeneration != generation || cancellationToken.IsCancellationRequested)
                return new PurchaseStartResult(false, false);

            _manager.StartPurchase(entry, vendor, location, quantity);
            var running = _manager.IsRunning;
            return new PurchaseStartResult(
                running,
                running
                && _requestGeneration == generation
                && !cancellationToken.IsCancellationRequested);
        }
    }

    private Task StopOnFrameworkThreadAsync(CancellationToken cancellationToken)
        => OnFrameworkThreadAsync(
            () =>
            {
                if (_manager.IsRunning)
                _manager.Stop();
            },
            cancellationToken);

    private async Task<string?> TryStopOnFrameworkThreadAsync()
    {
        try
        {
            await StopOnFrameworkThreadAsync(CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[LiveVendorPurchaseAdapter] Cleanup failed: {ex.Message}");
            return ex.Message;
        }
    }

    private static async Task OnFrameworkThreadAsync(Action action, CancellationToken cancellationToken)
    {
        await OnFrameworkThreadAsync(
            () =>
            {
                action();
                return true;
            },
            cancellationToken);
    }

    private static async Task<T> OnFrameworkThreadAsync<T>(Func<T> callback, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (global::GatherBuddy.Dalamud.Framework.IsInFrameworkUpdateThread)
            return callback();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackState = 0;
        CancellationTokenRegistration cancellationRegistration = default;
        void Run(IFramework _)
        {
            if (Interlocked.Exchange(ref callbackState, 1) != 0)
                return;
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    completion.TrySetCanceled(cancellationToken);
                else
                    completion.TrySetResult(callback());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                global::GatherBuddy.Dalamud.Framework.Update -= Run;
            }
        }

        global::GatherBuddy.Dalamud.Framework.Update += Run;
        if (cancellationToken.CanBeCanceled)
        {
            cancellationRegistration = cancellationToken.Register(() =>
            {
                if (Interlocked.Exchange(ref callbackState, 1) == 0)
                    global::GatherBuddy.Dalamud.Framework.Update -= Run;
                completion.TrySetCanceled(cancellationToken);
            });
        }

        try
        {
            var completed = await Task.WhenAny(completion.Task, Task.Delay(FrameworkDispatchTimeout));
            if (completed != completion.Task)
            {
                if (Interlocked.Exchange(ref callbackState, 1) == 0)
                    global::GatherBuddy.Dalamud.Framework.Update -= Run;
                throw new TimeoutException("Dalamud framework callback did not run before the vendor acquisition timeout.");
            }

            return await completion.Task;
        }
        finally
        {
            cancellationRegistration.Dispose();
        }
    }

    private static VendorCurrencyWalletSnapshot CaptureCurrencySnapshot(
        IReadOnlyList<VendorCurrencyCost> costs)
    {
        if (!VendorCurrencyAvailabilityResolver.TryCaptureAuthoritative(costs, out var snapshot, out var failure))
            throw new InvalidOperationException(failure);
        return snapshot;
    }

    private static ObservedCurrencySpend ObserveCurrencySpend(
        VendorCurrencyWalletSnapshot before,
        VendorCurrencyWalletSnapshot after)
    {
        if (!VendorCurrencyAvailabilityResolver.TryCalculateSpend(before, after, out var spent, out var failure))
        {
            return new ObservedCurrencySpend(
                false,
                new Dictionary<uint, long>(),
                0,
                failure);
        }

        var nonGilSpent = new Dictionary<uint, long>();
        foreach (var (currencyId, amount) in spent)
        {
            if (currencyId != VendorShopResolver.GilCurrencyItemId)
                nonGilSpent[currencyId] = amount;
        }

        return new ObservedCurrencySpend(
            true,
            nonGilSpent,
            spent.GetValueOrDefault(VendorShopResolver.GilCurrencyItemId),
            string.Empty);
    }

    private readonly record struct ObservedCurrencySpend(
        bool IsAuthoritative,
        IReadOnlyDictionary<uint, long> NonGilSpent,
        long GilSpent,
        string FailureReason);

    private static bool TryResolve(
        AcquisitionTransaction transaction,
        out VendorShopEntry entry,
        out VendorNpc vendor,
        out VendorNpcLocation location,
        out string failure)
    {
        entry = null!;
        vendor = null!;
        location = null!;
        failure = string.Empty;
        var entries = VendorShopResolver.GilShopEntries
            .Concat(VendorShopResolver.SpecialShopEntries)
            .Concat(VendorShopResolver.GcShopEntries)
            .Where(candidate => candidate.ItemId == transaction.ItemId)
            .ToList();
        foreach (var candidate in entries)
        {
            var matchedVendor = candidate.Npcs.FirstOrDefault(candidateVendor
                => string.Equals(
                    $"{candidate.TransactionSignature}:{VendorPreferenceHelper.GetRouteKey(candidateVendor)}",
                    transaction.SourceId,
                    StringComparison.Ordinal));
            if (matchedVendor == null)
                continue;

            entry = candidate;
            vendor = matchedVendor;
            break;
        }
        if (entry == null || vendor == null)
        {
            failure = $"Vendor offer for {transaction.ItemName} is no longer available.";
            return false;
        }
        var availability = VendorAvailabilityResolver.Resolve(entry, vendor);
        if (!availability.IsAvailable)
        {
            failure = $"Cannot purchase {transaction.ItemName}: {availability.Reason}";
            return false;
        }
        if (!VendorPurchaseManager.IsPurchaseSupported(entry, vendor))
        {
            failure = $"Vendor automation does not support the selected offer for {transaction.ItemName}.";
            return false;
        }

        location = VendorNpcLocationCache.TryGetFirstLocation(vendor.NpcId)!;
        if (location == null)
        {
            failure = $"No route to vendor {vendor.Name} is available.";
            return false;
        }
        return true;
    }

    private static LiveVendorPurchaseResult Failure(
        AcquisitionTransaction transaction,
        string message,
        bool requestSubmitted = false)
        => new(
            false,
            false,
            transaction.ItemId,
            0,
            new Dictionary<uint, long>(),
            0,
            message,
            requestSubmitted,
            transaction.IsHq);

    private readonly record struct PurchaseStartResult(bool Submitted, bool Running);
}
