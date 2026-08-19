using System.Collections.Generic;
using GatherBuddy.Crafting;

namespace GatherBuddy.Gui;

internal readonly record struct CraftingMaterialFinalRoots(IReadOnlyList<CraftingMaterialDemandNode> Nodes);

internal static class CraftingPrecraftPresentation
{
    internal static IReadOnlyList<CraftingMaterialDemandNode> FromFinalRoots(
        CraftingMaterialFinalRoots roots,
        IReadOnlyCollection<uint> visibleItemIds)
    {
        var visible = visibleItemIds as HashSet<uint> ?? new HashSet<uint>(visibleItemIds);
        var normalizedRoots = new List<CraftingMaterialDemandNode>();
        foreach (var root in roots.Nodes)
            MergeNode(root, normalizedRoots);

        var result = new List<CraftingMaterialDemandNode>();
        foreach (var root in normalizedRoots)
            foreach (var child in root.Children)
                AddVisible(child, result, visible);
        return result;
    }

    private static void MergeNode(
        CraftingMaterialDemandNode source,
        List<CraftingMaterialDemandNode> destination)
    {
        var target = destination.Find(node => node.ItemId == source.ItemId);
        if (target == null)
        {
            target = new CraftingMaterialDemandNode(source.ItemId, source.Demand);
            destination.Add(target);
        }
        else
        {
            target.MergeDemand(source.Demand);
        }

        foreach (var child in source.Children)
            MergeNode(child, target.Children);
    }

    private static void AddVisible(
        CraftingMaterialDemandNode source,
        List<CraftingMaterialDemandNode> destination,
        IReadOnlySet<uint> visibleItemIds)
    {
        if (!visibleItemIds.Contains(source.ItemId))
        {
            foreach (var child in source.Children)
                AddVisible(child, destination, visibleItemIds);
            return;
        }

        var target = destination.Find(node => node.ItemId == source.ItemId);
        if (target == null)
        {
            target = new CraftingMaterialDemandNode(source.ItemId, source.Demand);
            destination.Add(target);
        }
        else
        {
            target.MergeDemand(source.Demand);
        }

        foreach (var child in source.Children)
            AddVisible(child, target.Children, visibleItemIds);
    }
}
