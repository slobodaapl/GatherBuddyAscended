using System;
using System.Collections.Generic;
using System.Linq;
using LuminaSupplemental.Excel.Model;
using LuminaSupplemental.Excel.Services;

namespace GatherBuddy.Crafting.Acquisition;

internal readonly record struct AetherialReductionPresentation(
    uint SourceItemId,
    uint OutputItemId,
    int OutputNeeded,
    uint SourceClassJobId)
{
    internal bool SourceQuantityUnknown => true;
}

internal static class AetherialReductionSourceResolver
{
    private static readonly Lazy<IReadOnlyDictionary<uint, uint[]>> SourcesByOutput = new(LoadSources);

    internal static IReadOnlyList<uint> GetSourceItemIds(uint outputItemId)
        => SourcesByOutput.Value.GetValueOrDefault(outputItemId) ?? [];

    internal static IReadOnlyCollection<uint> GetOutputItemIds()
        => SourcesByOutput.Value.Keys.ToArray();

    internal static bool IsSourceForOutput(uint outputItemId, uint sourceItemId)
        => GetSourceItemIds(outputItemId).Contains(sourceItemId);

    internal static bool TryCreatePresentation(
        uint outputItemId,
        int outputNeeded,
        Func<uint, AcquisitionPath?> resolveOutputPath,
        out AetherialReductionPresentation presentation)
        => TryCreatePresentation(
            outputItemId,
            outputNeeded,
            SourcesByOutput.Value,
            resolveOutputPath,
            out presentation);

    internal static bool TryCreatePresentation(
        uint outputItemId,
        int outputNeeded,
        IReadOnlyDictionary<uint, uint[]> sourcesByOutput,
        Func<uint, AcquisitionPath?> resolveOutputPath,
        out AetherialReductionPresentation presentation)
    {
        presentation = default;
        if (outputNeeded <= 0
         || !sourcesByOutput.TryGetValue(outputItemId, out var sourceItemIds)
         || sourceItemIds.Length == 0)
            return false;

        var path = resolveOutputPath(outputItemId);
        if (path is not
            {
                Kind: AcquisitionPathKind.Reduction,
                SourceItemId: not 0,
                JobId: not 0,
                Capability.Status: AcquisitionCapabilityStatus.Usable,
            }
            || !sourceItemIds.Contains(path.SourceItemId))
            return false;

        presentation = new AetherialReductionPresentation(
            path.SourceItemId,
            outputItemId,
            outputNeeded,
            path.JobId);
        return true;
    }

    internal static IReadOnlyDictionary<uint, uint[]> BuildIndex(IEnumerable<ItemSupplement> relations)
        => relations
            .Where(relation => relation.ItemSupplementSource == ItemSupplementSource.Reduction
                && relation.ItemId != 0
                && relation.SourceItemId != 0)
            .GroupBy(relation => relation.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(relation => relation.SourceItemId).Distinct().Order().ToArray());

    private static IReadOnlyDictionary<uint, uint[]> LoadSources()
    {
        try
        {
            var relations = CsvLoader.LoadResource<ItemSupplement>(
                CsvLoader.ItemSupplementResourceName,
                true,
                out var failedLines,
                out var exceptions);
            if (exceptions.Count != 0)
                GatherBuddy.Log.Warning($"[AetherialReductionSourceResolver] ItemSupplement load reported {exceptions.Count} parser exceptions");
            if (failedLines.Count != 0)
                GatherBuddy.Log.Warning($"[AetherialReductionSourceResolver] ItemSupplement load failed on {failedLines.Count} lines");
            return BuildIndex(relations);
        }
        catch (Exception exception)
        {
            GatherBuddy.Log.Warning($"[AetherialReductionSourceResolver] Failed to load reduction relations: {exception.Message}");
            return new Dictionary<uint, uint[]>();
        }
    }
}
