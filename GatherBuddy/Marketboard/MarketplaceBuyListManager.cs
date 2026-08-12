using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Config;
using GatherBuddy.Crafting.Acquisition;

namespace GatherBuddy.Marketboard;

/// <summary>
/// Owns persisted marketplace lists and transient craft-owned managed lists.
/// </summary>
public sealed class MarketplaceBuyListManager
{
    private readonly Configuration _config;

    public MarketplaceBuyListManager(Configuration config)
    {
        _config = config;
        EnsureState();
    }

    public IReadOnlyList<MarketplaceBuyListDefinition> Lists
        => _config.MarketplaceBuyLists;

    public MarketplaceBuyListDefinition? ActiveList
        => Lists.FirstOrDefault(list => list.Id == _config.ActiveMarketplaceBuyListId);

    public Guid ActiveListId => _config.ActiveMarketplaceBuyListId;

    public MarketplaceBuyListDefinition CreateList(string name = "Marketplace List", bool select = true)
    {
        var list = new MarketplaceBuyListDefinition
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Marketplace List" : name.Trim(),
        };
        _config.MarketplaceBuyLists.Add(list);
        if (select)
            _config.ActiveMarketplaceBuyListId = list.Id;
        Save();
        return list;
    }

    public MarketplaceBuyListDefinition CreateManagedList(string name = "Craft acquisition")
        => new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Craft acquisition" : name.Trim(),
            IsManaged = true,
        };

    /// <summary>
    /// Creates the read-only marketplace view for one craft execution. The
    /// plan already excludes final outputs and usable craft/gather paths, so a
    /// managed list cannot accidentally turn into a persistent shopping list.
    /// </summary>
    public MarketplaceBuyListDefinition CreateManagedList(
        AcquisitionPlanningResult planning,
        LiveAcquisitionOptions? options = null,
        string name = "Craft acquisition")
    {
        ArgumentNullException.ThrowIfNull(planning);
        var list = CreateManagedList(name);
        var selected = planning.SelectedPlan;
        if (selected == null)
            return list;

        options ??= new LiveAcquisitionOptions();
        list.PreferHQ = options.PreferHQ;
        list.PreferVendors = options.PreferVendors;
        list.PreferMarketForSpecialCurrency = options.PreferMarketForSpecialCurrency;
        list.CurrentWorldOnly = options.CurrentWorldOnly;
        list.MaximumGilSpend = options.MaximumGilSpend;
        foreach (var group in selected.Transactions
                     .Where(transaction => transaction.SourceKind == AcquisitionSourceKind.Market)
                     .GroupBy(transaction => transaction.ItemId))
        {
            var first = group.First();
            AddItemInternal(list, first.ItemId, first.ItemName, 0, group.Sum(transaction => transaction.Quantity));
        }
        return list;
    }

    public bool IsManaged(Guid id)
        => Find(id)?.IsManaged == true;

    public bool SelectList(Guid id)
    {
        if (_config.MarketplaceBuyLists.All(list => list.Id != id))
            return false;
        if (_config.ActiveMarketplaceBuyListId == id)
            return true;
        _config.ActiveMarketplaceBuyListId = id;
        Save();
        return true;
    }

    public bool RenameList(Guid id, string name)
    {
        var list = Find(id);
        if (list == null || list.IsManaged || string.IsNullOrWhiteSpace(name))
            return false;
        list.Name = name.Trim();
        Save();
        return true;
    }

    public bool DeleteList(Guid id)
    {
        if (_config.MarketplaceBuyLists.Count <= 1)
            return false;
        var index = _config.MarketplaceBuyLists.FindIndex(list => list.Id == id);
        if (index < 0)
            return false;

        _config.MarketplaceBuyLists.RemoveAt(index);
        if (_config.ActiveMarketplaceBuyListId == id)
            _config.ActiveMarketplaceBuyListId = _config.MarketplaceBuyLists[Math.Min(index, _config.MarketplaceBuyLists.Count - 1)].Id;
        Save();
        return true;
    }

    public bool AddItem(Guid id, uint itemId, string itemName, uint iconId, int quantity)
    {
        if (itemId == 0 || quantity <= 0)
            return false;
        var list = Find(id);
        if (list == null || list.IsManaged)
            return false;

        AddItem(list, itemId, itemName, iconId, quantity);
        Save();
        return true;
    }

    public bool AddItem(MarketplaceBuyListDefinition list, uint itemId, string itemName, uint iconId, int quantity)
    {
        if (itemId == 0 || quantity <= 0 || list == null)
            return false;
        if (list.IsManaged)
        {
            AddItemInternal(list, itemId, itemName, iconId, quantity);
            return true;
        }

        if (!Lists.Any(candidate => candidate.Id == list.Id))
            return false;
        AddItemInternal(list, itemId, itemName, iconId, quantity);
        Save();
        return true;
    }

    public bool SetTarget(Guid id, uint itemId, int quantity)
    {
        var list = Find(id);
        if (list == null || list.IsManaged)
            return false;

        var entry = list.Entries.FirstOrDefault(candidate => candidate.ItemId == itemId);
        if (quantity <= 0)
        {
            if (entry == null)
                return false;
            list.Entries.Remove(entry);
        }
        else if (entry == null)
        {
            list.Entries.Add(new MarketplaceBuyListEntry { ItemId = itemId, TargetQuantity = quantity });
        }
        else
        {
            entry.TargetQuantity = quantity;
        }
        Save();
        return true;
    }

    public bool RemoveItem(Guid id, uint itemId)
        => SetTarget(id, itemId, 0);

    public bool UpdateSettings(Guid id, bool? preferHq = null, bool? currentWorldOnly = null,
        bool? preferVendors = null, bool? preferMarketForSpecialCurrency = null,
        long? maximumGilSpend = null, bool clearMaximumGilSpend = false)
    {
        var list = Find(id);
        if (list == null || list.IsManaged)
            return false;
        if (preferHq.HasValue) list.PreferHQ = preferHq.Value;
        if (currentWorldOnly.HasValue) list.CurrentWorldOnly = currentWorldOnly.Value;
        if (preferVendors.HasValue) list.PreferVendors = preferVendors.Value;
        if (preferMarketForSpecialCurrency.HasValue)
            list.PreferMarketForSpecialCurrency = preferMarketForSpecialCurrency.Value;
        if (clearMaximumGilSpend)
            list.MaximumGilSpend = null;
        else if (maximumGilSpend.HasValue)
            list.MaximumGilSpend = Math.Max(0, maximumGilSpend.Value);
        Save();
        return true;
    }

    public MarketplaceBuyListDefinition? Find(Guid id)
        => Lists.FirstOrDefault(list => list.Id == id);

    public void EnsureState()
    {
        _config.MarketplaceBuyLists ??= new List<MarketplaceBuyListDefinition>();
        if (_config.MarketplaceBuyLists.Count == 0)
            _config.MarketplaceBuyLists.Add(new MarketplaceBuyListDefinition { Name = "Default" });

        var active = _config.MarketplaceBuyLists.FirstOrDefault(list => list.Id == _config.ActiveMarketplaceBuyListId);
        if (active == null)
            _config.ActiveMarketplaceBuyListId = _config.MarketplaceBuyLists[0].Id;

        foreach (var list in _config.MarketplaceBuyLists)
        {
            list.Entries ??= new List<MarketplaceBuyListEntry>();
            list.Name = string.IsNullOrWhiteSpace(list.Name) ? "Marketplace List" : list.Name;
            list.Entries.RemoveAll(entry => entry == null || entry.ItemId == 0 || entry.TargetQuantity <= 0);
        }
    }

    private static void AddItemInternal(MarketplaceBuyListDefinition list, uint itemId, string itemName, uint iconId, int quantity)
    {
        var entry = list.Entries.FirstOrDefault(candidate => candidate.ItemId == itemId);
        if (entry == null)
        {
            list.Entries.Add(new MarketplaceBuyListEntry
            {
                ItemId = itemId,
                ItemName = itemName,
                IconId = iconId,
                TargetQuantity = quantity,
            });
            return;
        }

        entry.TargetQuantity = checked(entry.TargetQuantity + quantity);
        if (!string.IsNullOrWhiteSpace(itemName)) entry.ItemName = itemName;
        if (iconId > 0) entry.IconId = iconId;
    }

    private void Save() => _config.Save();
}
