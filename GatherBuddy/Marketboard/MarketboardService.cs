using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GatherBuddy.Utility;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Marketboard;

public sealed class MarketboardService : IDisposable
{
    private const int CacheExpiryMinutes = 15;
    private const int MaxHistoryItems    = 100;
    private const string HistoryFile     = "mb_history.json";

    private sealed record PersistedEntry(uint ItemId, string Name, uint IconId);
    private sealed record SearchIndexEntry(uint ItemId, string Name, uint IconId, string NormalizedName);
    private sealed class LookupOperation
    {
        public LookupOperation(CancellationTokenSource cancellation)
        {
            Cancellation = cancellation;
            Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public CancellationTokenSource Cancellation { get; }
        public TaskCompletionSource<bool> Completion { get; }
    }

    private readonly object              _lock        = new();
    private readonly UniversalisService  _universalis = new();
    private readonly UniversalisCache    _sharedCache = new();
    private readonly Func<uint, string, bool, CancellationToken, Task<MarketItemData?>> _marketFetch;
    private readonly bool            _persistentStateEnabled;
    private readonly System.Action?   _onUniversalisDisposed;

    private readonly object              _searchIndexLock = new();
    private IReadOnlyList<SearchIndexEntry>? _searchIndex;
    private string?                      _searchIndexLanguage;

    public MarketboardService()
        : this(null, initializePersistentState: true)
    {
    }

    internal MarketboardService(
        Func<uint, string, bool, CancellationToken, Task<MarketItemData?>>? marketFetch,
        bool initializePersistentState,
        System.Action? onUniversalisDisposed = null)
    {
        _marketFetch = marketFetch ?? ((itemId, scope, canBeHq, cancellationToken)
            => FetchMarketItemAsync(itemId, scope, canBeHq, cancellationToken));
        _persistentStateEnabled = initializePersistentState;
        _onUniversalisDisposed = onUniversalisDisposed;
        if (initializePersistentState)
        {
            LoadHistory();
            EnsureSearchIndex();
        }
    }
    private readonly Dictionary<(uint, string), (MarketItemData Data, DateTime FetchedAt)> _cache    = new();
    private readonly Dictionary<uint, string>                                              _names    = new();
    private readonly Dictionary<uint, uint>                                                _icons    = new();
    private readonly List<uint>                                                            _history  = new();
    private readonly HashSet<(uint, string)>                                               _pending  = new();
    private readonly HashSet<(uint, string)>                                               _errors   = new();
    private readonly Dictionary<(uint, string), int>                                       _generations = new();
    private readonly Dictionary<(uint, string), LookupOperation>                              _lookupOperations = new();
    private readonly HashSet<Task>                                                          _lookupTasks = new();
    private          CancellationTokenSource                                               _cts      = new();
    private bool                                                                            _disposed;

    public UniversalisCache Cache => _sharedCache;

    public bool IsPending(uint itemId, string scope) { lock (_lock) return _pending.Contains((itemId, scope)); }
    public bool HasError(uint itemId,  string scope) { lock (_lock) return _errors.Contains((itemId, scope));  }

    public MarketItemData? GetCached(uint itemId, string scope)
    {
        lock (_lock)
            return _cache.TryGetValue((itemId, scope), out var e) ? e.Data : null;
    }

    public DateTime GetFetchTime(uint itemId, string scope)
    {
        lock (_lock)
            return _cache.TryGetValue((itemId, scope), out var e) ? e.FetchedAt : DateTime.MinValue;
    }

    public string GetItemName(uint itemId)
    {
        lock (_lock)
            return _names.TryGetValue(itemId, out var n) ? n : $"Item #{itemId}";
    }

    public uint GetItemIcon(uint itemId)
    {
        lock (_lock)
            return _icons.TryGetValue(itemId, out var icon) ? icon : 0;
    }

    public List<uint> GetHistorySnapshot()
    {
        lock (_lock) return new List<uint>(_history);
    }

