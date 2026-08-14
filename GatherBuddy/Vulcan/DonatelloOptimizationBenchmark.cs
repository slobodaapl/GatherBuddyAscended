using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GatherBuddy.Vulcan;

internal sealed record DonatelloOptimizationBenchmarkResult(
    uint RecipeId,
    int RecommendedMilliseconds,
    int ReferenceWins,
    int Scenarios,
    IReadOnlyDictionary<int, int> WinsByBudget);

internal static class DonatelloOptimizationBenchmark
{
    private const uint FixtureRecipeId = 38202;
    private const int ReferenceBudget = 2000;
    private static readonly int[] Budgets = [50, 100, 200, 500, 1000];
    private static readonly List<VulcanSkill> FixtureBaseline =
    [
        VulcanSkill.QuickInnovation,
        VulcanSkill.Reflect,
        VulcanSkill.Manipulation,
        VulcanSkill.Veneration,
        VulcanSkill.DelicateSynthesis,
        VulcanSkill.DelicateSynthesis,
        VulcanSkill.DelicateSynthesis,
        VulcanSkill.Innovation,
        VulcanSkill.DelicateSynthesis,
        VulcanSkill.BasicTouch,
        VulcanSkill.StandardTouch,
        VulcanSkill.AdvancedTouch,
        VulcanSkill.Manipulation,
        VulcanSkill.Innovation,
        VulcanSkill.Observe,
        VulcanSkill.AdvancedTouch,
        VulcanSkill.GreatStrides,
        VulcanSkill.ByregotsBlessing,
        VulcanSkill.TrainedPerfection,
        VulcanSkill.Veneration,
        VulcanSkill.Groundwork,
        VulcanSkill.CarefulSynthesis,
        VulcanSkill.CarefulSynthesis,
        VulcanSkill.HeartAndSoul,
        VulcanSkill.IntensiveSynthesis,
    ];
    private static readonly Condition[] Conditions =
    [
        Condition.Good,
        Condition.Excellent,
        Condition.GoodOmen,
    ];

    internal static Task<DonatelloOptimizationBenchmarkResult> RunAsync(
        Action<int, int>? progress = null)
        => Task.Run(() => Run(progress));

    private static DonatelloOptimizationBenchmarkResult Run(
        Action<int, int>? progress)
    {
        var (craft, _, scenarios) = CreateFixtureScenarios();
        if (scenarios.Count == 0)
            throw new InvalidOperationException("The selected recipe has no variable-condition replan scenarios.");

        var referenceWins = 0;
        var totalWork = scenarios.Count * (Budgets.Length + 1);
        var completed = 0;
        DonatelloNative.ClearCache();
        for (var index = 0; index < scenarios.Count; ++index)
        {
            var scenario = scenarios[index];
            var incumbent = DonatelloPlanEvaluator.Evaluate(craft, scenario.Root, scenario.Incumbent);
            var reference = DonatelloNative.SolveDetailed(
                craft,
                scenario.Root,
                allowSpecialistActions: true,
                DonatelloNative.SolveMode.LiveAdaptive,
                incumbent: scenario.Incumbent,
                softDeadlineMillis: ReferenceBudget,
                hardDeadlineMillis: ReferenceBudget,
                bypassSolutionCache: true,
                minimizeSteps: true);
            var evaluation = DonatelloPlanEvaluator.Evaluate(craft, scenario.Root, reference.Actions);
            if (evaluation.IsStrictlyBetterThan(incumbent))
                referenceWins++;
            progress?.Invoke(++completed, totalWork);
        }
        if (referenceWins == 0)
        {
            DonatelloNative.ClearCache();
            throw new InvalidOperationException(
                $"Benchmark inconclusive: Donatello found no improvements within {ReferenceBudget} ms across "
                + $"{scenarios.Count} known-win scenarios for recipe {FixtureRecipeId}. The threshold was not changed.");
        }

        var winsByBudget = new Dictionary<int, int>();
        var minimumWins = Math.Max(1, referenceWins - (int)Math.Floor(scenarios.Count * 0.05));
        int? recommended = null;
        foreach (var budget in Budgets)
        {
            var wins = 0;
            for (var index = 0; index < scenarios.Count; ++index)
            {
                var scenario = scenarios[index];
                var incumbent = DonatelloPlanEvaluator.Evaluate(craft, scenario.Root, scenario.Incumbent);
                var bounded = DonatelloNative.SolveDetailed(
                    craft,
                    scenario.Root,
                    allowSpecialistActions: true,
                    DonatelloNative.SolveMode.LiveAdaptive,
                    incumbent: scenario.Incumbent,
                    softDeadlineMillis: budget,
                    hardDeadlineMillis: budget,
                    bypassSolutionCache: true,
                    minimizeSteps: true);
                var evaluation = DonatelloPlanEvaluator.Evaluate(craft, scenario.Root, bounded.Actions);
                if (evaluation.IsStrictlyBetterThan(incumbent))
                    wins++;
                progress?.Invoke(++completed, totalWork);
            }
            winsByBudget.Add(budget, wins);
            if (wins >= minimumWins)
            {
                recommended = budget;
                break;
            }
        }
        winsByBudget.Add(ReferenceBudget, referenceWins);
        DonatelloNative.ClearCache();

        return new(
            FixtureRecipeId,
            recommended ?? ReferenceBudget,
            referenceWins,
            scenarios.Count,
            winsByBudget);
    }

