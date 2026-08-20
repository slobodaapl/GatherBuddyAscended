using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.FcMesh.Protocol;

public enum FcItemQuality : byte
{
    Nq = 0,
    Hq = 1,
}

public readonly record struct ItemQuantityKey(uint ItemId, FcItemQuality Quality) : IComparable<ItemQuantityKey>
{
    public int CompareTo(ItemQuantityKey other)
    {
        var item = ItemId.CompareTo(other.ItemId);
        return item != 0 ? item : Quality.CompareTo(other.Quality);
    }
}

// Compatibility name for internal matcher/ledger callers. Public wire DTOs use
// ItemQuantityEntry exactly as specified by the protocol.
public readonly record struct FcQuantityKey(uint ItemId, FcItemQuality Quality) : IComparable<FcQuantityKey>
{
    public int CompareTo(FcQuantityKey other)
    {
        var item = ItemId.CompareTo(other.ItemId);
        return item != 0 ? item : Quality.CompareTo(other.Quality);
    }

    public static implicit operator ItemQuantityKey(FcQuantityKey value)
        => new(value.ItemId, value.Quality);

    public static implicit operator FcQuantityKey(ItemQuantityKey value)
        => new(value.ItemId, value.Quality);
}

public sealed record ItemQuantityEntry(uint ItemId, FcItemQuality Quality, int Quantity)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ItemQuantityKey Key => new(ItemId, Quality);
}

// Source-compatible helper for the original Phase-0 matcher API.
public sealed record FcQuantityEntry(uint ItemId, FcItemQuality Quality, int Quantity)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public FcQuantityKey Key => new(ItemId, Quality);
}

public sealed record CrystalQuantityEntry(uint CrystalId, int Quantity);

public sealed record FcCrystalQuantityEntry(uint CrystalId, int Quantity);

/// <summary>
/// Validated, sorted internal view of the public item array.
/// </summary>
public sealed class FcItemQuantityMap : IEquatable<FcItemQuantityMap>
{
    public ItemQuantityEntry[] Entries { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public FcItemQuantityMap(ItemQuantityEntry[] entries)
    {
        if (entries is null)
            throw new ArgumentNullException(nameof(entries));

        var sorted = entries
            .Select(entry =>
            {
                if (entry is null)
                    throw new ArgumentException("Quantity entry cannot be null.", nameof(entries));
                Validate(entry);
                return entry;
            })
            .OrderBy(entry => entry.ItemId)
            .ThenBy(entry => entry.Quality)
            .ToArray();

        for (var i = 1; i < sorted.Length; i++)
        {
            if (sorted[i - 1].Key == sorted[i].Key)
                throw new ArgumentException($"Duplicate quantity key {sorted[i].ItemId}/{sorted[i].Quality}.", nameof(entries));
        }

        Entries = sorted;
    }

    public FcItemQuantityMap(IEnumerable<ItemQuantityEntry> entries)
        : this(entries?.ToArray() ?? throw new ArgumentNullException(nameof(entries)))
    {
    }

    public FcItemQuantityMap(IEnumerable<FcQuantityEntry> entries)
        : this((entries ?? throw new ArgumentNullException(nameof(entries)))
            .Select(entry => new ItemQuantityEntry(entry.ItemId, entry.Quality, entry.Quantity)).ToArray())
    {
    }

    public static FcItemQuantityMap Empty { get; } = new(Array.Empty<ItemQuantityEntry>());

    public int Get(uint itemId, FcItemQuality quality = FcItemQuality.Nq)
        => Entries.FirstOrDefault(entry => entry.ItemId == itemId && entry.Quality == quality)?.Quantity ?? 0;

    public int Get(ItemQuantityKey key) => Get(key.ItemId, key.Quality);
    public int Get(FcQuantityKey key) => Get(key.ItemId, key.Quality);
    public bool Contains(ItemQuantityKey key) => Entries.Any(entry => entry.Key == key);
    public bool Contains(FcQuantityKey key) => Contains((ItemQuantityKey)key);

    public FcItemQuantityMap Add(FcItemQuantityMap other)
    {
        if (other is null)
            throw new ArgumentNullException(nameof(other));
        var values = Entries.ToDictionary(entry => (FcQuantityKey)entry.Key, entry => entry.Quantity);
        foreach (var entry in other.Entries)
            values[(FcQuantityKey)entry.Key] = checked(values.GetValueOrDefault((FcQuantityKey)entry.Key) + entry.Quantity);
        return FromDictionary(values);
    }

    public FcItemQuantityMap SubtractClamped(FcItemQuantityMap other)
    {
        if (other is null)
            throw new ArgumentNullException(nameof(other));
        var values = Entries.ToDictionary(entry => (FcQuantityKey)entry.Key, entry => entry.Quantity);
        foreach (var entry in other.Entries)
            values[(FcQuantityKey)entry.Key] = Math.Max(0, values.GetValueOrDefault((FcQuantityKey)entry.Key) - entry.Quantity);
        return FromDictionary(values);
    }

    public FcItemQuantityMap Min(FcItemQuantityMap other)
    {
        if (other is null)
            throw new ArgumentNullException(nameof(other));
        var keys = Entries.Select(entry => (FcQuantityKey)entry.Key)
            .Concat(other.Entries.Select(entry => (FcQuantityKey)entry.Key))
            .Distinct();
        return new FcItemQuantityMap(keys
            .Select(key => new ItemQuantityEntry(key.ItemId, key.Quality, Math.Min(Get(key), other.Get(key))))
            .Where(entry => entry.Quantity > 0));
    }

    public FcItemQuantityMap Filter(IEnumerable<FcQuantityKey> keys)
    {
        if (keys is null)
            throw new ArgumentNullException(nameof(keys));
        var allowed = keys.ToHashSet();
        return new FcItemQuantityMap(Entries.Where(entry => allowed.Contains((FcQuantityKey)entry.Key)));
    }

    public Dictionary<FcQuantityKey, int> ToDictionary()
        => Entries.ToDictionary(entry => (FcQuantityKey)entry.Key, entry => entry.Quantity);

    public bool Equals(FcItemQuantityMap? other)
        => other is not null && Entries.SequenceEqual(other.Entries);

    public override bool Equals(object? obj) => obj is FcItemQuantityMap other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in Entries)
            hash.Add(entry);
        return hash.ToHashCode();
    }

    public override string ToString()
        => string.Join(",", Entries.Select(entry => $"{entry.ItemId}:{entry.Quality}={entry.Quantity}"));

    public static FcItemQuantityMap FromDictionary(IReadOnlyDictionary<FcQuantityKey, int> values)
        => new(values.Select(pair => new ItemQuantityEntry(pair.Key.ItemId, pair.Key.Quality, pair.Value))
            .Where(entry => entry.Quantity > 0));

    public static FcItemQuantityMap FromDictionary(IReadOnlyDictionary<ItemQuantityKey, int> values)
        => new(values.Select(pair => new ItemQuantityEntry(pair.Key.ItemId, pair.Key.Quality, pair.Value))
            .Where(entry => entry.Quantity > 0));

    private static void Validate(ItemQuantityEntry entry)
    {
        if (entry.ItemId == 0)
            throw new ArgumentOutOfRangeException(nameof(entry), "Item ID must be non-zero.");
        if (!Enum.IsDefined(entry.Quality))
            throw new ArgumentOutOfRangeException(nameof(entry), "Unknown item quality.");
        if (entry.Quantity < 0)
            throw new ArgumentOutOfRangeException(nameof(entry), "Quantity cannot be negative.");
    }
}