    public List<MarketSearchResult> SearchItems(string query, int limit = 50)
    {
        var normalized = SearchTextNormalizer.Normalize(query);
        if (string.IsNullOrEmpty(normalized) || limit <= 0)
            return new List<MarketSearchResult>();

        var results = new List<MarketSearchResult>();
        try
        {
            foreach (var item in GetSearchIndex())
            {
                var score = FuzzySearch.Score(item.NormalizedName, new[] { normalized });
                if (score.HasValue)
                    results.Add(new MarketSearchResult(item.ItemId, item.Name, item.IconId, score.Value));
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[Marketboard] Item search failed: {ex.Message}");
        }

        return results
            .OrderBy(result => result.Score)
            .ThenBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private IReadOnlyList<SearchIndexEntry> GetSearchIndex()
    {
        EnsureSearchIndex();
        lock (_searchIndexLock)
            return _searchIndex ?? Array.Empty<SearchIndexEntry>();
    }

    private void EnsureSearchIndex()
    {
        var language = Dalamud.ClientState.ClientLanguage.ToString();
        lock (_searchIndexLock)
        {
            if (_searchIndex != null && _searchIndexLanguage == language)
                return;

            try
            {
                var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
                if (itemSheet == null)
                    return;

                var entries = new List<SearchIndexEntry>();
                foreach (var item in itemSheet)
                {
                    if (item.RowId == 0 || item.IsUntradable || item.ItemSearchCategory.RowId == 0)
                        continue;
                    var name = item.Name.ExtractText();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    entries.Add(new SearchIndexEntry(
                        item.RowId,
                        name,
                        (uint)item.Icon,
                        SearchTextNormalizer.Normalize(name)));
                }

                _searchIndex = entries.AsReadOnly();
                _searchIndexLanguage = language;
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"[Marketboard] Item search index build failed: {ex.Message}");
            }
        }
    }

    public void QueueLookup(uint itemId, string itemName, uint iconId = 0)
        => QueueLookup(itemId, itemName, iconId, GetDataCenter());

    public void QueueLookup(uint itemId, string itemName, uint iconId, string scope)
    {
        int generation;
        CancellationToken ct;
        LookupOperation lookupOperation;
        lock (_lock)
        {
            if (_disposed)
                return;
            _names[itemId] = itemName;
            if (iconId > 0) _icons[itemId] = iconId;

            if (_sharedCache.HasRecentError(itemId, scope))
            {
                _errors.Add((itemId, scope));
                return;
            }

            if (_cache.TryGetValue((itemId, scope), out var cached) &&
                (DateTime.UtcNow - cached.FetchedAt).TotalMinutes < CacheExpiryMinutes)
            {
                MoveToFront(itemId);
                return;
            }

            if (_pending.Contains((itemId, scope))) return;

            generation = NextLookupGeneration(itemId, scope);
            _pending.Add((itemId, scope));
            _errors.Remove((itemId, scope));
            MoveToFront(itemId);
            var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            lookupOperation = new LookupOperation(lookupCancellation);
            _lookupOperations[(itemId, scope)] = lookupOperation;
            _lookupTasks.Add(lookupOperation.Completion.Task);
            ct = lookupCancellation.Token;
        }

        var canBeHq = false;
        try
        {
            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            if (itemSheet?.TryGetRow(itemId, out var lumItem) == true)
                canBeHq = lumItem.CanBeHq;
        }
        catch { }

        _ = RunLookupAsync(itemId, itemName, scope, canBeHq, generation, lookupOperation, ct);
    }

    private async Task RunLookupAsync(
        uint itemId,
        string itemName,
        string scope,
        bool canBeHq,
        int generation,
        LookupOperation lookupOperation,
        CancellationToken ct)
    {
        try
        {
            var result = await _sharedCache.GetOrRefreshAsync(
                itemId,
                scope,
                token => _marketFetch(itemId, scope, canBeHq, token),
                ct);
            var data = result.Data;

            lock (_lock)
            {
                if (!IsLookupGenerationCurrent(itemId, scope, generation))
                    return;
                _pending.Remove((itemId, scope));
                if (data != null)
                {
                    data.ItemName = itemName;
                    if (_icons.TryGetValue(itemId, out var icon)) data.IconId = icon;
                    _cache[(itemId, scope)] = (data, result.FetchedAt);
                    if (result.HasError)
                        _errors.Add((itemId, scope));
                    else
                        _errors.Remove((itemId, scope));
                }
                else
                {
                    _errors.Add((itemId, scope));
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                if (IsLookupGenerationCurrent(itemId, scope, generation))
                    _pending.Remove((itemId, scope));
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (!IsLookupGenerationCurrent(itemId, scope, generation))
                    return;
                _pending.Remove((itemId, scope));
                _errors.Add((itemId, scope));
            }
            GatherBuddy.Log.Warning($"[Marketboard] Lookup failed for {itemName} ({itemId}): {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                if (_lookupOperations.TryGetValue((itemId, scope), out var current)
                    && ReferenceEquals(current, lookupOperation))
                {
                    _lookupOperations.Remove((itemId, scope));
                    _lookupTasks.Remove(lookupOperation.Completion.Task);
                }
            }
            lookupOperation.Cancellation.Dispose();
            lookupOperation.Completion.TrySetResult(true);
        }
    }

    private int NextLookupGeneration(uint itemId, string scope)
    {
        var key = (itemId, scope);
        var generation = _generations.TryGetValue(key, out var previous) ? previous + 1 : 1;
        _generations[key] = generation;
        return generation;
    }

    private bool IsLookupGenerationCurrent(uint itemId, string scope, int generation)
        => !_disposed
        && _generations.TryGetValue((itemId, scope), out var current)
        && current == generation;

    private async Task<MarketItemData?> FetchMarketItemAsync(uint itemId, string scope, bool canBeHq, CancellationToken ct)
    {
        if (!canBeHq)
            return (await _universalis.GetMarketDataAsync(scope, new[] { itemId }, 20, ct)).FirstOrDefault();

        var nqRes = await _universalis.GetMarketDataAsync(scope, new[] { itemId }, 10, ct, false);
        await Task.Delay(300, ct);
        var hqRes = await _universalis.GetMarketDataAsync(scope, new[] { itemId }, 10, ct, true);
        var nqData = nqRes.FirstOrDefault(result => result.ItemId == itemId);
        var hqData = hqRes.FirstOrDefault(result => result.ItemId == itemId);
        var baseData = nqData ?? hqData;
        return baseData == null
            ? null
            : new MarketItemData
            {
                ItemId = baseData.ItemId,
                MinPrice = baseData.MinPrice,
                Listings = (nqData?.Listings ?? new()).Concat(hqData?.Listings ?? new()).ToList(),
            };
    }

    public void ForceRefresh(uint itemId, string scope)
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _cache.Remove((itemId, scope));
            _errors.Remove((itemId, scope));
            var key = (itemId, scope);
            if (_lookupOperations.TryGetValue(key, out var previousOperation))
            {
                _pending.Remove(key);
                NextLookupGeneration(itemId, scope);
                previousOperation.Cancellation.Cancel();
            }
        }

