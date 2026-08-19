using System.Diagnostics;
using System.Text.Json;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;

namespace GatherBuddy.Vulcan.Tests;

/// <summary>
/// Plugin-path Raphael vs Donatello.
/// Raphael plans once and follows the tape. Donatello starts from that tape
/// and replans through CraftingProcessor when a special condition appears.
/// Native search budgets are the plugin deadlines already implemented on
/// DonatelloSolver — this harness does not sleep those budgets.
/// </summary>
internal static class PluginPathRaphaelDonatelloBenchmark
{
    internal static readonly int[] DefaultDeadlines = [200, 2_000, 30_000];
    internal const int GraphCraftsPerBracket = 10;
    internal const int GraphLevel100Crafts = 10;
    internal const int GraphExpertCrafts = 10;

    internal static async Task RunGraph(
        string poolPath,
        int seed,
        IReadOnlyList<int>? deadlines,
        string resultsPath,
        string svgPath,
        Action<bool, string> require)
    {
        var pool = LoadPool(poolPath, require);
        var sampled = SampleGraphCrafts(pool, seed, require);
        await RunSampled(
            sampled,
            seed,
            deadlines,
            resultsPath,
            svgPath,
            require,
            graph: true);
    }

    internal static async Task Run(
        string poolPath,
        int crafts,
        int seed,
        IReadOnlyList<int>? deadlines,
        Action<bool, string> require)
    {
        require(crafts > 0, "plugin-path benchmark requires at least one craft");
        var sampled = SampleCrafts(poolPath, crafts, seed, require);
        await RunSampled(sampled, seed, deadlines, resultsPath: null, svgPath: null, require, graph: false);
    }

    internal static async Task Diagnose(
        string poolPath,
        int seed,
        uint recipeId,
        Action<bool, string> require)
    {
        var pool = LoadPool(poolPath, require);
        var sampled = SampleGraphCrafts(pool, seed, require);
        var index = sampled.FindIndex(sample => sample.Craft.RecipeId == recipeId);
        require(index >= 0, $"seed {seed} graph sample does not contain recipe {recipeId}");
        var sample = sampled[index];
        var prepared = await PrepareCraft(sample, seed + index + 1, require);
        var raphaelEval = DonatelloPlanEvaluator.Evaluate(
            prepared.Craft,
            prepared.Root,
            prepared.Incumbent.ActionIds.ConvertAll(id => (VulcanSkill)id));
        var created = DonatelloSolverDefinition.CreateFromSolution(prepared.Incumbent, prepared.Craft);
        Console.WriteLine(
            $"DIAG {sample.Label} index={index} recipe={prepared.Craft.RecipeId} "
            + $"lv={prepared.Craft.StatLevel} cms={prepared.Craft.StatCraftsmanship} "
            + $"ctl={prepared.Craft.StatControl} cp={prepared.Craft.StatCP} "
            + $"progress={prepared.Craft.CraftProgress} quality={prepared.Craft.CraftQualityMax} "
            + $"dur={prepared.Craft.CraftDurability} "
            + $"raphaelEval=completes={raphaelEval.Completes} q={raphaelEval.Quality} n={raphaelEval.Steps} t={raphaelEval.Duration} "
            + $"solverType={created.GetType().Name} maxQ={raphaelEval.Quality >= prepared.Craft.CraftQualityMax}");
        Console.WriteLine("RAPHAEL_PLAN " + string.Join(",", prepared.Incumbent.ActionIds.Select(id => (VulcanSkill)id)));
        DumpGoodRootAdmission(prepared, require);
    }

    private static void DumpGoodRootAdmission(PreparedCraft prepared, Action<bool, string> require)
    {
        var game = new PluginPathSimulationAcceptanceTests.SeededGame(
            prepared.Craft,
            prepared.Root,
            prepared.ActionSeed,
            prepared.ConditionSeed);
        var prefix = new List<VulcanSkill>();
        foreach (var action in prepared.Incumbent.ActionIds.Select(id => (VulcanSkill)id))
        {
            if (!Simulator.CanUseAction(prepared.Craft, game.State, action)
                || game.State.Progress >= prepared.Craft.CraftProgress)
                break;
            game.Execute(action, require);
            prefix.Add(action);
            if (game.State.Condition != Condition.Normal)
                break;
        }

        var root = game.State;
        Console.WriteLine(
            $"GOOD_ROOT cond={root.Condition} q={root.Quality} p={root.Progress} d={root.Durability} cp={root.RemainingCP} "
            + $"iq={root.IQStacks} gs={root.GreatStridesLeft} inn={root.InnovationLeft} wn={root.WasteNotLeft} combo={root.ComboAction}");
        VulcanSkill[][] plans =
        [
            [VulcanSkill.Innovation, VulcanSkill.BasicTouch, VulcanSkill.GreatStrides, VulcanSkill.StandardTouch, VulcanSkill.Veneration, VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis],
            [VulcanSkill.TricksOfTrade, VulcanSkill.Innovation, VulcanSkill.StandardTouch, VulcanSkill.GreatStrides, VulcanSkill.StandardTouch, VulcanSkill.Veneration, VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis],
            [VulcanSkill.BasicTouch, VulcanSkill.Innovation, VulcanSkill.GreatStrides, VulcanSkill.StandardTouch, VulcanSkill.Veneration, VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis],
            [VulcanSkill.StandardTouch, VulcanSkill.Innovation, VulcanSkill.GreatStrides, VulcanSkill.StandardTouch, VulcanSkill.Veneration, VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis],
            [VulcanSkill.BasicTouch, VulcanSkill.GreatStrides, VulcanSkill.StandardTouch, VulcanSkill.Veneration, VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis],
            [VulcanSkill.StandardTouch, VulcanSkill.GreatStrides, VulcanSkill.StandardTouch, VulcanSkill.Veneration, VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis],
            [VulcanSkill.TricksOfTrade, VulcanSkill.BasicTouch, VulcanSkill.Innovation, VulcanSkill.GreatStrides, VulcanSkill.StandardTouch, VulcanSkill.Veneration, VulcanSkill.BasicSynthesis, VulcanSkill.BasicSynthesis],
        ];
        foreach (var plan in plans)
        {
            var score = DonatelloPlanEvaluator.Evaluate(prepared.Craft, root, plan);
            Console.WriteLine(
                $"ALT completes={score.Completes} q={score.Quality} n={score.Steps} t={score.Duration} [{string.Join(",", plan)}]");
        }

        var native = DonatelloNative.SolveDetailed(
            prepared.Craft,
            root,
            allowSpecialistActions: false,
            DonatelloNative.SolveMode.OptimizeQuality,
            incumbent: prefix.Count < prepared.Incumbent.ActionIds.Count
                ? prepared.Incumbent.ActionIds.Skip(prefix.Count).Select(id => (VulcanSkill)id).ToList()
                : prepared.Incumbent.ActionIds.ConvertAll(id => (VulcanSkill)id),
            softDeadlineMillis: 30_000,
            hardDeadlineMillis: 30_000,
            bypassSolutionCache: true,
            minimizeSteps: true);
        var nativeScore = DonatelloPlanEvaluator.Evaluate(prepared.Craft, root, native.Actions);
        Console.WriteLine(
            $"NATIVE_30S elapsed={native.ElapsedMillis}ms optimal={native.Optimal} deadline={native.DeadlineReached} "
            + $"bound={native.QualityUpperBound} achieved={native.AchievedQuality} "
            + $"eval=completes={nativeScore.Completes} q={nativeScore.Quality} n={nativeScore.Steps} "
            + $"actions=[{string.Join(",", native.Actions)}]");
    }

