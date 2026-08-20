using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GatherBuddy.FcMesh.Protocol;

/// <summary>
/// Canonical protocol serialization. Only collection presentation order is
/// normalized; all authoritative fields remain part of the semantic hash.
/// </summary>
public static class FcCanonical
{
    public static string Serialize<T>(T value)
        => value is null
            ? "null"
            : JsonSerializer.Serialize(Normalize(value), value.GetType(), FcJsonContext.Default.Options);

    public static byte[] SerializeUtf8<T>(T value)
        => Encoding.UTF8.GetBytes(Serialize(value));

    public static string Hash<T>(T value) => SemanticHash(value);

    public static string PayloadHash<T>(T value)
        => FcMeshSignature.HashPayload(SerializeUtf8(value));

    public static string SemanticHash<T>(T value)
        => FcMeshSignature.HashPayload(SerializeUtf8(Normalize(value)));

    private static T Normalize<T>(T value)
    {
        object normalized = value switch
        {
            PublishedListRecord list => list with
            {
                FinalTargets = NormalizeTargets(list.FinalTargets),
                FinalQualityPolicy = NormalizePolicy(list.FinalQualityPolicy),
                PrecraftQualityPolicy = NormalizePolicy(list.PrecraftQualityPolicy),
            },
            FcQualityPolicy policy => NormalizePolicy(policy),
            FcFulfillmentSelection selection => selection with
            {
                ListIds = (selection.ListIds ?? Array.Empty<Guid>()).OrderBy(id => id).ToArray(),
            },
            WorkerSessionRecord worker => worker with
            {
                Selection = Normalize(worker.Selection),
                HeldInventory = NormalizeEntries(worker.HeldInventory),
            },
            ChestSnapshotRecord chest => chest with
            {
                Items = NormalizeEntries(chest.Items),
                Crystals = new CrystalQuantityMap(NormalizeCrystals(chest.Crystals)),
            },
            CapabilityRequestRecord request => request with
            {
                Recipes = (request.Recipes ?? Array.Empty<RequiredCraftCapability>())
                    .OrderBy(recipe => recipe.RecipeId)
                    .Select(recipe => recipe with { QualityPolicy = NormalizePolicy(recipe.QualityPolicy) })
                    .ToArray(),
            },
            CapabilityResponseRecord response => response with
            {
                Results = (response.Results ?? Array.Empty<CraftCapabilityResult>())
                    .OrderBy(result => result.RecipeId)
                    .ToArray(),
            },
            FcInventoryTransferRecord transfer => transfer with
            {
                ActualTransferred = NormalizeEntries(transfer.ActualTransferred),
                ChestAfter = Normalize(transfer.ChestAfter),
                WorkerAfter = Normalize(transfer.WorkerAfter),
            },
            FcItemQuantityMap map => new FcItemQuantityMap(NormalizeEntries(map.Entries)),
            ItemQuantityEntry[] entries => NormalizeEntries(entries),
            FcQuantityEntry[] entries => entries.OrderBy(entry => entry.ItemId).ThenBy(entry => entry.Quality).ToArray(),
            FcCrystalQuantityMap legacyCrystals => new FcCrystalQuantityMap(NormalizeCrystals(legacyCrystals)),
            CrystalQuantityMap crystals => new CrystalQuantityMap(NormalizeCrystals(crystals)),
            CrystalQuantityEntry[] crystals => NormalizeCrystals(crystals),
            _ => value!
        };
        return (T)normalized;
    }

    private static PublishedRecipeTarget[] NormalizeTargets(PublishedRecipeTarget[]? targets)
        => (targets ?? Array.Empty<PublishedRecipeTarget>())
            .OrderBy(target => target.ItemId)
            .ThenBy(target => target.Quality)
            .ThenBy(target => target.RecipeId)
            .ToArray();

    private static FcQualityPolicy NormalizePolicy(FcQualityPolicy? policy)
        => policy is null
            ? FcQualityPolicy.Empty
            : policy with
            {
                Rules = (policy.Rules ?? Array.Empty<FcQualityRule>())
                    .OrderBy(rule => rule.ItemId)
                    .ThenBy(rule => rule.Quality)
                    .ToArray(),
            };

    private static ItemQuantityEntry[] NormalizeEntries(ItemQuantityEntry[]? entries)
        => (entries ?? Array.Empty<ItemQuantityEntry>())
            .OrderBy(entry => entry.ItemId)
            .ThenBy(entry => entry.Quality)
            .ToArray();

    private static CrystalQuantityEntry[] NormalizeCrystals(CrystalQuantityMap? map)
        => map is null
            ? Array.Empty<CrystalQuantityEntry>()
            : NormalizeCrystals(map.Entries);

    private static CrystalQuantityEntry[] NormalizeCrystals(CrystalQuantityEntry[]? entries)
        => (entries ?? Array.Empty<CrystalQuantityEntry>())
            .OrderBy(entry => entry.CrystalId)
            .ToArray();
}