        _sharedCache.Invalidate(itemId, scope);
        var name = GetItemName(itemId);
        var icon = GetItemIcon(itemId);
        QueueLookup(itemId, name, icon, scope);
    }

    public void RefreshAll()
        => RefreshAll(GetDataCenter());

    public void RefreshAll(string scope)
    {
        List<uint> ids;

        lock (_lock)
        {
            ids   = new List<uint>(_history);
        }

        foreach (var id in ids)
            ForceRefresh(id, scope);
    }

    public void RemoveFromHistory(uint itemId)
    {
        lock (_lock)
        {
            _history.Remove(itemId);
            _names.Remove(itemId);
            _icons.Remove(itemId);
            var pendingKeys = _pending.Where(k => k.Item1 == itemId).ToList();
            foreach (var k in pendingKeys)
                if (_lookupOperations.TryGetValue(k, out var operation))
                    operation.Cancellation.Cancel();
            var cacheKeys = _cache.Keys.Where(k => k.Item1 == itemId).ToList();
            foreach (var k in cacheKeys)
            {
                _cache.Remove(k);
                _pending.Remove(k);
                _generations[k] = _generations.TryGetValue(k, out var generation) ? generation + 1 : 1;
            }
            var errKeys = _errors.Where(k => k.Item1 == itemId).ToList();
            foreach (var k in errKeys) _errors.Remove(k);
            foreach (var k in pendingKeys)
            {
                _pending.Remove(k);
                _generations[k] = _generations.TryGetValue(k, out var generation) ? generation + 1 : 1;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _history.Clear();
            _errors.Clear();
            foreach (var operation in _lookupOperations.Values)
                operation.Cancellation.Cancel();
            _pending.Clear();
            foreach (var key in _generations.Keys.ToList())
                _generations[key]++;
        }
        _sharedCache.Clear();
    }

    public List<string> GetOtherDcs()
    {
        var result = new List<string>();
        var homeDc = GetDataCenter();
        try
        {
            var dcSheet = Dalamud.GameData.GetExcelSheet<WorldDCGroupType>();
            if (dcSheet == null) return result;
            foreach (var dc in dcSheet)
            {
                if (dc.IsCloud) continue;
                if (dc.Region.RowId < 1 || dc.Region.RowId > 4) continue;
                var name = dc.Name.ExtractText();
                if (!string.IsNullOrEmpty(name) && name != homeDc)
                    result.Add(name);
            }
            result.Sort();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[Marketboard] Other DCs query failed: {ex.Message}");
        }
        return result;
    }

    public List<string> GetDcWorlds()
    {
        var result = new List<string>();
        try
        {
            var worldId = GetCurrentOrHomeWorldId();
            if (worldId == 0) return result;

            var worldSheet = Dalamud.GameData.GetExcelSheet<World>();
            if (worldSheet == null) return result;
            if (!worldSheet.TryGetRow(worldId, out var homeWorld)) return result;

            var dcId = homeWorld.DataCenter.RowId;
            foreach (var world in worldSheet)
            {
                if (world.DataCenter.RowId == dcId && world.IsPublic)
                {
                    var name = world.Name.ExtractText();
                    if (!string.IsNullOrEmpty(name))
                        result.Add(name);
                }
            }
            result.Sort();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[Marketboard] DC world list failed: {ex.Message}");
        }
        return result;
    }

    public string GetDataCenter()
    {
        try
        {
            var worldId = GetCurrentOrHomeWorldId();
            if (worldId == 0) return "Aether";
            var worldSheet = Dalamud.GameData.GetExcelSheet<World>();
            if (worldSheet?.TryGetRow(worldId, out var world) == true)
            {
                var dc = world.DataCenter.ValueNullable?.Name.ExtractText();
                if (!string.IsNullOrEmpty(dc)) return dc;
            }
            return "Aether";
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[Marketboard] DC resolution failed: {ex.Message}");
            return "Aether";
        }
    }

    public string GetCurrentWorld()
    {
        try
        {
            var player = Dalamud.Objects.LocalPlayer;
            var current = player?.CurrentWorld.ValueNullable?.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(current))
                return current;
            var home = player?.HomeWorld.ValueNullable?.Name.ExtractText();
            return string.IsNullOrWhiteSpace(home) ? GetDataCenter() : home;
        }
        catch
        {
            return GetDataCenter();
        }
    }

