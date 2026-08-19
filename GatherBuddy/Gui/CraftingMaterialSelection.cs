using System.Collections.Generic;

namespace GatherBuddy.Gui;

internal readonly record struct CraftingMaterialRowKey(uint ItemId, bool IsPrecraft, string Context = "");

internal enum CraftingMaterialAcquisitionSource
{
    Reduction,
    Currency,
}

internal sealed class CraftingMaterialAcquisitionSelection
{
    private readonly Dictionary<uint, CraftingMaterialAcquisitionSource> _sourceByItem = [];
    private readonly Dictionary<uint, string> _currencyOfferByItem = [];

    public bool ShouldUseReduction(uint itemId, bool reductionAvailable, bool currencyAvailable)
        => reductionAvailable
        && (!currencyAvailable
         || !_sourceByItem.TryGetValue(itemId, out var source)
         || source == CraftingMaterialAcquisitionSource.Reduction);

    public bool IsCurrencySelected(uint itemId)
        => _sourceByItem.GetValueOrDefault(itemId) == CraftingMaterialAcquisitionSource.Currency;

    public void SelectReduction(uint itemId)
        => _sourceByItem[itemId] = CraftingMaterialAcquisitionSource.Reduction;

    public void SelectCurrency(uint itemId, string offerKey)
    {
        _currencyOfferByItem[itemId] = offerKey;
        _sourceByItem[itemId] = CraftingMaterialAcquisitionSource.Currency;
    }

    public bool TryGetCurrencyOffer(uint itemId, out string offerKey)
        => _currencyOfferByItem.TryGetValue(itemId, out offerKey!);
}

internal sealed class CraftingMaterialSelection
{
    private readonly HashSet<CraftingMaterialRowKey> _selected = [];
    private CraftingMaterialRowKey? _anchor;

    public int Count
        => _selected.Count;

    public bool Contains(CraftingMaterialRowKey key)
        => _selected.Contains(key);

    public void Clear()
    {
        _selected.Clear();
        _anchor = null;
    }

    public void Click(
        CraftingMaterialRowKey key,
        IReadOnlyList<CraftingMaterialRowKey> displayOrder,
        bool control,
        bool shift)
    {
        if (shift && _anchor is { } anchor)
        {
            var anchorIndex = IndexOf(displayOrder, anchor);
            var clickedIndex = IndexOf(displayOrder, key);
            if (anchorIndex >= 0 && clickedIndex >= 0)
            {
                if (!control)
                    _selected.Clear();
                var first = System.Math.Min(anchorIndex, clickedIndex);
                var last = System.Math.Max(anchorIndex, clickedIndex);
                for (var i = first; i <= last; i++)
                    _selected.Add(displayOrder[i]);
                return;
            }
        }

        if (control)
        {
            if (!_selected.Remove(key))
                _selected.Add(key);
        }
        else
        {
            _selected.Clear();
            _selected.Add(key);
        }

        _anchor = key;
    }

    public void RightClick(CraftingMaterialRowKey key)
    {
        if (_selected.Contains(key))
            return;

        _selected.Clear();
        _selected.Add(key);
        _anchor = key;
    }

    public void RetainVisible(IReadOnlyCollection<CraftingMaterialRowKey> visible)
    {
        var visibleSet = visible as HashSet<CraftingMaterialRowKey> ?? new HashSet<CraftingMaterialRowKey>(visible);
        _selected.IntersectWith(visibleSet);
        if (_anchor is { } anchor && !visibleSet.Contains(anchor))
            _anchor = null;
    }

    private static int IndexOf(IReadOnlyList<CraftingMaterialRowKey> values, CraftingMaterialRowKey value)
    {
        for (var i = 0; i < values.Count; i++)
            if (values[i] == value)
                return i;
        return -1;
    }
}
