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
    private static readonly TimeSpan NavigationPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan VendorExitRetryDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan VendorExitTimeout = TimeSpan.FromSeconds(5);

    private readonly VendorPurchaseManager _manager;
    private readonly object _requestGate = new();
    private long _requestGeneration;

    public LiveVendorPurchaseAdapter(VendorPurchaseManager manager)
        => _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    public async Task<LiveVendorPurchaseResult> PurchaseAsync(
        AcquisitionTransaction transaction,
        TimeSpan navigationTimeout,
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
            var lastNavigationProgressAt = DateTime.UtcNow;
            var navigationProgress = await OnFrameworkThreadAsync(
                GatherBuddy.VendorNavigator.CaptureProgressSnapshot,
                cancellationToken);
            while (!completion.Task.IsCompleted && _manager.IsNavigating)
            {
                var currentProgress = await OnFrameworkThreadAsync(
                    GatherBuddy.VendorNavigator.CaptureProgressSnapshot,
                    cancellationToken);
                if (VendorNavigator.HasNavigationProgress(navigationProgress, currentProgress))
                    lastNavigationProgressAt = DateTime.UtcNow;
                navigationProgress = currentProgress;
                if (DateTime.UtcNow - lastNavigationProgressAt >= navigationTimeout)
                {
                    await StopOnFrameworkThreadAsync(cancellationToken);
                    cleanupAttempted = true;
                    return Failure(
                        transaction,
                        $"Vendor navigation made no progress for {navigationTimeout.TotalSeconds:N0} seconds while acquiring {transaction.ItemName}.");
                }

                var finished = await Task.WhenAny(
                    completion.Task,
                    Task.Delay(NavigationPollInterval, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                if (finished == completion.Task)
                    break;
            }

            // VendorPurchaseManager owns bounded shop-open, confirmation, and
            // inventory-verification states. Do not apply the travel watchdog
            // after navigation has completed.
            var result = await completion.Task.WaitAsync(cancellationToken);
            var exitFailure = await ExitVendorInteractionAsync(cancellationToken);
            var nonGilSpent = result.CurrencySpent
                .Where(pair => pair.Key != VendorShopResolver.GilCurrencyItemId)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var gilSpent = result.CurrencySpent.GetValueOrDefault(VendorShopResolver.GilCurrencyItemId);
            var completedQuantity = checked((int)result.CompletedQuantity);
            var verified = result.State == VendorPurchaseManager.CompletionState.Completed
                && completedQuantity >= transaction.Quantity;
            var purchaseResult = new LiveVendorPurchaseResult(
                result.State is not VendorPurchaseManager.CompletionState.Failed
                    and not VendorPurchaseManager.CompletionState.Cancelled
                    and not VendorPurchaseManager.CompletionState.Skipped,
                verified,
                transaction.ItemId,
                completedQuantity,
                nonGilSpent,
                gilSpent,
                exitFailure == null
                    ? result.Message
                    : $"{result.Message} {exitFailure}",
                RequestSubmitted: requestSubmitted,
                IsHq: transaction.IsHq)
            {
                InteractionClosed = exitFailure == null,
                CurrencySpendIsAuthoritative = true,
                OutputQuantities = result.OutputQuantities,
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

    private static async Task<string?> ExitVendorInteractionAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var lastBlocker = string.Empty;
        while (true)
        {
            lastBlocker = await OnFrameworkThreadAsync(
                VendorInteractionHelper.GetVendorExitBlocker,
                cancellationToken) ?? string.Empty;
            if (lastBlocker.Length == 0)
                return null;

            await OnFrameworkThreadAsync(
                () => VendorInteractionHelper.TryExitVendorInteraction(),
                cancellationToken);
            if (DateTime.UtcNow - startedAt >= VendorExitTimeout)
                return $"Could not leave the vendor interaction: {lastBlocker}.";
            await Task.Delay(VendorExitRetryDelay, cancellationToken);
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