    private static async Task TracePlay(
        PreparedCraft prepared,
        ISolverDefinition definition,
        bool waitForOpportunistic,
        string name,
        Action<bool, string> require)
    {
        CraftingProcessor.Setup();
        CraftingProcessor.RegisterSolver(definition);
        CraftingProcessor.OnCraftStarted(prepared.Craft, prepared.Root, prepared.Craft.RecipeId, isTrial: false);
        var game = new PluginPathSimulationAcceptanceTests.SeededGame(
            prepared.Craft,
            prepared.Root,
            prepared.ActionSeed,
            prepared.ConditionSeed);
        var current = prepared.Root;
        Console.WriteLine(
            $"TRACE_START {name} solver={CraftingProcessor.ActiveSolver?.GetType().Name} "
            + $"q={current.Quality} p={current.Progress} cond={current.Condition}");
        try
        {
            for (var actionNumber = 1; actionNumber <= 100; ++actionNumber)
            {
                var remainingBefore = CraftingProcessor.ActiveSolver is DonatelloSolver before
                    ? string.Join(",", before.RemainingActions)
                    : "";
                var replansBefore = CraftingProcessor.ActiveSolver is DonatelloSolver counted
                    ? counted.NativeReplanCount
                    : 0;
                var recommendation = await AwaitPluginRecommendation(
                    current,
                    TimeSpan.FromSeconds(40),
                    waitForOpportunistic);
                var remainingAfter = CraftingProcessor.ActiveSolver is DonatelloSolver after
                    ? string.Join(",", after.RemainingActions)
                    : "";
                var replansAfter = CraftingProcessor.ActiveSolver is DonatelloSolver countedAfter
                    ? countedAfter.NativeReplanCount
                    : 0;
                if (recommendation.IsTerminalFailure || recommendation.Action == VulcanSkill.None)
                {
                    Console.WriteLine($"TRACE_STOP {name} #{actionNumber} {recommendation.Comment}");
                    return;
                }

                var executed = game.SelectAction(recommendation.Action, out _);
                if (replansAfter > replansBefore && CraftingProcessor.ActiveSolver is DonatelloSolver)
                {
                    var incumbentPlan = ParseActions(remainingBefore);
                    var candidatePlan = new List<VulcanSkill> { executed };
                    candidatePlan.AddRange(ParseActions(remainingAfter));
                    var incumbentScore = DonatelloPlanEvaluator.Evaluate(prepared.Craft, current, incumbentPlan);
                    var candidateScore = DonatelloPlanEvaluator.Evaluate(prepared.Craft, current, candidatePlan);
                    Console.WriteLine(
                        $"ADMIT {name} #{actionNumber} root={current.Condition} q={current.Quality} p={current.Progress} cp={current.RemainingCP} "
                        + $"incumbent=[{remainingBefore}] => completes={incumbentScore.Completes} q={incumbentScore.Quality} n={incumbentScore.Steps} t={incumbentScore.Duration} "
                        + $"candidate=[{string.Join(",", candidatePlan)}] => completes={candidateScore.Completes} q={candidateScore.Quality} n={candidateScore.Steps} t={candidateScore.Duration} "
                        + $"strictlyBetter={candidateScore.IsStrictlyBetterThan(incumbentScore)} "
                        + $"adopt={DonatelloSolver.ShouldAdoptCandidate(prepared.Craft, candidateScore, incumbentScore, stagedProgressPlan: false)}");
                    foreach (var (label, score) in new[] { ("incumbent", incumbentScore), ("candidate", candidateScore) })
                    {
                        Console.WriteLine(
                            $"ADMIT_TRAJ {name} #{actionNumber} {label} "
                            + string.Join(" | ", score.Trajectory.Select(step =>
                                $"q={step.Quality} p={step.Progress} d={step.Durability} cp={step.RemainingCP} {step.Condition}")));
                    }
                }

                var actual = game.Execute(executed, require);
                Console.WriteLine(
                    $"TRACE {name} #{actionNumber} rec={recommendation.Action} comment={recommendation.Comment} "
                    + $"cond={current.Condition}->{actual.Condition} q={actual.Quality} p={actual.Progress}/{prepared.Craft.CraftProgress} "
                    + $"dur={actual.Durability} cp={actual.RemainingCP} replans={replansBefore}->{replansAfter} "
                    + $"planBefore=[{remainingBefore}] planAfter=[{remainingAfter}]");
                if (CraftingProcessor.ActiveSolver is DonatelloSolver issued)
                    issued.NotifyOpportunisticActionIssued();
                if (actual.Progress >= prepared.Craft.CraftProgress || actual.Durability <= 0)
                {
                    Console.WriteLine(
                        $"TRACE_END {name} complete={actual.Progress >= prepared.Craft.CraftProgress} "
                        + $"q={Math.Min(actual.Quality, prepared.Craft.CraftQualityMax)} n={actionNumber}");
                    CraftingProcessor.OnCraftFinished(prepared.Craft, actual, prepared.Craft.RecipeId, cancelled: false);
                    return;
                }

                current = PluginPathSimulationAcceptanceTests.ReconcileRecommended(
                    prepared.Craft,
                    current,
                    executed,
                    actual,
                    require);
                CraftingProcessor.OnCraftAdvanced(prepared.Craft, current, prepared.Craft.RecipeId);
            }
        }
        finally
        {
            CraftingProcessor.Dispose();
        }
    }