    private static uint GetCurrentOrHomeWorldId()
    {
        var player = Dalamud.Objects.LocalPlayer;
        var current = player?.CurrentWorld.RowId ?? 0u;
        return current != 0 ? current : player?.HomeWorld.RowId ?? 0u;
    }

    private void MoveToFront(uint itemId)
    {
        _history.Remove(itemId);
        _history.Insert(0, itemId);
        if (_history.Count > MaxHistoryItems)
            _history.RemoveAt(_history.Count - 1);
    }

    private void LoadHistory()
    {
        try
        {
            var path = Path.Combine(Dalamud.PluginInterface.GetPluginConfigDirectory(), HistoryFile);
            if (!File.Exists(path)) return;
            var entries = JsonSerializer.Deserialize<List<PersistedEntry>>(File.ReadAllText(path));
            if (entries == null) return;
            lock (_lock)
            {
                foreach (var e in entries)
                {
                    if (e.ItemId == 0) continue;
                    _names[e.ItemId] = e.Name;
                    if (e.IconId > 0) _icons[e.ItemId] = e.IconId;
                    if (!_history.Contains(e.ItemId)) _history.Add(e.ItemId);
                }
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[Marketboard] Failed to load history: {ex.Message}");
        }
    }

    private void SaveHistory()
    {
        if (!_persistentStateEnabled)
            return;
        try
        {
            List<PersistedEntry> entries;
            lock (_lock)
            {
                entries = _history
                    .Select(id => new PersistedEntry(
                        id,
                        _names.TryGetValue(id, out var n) ? n : string.Empty,
                        _icons.TryGetValue(id, out var ic) ? ic : 0u))
                    .ToList();
            }
            var path = Path.Combine(Dalamud.PluginInterface.GetPluginConfigDirectory(), HistoryFile);
            File.WriteAllText(path, JsonSerializer.Serialize(entries));
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[Marketboard] Failed to save history: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Task[] lookupTasks;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            foreach (var operation in _lookupOperations.Values)
                operation.Cancellation.Cancel();
            lookupTasks = _lookupTasks.ToArray();
        }
        SaveHistory();
        var cacheShutdown = _sharedCache.ShutdownAsync();
        _ = FinishDisposeAsync(lookupTasks, cacheShutdown);
    }

    private async Task FinishDisposeAsync(Task[] lookupTasks, Task cacheShutdown)
    {
        try
        {
            await cacheShutdown.ConfigureAwait(false);
            await Task.WhenAll(lookupTasks).ConfigureAwait(false);
        }
        catch
        {
            // Lookup tasks convert expected cancellation/fetch failures into
            // state. Disposal remains best effort for unexpected failures.
        }
        finally
        {
            _cts.Dispose();
            _universalis.Dispose();
            _onUniversalisDisposed?.Invoke();
        }
    }
}
