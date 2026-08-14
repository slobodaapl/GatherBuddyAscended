using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Utility;

internal static class RecipeSearch
{
    internal static IReadOnlyList<T> Filter<T>(
        IEnumerable<T> candidates,
        string query,
        Func<T, string> nameSelector)
        => FilterNormalized(candidates, query, candidate => SearchTextNormalizer.Normalize(nameSelector(candidate)));

    internal static IReadOnlyList<T> FilterNormalized<T>(
        IEnumerable<T> candidates,
        string query,
        Func<T, string> normalizedNameSelector)
    {
        var candidateList = candidates.ToList();
        var keywords = query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(SearchTextNormalizer.Normalize)
            .Where(keyword => keyword.Length > 0)
            .ToArray();
        if (keywords.Length == 0)
            return candidateList;

        var searchableCandidates = candidateList
            .Select(candidate => (
                Candidate: candidate,
                NormalizedName: normalizedNameSelector(candidate)))
            .ToList();
        var exactMatches = searchableCandidates
            .Where(entry => keywords.All(entry.NormalizedName.Contains))
            .Select(entry => entry.Candidate)
            .ToList();
        if (exactMatches.Count > 0)
            return exactMatches;

        var bestScore = int.MaxValue;
        var bestMatches = new List<T>();
        foreach (var entry in searchableCandidates)
        {
            var score = FuzzySearch.Score(entry.NormalizedName, keywords, bestScore);
            if (!score.HasValue)
                continue;
            if (score.Value < bestScore)
            {
                bestScore = score.Value;
                bestMatches.Clear();
            }
            bestMatches.Add(entry.Candidate);
        }
        return bestMatches;
    }
}
