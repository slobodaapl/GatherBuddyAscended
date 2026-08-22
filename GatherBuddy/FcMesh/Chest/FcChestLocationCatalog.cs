using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.FcMesh.Chest;

/// <summary>
/// Public-city chest destinations. The coordinates are map-search anchors,
/// never exact vnav targets and never persisted as an object position.
/// </summary>
public sealed record FcPublicChestDestination(
    string Region,
    string Zone,
    string AethernetShard,
    string ObjectHint,
    float ApproximateMapX,
    float ApproximateMapY)
{
    public string Key => Region + "/" + Zone + "/" + AethernetShard;
}

public static class FcChestLocationCatalog
{
    private static readonly FcPublicChestDestination[] Destinations =
    [
        new("The Black Shroud", "Old Gridania", "Leatherworkers' Guild & Shaded Bower", "Company Chest", 14f, 9f),
        new("The Black Shroud", "New Gridania", "Gridania Aetheryte Plaza", "The Adders' Nest", 9f, 11f),
        new("La Noscea", "Limsa Lominsa Lower Decks", "Hawkers' Round", "Company Chest", 7f, 12f),
        new("La Noscea", "Limsa Lominsa Upper Decks", "The Aftcastle", "Maelstrom Command", 13f, 12f),
        new("Thanalan", "Ul'dah - Steps of Nald", "Ul'dah Aetheryte Plaza", "The Hall of Flames", 8.3f, 9.4f),
        new("Thanalan", "Ul'dah - Steps of Thal", "The Sapphire Avenue Exchange", "Company Chest", 13.8f, 10.4f),
        new("Gyr Abania", "Rhalgr's Reach", "Western Rhalgr's Reach", "Company Chest", 10.1f, 12.9f),
        new("Hingashi", "Kugane", "Kogane Dori", "Company Chest", 12.3f, 12.3f),
    ];

    public static IReadOnlyList<FcPublicChestDestination> All { get; } = Destinations;

    public static bool TryGet(string zone, out FcPublicChestDestination destination)
    {
        destination = Destinations.FirstOrDefault(value =>
            string.Equals(value.Zone, zone, StringComparison.OrdinalIgnoreCase))!;
        return destination is not null;
    }
}