    private static async Task RunSampled(
        IReadOnlyList<SampledCraft> sampled,
        int seed,
        IReadOnlyList<int>? deadlines,
        string? resultsPath,
        string? svgPath,
        Action<bool, string> require,
        bool graph)
    {
        require(sampled.Count > 0, "plugin-path benchmark sampled no recipes");
        var budgetList = (deadlines ?? DefaultDeadlines).Where(deadline => deadline > 0).ToArray();
        require(budgetList.Length > 0, "plugin-path benchmark requires at least one positive deadline");

        var config = global::GatherBuddy.GatherBuddy.Config;
        var raphaelConfig = config.RaphaelSolverConfig;
        var previousDelay = config.VulcanExecutionDelayMs;
        var previousThreshold = raphaelConfig.DonatelloOptimizationThresholdMs;
        var previousTimeout = raphaelConfig.RaphaelTimeoutMinutes;
        raphaelConfig.RaphaelTimeoutMinutes = Math.Max(previousTimeout, 2);

        Console.WriteLine(
            $"Plugin-path Raphael vs Donatello: crafts={sampled.Count} deadlines=[{string.Join(",", budgetList)}] seed={seed}");

        var totals = budgetList.ToDictionary(deadline => deadline, _ => new Tally());
        var series = new Dictionary<(string Key, int Level, bool Expert, int Deadline), Tally>();
        var crafts = new List<CraftRecord>();
        try
        {
            for (var index = 0; index < sampled.Count; ++index)
            {
                var sample = sampled[index];
                var craftSeed = seed + index + 1;
                var prepared = await PrepareCraft(sample, craftSeed, require);
                var raphaelPlay = await Play(
                    prepared.Craft,
                    prepared.Root,
                    new SeededRaphaelDefinition(prepared.Incumbent),
                    prepared.ActionSeed,
                    prepared.ConditionSeed,
                    TimeSpan.FromSeconds(15),
                    waitForOpportunistic: false,
                    require,
                    prepared.Label + " Raphael");

                foreach (var deadline in budgetList)
                {
                    raphaelConfig.DonatelloOptimizationThresholdMs = Math.Clamp(deadline, 10, 10_000);
                    var donatelloCraft = prepared.Craft with
                    {
                        DonatelloOptions = new DonatelloExecutionOptions(
                            DonatelloSolveObjective.MaximizeQuality,
                            MinimizeSteps: true,
                            ReplanDeadlineMillis: deadline),
                    };
                    var donatelloPlay = await Play(
                        donatelloCraft,
                        prepared.Root,
                        new SeededDonatelloDefinition(prepared.Incumbent),
                        prepared.ActionSeed,
                        prepared.ConditionSeed,
                        TimeSpan.FromMilliseconds(Math.Max(deadline, DonatelloSolver.ProtectedRaphaelTakeoverDeadlineMillis))
                            + TimeSpan.FromSeconds(5),
                        waitForOpportunistic: true,
                        require,
                        prepared.Label + " Donatello");
                    var verdict = Compare(donatelloPlay, prepared.IncumbentSolved);
                    totals[deadline].Add(verdict);
                    var seriesKey = (sample.Series, sample.Level, sample.Expert, deadline);
                    if (!series.TryGetValue(seriesKey, out var seriesTally))
                    {
                        seriesTally = new Tally();
                        series[seriesKey] = seriesTally;
                    }
                    seriesTally.Add(verdict);
                    crafts.Add(new(
                        sample.Label,
                        sample.Series,
                        sample.Level,
                        sample.Expert,
                        deadline,
                        raphaelPlay,
                        donatelloPlay,
                        verdict.ToString()));
                    Console.WriteLine(
                        $"  [{deadline}ms {index + 1}/{sampled.Count}] {prepared.Label} "
                        + $"incumbent={prepared.IncumbentSolved.Describe()} "
                        + $"Rlive={raphaelPlay.Describe()} D={donatelloPlay.Describe()} {verdict}");
                }

                if (resultsPath != null)
                    WriteResults(resultsPath, seed, budgetList, sampled.Count, index + 1, totals, series, crafts);
                if (svgPath != null)
                    WriteSvg(svgPath, seed, budgetList, series, crafts.Count);
            }

            foreach (var deadline in budgetList)
            {
                var tally = totals[deadline];
                require(tally.Losses == 0,
                    $"Donatello lost {tally.Losses} crafts at {deadline} ms; never-worse forbids a worse plugin result");
                var decided = tally.Wins + tally.Ties;
                Console.WriteLine(
                    $"Deadline {deadline} ms: wins={tally.Wins} ties={tally.Ties} losses={tally.Losses} "
                    + $"winRate={(decided == 0 ? 0 : (double)tally.Wins / decided):P1}");
            }

            if (graph)
            {
                foreach (var group in series.Keys.Select(key => (key.Key, key.Level, key.Expert)).Distinct())
                    require(
                        series.Where(entry => entry.Key.Key == group.Key
                                && entry.Key.Level == group.Level
                                && entry.Key.Expert == group.Expert)
                            .Sum(entry => entry.Value.Wins + entry.Value.Ties + entry.Value.Losses)
                            > 0,
                        $"graph series {group.Key} L{group.Level} produced no crafts");
            }
        }
        finally
        {
            config.VulcanExecutionDelayMs = previousDelay;
            raphaelConfig.DonatelloOptimizationThresholdMs = previousThreshold;
            raphaelConfig.RaphaelTimeoutMinutes = previousTimeout;
            CraftingProcessor.Dispose();
            DonatelloNative.ClearCache();
        }
    }