    internal static (
        CraftState Craft,
        IReadOnlyList<VulcanSkill> Baseline,
        List<(StepState Root, List<VulcanSkill> Incumbent)> Scenarios)
        CreateFixtureScenarios()
    {
        var craft = new CraftState
        {
            RecipeId = FixtureRecipeId,
            ItemId = FixtureRecipeId,
            StatCraftsmanship = 5328,
            StatControl = 4779,
            StatCP = 573,
            StatLevel = 100,
            UnlockedManipulation = true,
            Specialist = true,
            CrafterDelineations = 2,
            CraftExpert = true,
            CraftLevel = 100,
            CraftDurability = 45,
            CraftProgress = 6900,
            CraftQualityMax = 22100,
            CraftProgressDivider = 170,
            CraftProgressModifier = 90,
            CraftQualityDivider = 150,
            CraftQualityModifier = 75,
            ConditionFlags = ConditionFlags.Normal
                | ConditionFlags.Good
                | ConditionFlags.Excellent
                | ConditionFlags.Poor
                | ConditionFlags.Centered
                | ConditionFlags.Sturdy
                | ConditionFlags.Pliant
                | ConditionFlags.Malleable
                | ConditionFlags.Primed
                | ConditionFlags.GoodOmen
                | ConditionFlags.Robust,
        };
        var initial = GameStateBuilder.BuildInitialStepState(craft);
        var baseline = FixtureBaseline;
        var baselineEvaluation = DonatelloPlanEvaluator.Evaluate(craft, initial, baseline);
        if (!baselineEvaluation.Completes)
        {
            var final = baselineEvaluation.Trajectory[^1];
            throw new InvalidOperationException(
                $"Benchmark fixture recipe {FixtureRecipeId} baseline no longer completes: "
                + $"executed={baselineEvaluation.Steps}, progress={final.Progress}/{craft.CraftProgress}, "
                + $"quality={final.Quality}/{craft.CraftQualityMax}, durability={final.Durability}, cp={final.RemainingCP}.");
        }
        var scenarios = new List<(StepState Root, List<VulcanSkill> Incumbent)>();
        foreach (var index in new[] { 20, 21, 22 })
        {
            foreach (var condition in Conditions)
            {
                scenarios.Add((
                    baselineEvaluation.Trajectory[index] with { Condition = condition },
                    baseline.GetRange(index, baseline.Count - index)));
            }
        }
        return (craft, baseline, scenarios);
    }
}
