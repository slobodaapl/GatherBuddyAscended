using System;
using System.Collections.Generic;

namespace GatherBuddy.Marketboard;

/// <summary>
/// One item target in a marketplace buy list. The target is an inventory quantity,
/// not a number of market listings or stacks.
/// </summary>
public sealed class MarketplaceBuyListEntry
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public uint IconId { get; set; }
    public int TargetQuantity { get; set; }
}

/// <summary>
/// Persisted marketplace acquisition policy and item targets.
/// </summary>
public sealed class MarketplaceBuyListDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<MarketplaceBuyListEntry> Entries { get; set; } = new();

    public bool PreferHQ { get; set; }
    public bool CurrentWorldOnly { get; set; }
    public bool PreferVendors { get; set; }
    public bool PreferMarketForSpecialCurrency { get; set; } = true;
    public long? MaximumGilSpend { get; set; }

    /// <summary>
    /// Managed lists are intentionally not serialized. They are useful for a
    /// craft-list execution plan and must not leak into the user's saved lists.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public bool IsManaged { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public bool IsReadOnly
        => IsManaged;

    public MarketplaceBuyListDefinition Clone(bool managed = false)
        => new()
        {
            Id = managed ? Guid.NewGuid() : Id,
            Name = Name,
            CreatedAt = CreatedAt,
            Entries = new List<MarketplaceBuyListEntry>(Entries.ConvertAll(entry => new MarketplaceBuyListEntry
            {
                ItemId = entry.ItemId,
                ItemName = entry.ItemName,
                IconId = entry.IconId,
                TargetQuantity = entry.TargetQuantity,
            })),
            PreferHQ = PreferHQ,
            CurrentWorldOnly = CurrentWorldOnly,
            PreferVendors = PreferVendors,
            PreferMarketForSpecialCurrency = PreferMarketForSpecialCurrency,
            MaximumGilSpend = MaximumGilSpend,
            IsManaged = managed,
        };
}