    private static async Task<PreparedCraft> PrepareCraft(
        SampledCraft sampled,
        int seed,
        Action<bool, string> require)
    {
        var craft = sampled.Craft;
        if (craft.CraftExpert)
        {
            require(
                craft.CraftConditionProfileCataloged
                && craft.CraftConditionProbabilities.Length >= 11,
                $"{sampled.Label}: expert crafts must use the cataloged condition vector");
        }
        else
        {
            require(
                craft.CraftConditionProbabilities.Length >= 3
                && Math.Abs(craft.CraftConditionProbabilities[2] - 0.04f) < 0.0001f,
                $"{sampled.Label}: normal crafts must use Excellent 4%");
            var expectedGood = craft.StatLevel >= 63 ? 0.25f : 0.20f;
            require(
                Math.Abs(craft.CraftConditionProbabilities[1] - expectedGood) < 0.0001f,
                $"{sampled.Label}: Good must be {expectedGood:P0} at level {craft.StatLevel}");
        }

        var root = GameStateBuilder.BuildInitialStepState(craft);
        DonatelloNative.ClearCache();
        var raphael = DonatelloNative.SolveDetailed(
            craft,
            root,
            allowSpecialistActions: false,
            DonatelloNative.SolveMode.OptimizeQuality,
            softDeadlineMillis: 30_000,
            hardDeadlineMillis: 30_000,
            bypassSolutionCache: true);
        require(raphael.Actions.Count > 0, $"{sampled.Label}: Raphael must produce an incumbent");
        var incumbentActions = raphael.Actions.Select(action => (uint)action).ToList();
        var incumbentSolved = Score(
            craft,
            DonatelloPlanEvaluator.Evaluate(
                craft,
                root,
                incumbentActions.ConvertAll(id => (VulcanSkill)id)));
        return new(
            sampled.Label,
            craft,
            root,
            new CachedRaphaelSolution
            {
                ActionIds = incumbentActions,
            },
            incumbentSolved,
            0xA11CEu + (uint)seed * 0x101u,
            0xC0FFEEu + (uint)seed * 0x10001u);
    }

    private static async Task<PlayScore> Play(
        CraftState craft,
        StepState root,
        ISolverDefinition definition,
        uint actionSeed,
        uint conditionSeed,
        TimeSpan recommendationTimeout,
        bool waitForOpportunistic,
        Action<bool, string> require,
        string label)
    {
        CraftingProcessor.Setup();
        CraftingProcessor.RegisterSolver(definition);
        CraftingProcessor.OnCraftStarted(craft, root, craft.RecipeId, isTrial: false);
        var game = new PluginPathSimulationAcceptanceTests.SeededGame(
            craft,
            root,
            actionSeed,
            conditionSeed);
        var current = root;
        var actions = 0;
        var duration = 0;
        try
        {
            for (var actionNumber = 1; actionNumber <= 100; ++actionNumber)
            {
                var recommendation = await AwaitPluginRecommendation(
                    current,
                    recommendationTimeout,
                    waitForOpportunistic);
                if (recommendation.IsTerminalFailure || recommendation.Action == VulcanSkill.None)
                {
                    return new(
                        Completes: false,
                        Quality: Math.Min(current.Quality, craft.CraftQualityMax),
                        Steps: actions,
                        Duration: duration);
                }

                require(
                    Simulator.CanUseAction(craft, current, recommendation.Action),
                    $"{label}: illegal recommendation {recommendation.Action} at {current}");
                var executed = game.SelectAction(recommendation.Action, out var manual);
                require(!manual, $"{label}: unexpected scripted manual action");
                var actual = game.Execute(executed, require);
                actions++;
                duration += ActionDuration(executed);
                if (CraftingProcessor.ActiveSolver is DonatelloSolver issued)
                    issued.NotifyOpportunisticActionIssued();
                if (actual.Progress >= craft.CraftProgress || actual.Durability <= 0)
                {
                    CraftingProcessor.OnCraftFinished(craft, actual, craft.RecipeId, cancelled: false);
                    return Score(craft, actual, actions, duration);
                }

                current = PluginPathSimulationAcceptanceTests.ReconcileRecommended(
                    craft,
                    current,
                    executed,
                    actual,
                    require);
                CraftingProcessor.OnCraftAdvanced(craft, current, craft.RecipeId);
            }
        }
        finally
        {
            CraftingProcessor.Dispose();
        }

        return new(false, Math.Min(current.Quality, craft.CraftQualityMax), actions, duration);
    }

    /// <summary>
    /// Mirrors CraftingGameInterop: poll CraftingProcessor.Update until the
    /// solver emits an action (blocking replans return when the native deadline
    /// expires), then apply a completed opportunistic result against the live
    /// step. Action delay is the only wall-clock wait; solver deadlines are
    /// native search budgets.
    /// </summary>
    private static async Task<Solver.Recommendation> AwaitPluginRecommendation(
        StepState liveStep,
        TimeSpan timeout,
        bool waitForOpportunistic)
    {
        var recommendation = await PluginPathSimulationAcceptanceTests.AwaitRecommendation(timeout);
        if (!waitForOpportunistic || CraftingProcessor.ActiveSolver is not DonatelloSolver donatello)
            return RecommendationForExecution(liveStep, recommendation);

        recommendation = RecommendationForExecution(liveStep, recommendation);
        if (!donatello.HasPendingOpportunisticReplan)
            return recommendation;

        var delayMs = Math.Max(0, global::GatherBuddy.GatherBuddy.Config.VulcanExecutionDelayMs);
        var delay = Stopwatch.StartNew();
        while (donatello.HasPendingOpportunisticReplan && delay.ElapsedMilliseconds < delayMs)
        {
            await Task.Delay(1);
            recommendation = RecommendationForExecution(liveStep, CraftingProcessor.NextRecommendation);
        }

        return RecommendationForExecution(liveStep, recommendation);
    }

