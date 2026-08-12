using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GatherBuddy.Config;
using GatherBuddy.Marketboard;

namespace GatherBuddy.Vulcan.Tests;

public static class MarketplaceAcceptanceTests
{
    public static async Task Run(Action<bool, string> require)
    {
        const string universalisFixture = """
        {
          "items": {
            "123": {
              "itemID": "123",
              "minPrice": 345,
              "listings": [
                {
                  "listingID": "18446744073709551614",
                  "retainerID": 9223372036854775808,
                  "pricePerUnit": "345",
                  "quantity": 10,
                  "hq": false,
                  "tax": "2147483648",
                  "onMannequin": true,
                  "worldID": "74",
                  "worldName": "Faerie",
                  "retainerCity": 2
                }
              ]
            }
          }
        }
        """;
        var parsedFixture = UniversalisService.ParseMarketResponse(universalisFixture);
        require(parsedFixture.Count == 1
                && parsedFixture[0].ItemId == 123
                && parsedFixture[0].Listings.Count == 1,
            "real Universalis response shape must parse item and listing records");
        var parsedListing = parsedFixture[0].Listings[0];
        require(parsedListing.ListingId == 18446744073709551614UL
                && parsedListing.RetainerId == 9223372036854775808UL
                && parsedListing.TotalTax == 2147483648L
                && parsedListing.WorldId == 74
                && parsedListing.IsMannequin == true
                && parsedListing.IsSellingAsSet == null,
            "Universalis listing IDs, tax, mannequin state, and unknown set-sale metadata must be preserved");

        var now = DateTime.UtcNow;
        var fetches = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cache = new UniversalisCache(() => now);

        var first = cache.GetOrRefreshAsync(1, "Aether", async _ =>
        {
            Interlocked.Increment(ref fetches);
            await gate.Task;
            return new MarketItemData { ItemId = 1, MinPrice = 12 };
        });
        var coalesced = cache.GetOrRefreshAsync(1, "Aether", _ =>
            Task.FromResult<MarketItemData?>(new MarketItemData { ItemId = 1, MinPrice = 99 }));
        require(fetches == 1, "concurrent identical Universalis lookups must share one fetch");
        gate.SetResult(true);
        var firstResult = await first;
        var coalescedResult = await coalesced;
        require(firstResult.Data?.MinPrice == 12 && coalescedResult.Data?.MinPrice == 12,
            "coalesced lookup must retain the first completed market result");
        require(firstResult.IsFresh && !firstResult.IsStale && !firstResult.HasError,
            "successful cache result must be marked fresh");

        var forcedRefresh = await cache.GetOrRefreshAsync(1, "Aether", _ =>
            Task.FromException<MarketItemData?>(new InvalidOperationException("forced refresh outage")),
            forceRefresh: true);
        require(forcedRefresh.Data?.MinPrice == 12
                && forcedRefresh.IsStale
                && forcedRefresh.HasError,
            "forced refresh failure must take precedence over positive TTL and return stale error state");
        var normalAfterForcedFailure = await cache.GetOrRefreshAsync(1, "Aether", _ =>
            Task.FromResult<MarketItemData?>(new MarketItemData { ItemId = 1, MinPrice = 99 }));
        require(normalAfterForcedFailure.Data?.MinPrice == 12
                && normalAfterForcedFailure.IsStale
                && normalAfterForcedFailure.HasError,
            "normal lookup must honor error backoff after forced refresh failure");

        now += UniversalisCache.PositiveTtl + TimeSpan.FromSeconds(1);
        var refresh = await cache.GetOrRefreshAsync(1, "Aether", _ =>
        {
            Interlocked.Increment(ref fetches);
            return Task.FromResult<MarketItemData?>(new MarketItemData { ItemId = 1, MinPrice = 14 });
        });
        require(fetches == 2 && refresh.Data?.MinPrice == 14,
            "a post-TTL refresh must not reuse the old value or count the in-flight fetch twice");
        require(refresh.IsFresh && !refresh.IsStale,
            "successful post-TTL refresh must be fresh");

        now += UniversalisCache.PositiveTtl + TimeSpan.FromSeconds(1);
        var staleError = await cache.GetOrRefreshAsync(1, "Aether", _ =>
            Task.FromException<MarketItemData?>(new InvalidOperationException("synthetic Universalis outage")));
        require(staleError.Data?.MinPrice == 14 && staleError.IsStale && staleError.HasError,
            "failed refresh must return stale data with an error instead of making stale data fresh");
        require(cache.GetFetchTime(1, "Aether") == staleError.FetchedAt,
            "failed refresh must preserve the original successful fetch timestamp");
        var retryDuringBackoff = await cache.GetOrRefreshAsync(1, "Aether", _ =>
            Task.FromResult<MarketItemData?>(new MarketItemData { ItemId = 1, MinPrice = 20 }));
        require(retryDuringBackoff.Data?.MinPrice == 14 && retryDuringBackoff.HasError,
            "error backoff must retain stale data without issuing an immediate retry");

        now += UniversalisCache.ErrorBackoff + TimeSpan.FromSeconds(1);
        var recovered = await cache.GetOrRefreshAsync(1, "Aether", _ =>
            Task.FromResult<MarketItemData?>(new MarketItemData { ItemId = 1, MinPrice = 20 }));
        require(recovered.Data?.MinPrice == 20 && recovered.IsFresh && !recovered.HasError,
            "lookup must retry and recover after error backoff expires");

        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellationObserved = false;
        try
        {
            await cache.GetOrRefreshAsync(99, "Aether", async token =>
            {
                token.ThrowIfCancellationRequested();
                return new MarketItemData { ItemId = 99 };
            }, cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        require(cancellationObserved, "caller cancellation must cancel its cache wait");
        require(!cache.IsPending(99, "Aether"), "cancelled lookup must not remain pending");

        var scopeA = await cache.GetOrRefreshAsync(7, "Aether", _ =>
            Task.FromResult<MarketItemData?>(new MarketItemData { ItemId = 7, MinPrice = 1 }));
        var scopeB = await cache.GetOrRefreshAsync(7, "Primal", _ =>
            Task.FromResult<MarketItemData?>(new MarketItemData { ItemId = 7, MinPrice = 2 }));
        require(scopeA.Data?.MinPrice == 1 && scopeB.Data?.MinPrice == 2,
            "world and data-center scopes must have independent cache entries");

        var inflightStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inflightGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var inflightCancellation = new CancellationTokenSource())
        {
            var inflightWait = cache.GetOrRefreshAsync(8, "Aether", async _ =>
            {
                inflightStarted.SetResult(true);
                await inflightGate.Task;
                return new MarketItemData { ItemId = 8, MinPrice = 8 };
            }, inflightCancellation.Token);
            await inflightStarted.Task;
            inflightCancellation.Cancel();
            var callerCancellationObserved = false;
            try { await inflightWait; }
            catch (OperationCanceledException) { callerCancellationObserved = true; }
            require(callerCancellationObserved && cache.IsPending(8, "Aether"),
                "cancelling an in-flight caller must not orphan the shared refresh");
            inflightGate.SetResult(true);
            var recoveredInflight = await cache.GetOrRefreshAsync(8, "Aether", _ =>
                Task.FromResult<MarketItemData?>(new MarketItemData { ItemId = 8, MinPrice = 99 }));
            require(recoveredInflight.Data?.MinPrice == 8 && !cache.IsPending(8, "Aether"),
                "the shared refresh must finish safely after its first caller cancels");
        }

        var forcedRefreshStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forcedRefreshCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRefresh = cache.GetOrRefreshAsync(9, "Aether", async token =>
        {
            forcedRefreshStarted.SetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new MarketItemData { ItemId = 9, MinPrice = 9 };
            }
            catch (OperationCanceledException)
            {
                forcedRefreshCancelled.TrySetResult(true);
                throw;
            }
        });
        await forcedRefreshStarted.Task;
        var replacementRefresh = cache.GetOrRefreshAsync(9, "Aether", _ =>
            Task.FromResult<MarketItemData?>(new MarketItemData { ItemId = 9, MinPrice = 99 }),
            forceRefresh: true);
        var oldRefreshCancelled = false;
        try { await oldRefresh; }
        catch (OperationCanceledException) { oldRefreshCancelled = true; }
        var replacementResult = await replacementRefresh;
        await forcedRefreshCancelled.Task;
        require(oldRefreshCancelled
                && replacementResult.Data?.MinPrice == 99
                && !cache.IsPending(9, "Aether"),
            "forced refresh must cancel pending shared work and prevent its stale result from repopulating the cache");

        var removalStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removalFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removalGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var service = new MarketboardService(async (itemId, _, _, _) =>
        {
            removalStarted.SetResult(true);
            try
            {
                await removalGate.Task;
                return new MarketItemData { ItemId = itemId, MinPrice = 11 };
            }
            finally
            {
                removalFinished.SetResult(true);
            }
        }, initializePersistentState: false))
        {
            service.QueueLookup(11, "Removal Fixture", 0, "Aether");
            await removalStarted.Task;
            service.RemoveFromHistory(11);
            removalGate.SetResult(true);
            await removalFinished.Task;
            require(!service.GetHistorySnapshot().Contains(11)
                    && !service.IsPending(11, "Aether")
                    && service.GetCached(11, "Aether") == null,
                "removed history entries must ignore an already-started lookup completing later");
        }

