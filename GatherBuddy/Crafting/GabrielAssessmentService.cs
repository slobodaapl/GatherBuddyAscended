using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public enum GabrielAssessmentState
{
    Unavailable,
    NotGenerated,
    Generating,
    Failed,
    Ready,
}

public sealed record GabrielAssessment(
    GabrielAssessmentState State,
    string Summary,
    string Details,
    int Successes = 0,
    int Samples = 0,
    double Probability = 0,
    double ConfidenceLow = 0,
    double ConfidenceHigh = 0);

public static class GabrielAssessmentService
{
    internal const int DefaultSamples = 100;

    private sealed record CacheEntry(Task<GabrielPluginPathEstimate> Task);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();
    private static readonly SemaphoreSlim EstimateGate = new(1, 1);

    public static bool TryAssessRecipe(
        uint recipeId,
        RecipeCraftSettings? settings,
        bool queue,
        out GabrielAssessment assessment)
    {
        var recipe = RecipeManager.GetRecipe(recipeId);
        if (!recipe.HasValue)
        {
            assessment = Unavailable("Recipe data could not be resolved.");
            return false;
        }
        var item = new CraftingListItem(recipeId, 1)
        {
            IsOriginalRecipe = true,
            CraftSettings = settings?.Clone(),
        };
        var executionContext = CraftingContextResolver.ResolveExecutionContext(item, recipe.Value, null);
        return TryAssessExecutionContext(recipe.Value, executionContext, queue, out assessment);
    }

    public static bool TryAssessListRecipe(
        uint recipeId,
        CraftingListDefinition list,
        RecipeCraftSettings? settings,
        bool queue,
        out GabrielAssessment assessment)
        => TryAssessListContext(recipeId, list, true, settings, queue, out assessment);

    public static bool TryAssessListPrecraft(
        uint recipeId,
        CraftingListDefinition list,
        RecipeCraftSettings? settings,
        bool queue,
        out GabrielAssessment assessment)
        => TryAssessListContext(recipeId, list, false, settings, queue, out assessment);

    private static bool TryAssessListContext(
        uint recipeId,
        CraftingListDefinition list,
        bool isOriginalRecipe,
        RecipeCraftSettings? settings,
        bool queue,
        out GabrielAssessment assessment)
    {
        var recipe = RecipeManager.GetRecipe(recipeId);
        if (!recipe.HasValue)
        {
            assessment = Unavailable("Recipe data could not be resolved.");
            return false;
        }
        if (!CraftingContextResolver.TryResolveListExecutionContext(
                list,
                recipeId,
                isOriginalRecipe,
                settings,
                out var executionContext))
        {
            assessment = Unavailable("List crafting settings could not be resolved.");
            return false;
        }
        return TryAssessExecutionContext(recipe.Value, executionContext, queue, out assessment);
    }