/// <summary>Validated sorted crystal array; crystals are kept separate from NQ/HQ items.</summary>
public class CrystalQuantityMap : IEquatable<CrystalQuantityMap>
{
    public CrystalQuantityEntry[] Entries { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public CrystalQuantityMap(CrystalQuantityEntry[] entries)
    {
        if (entries is null)
            throw new ArgumentNullException(nameof(entries));
        var sorted = entries
            .Select(entry =>
            {
                if (entry is null || entry.CrystalId == 0 || entry.Quantity < 0)
                    throw new ArgumentOutOfRangeException(nameof(entries), "Invalid crystal quantity.");
                return entry;
            })
            .OrderBy(entry => entry.CrystalId)
            .ToArray();
        for (var i = 1; i < sorted.Length; i++)
        {
            if (sorted[i - 1].CrystalId == sorted[i].CrystalId)
                throw new ArgumentException($"Duplicate crystal key {sorted[i].CrystalId}.", nameof(entries));
        }
        Entries = sorted;
    }

    public CrystalQuantityMap(IEnumerable<CrystalQuantityEntry> entries)
        : this(entries?.ToArray() ?? throw new ArgumentNullException(nameof(entries)))
    {
    }

    public int Get(uint crystalId)
        => Entries.FirstOrDefault(entry => entry.CrystalId == crystalId)?.Quantity ?? 0;

    public bool Equals(CrystalQuantityMap? other)
        => other is not null && Entries.SequenceEqual(other.Entries);

    public override bool Equals(object? obj) => obj is CrystalQuantityMap other && Equals(other);
    public override int GetHashCode() => Entries.Aggregate(17, (hash, entry) => HashCode.Combine(hash, entry));
}

// Compatibility wrapper. The wire DTO uses CrystalQuantityMap; this wrapper
// lets older local callers construct a map without creating a second state type.
public sealed class FcCrystalQuantityMap : CrystalQuantityMap
{
    [System.Text.Json.Serialization.JsonConstructor]
    public FcCrystalQuantityMap(CrystalQuantityEntry[] entries)
        : base(entries)
    {
    }

    public FcCrystalQuantityMap(IEnumerable<FcCrystalQuantityEntry> entries)
        : base((entries ?? throw new ArgumentNullException(nameof(entries)))
            .Select(entry => new CrystalQuantityEntry(entry.CrystalId, entry.Quantity)).ToArray())
    {
    }

    public static FcCrystalQuantityMap Empty { get; } = new(Array.Empty<CrystalQuantityEntry>());
}