    private static Solver.Recommendation RecommendationForExecution(
        StepState liveStep,
        Solver.Recommendation solverRecommendation)
    {
        CraftingProcessor.Update();
        var recommendation = CraftingProcessor.NextRecommendation.Action != VulcanSkill.None
            || CraftingProcessor.NextRecommendation.IsTerminalFailure
            ? CraftingProcessor.NextRecommendation
            : solverRecommendation;
        if (CraftingProcessor.ActiveSolver is DonatelloSolver donatello
            && recommendation.Action != VulcanSkill.None
            && donatello.TryApplyCompletedOpportunisticReplan(liveStep, out var refreshed)
            && refreshed.Action != VulcanSkill.None)
            return refreshed;
        return recommendation;
    }

    private static PlayScore Score(CraftState craft, StepState final, int actions, int duration)
        => new(
            final.Progress >= craft.CraftProgress,
            Math.Min(final.Quality, craft.CraftQualityMax),
            actions,
            duration);

    private static PlayScore Score(CraftState craft, DonatelloPlanEvaluation evaluation)
        => new(
            evaluation.Completes,
            Math.Min(evaluation.Quality, craft.CraftQualityMax),
            evaluation.Steps,
            evaluation.Duration);

    private static Comparison Compare(PlayScore donatello, PlayScore incumbentSolved)
    {
        if (donatello.Completes != incumbentSolved.Completes)
            return donatello.Completes ? Comparison.Win : Comparison.Loss;
        if (donatello.Quality != incumbentSolved.Quality)
            return donatello.Quality > incumbentSolved.Quality ? Comparison.Win : Comparison.Loss;
        if (donatello.Steps != incumbentSolved.Steps)
            return donatello.Steps < incumbentSolved.Steps ? Comparison.Win : Comparison.Loss;
        if (donatello.Duration != incumbentSolved.Duration)
            return donatello.Duration < incumbentSolved.Duration ? Comparison.Win : Comparison.Loss;
        return Comparison.Tie;
    }

    private static int ActionDuration(VulcanSkill action)
        => action is VulcanSkill.WasteNot or VulcanSkill.WasteNot2 or VulcanSkill.Veneration
            or VulcanSkill.Innovation or VulcanSkill.GreatStrides or VulcanSkill.Manipulation
            or VulcanSkill.StellarSteadyHand
            ? 2
            : 3;