    internal static bool TryAssessExecutionContext(
        Recipe recipe,
        CraftingExecutionContext executionContext,
        bool queue,
        out GabrielAssessment assessment)
    {
        if (executionContext.EffectiveSolverMode != VulcanSolverMode.Gabriel)
        {
            assessment = Unavailable("Gabriel is inactive for the current item solver selection.");
            return false;
        }
        if (!CraftingContextResolver.TryBuildSimulationContext(
                recipe,
                executionContext,
                CraftingStatsSource.AlwaysGearsetStats,
                CraftingSimulationIntent.ValidatorPreview,
                out var context))
        {
            assessment = Unavailable("No current or saved gearset stats are available for this recipe.");
            return false;
        }
        if (!GabrielPolicyCatalog.TryResolve(context.SimulationState, out var policy, out var reason))
        {
            assessment = Unavailable(reason);
            return false;
        }

        var craft = context.SimulationState;
        var root = GameStateBuilder.BuildInitialStepState(craft, craft.InitialQuality);
        var workerThreads = DonatelloNative.ResolveGabrielWorkerThreads(craft.GabrielWorkerThreads);
        var key = $"gabriel/{(int)policy.Profile}/{context.RaphaelRequest.GetKey()}/{craft.RecipeLevelTableId}/{Simulator.BaseProgress(craft)}/{Simulator.BaseQuality(craft)}/{craft.CrafterDelineations}/{workerThreads}";
        if (Cache.TryGetValue(key, out var failedEntry)
         && (failedEntry.Task.IsFaulted || failedEntry.Task.IsCanceled)
         && queue)
        {
            Cache.TryRemove(key, out _);
        }
        if (!Cache.TryGetValue(key, out var entry) && queue)
        {
            if (CraftingProcessor.IsActive)
            {
                assessment = Unavailable("Probability estimation is disabled while a craft is active.");
                return false;
            }
            var seed = StableSeed(key);
            var craftCopy = craft with { CraftConditionProbabilities = [.. craft.CraftConditionProbabilities] };
            var rootCopy = root with { };
            entry = Cache.GetOrAdd(
                key,
                _ => new(Task.Run(() =>
                {
                    EstimateGate.Wait();
                    try
                    {
                        return CraftingPluginPathSimulator.EstimateGabriel(
                            craftCopy,
                            rootCopy,
                            DefaultSamples,
                            seed,
                            cancellationRequested: () => CraftingProcessor.IsActive);
                    }
                    finally
                    {
                        EstimateGate.Release();
                    }
                })));
        }
        if (entry == null)
        {
            assessment = new(
                GabrielAssessmentState.NotGenerated,
                "No chance estimate generated yet.",
                "Run the estimate to simulate this exact item, stat, tool, specialist, and condition-profile configuration.");
            return true;
        }
        if (!entry.Task.IsCompleted)
        {
            assessment = new(
                GabrielAssessmentState.Generating,
                "Estimating chance to reach full quality...",
                $"Running {DefaultSamples:N0} catalog-driven stochastic policy samples in the background.");
            return true;
        }
        if (entry.Task.IsFaulted)
        {
            var failure = entry.Task.Exception?.GetBaseException().Message ?? "Unknown Gabriel estimate failure.";
            assessment = new(
                GabrielAssessmentState.Failed,
                "Chance estimate failed.",
                failure);
            return false;
        }
        if (entry.Task.IsCanceled)
        {
            assessment = new(
                GabrielAssessmentState.Failed,
                "Chance estimate cancelled.",
                "A live craft started while the faithful plugin-path simulation was running. Retry after crafting stops.");
            return false;
        }

        var estimate = entry.Task.Result;
        var (low, high) = WilsonInterval(estimate.Successes, estimate.Samples);
        assessment = new(
            GabrielAssessmentState.Ready,
            FormatReadySummary(low, high),
            FormatReadyDetails(estimate.Successes, estimate.Samples, estimate.ElapsedMillis),
            estimate.Successes,
            estimate.Samples,
            estimate.Probability,
            low,
            high);
        return true;
    }

    internal static string FormatReadySummary(double confidenceLow, double confidenceHigh)
        => FormattableString.Invariant(
            $"Estimated {confidenceLow * 100:F2}-{confidenceHigh * 100:F2}% chance to reach full quality.");

    internal static string FormatReadyDetails(int successes, int samples, long elapsedMillis)
        => FormattableString.Invariant(
            $"{successes:N0}/{samples:N0} full quality completions, {elapsedMillis / 1000d:F1}s total time spent");

    private static GabrielAssessment Unavailable(string details)
        => new(
            GabrielAssessmentState.Unavailable,
            "Chance estimate unavailable.",
            details);

    private static ulong StableSeed(string value)
    {
        const ulong offset = 14_695_981_039_346_656_037;
        const ulong prime = 1_099_511_628_211;
        var hash = offset;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }
        return hash;
    }

    private static (double Low, double High) WilsonInterval(int successes, int samples)
    {
        const double z = 1.959963984540054;
        var n = Math.Max(1, samples);
        var probability = (double)successes / n;
        var denominator = 1 + z * z / n;
        var center = (probability + z * z / (2 * n)) / denominator;
        var half = z * Math.Sqrt(probability * (1 - probability) / n + z * z / (4 * n * n)) / denominator;
        return (Math.Max(0, center - half), Math.Min(1, center + half));
    }
}
