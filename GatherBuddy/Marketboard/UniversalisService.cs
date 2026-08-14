using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GatherBuddy.Marketboard;

public sealed class UniversalisService : IDisposable
{
    private const string BaseUrl            = "https://universalis.app/api/v2";
    private const int    RequestTimeoutMs   = 15000;
    private const int    MaxItemsPerBatch   = 10;
    private const int    InterBatchDelayMs  = 500;
    private const int    MaxRetries         = 2;
    private const int    RetryDelayMs       = 2000;
    private const int    MaxResponseSizeBytes = 1024 * 1024;

    private readonly HttpClient      _http;
    private readonly SemaphoreSlim    _throttle = new(3, 3);

    public UniversalisService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(RequestTimeoutMs) };
        _http.DefaultRequestHeaders.Add("User-Agent", "GatherBuddyAscended-Vulcan");
    }

    public async Task<List<MarketItemData>> GetMarketDataAsync(
        string worldOrDc, IReadOnlyList<uint> itemIds, int listingCount = 20, CancellationToken ct = default, bool? hqFilter = null)
    {
        if (itemIds.Count == 0) return new();

        var results = new List<MarketItemData>();

        for (var i = 0; i < itemIds.Count; i += MaxItemsPerBatch)
        {
            ct.ThrowIfCancellationRequested();

            if (i > 0)
                await Task.Delay(InterBatchDelayMs, ct);

            var end = Math.Min(i + MaxItemsPerBatch, itemIds.Count);
            var sb  = new System.Text.StringBuilder((end - i) * 8);
            for (var j = i; j < end; j++)
            {
                if (j > i) sb.Append(',');
                sb.Append(itemIds[j]);
            }

            var hqParam = hqFilter.HasValue ? $"&hq={(hqFilter.Value ? 1 : 0)}" : string.Empty;
            var url     = string.Concat(BaseUrl, "/", worldOrDc, "/", sb.ToString(), $"?listings={listingCount}&entries=0{hqParam}");
            var success = false;

            for (var attempt = 0; attempt <= MaxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    var delay = RetryDelayMs * attempt;
                    await Task.Delay(delay, ct);
                }

                var countBefore = results.Count;
                var (json, statusCode) = await FetchWithStatusAsync(url, ct);
                if (json != null)
                {
                    ParseMarketResponse(json, results);
                    success = true;
                    break;
                }

                GatherBuddy.Log.Warning(
                    $"[Marketboard] Batch {i / MaxItemsPerBatch} attempt {attempt}: HTTP {statusCode}. IDs: {sb}");

                if (statusCode == 404) break;
            }

            if (!success)
            {
                for (var j = i; j < end; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(InterBatchDelayMs, ct);

                    var singleUrl = string.Concat(BaseUrl, "/", worldOrDc, "/", itemIds[j].ToString(), $"?listings={listingCount}&entries=0{hqParam}");
                    var (singleJson, _) = await FetchWithStatusAsync(singleUrl, ct);
                    if (singleJson != null)
                    {
                        ParseMarketResponse(singleJson, results);
                    }
                }
            }
        }

        return results;
    }

    private async Task<(string? Json, int StatusCode)> FetchWithStatusAsync(string url, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            using var response = await _http.GetAsync(url, ct);
            var statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode) return (null, statusCode);

            if (response.Content.Headers.ContentLength > MaxResponseSizeBytes)
                return (null, statusCode);

            var json = await response.Content.ReadAsStringAsync(ct);
            return json.Length > MaxResponseSizeBytes ? (null, statusCode) : (json, statusCode);
        }
        catch (TaskCanceledException)
        {
            GatherBuddy.Log.Warning($"[Marketboard] Request timed out: {url}");
            return (null, 408);
        }
        catch (HttpRequestException ex)
        {
            GatherBuddy.Log.Warning($"[Marketboard] Request failed: {url} — {ex.Message}");
            return (null, 0);
        }
        finally
        {
            _throttle.Release();
        }
    }

    internal static List<MarketItemData> ParseMarketResponse(string json)
    {
        var results = new List<MarketItemData>();
        ParseMarketResponse(json, results);
        return results;
    }

    private static void ParseMarketResponse(string json, List<MarketItemData> results)
    {
        try
        {
            using var doc  = JsonDocument.Parse(json);
            var       root = doc.RootElement;

            if (root.TryGetProperty("items", out var itemsObj) &&
                itemsObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in itemsObj.EnumerateObject())
                {
                    var data = ParseSingleItem(prop.Value);
                    if (data != null) results.Add(data);
                }
            }
            else
            {
                var data = ParseSingleItem(root);
                if (data != null) results.Add(data);
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[Marketboard] Failed to parse market response: {ex.Message}");
        }
    }

    private static MarketItemData? ParseSingleItem(JsonElement el)
    {
        var itemId = GetUInt(el, "itemID");
        if (itemId == 0) return null;

        var listings = new List<MarketListing>();
        if (el.TryGetProperty("listings", out var listingsEl) &&
            listingsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in listingsEl.EnumerateArray())
            {
                listings.Add(new MarketListing
                {
                    ListingId   = GetULong(entry, "listingID"),
                    RetainerId   = GetULong(entry, "retainerID"),
                    PricePerUnit = GetInt(entry, "pricePerUnit"),
                    Quantity     = GetInt(entry, "quantity"),
                    IsHq         = GetBool(entry, "hq") ?? false,
                    WorldId      = GetUInt(entry, "worldID"),
                    WorldName    = GetString(entry, "worldName"),
                    TotalTax     = GetLong(entry, "tax", "totalTax"),
                    TownId       = GetInt(entry, "retainerCity"),
                    IsMannequin  = GetBool(entry, "onMannequin"),
                    // Universalis does not expose authoritative set-sale
                    // metadata. Unknown must remain unknown until live UI
                    // authority confirms it is safe to purchase.
                    IsSellingAsSet = null,
                });
            }
        }

        return new MarketItemData
        {
            ItemId   = itemId,
            MinPrice = GetFloat(el, "minPrice"),
            Listings = listings,
        };
    }

    private static float GetFloat(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var value))
            return 0f;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String
            && float.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number : 0f;
    }

    private static int GetInt(JsonElement el, params string[] props)
    {
        foreach (var prop in props)
        {
            if (!el.TryGetProperty(prop, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var integer))
                return integer;
            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
                return integer;
        }
        return 0;
    }

    private static long GetLong(JsonElement el, params string[] props)
    {
        foreach (var prop in props)
        {
            if (!el.TryGetProperty(prop, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer))
                return integer;
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
                return integer;
        }
        return 0;
    }

    private static ulong GetULong(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var integer))
            return integer;
        return value.ValueKind == JsonValueKind.String
            && ulong.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)
            ? integer : 0;
    }

    private static uint GetUInt(JsonElement el, string prop)
        => (uint)GetULong(el, prop);

    private static bool? GetBool(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind == JsonValueKind.String
            && bool.TryParse(value.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static string GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    public void Dispose()
    {
        _http.Dispose();
        _throttle.Dispose();
    }
}
