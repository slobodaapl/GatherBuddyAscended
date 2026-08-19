using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GatherBuddy.Vulcan;

namespace GatherBuddy.Crafting;

public enum ExpertConditionProfileEvidence
{
    ExtractedExact,
    EmpiricallyInferred,
    ProvisionalPublic,
}

public sealed record ExpertConditionProfile(
    ushort RecipeLevelTableId,
    ConditionFlags Conditions,
    IReadOnlyList<ushort> BaseProbabilityBasisPoints,
    ExpertConditionProfileEvidence Evidence,
    string Provenance)
{
    public float[] ToSimulatorProbabilities()
    {
        var probabilities = new float[BaseProbabilityBasisPoints.Count];
        for (var index = 0; index < probabilities.Length; index++)
            probabilities[index] = BaseProbabilityBasisPoints[index] / 10_000f;
        return probabilities;
    }
}

public static class ExpertConditionProfileCatalog
{
    private const int CatalogSchemaVersion = 1;
    private const string CatalogResourceName = "GatherBuddy.CustomInfo.expert_condition_profiles.json";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly IReadOnlyDictionary<ushort, ExpertConditionProfile> Profiles = Load();

    private sealed class CatalogDocument
    {
        public int SchemaVersion { get; init; }
        public string[] ConditionOrder { get; init; } = [];
        public Dictionary<string, CatalogSource> Sources { get; init; } = [];
        public CatalogVector[] Vectors { get; init; } = [];
        public CatalogProfile[] Profiles { get; init; } = [];
    }

