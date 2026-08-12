using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.AutoGather.Helpers;

internal static class CordialSelector
{
    internal const uint CordialItemId = 6141;
    internal const uint HiCordialItemId = 12669;
    internal const uint WateredCordialItemId = 16911;
    internal const uint HqItemOffset = 1_000_000;

    private static readonly uint[] StrongestFirst = [HiCordialItemId, CordialItemId, WateredCordialItemId];
    private static readonly uint[] WeakestFirst = [WateredCordialItemId, CordialItemId, HiCordialItemId];

    internal static uint Select(ConfigPreset.CordialConfig config, uint currentGp, uint maxGp, Func<uint, int> inventoryCount)
        => GetConfiguredItems(config).FirstOrDefault(itemId =>
            inventoryCount(itemId) > 0
         && (!config.PreventGpOvercap || currentGp + GetGpRestoration(itemId) <= maxGp));

    internal static int GetGpRestoration(uint itemId)
        => itemId switch
        {
            HiCordialItemId                     => 400,
            CordialItemId + HqItemOffset        => 350,
            CordialItemId                       => 300,
            WateredCordialItemId + HqItemOffset => 200,
            WateredCordialItemId                => 150,
            _                                   => 0,
        };

    private static IEnumerable<uint> GetConfiguredItems(ConfigPreset.CordialConfig config)
        => config.SelectionMode switch
        {
            ConfigPreset.CordialSelectionMode.StrongestFirst => StrongestFirst.SelectMany(itemId => GetQualityOrder(itemId, config.HqPreference)),
            ConfigPreset.CordialSelectionMode.WeakestFirst   => WeakestFirst.SelectMany(itemId => GetQualityOrder(itemId, config.HqPreference)),
            _ when config.ItemId > 0                         => [config.ItemId],
            _                                                => [],
        };

    private static IEnumerable<uint> GetQualityOrder(uint itemId, ConfigPreset.CordialHqPreference preference)
    {
        var canBeHq = itemId is CordialItemId or WateredCordialItemId;
        var hqItemId = itemId + HqItemOffset;
        return preference switch
        {
            ConfigPreset.CordialHqPreference.HqBeforeNq when canBeHq => [hqItemId, itemId],
            ConfigPreset.CordialHqPreference.NqBeforeHq when canBeHq => [itemId, hqItemId],
            ConfigPreset.CordialHqPreference.HqOnly when canBeHq     => [hqItemId],
            ConfigPreset.CordialHqPreference.HqOnly                  => [],
            _                                                        => [itemId],
        };
    }
}
