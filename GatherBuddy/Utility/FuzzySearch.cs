using System;
using System.Collections.Generic;

namespace GatherBuddy.Utility;

internal static class FuzzySearch
{
    internal static int? Score(string corpus, IReadOnlyList<string> queries)
    {
        var score = 0;
        foreach (var query in queries)
        {
            var maximumDistance = query.Length switch
            {
                < 3 => 0,
                < 6 => 1,
                < 10 => 2,
                _ => 3,
            };
            var distance = DistanceToSubstring(corpus, query, maximumDistance);
            if (distance > maximumDistance)
                return null;
            score += distance;
        }

        return score;
    }

    private static int DistanceToSubstring(string corpus, string query, int maximumDistance)
    {
        var previousPrevious = new int[corpus.Length + 1];
        var previous = new int[corpus.Length + 1];
        var current = new int[corpus.Length + 1];

        for (var row = 1; row <= query.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= corpus.Length; column++)
            {
                var substitutionCost = query[row - 1] == corpus[column - 1] ? 0 : 1;
                current[column] = System.Math.Min(
                    System.Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitutionCost);

                if (row > 1 && column > 1
                 && query[row - 1] == corpus[column - 2]
                 && query[row - 2] == corpus[column - 1])
                    current[column] = System.Math.Min(current[column], previousPrevious[column - 2] + 1);
            }

            (previousPrevious, previous, current) = (previous, current, previousPrevious);
        }

        var best = query.Length;
        for (var column = 1; column <= corpus.Length; column++)
            best = System.Math.Min(best, previous[column]);
        return best <= maximumDistance ? best : maximumDistance + 1;
    }
}
