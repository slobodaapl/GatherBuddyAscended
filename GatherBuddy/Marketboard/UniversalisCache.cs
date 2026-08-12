using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GatherBuddy.Marketboard;

public sealed record MarketCacheResult(
    MarketItemData? Data,
    DateTime FetchedAt,
    bool IsFresh,
    bool IsStale,
    bool HasError,
    Exception? Error);

/// <summary>
/// Shared item/scope cache for Universalis requests. Expired data remains
/// readable while one refresh is in flight, so UI and planning code never need
/// to blank a previously known market estimate.
/// </summary>
public sealed class UniversalisCache : IDisposable
{
    public static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(30);

    private sealed class Entry
    {
        public MarketItemData? Data;
        public DateTime FetchedAt = DateTime.MinValue;
        public DateTime ErrorUntil = DateTime.MinValue;
        public Exception? LastError;
        public TaskCompletionSource<MarketCacheResult>? Inflight;
        public CancellationTokenSource? RefreshCancellation;
        public long Generation;
    }

    private readonly object _lock = new();
    private readonly Dictionary<(string Scope, uint ItemId), Entry> _entries = new();
    private readonly HashSet<Task> _refreshTasks = new();
    private readonly Func<DateTime> _utcNow;
    private readonly CancellationTokenSource _disposeCts = new();
    private TaskCompletionSource<bool>? _shutdownCompletion;
    private bool _disposed;

    public UniversalisCache(Func<DateTime>? utcNow = null)
        => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    public MarketItemData? GetCached(uint itemId, string scope)
    {
        lock (_lock)
            return _entries.TryGetValue((scope, itemId), out var entry) ? entry.Data : null;
    }

    public DateTime GetFetchTime(uint itemId, string scope)
    {
        lock (_lock)
            return _entries.TryGetValue((scope, itemId), out var entry) ? entry.FetchedAt : DateTime.MinValue;
    }