    private static List<SampledCraft> SampleCrafts(
        string path,
        int crafts,
        int seed,
        Action<bool, string> require)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("brackets", out _))
            return SampleFromPool(LoadPoolFromJson(json, require), crafts, seed, require);
        return SampleFromCorpus(json, crafts, seed, require);
    }

    private static BenchmarkPool LoadPool(string path, Action<bool, string> require)
        => LoadPoolFromJson(File.ReadAllText(path), require);

    private static BenchmarkPool LoadPoolFromJson(string json, Action<bool, string> require)
    {
        var pool = JsonSerializer.Deserialize<BenchmarkPool>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("plugin-path benchmark pool was empty");
        require(pool.Version == 1, "plugin-path benchmark pool version must be 1");
        require(pool.Brackets.Count == 10, "plugin-path benchmark pool must contain 10 regular level brackets");
        return pool;
    }

    private static List<SampledCraft> SampleFromPool(
        BenchmarkPool pool,
        int crafts,
        int seed,
        Action<bool, string> require)
    {
        require(pool.Brackets.Count > 0, "plugin-path benchmark pool has no level brackets");
        var rng = new Random(seed);
        var sampled = new List<SampledCraft>();
        var cursor = 0;
        var attempts = 0;
        while (sampled.Count < crafts && attempts < crafts * 32)
        {
            attempts++;
            var bracket = pool.Brackets[cursor % pool.Brackets.Count];
            cursor++;
            if (!TryRollCraft(bracket, rng, expert: false, lockLevel: null, recipeJobLevel: null, out var craft, out var label))
                continue;
            sampled.Add(new(label, $"regular-{bracket.JobLevel}", bracket.JobLevel, false, craft));
        }

        require(sampled.Count == crafts,
            $"plugin-path benchmark could only sample {sampled.Count}/{crafts} crafts from the pool");
        return sampled;
    }

    private static List<SampledCraft> SampleGraphCrafts(
        BenchmarkPool pool,
        int seed,
        Action<bool, string> require)
    {
        var rng = new Random(seed);
        var sampled = new List<SampledCraft>();
        foreach (var bracket in pool.Brackets.OrderBy(item => item.JobLevel))
        {
            sampled.AddRange(SampleBracket(
                bracket,
                rng,
                GraphCraftsPerBracket,
                expert: false,
                lockLevel: null,
                recipeJobLevel: null,
                $"regular-{bracket.JobLevel}",
                bracket.JobLevel,
                require));
        }

        var level100 = pool.Brackets.Single(bracket => bracket.JobLevel == 100);
        sampled.AddRange(SampleBracket(
            level100,
            rng,
            GraphLevel100Crafts,
            expert: false,
            lockLevel: 100,
            recipeJobLevel: 100,
            "level100",
            100,
            require));

        require(pool.ExpertBrackets.Count >= 3, "plugin-path benchmark pool must contain expert bands at 80, 90, and 100");
        foreach (var expert in pool.ExpertBrackets.OrderBy(item => item.JobLevel))
        {
            require(expert.Recipes.Count >= GraphExpertCrafts,
                $"expert band L{expert.JobLevel} must contain at least {GraphExpertCrafts} recipes");
            sampled.AddRange(SampleBracket(
                expert,
                rng,
                GraphExpertCrafts,
                expert: true,
                lockLevel: expert.JobLevel,
                recipeJobLevel: null,
                $"expert-{expert.JobLevel}",
                expert.JobLevel,
                require));
        }
        return sampled;
    }

    private static List<SampledCraft> SampleBracket(
        BenchmarkBracket bracket,
        Random rng,
        int count,
        bool expert,
        int? lockLevel,
        int? recipeJobLevel,
        string series,
        int level,
        Action<bool, string> require)
    {
        var sampled = new List<SampledCraft>();
        var usedRecipes = new HashSet<uint>();
        var attempts = 0;
        while (sampled.Count < count && attempts < count * 64)
        {
            attempts++;
            if (!TryRollCraft(bracket, rng, expert, lockLevel, recipeJobLevel, out var craft, out var label))
                continue;
            if (!usedRecipes.Add(craft.RecipeId) && bracket.Recipes.Count >= count)
                continue;
            sampled.Add(new(label, series, level, expert, craft));
        }

        require(sampled.Count == count,
            $"could not sample {count} {series} crafts from L{bracket.JobLevel}; got {sampled.Count}");
        return sampled;
    }

    private static bool TryRollCraft(
        BenchmarkBracket bracket,
        Random rng,
        bool expert,
        int? lockLevel,
        int? recipeJobLevel,
        out CraftState craft,
        out string label)
    {
        craft = default!;
        label = string.Empty;
        if (bracket.Recipes.Count == 0)
            return false;

        for (var attempt = 0; attempt < 16; ++attempt)
        {
            var level = lockLevel ?? rng.Next(bracket.LevelLow, bracket.JobLevel + 1);
            var craftsmanship = NextInclusive(rng, bracket.CraftsmanshipLow, bracket.CraftsmanshipHigh);
            var control = NextInclusive(rng, bracket.ControlLow, bracket.ControlHigh);
            var cp = NextInclusive(rng, bracket.CpLow, bracket.CpHigh);
            var eligible = bracket.Recipes
                .Where(recipe => recipe.Expert == expert
                    && recipe.ReqCraftsmanship <= craftsmanship
                    && recipe.ReqControl <= control
                    && (recipeJobLevel == null || recipe.RecipeJobLevel == recipeJobLevel))
                .ToList();
            if (eligible.Count == 0)
                continue;

            var recipe = eligible[rng.Next(eligible.Count)];
            if (recipe.ProgressDiv <= 0
                || recipe.QualityDiv <= 0
                || recipe.MaxDurability <= 0
                || recipe.MaxProgress <= 0
                || recipe.MaxQuality <= 0
                || recipe.MaxProgress > ushort.MaxValue
                || recipe.MaxQuality > ushort.MaxValue)
                continue;

            var flags = expert
                ? ConditionFlags.Normal | ConditionFlags.Good | ConditionFlags.Excellent
                    | ConditionFlags.Poor | ConditionFlags.Centered | ConditionFlags.Sturdy
                    | ConditionFlags.Pliant | ConditionFlags.Malleable | ConditionFlags.Primed
                    | ConditionFlags.GoodOmen | ConditionFlags.Robust
                : ConditionFlags.Normal | ConditionFlags.Good | ConditionFlags.Excellent | ConditionFlags.Poor;
            var tableId = (ushort)recipe.RecipeLevelTableId;
            if (expert && !ExpertConditionProfileCatalog.TryGet(tableId, out _))
                continue;

            craft = new CraftState
            {
                RecipeId = recipe.RecipeId,
                ItemId = recipe.ItemId,
                StatCraftsmanship = craftsmanship,
                StatControl = control,
                StatCP = cp,
                StatLevel = level,
                UnlockedManipulation = level >= 65,
                Specialist = false,
                CraftHQ = true,
                CraftExpert = expert,
                RecipeLevelTableId = tableId,
                CraftLevel = recipe.RecipeJobLevel,
                CraftDurability = recipe.MaxDurability,
                CraftProgress = recipe.MaxProgress,
                CraftQualityMax = recipe.MaxQuality,
                CraftProgressDivider = recipe.ProgressDiv,
                CraftProgressModifier = recipe.ProgressMod,
                CraftQualityDivider = recipe.QualityDiv,
                CraftQualityModifier = recipe.QualityMod,
                ConditionFlags = flags,
                CraftConditionProbabilities = GameStateBuilder.GetConditionProbabilities(
                    flags,
                    level,
                    expert,
                    tableId),
                CraftConditionProfileCataloged = expert
                    && ExpertConditionProfileCatalog.TryGet(tableId, out _),
                DonatelloOptions = new DonatelloExecutionOptions(),
            };
            label = $"{(expert ? "E" : "L")}{bracket.JobLevel}-lv{level}-r{recipe.RecipeId}-cms{craftsmanship}";
            return true;
        }

        return false;
    }

    private static List<SampledCraft> SampleFromCorpus(
        string json,
        int crafts,
        int seed,
        Action<bool, string> require)
    {
        var corpus = JsonSerializer.Deserialize<Corpus>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("plugin-path benchmark corpus was empty");
        var cases = corpus.Cases
            .Where(testCase => testCase.Expert == false
                && testCase.Name.StartsWith('L')
                && testCase.RecipeId != null)
            .ToList();
        require(cases.Count > 0, "plugin-path benchmark corpus has no normal recipes");
        var rng = new Random(seed);
        var groups = cases
            .GroupBy(testCase => testCase.JobLevel ?? 0)
            .OrderBy(group => group.Key)
            .Select(group => group.OrderBy(_ => rng.Next()).ToList())
            .Where(group => group.Count > 0)
            .ToList();
        var sampled = new List<SampledCraft>();
        var cursor = 0;
        while (sampled.Count < crafts)
        {
            var group = groups[cursor % groups.Count];
            var testCase = group[rng.Next(group.Count)];
            var craft = JitterCorpusCraft(PluginPathRecipeMatrix.BuildCraft(testCase), rng);
            sampled.Add(new(
                $"{testCase.Name}-cms{craft.StatCraftsmanship}",
                $"regular-{testCase.JobLevel ?? 0}",
                testCase.JobLevel ?? 0,
                false,
                craft));
            cursor++;
            if (cursor > crafts * groups.Count)
                break;
        }

        return sampled;
    }

    private static CraftState JitterCorpusCraft(CraftState craft, Random rng)
    {
        var level = Math.Clamp(craft.StatLevel + rng.Next(-4, 5), Math.Max(1, craft.StatLevel - 9), craft.StatLevel);
        var craftsmanship = JitterStat(rng, craft.StatCraftsmanship);
        var control = JitterStat(rng, craft.StatControl);
        var cp = JitterStat(rng, craft.StatCP);
        var flags = craft.ConditionFlags;
        return craft with
        {
            StatCraftsmanship = craftsmanship,
            StatControl = control,
            StatCP = cp,
            StatLevel = level,
            UnlockedManipulation = level >= 65,
            CraftConditionProbabilities = GameStateBuilder.GetConditionProbabilities(
                flags,
                level,
                craft.CraftExpert),
        };
    }

    private static int JitterStat(Random rng, int value)
    {
        var low = Math.Max(1, value * 85 / 100);
        var high = Math.Max(low, value * 110 / 100);
        return NextInclusive(rng, low, high);
    }

    private static List<VulcanSkill> ParseActions(string text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Enum.Parse<VulcanSkill>)
                .ToList();

    private static int NextInclusive(Random rng, int low, int high)
    {
        var min = Math.Min(low, high);
        var max = Math.Max(low, high);
        return rng.Next(min, max + 1);
    }

    private static void WriteResults(
        string path,
        int seed,
        IReadOnlyList<int> deadlines,
        int totalCrafts,
        int completedCrafts,
        IReadOnlyDictionary<int, Tally> totals,
        IReadOnlyDictionary<(string Key, int Level, bool Expert, int Deadline), Tally> series,
        IReadOnlyList<CraftRecord> crafts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var document = new
        {
            seed,
            deadlines,
            totalCrafts,
            completedCrafts,
            totals = totals.Select(entry => new
            {
                deadline = entry.Key,
                entry.Value.Wins,
                entry.Value.Ties,
                entry.Value.Losses,
                winRate = entry.Value.WinRate,
            }),
            series = series
                .OrderBy(entry => entry.Key.Expert)
                .ThenBy(entry => entry.Key.Level)
                .ThenBy(entry => entry.Key.Deadline)
                .Select(entry => new
                {
                    key = entry.Key.Key,
                    level = entry.Key.Level,
                    expert = entry.Key.Expert,
                    deadline = entry.Key.Deadline,
                    entry.Value.Wins,
                    entry.Value.Ties,
                    entry.Value.Losses,
                    winRate = entry.Value.WinRate,
                }),
            crafts = crafts.Select(craft => new
            {
                craft.Label,
                craft.Series,
                craft.Level,
                craft.Expert,
                craft.Deadline,
                raphael = craft.Raphael.Describe(),
                donatello = craft.Donatello.Describe(),
                craft.Verdict,
            }),
        };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteSvg(
        string path,
        int seed,
        IReadOnlyList<int> deadlines,
        IReadOnlyDictionary<(string Key, int Level, bool Expert, int Deadline), Tally> series,
        int comparisons)
    {
        const double left = 120;
        const double right = 1110;
        const double top = 150;
        const double bottom = 560;
        var regularLevels = new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        var deadline = deadlines.DefaultIfEmpty(2_000).Max();
        double X(int level) => left + (level - 10) / 90.0 * (right - left);
        double Y(double rate) => bottom - Math.Clamp(rate, 0, 1) * (bottom - top);

        Tally? RegularTally(int level)
        {
            var key = level == 100
                ? series.Keys.FirstOrDefault(item => item.Key == "level100" && item.Deadline == deadline)
                : series.Keys.FirstOrDefault(item =>
                    item.Key == $"regular-{level}" && item.Level == level && !item.Expert && item.Deadline == deadline);
            return key.Key == null || !series.TryGetValue(key, out var tally) || tally.Total == 0
                ? null
                : tally;
        }

        var points = new List<string>();
        var pointMarkup = new System.Text.StringBuilder();
        foreach (var level in regularLevels)
        {
            var tally = RegularTally(level);
            if (tally == null)
                continue;
            points.Add($"{X(level):0.##},{Y(tally.WinRate):0.##}");
            pointMarkup.AppendLine(
                $"    <circle cx=\"{X(level):0.##}\" cy=\"{Y(tally.WinRate):0.##}\" r=\"6\" fill=\"#111827\" stroke=\"#5eead4\" stroke-width=\"3\"/>");
        }

        var labelMarkup = new System.Text.StringBuilder();
        if (RegularTally(10) is { } first)
            labelMarkup.AppendLine(
                $"    <text x=\"{X(10) + 12:0.##}\" y=\"{Y(first.WinRate) - 12:0.##}\" fill=\"#f8fafc\" font-size=\"17\" font-weight=\"600\">{first.WinRate:P0}</text>");
        if (RegularTally(100) is { } last)
            labelMarkup.AppendLine(
                $"    <text x=\"{X(100) - 16:0.##}\" y=\"{Y(last.WinRate) + 24:0.##}\" fill=\"#f8fafc\" font-size=\"18\" font-weight=\"600\" text-anchor=\"end\">{last.WinRate:P0}</text>");

        foreach (var expertKey in series.Keys
                     .Where(item => item.Expert && item.Deadline == deadline)
                     .OrderBy(item => item.Level))
        {
            if (!series.TryGetValue(expertKey, out var expert) || expert.Total == 0)
                continue;
            var ex = X(expertKey.Level);
            var ey = Y(expert.WinRate);
            pointMarkup.AppendLine(
                $"    <path d=\"M{ex:0.##} {ey - 9:0.##}l9 9-9 9-9-9z\" fill=\"#c084fc\" stroke=\"#111827\" stroke-width=\"3\"/>");
        }

        var line = points.Count == 0 ? "" : $"    <path d=\"M{string.Join("L", points)}\" fill=\"none\" stroke=\"#2dd4bf\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>";
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="1200" height="675" viewBox="0 0 1200 675" role="img" aria-labelledby="title desc">
              <title id="title">Donatello improvement rate by recipe level</title>
              <desc id="desc">Share of crafts where Donatello improved on the solved Raphael incumbent, by level bracket. Expert recipes at 80, 90, and 100 are marked separately.</desc>
              <rect width="1200" height="675" fill="#111827"/>
              <g font-family="Inter,Segoe UI,sans-serif">
                <text x="88" y="63" fill="#f8fafc" font-size="30" font-weight="600">Donatello improvement rate by recipe level</text>
                <text x="88" y="96" fill="#94a3b8" font-size="17">Share of crafts where Donatello found a strictly better continuation than the Raphael plan</text>
                <g stroke="#2b3a50" stroke-width="1">
                  <path d="M120 560H1110"/><path d="M120 519H1110"/><path d="M120 478H1110"/>
                  <path d="M120 437H1110"/><path d="M120 396H1110"/><path d="M120 355H1110"/>
                  <path d="M120 314H1110"/><path d="M120 273H1110"/><path d="M120 232H1110"/>
                  <path d="M120 191H1110"/><path d="M120 150H1110"/>
                </g>
                <g fill="#8290a6" font-size="15" text-anchor="end">
                  <text x="103" y="566">0%</text><text x="103" y="525">10%</text>
                  <text x="103" y="484">20%</text><text x="103" y="443">30%</text>
                  <text x="103" y="402">40%</text><text x="103" y="361">50%</text>
                  <text x="103" y="320">60%</text><text x="103" y="279">70%</text>
                  <text x="103" y="238">80%</text><text x="103" y="197">90%</text>
                  <text x="103" y="156">100%</text>
                </g>
            {line}
            {pointMarkup}{labelMarkup}
                <g fill="#8290a6" font-size="15" text-anchor="middle">
                  <text x="120" y="590">10</text><text x="230" y="590">20</text>
                  <text x="340" y="590">30</text><text x="450" y="590">40</text>
                  <text x="560" y="590">50</text><text x="670" y="590">60</text>
                  <text x="780" y="590">70</text><text x="890" y="590">80</text>
                  <text x="1000" y="590">90</text><text x="1110" y="590">100</text>
                  <text x="615" y="622" fill="#aeb9c9" font-size="17">Crafter / recipe level</text>
                </g>
                <g font-size="15" fill="#cbd5e1">
                  <path d="M532 118H572" stroke="#2dd4bf" stroke-width="2.5"/>
                  <circle cx="552" cy="118" r="5" fill="#111827" stroke="#5eead4" stroke-width="2.5"/>
                  <text x="583" y="124">Regular recipes</text>
                  <path d="M760 109l8 8-8 8-8-8z" fill="#c084fc"/>
                  <text x="778" y="124">Expert recipes</text>
                </g>
                <text x="1110" y="650" fill="#66758c" font-size="14" text-anchor="end">{comparisons} crafts · random recipes · level-appropriate crafter stats</text>
              </g>
            </svg>
            """;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, svg);
    }

    private enum Comparison
    {
        Win,
        Tie,
        Loss,
    }

    private sealed class Tally
    {
        public int Wins;
        public int Ties;
        public int Losses;
        public int Total => Wins + Ties + Losses;
        public double WinRate => Total == 0 ? 0 : (double)Wins / Total;

        public void Add(Comparison verdict)
        {
            switch (verdict)
            {
                case Comparison.Win:
                    Wins++;
                    break;
                case Comparison.Tie:
                    Ties++;
                    break;
                case Comparison.Loss:
                    Losses++;
                    break;
            }
        }
    }

    private sealed record PlayScore(bool Completes, int Quality, int Steps, int Duration)
    {
        public string Describe()
            => $"{(Completes ? "ok" : "fail")} q={Quality} n={Steps} t={Duration}";
    }

    private sealed record SampledCraft(
        string Label,
        string Series,
        int Level,
        bool Expert,
        CraftState Craft);

    private sealed record CraftRecord(
        string Label,
        string Series,
        int Level,
        bool Expert,
        int Deadline,
        PlayScore Raphael,
        PlayScore Donatello,
        string Verdict);

    private sealed record PreparedCraft(
        string Label,
        CraftState Craft,
        StepState Root,
        CachedRaphaelSolution Incumbent,
        PlayScore IncumbentSolved,
        uint ActionSeed,
        uint ConditionSeed);

    private sealed class Corpus
    {
        public List<PluginPathRecipeMatrix.MatrixCase> Cases { get; set; } = [];
    }

    private sealed class BenchmarkPool
    {
        public int Version { get; set; }
        public List<BenchmarkBracket> Brackets { get; set; } = [];
        public List<BenchmarkBracket> ExpertBrackets { get; set; } = [];
    }

    private sealed class BenchmarkBracket
    {
        public int JobLevel { get; set; }
        public int LevelLow { get; set; }
        public int CraftsmanshipLow { get; set; }
        public int CraftsmanshipHigh { get; set; }
        public int ControlLow { get; set; }
        public int ControlHigh { get; set; }
        public int CpLow { get; set; }
        public int CpHigh { get; set; }
        public List<BenchmarkRecipe> Recipes { get; set; } = [];
    }

    private sealed class BenchmarkRecipe
    {
        public uint RecipeId { get; set; }
        public uint ItemId { get; set; }
        public int ReqCraftsmanship { get; set; }
        public int ReqControl { get; set; }
        public int RecipeJobLevel { get; set; }
        public int RecipeLevelTableId { get; set; }
        public bool Expert { get; set; }
        public int ProgressDiv { get; set; }
        public int QualityDiv { get; set; }
        public int ProgressMod { get; set; }
        public int QualityMod { get; set; }
        public int MaxDurability { get; set; }
        public int MaxProgress { get; set; }
        public int MaxQuality { get; set; }
    }

    private sealed class SeededRaphaelDefinition(CachedRaphaelSolution solution) : ISolverDefinition
    {
        public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
        {
            yield return new(this, 0, 1000, "Benchmark Raphael");
        }

        public Solver Create(CraftState craft, int flavor)
            => new RaphaelMacroSolver(solution, craft);
    }

    private sealed class SeededDonatelloDefinition(CachedRaphaelSolution solution) : ISolverDefinition
    {
        public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
        {
            yield return new(this, 0, 1000, "Benchmark Donatello");
        }

        public Solver Create(CraftState craft, int flavor)
            => DonatelloSolverDefinition.CreateFromSolution(solution, craft);
    }

    private sealed class SeededProtectedDonatelloDefinition(CachedRaphaelSolution solution) : ISolverDefinition
    {
        public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
        {
            yield return new(this, 0, 1000, "Benchmark Protected Donatello");
        }

        public Solver Create(CraftState craft, int flavor)
            => new DonatelloProtectedRaphaelSolver(solution, craft);
    }
}