    private sealed class CatalogSource
    {
        public string Description { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
        public string Retrieved { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
    }

    private sealed class CatalogVector
    {
        public string Id { get; init; } = string.Empty;
        public ushort[] BasisPoints { get; init; } = [];
    }

    private sealed class CatalogProfile
    {
        public ushort RecipeLevelTableId { get; init; }
        public int ConditionsFlag { get; init; }
        public string VectorId { get; init; } = string.Empty;
        public string Evidence { get; init; } = string.Empty;
        public string SourceId { get; init; } = string.Empty;
        public string[] SourceRows { get; init; } = [];
    }

    private static IReadOnlyDictionary<ushort, ExpertConditionProfile> Load()
    {
        using var stream = typeof(ExpertConditionProfileCatalog).Assembly
            .GetManifestResourceStream(CatalogResourceName)
            ?? throw new InvalidOperationException($"Missing embedded Expert condition profile catalog {CatalogResourceName}.");
        var document = JsonSerializer.Deserialize<CatalogDocument>(stream, SerializerOptions)
            ?? throw new InvalidDataException("The embedded Expert condition profile catalog is empty.");

        if (document.SchemaVersion != CatalogSchemaVersion)
            throw new InvalidDataException($"Unsupported Expert condition profile catalog schema {document.SchemaVersion}.");

        var expectedConditionOrder = Enum.GetNames<Condition>().Take((int)Condition.Unknown);
        if (!document.ConditionOrder.SequenceEqual(expectedConditionOrder))
            throw new InvalidDataException("The Expert condition profile catalog condition order does not match the runtime Condition enum.");

        ValidateSources(document.Sources);
        var vectors = LoadVectors(document.Vectors);
        var profiles = LoadProfiles(document.Profiles, document.Sources, vectors);
        var referencedVectorIds = document.Profiles.Select(profile => profile.VectorId).ToHashSet(StringComparer.Ordinal);
        if (vectors.Keys.Any(vectorId => !referencedVectorIds.Contains(vectorId)))
            throw new InvalidDataException("The Expert condition profile catalog contains an unreferenced vector.");
        return profiles;
    }

    private static void ValidateSources(IReadOnlyDictionary<string, CatalogSource> sources)
    {
        foreach (var (sourceId, source) in sources)
        {
            if (string.IsNullOrWhiteSpace(sourceId)
                || string.IsNullOrWhiteSpace(source.Description)
                || string.IsNullOrWhiteSpace(source.Url)
                || string.IsNullOrWhiteSpace(source.Retrieved))
                throw new InvalidDataException($"Expert condition catalog source {sourceId} is incomplete.");
            if (source.Sha256.Length != 0
                && (source.Sha256.Length != 64 || source.Sha256.Any(character => !Uri.IsHexDigit(character))))
                throw new InvalidDataException($"Expert condition catalog source {sourceId} has an invalid SHA-256 digest.");
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ushort>> LoadVectors(IEnumerable<CatalogVector> sourceVectors)
    {
        var vectors = new Dictionary<string, IReadOnlyList<ushort>>();
        var distinctVectors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in sourceVectors)
        {
            if (vector.BasisPoints.Length != (int)Condition.Unknown)
                throw new InvalidDataException($"Expert condition catalog vector {vector.Id} must contain one value per known Condition.");
            if (vector.BasisPoints.Sum(value => value) != 10_000)
                throw new InvalidDataException($"Expert condition catalog vector {vector.Id} must sum to 10000 basis points.");
            if (string.IsNullOrWhiteSpace(vector.Id)
                || !vectors.TryAdd(vector.Id, Array.AsReadOnly(vector.BasisPoints)))
                throw new InvalidDataException($"Expert condition catalog vector ID {vector.Id} is empty or duplicated.");
            if (!distinctVectors.Add(string.Join(",", vector.BasisPoints)))
                throw new InvalidDataException($"Expert condition catalog vector {vector.Id} duplicates another stored vector.");
        }

        return vectors;
    }

    private static IReadOnlyDictionary<ushort, ExpertConditionProfile> LoadProfiles(
        IEnumerable<CatalogProfile> sourceProfiles,
        IReadOnlyDictionary<string, CatalogSource> sources,
        IReadOnlyDictionary<string, IReadOnlyList<ushort>> vectors)
    {
        var profiles = new Dictionary<ushort, ExpertConditionProfile>();
        var knownFlagsMask = (1 << (int)Condition.Unknown) - 1;
        foreach (var profileData in sourceProfiles)
        {
            if (profileData.RecipeLevelTableId == 0)
                throw new InvalidDataException("Expert condition profiles require a nonzero RecipeLevelTable ID.");
            if (!vectors.TryGetValue(profileData.VectorId, out var basisPoints))
                throw new InvalidDataException($"Expert condition profile {profileData.RecipeLevelTableId} references unknown vector {profileData.VectorId}.");
            if (!sources.TryGetValue(profileData.SourceId, out var source))
                throw new InvalidDataException($"Expert condition profile {profileData.RecipeLevelTableId} references unknown source {profileData.SourceId}.");
            if (!Enum.TryParse<ExpertConditionProfileEvidence>(profileData.Evidence, out var evidence))
                throw new InvalidDataException($"Expert condition profile {profileData.RecipeLevelTableId} has unknown evidence {profileData.Evidence}.");
            if (!Enum.IsDefined(evidence))
                throw new InvalidDataException($"Expert condition profile {profileData.RecipeLevelTableId} has undefined evidence {profileData.Evidence}.");
            if (profileData.SourceRows.Length == 0 || profileData.SourceRows.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException($"Expert condition profile {profileData.RecipeLevelTableId} requires source-row provenance.");
            if ((profileData.ConditionsFlag & ~knownFlagsMask) != 0)
                throw new InvalidDataException($"Expert condition profile {profileData.RecipeLevelTableId} has unknown condition flags.");

            var conditions = (ConditionFlags)profileData.ConditionsFlag;
            if ((conditions & ConditionFlags.Normal) == 0)
                throw new InvalidDataException($"Expert condition profile {profileData.RecipeLevelTableId} must include Normal.");
            for (var index = 0; index < basisPoints.Count; ++index)
            {
                var flagPresent = (profileData.ConditionsFlag & (1 << index)) != 0;
                var probabilityPresent = basisPoints[index] > 0;
                if (flagPresent != probabilityPresent)
                    throw new InvalidDataException($"Expert condition profile {profileData.RecipeLevelTableId} disagrees with its condition flags at {(Condition)index}.");
            }

            var digest = source.Sha256.Length == 0 ? string.Empty : $"; SHA-256 {source.Sha256}";
            var provenance = $"{source.Description}; {string.Join(", ", profileData.SourceRows)}; {source.Url}; retrieved {source.Retrieved}{digest}";
            var profile = new ExpertConditionProfile(
                profileData.RecipeLevelTableId,
                conditions,
                basisPoints,
                evidence,
                provenance);
            if (!profiles.TryAdd(profile.RecipeLevelTableId, profile))
                throw new InvalidDataException($"Duplicate Expert condition profile for RLT {profile.RecipeLevelTableId}.");
        }

        return profiles;
    }

    public static bool TryGet(ushort recipeLevelTableId, out ExpertConditionProfile profile)
        => Profiles.TryGetValue(recipeLevelTableId, out profile!);

    public static IReadOnlyCollection<ExpertConditionProfile> All
        => Profiles.Values.OrderBy(profile => profile.RecipeLevelTableId).ToArray();
}