        var disposeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeFetchFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependencyDisposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependencyDisposedAfterFetch = false;
        var disposableService = new MarketboardService(async (itemId, _, _, token) =>
        {
            disposeStarted.SetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new MarketItemData { ItemId = itemId };
            }
            catch (OperationCanceledException)
            {
                disposeCancelled.SetResult(true);
                throw;
            }
            finally
            {
                disposeFetchFinished.SetResult(true);
            }
        }, initializePersistentState: false, onUniversalisDisposed: () =>
        {
            dependencyDisposedAfterFetch = disposeFetchFinished.Task.IsCompleted;
            dependencyDisposed.SetResult(true);
        });
        disposableService.QueueLookup(12, "Dispose Fixture", 0, "Aether");
        await disposeStarted.Task;
        disposableService.Dispose();
        await disposeCancelled.Task;
        await disposeFetchFinished.Task;
        await dependencyDisposed.Task;
        require(dependencyDisposedAfterFetch,
            "marketboard dependencies must be disposed only after shared refresh fetches finish");

        var config = new Configuration
        {
            Version = 17,
            MarketplaceBuyLists = new List<MarketplaceBuyListDefinition>(),
        };
        require(config.EnsureMarketplaceBuyListState() && config.MarketplaceBuyLists.Count == 1,
            "marketplace migration must create a Default list for old configurations");
        var manager = new MarketplaceBuyListManager(config);
        var list = manager.ActiveList!;
        require(manager.AddItem(list.Id, 42, "Test Item", 1, 3)
                && manager.AddItem(list.Id, 42, "Test Item", 1, 2)
                && list.Entries.Count == 1
                && list.Entries[0].TargetQuantity == 5,
            "marketplace list additions must merge inventory targets");
        var managed = manager.CreateManagedList();
        require(manager.AddItem(managed, 42, "Test Item", 1, 1)
                && !config.MarketplaceBuyLists.Contains(managed),
            "managed marketplace lists must remain transient");
        require(manager.CreateList("Second") != null && manager.DeleteList(list.Id)
                && manager.ActiveList != null,
            "persistent lists must support deletion while retaining an active list");
    }
}