    public MarketCacheResult GetStatus(uint itemId, string scope)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue((scope, itemId), out var entry))
                return new MarketCacheResult(null, DateTime.MinValue, false, false, false, null);
            var now = _utcNow();
            var failed = entry.ErrorUntil > now;
            var fresh = !failed && entry.Data != null && now - entry.FetchedAt < PositiveTtl;
            return CreateResult(entry, fresh, failed, entry.LastError);
        }
    }

    public bool IsPending(uint itemId, string scope)
    {
        lock (_lock)
            return _entries.TryGetValue((scope, itemId), out var entry) && entry.Inflight != null;
    }

    public bool HasRecentError(uint itemId, string scope)
    {
        lock (_lock)
            return _entries.TryGetValue((scope, itemId), out var entry) && entry.ErrorUntil > _utcNow();
    }

    public bool IsFresh(uint itemId, string scope)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue((scope, itemId), out var entry) || entry.Data == null)
                return false;
            var now = _utcNow();
            return entry.ErrorUntil <= now && now - entry.FetchedAt < PositiveTtl;
        }
    }

    public Task<MarketCacheResult> GetOrRefreshAsync(
        uint itemId,
        string scope,
        Func<CancellationToken, Task<MarketItemData?>> fetch,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        cancellationToken.ThrowIfCancellationRequested();
        Task<MarketCacheResult> task;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = (scope, itemId);
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _entries.Add(key, entry);
            }

            var now = _utcNow();
            if (!forceRefresh && entry.ErrorUntil > now && entry.Inflight == null)
            {
                return WaitWithCancellation(
                    Task.FromResult(CreateResult(entry, isFresh: false, hasError: true, entry.LastError)),
                    cancellationToken);
            }

            if (!forceRefresh && entry.Data != null && now - entry.FetchedAt < PositiveTtl)
            {
                return WaitWithCancellation(
                    Task.FromResult(CreateResult(entry, isFresh: true, hasError: false, null)),
                    cancellationToken);
            }

            if (forceRefresh)
                InvalidateEntry(entry);

            if (entry.Inflight != null)
                return WaitWithCancellation(entry.Inflight.Task, cancellationToken);

            var completion = new TaskCompletionSource<MarketCacheResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            var generation = ++entry.Generation;
            entry.Inflight = completion;
            entry.RefreshCancellation = refreshCancellation;
            task = completion.Task;
            var refreshTask = RefreshAsync(
                key,
                entry,
                fetch,
                completion,
                refreshCancellation,
                generation);
            _refreshTasks.Add(refreshTask);
            _ = ObserveRefreshAsync(refreshTask);
        }

        return WaitWithCancellation(task, cancellationToken);
    }

    public void Invalidate(uint itemId, string scope)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue((scope, itemId), out var entry))
                InvalidateEntry(entry);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var entry in _entries.Values)
                InvalidateEntry(entry);
            _entries.Clear();
        }
    }

    private async Task RefreshAsync(
        (string Scope, uint ItemId) key,
        Entry entry,
        Func<CancellationToken, Task<MarketItemData?>> fetch,
        TaskCompletionSource<MarketCacheResult> completion,
        CancellationTokenSource refreshCancellation,
        long generation)
    {
        MarketItemData? data = null;
        Exception? error = null;
        var refreshToken = refreshCancellation.Token;
        try
        {
            data = await fetch(refreshToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (refreshToken.IsCancellationRequested)
        {
            error = new TaskCanceledException();
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (error == null && data == null)
            error = new InvalidOperationException("Universalis returned no market data");

        lock (_lock)
        {
            var now = _utcNow();
            var isCurrent = _entries.TryGetValue(key, out var currentEntry)
                && ReferenceEquals(currentEntry, entry)
                && entry.Generation == generation
                && ReferenceEquals(entry.Inflight, completion);
            if (!isCurrent)
            {
                completion.TrySetCanceled(refreshToken);
            }
            else
            {
                entry.Inflight = null;
                entry.RefreshCancellation = null;
                if (error == null && data != null)
                {
                    entry.Data = data;
                    entry.FetchedAt = now;
                    entry.ErrorUntil = DateTime.MinValue;
                    entry.LastError = null;
                }
                else if (error is not TaskCanceledException)
                {
                    // Keep Data and FetchedAt untouched. A stale result must not
                    // become fresh merely because its refresh failed.
                    entry.ErrorUntil = now + ErrorBackoff;
                    entry.LastError = error;
                }

                if (error is TaskCanceledException)
                    completion.TrySetCanceled(refreshToken);
                else
                    completion.TrySetResult(CreateResult(entry, error == null && data != null, error != null, error));
            }
        }
        refreshCancellation.Dispose();
    }

    private static void InvalidateEntry(Entry entry)
    {
        entry.Generation++;
        entry.FetchedAt = DateTime.MinValue;
        entry.ErrorUntil = DateTime.MinValue;
        entry.LastError = null;
        var completion = entry.Inflight;
        var refreshCancellation = entry.RefreshCancellation;
        entry.Inflight = null;
        entry.RefreshCancellation = null;
        if (refreshCancellation != null)
        {
            refreshCancellation.Cancel();
            completion?.TrySetCanceled(refreshCancellation.Token);
        }
        else
        {
            completion?.TrySetCanceled();
        }
    }

    private async Task ObserveRefreshAsync(Task refreshTask)
    {
        try
        {
            await refreshTask.ConfigureAwait(false);
        }
        catch
        {
            // RefreshAsync converts fetch failures into cache results. This
            // guard covers only unexpected implementation failures and keeps
            // the detached observer from becoming unobserved.
        }
        finally
        {
            lock (_lock) _refreshTasks.Remove(refreshTask);
        }
    }

    private static MarketCacheResult CreateResult(Entry entry, bool isFresh, bool hasError, Exception? error)
        => new(entry.Data, entry.FetchedAt, isFresh, entry.Data != null && !isFresh, hasError, error ?? entry.LastError);

    private static async Task<T> WaitWithCancellation<T>(Task<T> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
            return await task.ConfigureAwait(false);
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ShutdownAsync()
    {
        Task[] refreshTasks;
        TaskCompletionSource<bool> shutdownCompletion;
        lock (_lock)
        {
            if (_shutdownCompletion != null)
                return _shutdownCompletion.Task;

            _disposed = true;
            shutdownCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownCompletion = shutdownCompletion;
            refreshTasks = _refreshTasks.ToArray();
            foreach (var entry in _entries.Values)
                InvalidateEntry(entry);
            _entries.Clear();
        }

        try
        {
            _disposeCts.Cancel();
        }
        catch
        {
            // A cancellation callback must not prevent the cache from
            // awaiting and completing its shutdown contract.
        }

        _ = FinishDisposeAsync(refreshTasks, shutdownCompletion);
        return shutdownCompletion.Task;
    }

    public void Dispose()
        => _ = ShutdownAsync();

    private async Task FinishDisposeAsync(
        Task[] refreshTasks,
        TaskCompletionSource<bool> shutdownCompletion)
    {
        try
        {
            await Task.WhenAll(refreshTasks).ConfigureAwait(false);
        }
        catch
        {
            // Refresh tasks are observed and disposal must remain best effort.
        }
        finally
        {
            _disposeCts.Dispose();
            shutdownCompletion.TrySetResult(true);
        }
    }
}
