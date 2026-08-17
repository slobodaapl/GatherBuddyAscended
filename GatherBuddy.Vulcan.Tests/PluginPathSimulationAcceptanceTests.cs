using System.Diagnostics;
using System.Reflection;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;

namespace GatherBuddy.Vulcan.Tests;

internal static class PluginPathSimulationAcceptanceTests
{
    private const ConditionFlags NormalConditions = ConditionFlags.Normal
        | ConditionFlags.Good
        | ConditionFlags.Excellent
        | ConditionFlags.Poor;

    public static async Task Run(Action<bool, string> require)
    {
        ValidateNormalConditionDistribution(require);
        await ValidateNormalRerollAndReactiveReplan(require);
        await ValidatePoorPlannerReroll(require);
        ValidateAmbiguousFailedManualActionStops(require);
        await ValidateManualCarefulObservationRecovery(require);
        await ValidateScriptedConditionAndManualActionRecovery(require);
        await ValidateImprovedRivetsMaterialMiracleRecovery(require);
    }

    internal static async Task RunFiveStarDistribution(
        int seedStart,
        int seedCount,
        Action<bool, string> require)
    {
        require(seedStart > 0, "five-star plugin-path simulation requires a positive starting seed");
        require(seedCount > 0, "five-star plugin-path simulation requires at least one seed");
        var config = global::GatherBuddy.GatherBuddy.Config.RaphaelSolverConfig;
        var previousCacheMemory = config.DonatelloCacheMemoryMiB;
        config.DonatelloCacheMemoryMiB = 64;
        var raphaelResults = new List<FiveStarRunResult>(seedCount);
        var donatelloResults = new List<FiveStarRunResult>(seedCount);
        try
        {
            for (var seed = seedStart; seed < seedStart + seedCount; ++seed)
            {
                raphaelResults.Add(await RunFiveStarSeed(seed, useDonatello: false, require));
                donatelloResults.Add(await RunFiveStarSeed(seed, useDonatello: true, require));
            }
        }
        finally
        {
            CraftingProcessor.Dispose();
            DonatelloNative.ClearCache();
            config.DonatelloCacheMemoryMiB = previousCacheMemory;
        }

        ReportFiveStarResults("Raphael", seedStart, seedCount, raphaelResults);
        ReportFiveStarResults("Donatello", seedStart, seedCount, donatelloResults);
    }

    private static void ReportFiveStarResults(
        string solver,
        int seedStart,
        int seedCount,
        IReadOnlyList<FiveStarRunResult> results)
    {
        var completed = results.Count(result => result.SynthesisCompleted);
        var successful = results.Count(result => result.Successful);
        var terminalFailures = results.Count(result => result.TerminalFailure);
        var qualities = results.Where(result => result.SynthesisCompleted).Select(result => result.Quality).ToArray();
        Console.WriteLine(
            $"five-star {solver} plugin distribution seedRange={seedStart}..{seedStart + seedCount - 1} "
            + $"synthesesCompleted={completed} successful={successful} "
            + $"terminalFailures={terminalFailures} minQuality={(qualities.Length == 0 ? 0 : qualities.Min())} "
            + $"avgQuality={(qualities.Length == 0 ? 0 : qualities.Average()):F1} "
            + $"maxQuality={(qualities.Length == 0 ? 0 : qualities.Max())} "
            + $"nativeReplans={results.Sum(result => result.NativeReplans)}");
    }

    private static async Task<FiveStarRunResult> RunFiveStarSeed(
        int seed,
        bool useDonatello,
        Action<bool, string> require)
    {
        var craft = FiveStarCraft();
        var root = GameStateBuilder.BuildInitialStepState(craft);
        var solution = new CachedRaphaelSolution
        {
            ActionIds = FiveStarRaphaelSeed.Select(action => (uint)action).ToList(),
        };
        CraftingProcessor.Setup();
        CraftingProcessor.RegisterSolver(useDonatello
            ? new SeededDonatelloDefinition(solution)
            : new SeededRaphaelDefinition(solution));
        CraftingProcessor.OnCraftStarted(craft, root, craft.RecipeId, isTrial: false);
        require(useDonatello
                ? CraftingProcessor.ActiveSolver is DonatelloSolver
                : CraftingProcessor.ActiveSolver is RaphaelMacroSolver,
            $"five-star simulation must select the real {(useDonatello ? "Donatello" : "Raphael")} runtime solver");
        var activeDonatello = CraftingProcessor.ActiveSolver as DonatelloSolver;
        var solverName = useDonatello ? "Donatello" : "Raphael";
        var game = new SeededGame(
            craft,
            root,
            actionSeed: 0xA11CEu + (uint)seed * 0x101u,
            conditionSeed: 0xC0FFEEu + (uint)seed * 0x10001u);
        var current = root;
        var terminalFailure = false;
        try
        {
            for (var actionNumber = 1; actionNumber <= 100; ++actionNumber)
            {
                var recommendation = await AwaitRecommendation(
                    useDonatello ? TimeSpan.FromMinutes(6) : TimeSpan.FromSeconds(10));
                if (recommendation.IsTerminalFailure || recommendation.Action == VulcanSkill.None)
                {
                    terminalFailure = true;
                    Console.WriteLine(
                        $"five-star solver={solverName} seed={seed} action={actionNumber} terminal={recommendation.IsTerminalFailure} "
                        + $"comment={recommendation.Comment} state={current}");
                    break;
                }

                require(Simulator.CanUseAction(craft, current, recommendation.Action),
                    $"five-star seed {seed}: plugin recommendation {recommendation.Action} must be legal at {current}");
                var executed = game.SelectAction(recommendation.Action, out var manual);
                require(!manual, $"five-star seed {seed}: random distribution run must not inject manual actions");
                var previousCondition = current.Condition;
                var actual = game.Execute(executed, require);
                Console.WriteLine(
                    $"five-star solver={solverName} seed={seed} action={actionNumber} skill={executed} "
                    + $"condition={previousCondition}->{actual.Condition} progress={actual.Progress}/{craft.CraftProgress} "
                    + $"quality={actual.Quality}/{craft.CraftQualityMax} durability={actual.Durability}/{craft.CraftDurability} "
                    + $"cp={actual.RemainingCP}/{craft.StatCP} source={recommendation.Comment}");
                Console.Out.Flush();

                if (actual.Progress >= craft.CraftProgress || actual.Durability <= 0)
                {
                    CraftingProcessor.OnCraftFinished(craft, actual, craft.RecipeId, cancelled: false);
                    current = actual;
                    break;
                }

                current = ReconcileRecommended(craft, current, executed, actual, require);
                CraftingProcessor.OnCraftAdvanced(craft, current, craft.RecipeId);
            }

            var completed = SolverUtils.Status(craft, current) == SolverUtils.CraftStatus.Complete;
            var successful = completed && current.Quality >= craft.CraftRequiredQuality;
            var replans = activeDonatello?.NativeReplanCount ?? 0;
            Console.WriteLine(
                $"five-star solver={solverName} seed={seed} result={(successful ? "success" : completed ? "quality-failure" : "failed")} "
                + $"quality={current.Quality}/{craft.CraftQualityMax} steps={game.Trace.Count} "
                + $"nativeReplans={replans} terminalFailure={terminalFailure}");
            return new(completed, successful, current.Quality, replans, terminalFailure);
        }
        finally
        {
            CraftingProcessor.Dispose();
        }
    }

    private static CraftState FiveStarCraft()
    {
        const ConditionFlags conditions = ConditionFlags.Normal
            | ConditionFlags.Good
            | ConditionFlags.Centered
            | ConditionFlags.Sturdy
            | ConditionFlags.Pliant
            | ConditionFlags.Malleable
            | ConditionFlags.Primed
            | ConditionFlags.Robust;
        return new CraftState
        {
            RecipeId = 38247,
            ItemId = 52642,
            StatCraftsmanship = 5707,
            StatControl = 5110,
            StatCP = 700,
            StatLevel = 100,
            UnlockedManipulation = true,
            Specialist = true,
            CrafterDelineations = 2,
            SplendorCosmic = true,
            CraftHQ = false,
            CraftExpert = true,
            CraftStars = 5,
            CraftLevel = 100,
            CraftDurability = 60,
            CraftProgress = 11_250,
            CraftQualityMax = 31_520,
            CraftQualityMin1 = 31_520,
            CraftQualityMin2 = 31_520,
            CraftQualityMin3 = 31_520,
            CraftRequiredQuality = 31_520,
            CraftProgressDivider = 180,
            CraftProgressModifier = 100,
            CraftQualityDivider = 180,
            CraftQualityModifier = 100,
            ConditionFlags = conditions,
            // Index 0 is the Normal fallback. Non-Normal mass totals 0.80, leaving Normal at 0.20.
            CraftConditionProbabilities = [1f, 0.10f, 0f, 0f, 0.15f, 0.10f, 0.15f, 0.10f, 0.10f, 0f, 0.10f],
            DonatelloOptions = new DonatelloExecutionOptions(
                DonatelloSolveObjective.MaximizeQuality,
                MaximizeQualityAtCostOfTime: true,
                AllowSpecialistActions: true),
        };
    }

    private static readonly VulcanSkill[] FiveStarRaphaelSeed =
    [
        VulcanSkill.QuickInnovation,
        VulcanSkill.Reflect,
        VulcanSkill.BasicSynthesis,
        VulcanSkill.Innovation,
        VulcanSkill.PrudentTouch,
        VulcanSkill.BasicTouch,
        VulcanSkill.StandardTouch,
        VulcanSkill.AdvancedTouch,
        VulcanSkill.ImmaculateMend,
        VulcanSkill.Innovation,
        VulcanSkill.PrudentTouch,
        VulcanSkill.BasicTouch,
        VulcanSkill.StandardTouch,
        VulcanSkill.AdvancedTouch,
        VulcanSkill.Innovation,
        VulcanSkill.BasicTouch,
        VulcanSkill.GreatStrides,
        VulcanSkill.ByregotsBlessing,
        VulcanSkill.ImmaculateMend,
        VulcanSkill.Veneration,
        VulcanSkill.WasteNot,
        VulcanSkill.Groundwork,
        VulcanSkill.Groundwork,
        VulcanSkill.Groundwork,
        VulcanSkill.BasicSynthesis,
        VulcanSkill.TrainedPerfection,
        VulcanSkill.Veneration,
        VulcanSkill.Groundwork,
        VulcanSkill.CarefulSynthesis,
        VulcanSkill.CarefulSynthesis,
        VulcanSkill.HeartAndSoul,
        VulcanSkill.IntensiveSynthesis,
    ];

    private sealed record FiveStarRunResult(
        bool SynthesisCompleted,
        bool Successful,
        int Quality,
        int NativeReplans,
        bool TerminalFailure);

    private static void ValidateNormalConditionDistribution(Action<bool, string> require)
    {
        var probabilities = GameStateBuilder.GetConditionProbabilities(
            NormalConditions,
            statLevel: 100,
            craftExpert: false);
        require(probabilities.Length == 3
                && probabilities[(int)Condition.Good] == 0.25f
                && probabilities[(int)Condition.Excellent] == 0.04f,
            "level 63+ normal crafts must use the game Good/Excellent rates");

        var lowLevelProbabilities = GameStateBuilder.GetConditionProbabilities(
            NormalConditions,
            statLevel: 62,
            craftExpert: false);
        require(lowLevelProbabilities[(int)Condition.Good] == 0.20f,
            "pre-63 normal crafts must retain the lower Good rate");

        var rng = new XorShift32(0xC0FFEEu);
        var counts = new int[4];
        const int samples = 100_000;
        for (var i = 0; i < samples; ++i)
            counts[(int)RollCondition(Condition.Normal, probabilities, rng.NextUnit())]++;

        require(counts[(int)Condition.Poor] == 0,
            "normal crafts must never roll Poor directly from Normal");
        require(Math.Abs(counts[(int)Condition.Good] / (double)samples - 0.25) < 0.005
                && Math.Abs(counts[(int)Condition.Excellent] / (double)samples - 0.04) < 0.003,
            "seeded normal-craft sampling must reproduce the configured Good/Excellent distribution");
        require(RollCondition(Condition.Excellent, probabilities, 0.99f) == Condition.Poor
                && RollCondition(Condition.Poor, probabilities, 0f) == Condition.Normal,
            "normal-craft Excellent and Poor transitions must remain deterministic");
        var expertProbabilities = GameStateBuilder.GetConditionProbabilities(
            ConditionFlags.Normal | ConditionFlags.Good | ConditionFlags.Excellent
                | ConditionFlags.Poor | ConditionFlags.Centered | ConditionFlags.Sturdy
                | ConditionFlags.Pliant | ConditionFlags.Malleable | ConditionFlags.Primed
                | ConditionFlags.GoodOmen | ConditionFlags.Robust,
            statLevel: 100,
            craftExpert: true);
        require(RollCondition(Condition.Good, expertProbabilities, 0.85f, craftExpert: true) == Condition.Robust
                && RollCondition(Condition.GoodOmen, expertProbabilities, 0.5f, craftExpert: true) == Condition.Good
                && RollCondition(Condition.Robust, expertProbabilities, 0.5f, craftExpert: true) == Condition.Sturdy,
            "seeded expert simulation must preserve Good random transitions and fixed Good Omen/Robust transitions");
    }

    private static async Task ValidateNormalRerollAndReactiveReplan(Action<bool, string> require)
    {
        var craft = Craft();
        var root = Root(craft, Condition.Normal) with
        {
            GreatStridesLeft = 2,
            InnovationLeft = 2,
        };

        SetupWithSeededDonatello();
        try
        {
            require(CraftingProcessor.TryAdoptLiveCraft(
                    craft,
                    root,
                    allowDonatelloLiveRecovery: true,
                    out var failureReason),
                $"plugin-path simulator must start live Donatello: {failureReason}");
            var solver = CraftingProcessor.ActiveSolver as DonatelloSolver;
            require(solver != null, "plugin-path simulator must use the real active Donatello solver");

            var recommendation = await AwaitRecommendation();
            require(recommendation.Action == VulcanSkill.CarefulObservation,
                "the active plugin solver must interject Careful Observation at an eligible Normal live root");
            require(solver!.NativeReplanCount == 1,
                "live adoption must establish its plan through the native re-solver");

            var game = new SeededGame(craft, root, actionSeed: 1, conditionSeed: 4096);
            var actual = game.Execute(recommendation.Action, require);
            require(actual.Condition == Condition.Excellent,
                "the fixed game seed must produce a Normal-to-Excellent condition roll");
            var reconciled = ReconcileRecommended(craft, root, recommendation.Action, actual, require);
            require(reconciled.GreatStridesLeft == root.GreatStridesLeft
                    && reconciled.InnovationLeft == root.InnovationLeft,
                "the observed plugin path must preserve buffs across Careful Observation");

            CraftingProcessor.OnCraftAdvanced(craft, reconciled, craft.RecipeId);
            var reacted = await AwaitRecommendation();
            require(solver.NativeReplanCount >= 2,
                "the active plugin solver must invoke the native re-solver after the unexpected Excellent state");
            require(reacted.Action != VulcanSkill.None
                    && Simulator.CanUseAction(craft, reconciled, reacted.Action),
                "the reactive re-solve must return an action legal in the reconciled game state");

            var conditions = new HashSet<Condition> { root.Condition, actual.Condition };
            var current = reconciled;
            var nextRecommendation = reacted;
            StepState? final = null;
            for (var actionsExecuted = 1; actionsExecuted < 40; ++actionsExecuted)
            {
                actual = game.Execute(nextRecommendation.Action, require);
                conditions.Add(actual.Condition);
                if (actual.Progress >= craft.CraftProgress)
                {
                    final = actual;
                    break;
                }
                require(actual.Durability > 0,
                    "seeded plugin-path craft must not exhaust durability before completion");

                current = ReconcileRecommended(
                    craft,
                    current,
                    nextRecommendation.Action,
                    actual,
                    require);
                CraftingProcessor.OnCraftAdvanced(craft, current, craft.RecipeId);
                nextRecommendation = await AwaitRecommendation();
                require(!nextRecommendation.IsTerminalFailure
                        && nextRecommendation.Action != VulcanSkill.None
                        && Simulator.CanUseAction(craft, current, nextRecommendation.Action),
                    "the active plugin solver must continue issuing legal actions through the seeded craft");
            }

            require(final != null,
                "the seeded plugin-path craft must complete within the bounded action count");
            require(final!.Quality >= 337,
                "the completed seeded craft must preserve the manually derived guaranteed baseline quality");
            require(conditions.Contains(Condition.Normal)
                    && conditions.Contains(Condition.Excellent)
                    && conditions.Contains(Condition.Poor),
                "the complete seeded craft must exercise Normal, Excellent, and deterministic Poor states");
            require(solver.NativeReplanCount >= 3,
                "the complete plugin path must natively re-solve at initial, Excellent, and Poor roots");
            CraftingProcessor.OnCraftFinished(craft, final, craft.RecipeId, cancelled: false);
        }
        finally
        {
            CraftingProcessor.Dispose();
        }
    }

    private static async Task ValidatePoorPlannerReroll(Action<bool, string> require)
    {
        var craft = Craft() with
        {
            StatCraftsmanship = 98,
            StatControl = 65,
            CraftDurability = 40,
            CraftProgress = 500,
            CraftQualityMax = 5_000,
        };
        var root = Root(craft, Condition.Poor) with
        {
            CarefulObservationLeft = 1,
            CrafterDelineationsLeft = 1,
            GreatStridesLeft = 2,
            InnovationLeft = 2,
            HeartAndSoulAvailable = false,
            QuickInnoAvailable = false,
            QuickInnoLeft = 0,
        };

        SetupWithSeededDonatello(
            VulcanSkill.BasicSynthesis,
            VulcanSkill.BasicSynthesis,
            VulcanSkill.BasicSynthesis,
            VulcanSkill.MastersMend,
            VulcanSkill.BasicSynthesis,
            VulcanSkill.BasicSynthesis);
        try
        {
            require(CraftingProcessor.TryAdoptLiveCraft(
                    craft,
                    root,
                    allowDonatelloLiveRecovery: true,
                    out var failureReason),
                $"plugin-path Poor simulation must start live Donatello: {failureReason}");

            var recommendation = await AwaitRecommendation();
            require(!recommendation.IsTerminalFailure
                    && recommendation.Action != VulcanSkill.None
                    && Simulator.CanUseAction(craft, root, recommendation.Action),
                $"the native planner must produce a legal quality-preserving action at a Poor live root; actual={recommendation.Action}, terminal={recommendation.IsTerminalFailure}, comment={recommendation.Comment}");

            var game = new SeededGame(craft, root, actionSeed: 2, conditionSeed: 3);
            var actual = game.Execute(recommendation.Action, require);
            require(actual.Condition is Condition.Poor or Condition.Normal,
                "the scripted Poor root must remain Poor for a zero-step action or advance deterministically to Normal");
            var reconciled = ReconcileRecommended(craft, root, recommendation.Action, actual, require);
            require(recommendation.Action != VulcanSkill.CarefulObservation
                    || reconciled.CarefulObservationLeft == root.CarefulObservationLeft - 1
                    && reconciled.CrafterDelineationsLeft == root.CrafterDelineationsLeft - 1,
                "the observed plugin path must reconcile both Careful Observation resources when native selects it");

            CraftingProcessor.OnCraftAdvanced(craft, reconciled, craft.RecipeId);
            var continued = await AwaitRecommendation();
            require(continued.Action != VulcanSkill.None
                    && Simulator.CanUseAction(craft, reconciled, continued.Action),
                "the active solver must continue from the observed post-reroll state");
        }
        finally
        {
            CraftingProcessor.Dispose();
        }
    }

    private static void ValidateAmbiguousFailedManualActionStops(Action<bool, string> require)
    {
        var craft = Craft();
        var root = Root(craft, Condition.Normal);
        var game = new SeededGame(craft, root, actionSeed: 14336, conditionSeed: 7);
        var actual = game.Execute(VulcanSkill.HastyTouch, require, expectedResult: Simulator.ExecuteResult.Failed);
        var observed = Observe(root, actual);
        require(!StepStateReconciler.TryReconcileExternalAction(
                craft,
                root,
                observed,
                out _,
                out _,
                out _),
            "ambiguous failed manual actions must stop safely instead of inventing hidden combo state");
    }

    private static async Task ValidateManualCarefulObservationRecovery(Action<bool, string> require)
    {
        var craft = Craft();
        var root = Root(craft, Condition.Normal);
        var game = new SeededGame(craft, root, actionSeed: 11, conditionSeed: 4096);
        var actual = game.Execute(VulcanSkill.CarefulObservation, require);
        var observed = Observe(root, actual);
        require(StepStateReconciler.TryReconcileExternalAction(
                craft,
                root,
                observed,
                out var reconciled,
                out var externalActionObserved,
                out var inferredAction)
                && externalActionObserved
                && inferredAction == VulcanSkill.CarefulObservation
                && reconciled.CarefulObservationLeft == root.CarefulObservationLeft - 1
                && reconciled.CrafterDelineationsLeft == root.CrafterDelineationsLeft - 1,
            "the live reconciliation path must infer an external Careful Observation and its resources");

        SetupWithSeededDonatello();
        try
        {
            require(CraftingProcessor.TryAdoptLiveCraft(
                    craft,
                    reconciled,
                    allowDonatelloLiveRecovery: true,
                    out var failureReason),
                $"manual intervention must establish a new live Donatello baseline: {failureReason}");
            var recommendation = await AwaitRecommendation();
            var active = CraftingProcessor.ActiveSolver as DonatelloSolver;
            require(active is { NativeReplanCount: >= 1 }
                    && recommendation.Action != VulcanSkill.None
                    && Simulator.CanUseAction(craft, reconciled, recommendation.Action),
                $"the plugin must natively re-solve and continue from the externally rerolled state; replans={active?.NativeReplanCount}, action={recommendation.Action}, terminal={recommendation.IsTerminalFailure}, comment={recommendation.Comment}");
        }
        finally
        {
            CraftingProcessor.Dispose();
        }
    }

    private static async Task ValidateScriptedConditionAndManualActionRecovery(Action<bool, string> require)
    {
        var craft = Craft() with { CrafterDelineations = 3 };
        var root = Root(craft, Condition.Normal) with
        {
            CrafterDelineationsLeft = 3,
            GreatStridesLeft = 2,
            InnovationLeft = 2,
        };
        var game = new SeededGame(
            craft,
            root,
            actionSeed: 17,
            conditionSeed: 19,
            forcedConditions: new Dictionary<int, Condition>
            {
                [1] = Condition.Normal,
                [2] = Condition.Good,
            },
            manualActions: new Dictionary<int, VulcanSkill>
            {
                [2] = VulcanSkill.CarefulObservation,
            });

        SetupWithSeededDonatello();
        try
        {
            require(CraftingProcessor.TryAdoptLiveCraft(
                    craft,
                    root,
                    allowDonatelloLiveRecovery: true,
                    out var failureReason),
                $"scripted plugin-path simulation must start live Donatello: {failureReason}");
            var recommendation = await AwaitRecommendation();
            require(recommendation.Action == VulcanSkill.CarefulObservation,
                "scripted scenario requires the first automatic Normal reroll");

            var firstAction = game.SelectAction(recommendation.Action, out var firstManual);
            require(!firstManual && firstAction == recommendation.Action,
                "unscripted action ordinal must execute the plugin recommendation");
            var actual = game.Execute(firstAction, require);
            require(actual.Condition == Condition.Normal,
                "action-ordinal condition override must force the first result to Normal");
            var current = ReconcileRecommended(craft, root, firstAction, actual, require);
            CraftingProcessor.OnCraftAdvanced(craft, current, craft.RecipeId);

            recommendation = await AwaitRecommendation();
            require(recommendation.Action != VulcanSkill.CarefulObservation,
                "automatic rerolls must stop after reaching the two-delineation reservation");
            var manualAction = game.SelectAction(recommendation.Action, out var isManual);
            require(isManual && manualAction == VulcanSkill.CarefulObservation,
                "manual-action schedule must replace the plugin recommendation at action ordinal two");
            actual = game.Execute(manualAction, require);
            require(actual.Condition == Condition.Good,
                "action-ordinal condition override must force the manual reroll result to Good");

            var observed = Observe(current, actual);
            require(StepStateReconciler.TryReconcileExternalAction(
                    craft,
                    current,
                    observed,
                    out var reconciled,
                    out var externalActionObserved,
                    out var inferredAction)
                    && externalActionObserved
                    && inferredAction == VulcanSkill.CarefulObservation,
                "scripted manual action must traverse the live external-action reconciliation branch");
            require(CraftingProcessor.TryAdoptLiveCraft(
                    craft,
                    reconciled,
                    allowDonatelloLiveRecovery: true,
                    out failureReason),
                $"scripted manual action must reset the live native baseline: {failureReason}");
            var recovered = await AwaitRecommendation();
            require(CraftingProcessor.ActiveSolver is DonatelloSolver { NativeReplanCount: >= 1 }
                    && recovered.Action != VulcanSkill.None
                    && Simulator.CanUseAction(craft, reconciled, recovered.Action),
                "scripted manual interjection must produce a fresh native plan from the forced observed state");

            require(game.Trace.Count == 2
                    && game.Trace[0] is { ActionNumber: 1, Manual: false, ResultCondition: Condition.Normal }
                    && game.Trace[1] is
                    {
                        ActionNumber: 2,
                        Manual: true,
                        RecommendedAction: not VulcanSkill.CarefulObservation,
                        ExecutedAction: VulcanSkill.CarefulObservation,
                        ResultCondition: Condition.Good,
                    },
                "scripted simulator trace must preserve recommended/executed actions and forced conditions");
        }
        finally
        {
            CraftingProcessor.Dispose();
        }
    }

    private static async Task ValidateImprovedRivetsMaterialMiracleRecovery(Action<bool, string> require)
    {
        var conditions = ConditionFlags.Normal | ConditionFlags.Good | ConditionFlags.Excellent
            | ConditionFlags.Poor | ConditionFlags.Centered | ConditionFlags.Sturdy
            | ConditionFlags.Pliant | ConditionFlags.Malleable | ConditionFlags.Primed
            | ConditionFlags.GoodOmen | ConditionFlags.Robust;
        var craft = new CraftState
        {
            RecipeId = 37579,
            ItemId = 49891,
            StatCraftsmanship = 5573,
            StatControl = 4800,
            StatCP = 673,
            StatLevel = 100,
            UnlockedManipulation = false,
            Specialist = true,
            CrafterDelineations = 116,
            SplendorCosmic = true,
            IsCosmic = true,
            CraftHQ = false,
            CraftCollectible = true,
            CraftExpert = false,
            CraftLevel = 100,
            CraftDurability = 55,
            CraftProgress = 3498,
            CraftQualityMax = 5640,
            CraftProgressDivider = 170,
            CraftProgressModifier = 90,
            CraftQualityDivider = 150,
            CraftQualityModifier = 75,
            ConditionFlags = conditions,
            CraftConditionProbabilities = [1f, 0.2f, 0.04f, 0.05f, 0.1f, 0.1f, 0.1f, 0.1f, 0.05f, 0.05f, 0.1f],
            MissionHasMaterialMiracle = true,
            CurrentMaterialMiracleCharges = 3,
            DonatelloOptions = new DonatelloExecutionOptions(
                DonatelloSolveObjective.MaximizeQuality,
                AllowSpecialistActions: false),
        };
        var initial = GameStateBuilder.BuildInitialStepState(craft);
        VulcanSkill[] raphaelSeed =
        [
            VulcanSkill.Reflect,
            VulcanSkill.WasteNot2,
            VulcanSkill.PreparatoryTouch,
            VulcanSkill.PreparatoryTouch,
            VulcanSkill.PreparatoryTouch,
            VulcanSkill.PreparatoryTouch,
            VulcanSkill.TrainedPerfection,
            VulcanSkill.ByregotsBlessing,
            VulcanSkill.ImmaculateMend,
            VulcanSkill.Veneration,
            VulcanSkill.Groundwork,
            VulcanSkill.Groundwork,
            VulcanSkill.CarefulSynthesis,
        ];
        var seededRaphaelScore = DonatelloPlanEvaluator.Evaluate(craft, initial, raphaelSeed);
        require(seededRaphaelScore.Completes
                && seededRaphaelScore.Quality >= craft.CraftQualityMax * 98 / 100
                && seededRaphaelScore.Quality < craft.CraftQualityMax,
            $"Improved Rivets fixture must represent a completing near-99% Raphael incumbent; actual={seededRaphaelScore.Quality}/{craft.CraftQualityMax}");
        DonatelloNative.ClearCache();
        var raphael = DonatelloNative.SolveDetailed(
            craft with { CurrentMaterialMiracleCharges = 0 },
            initial with { MaterialMiracleCharges = 0 },
            allowSpecialistActions: false,
            DonatelloNative.SolveMode.OptimizeQuality,
            incumbent: raphaelSeed,
            softDeadlineMillis: 10,
            hardDeadlineMillis: 10,
            bypassSolutionCache: true);
        var raphaelScore = DonatelloPlanEvaluator.Evaluate(craft, initial, raphael.Actions);
        require(raphaelScore.Completes
                && raphaelScore.Quality >= craft.CraftQualityMax * 98 / 100
                && raphaelScore.Quality < craft.CraftQualityMax,
            $"Improved Rivets probe must establish a completing near-99% Raphael incumbent; complete={raphaelScore.Completes}, actual={raphaelScore.Quality}/{craft.CraftQualityMax}, nativeFinal={raphael.FinalState.Progress}/{craft.CraftProgress}:{raphael.FinalState.Quality}, stats={craft.StatCraftsmanship}/{craft.StatControl}/{craft.StatCP}, actions={string.Join(',', raphael.Actions)}, optimal={raphael.Optimal}, deadline={raphael.DeadlineReached}, nativeQuality={raphael.AchievedQuality}");

        var miracleGame = new SeededGame(
            craft,
            initial,
            actionSeed: 31,
            conditionSeed: 37,
            forcedConditions: new Dictionary<int, Condition> { [1] = Condition.Primed });
        var afterMiracle = miracleGame.Execute(VulcanSkill.MaterialMiracle, require);
        require(afterMiracle.Condition == Condition.Primed
                && afterMiracle.MaterialMiracleCharges == 2,
            "Improved Rivets simulation must begin from the observed post-Material-Miracle Primed root");
        var postMiracleIncumbent = DonatelloPlanEvaluator.Evaluate(craft, afterMiracle, raphael.Actions);
        require(postMiracleIncumbent.Completes
                && postMiracleIncumbent.Quality >= raphaelScore.Quality,
            "the pre-Miracle Raphael incumbent must remain a valid quality floor after the condition-only Miracle transition");

        var solution = new CachedRaphaelSolution
        {
            ActionIds = raphael.Actions.Select(action => (uint)action).ToList(),
            AchievedQuality = raphaelScore.Quality,
        };
        var definition = new SeededDonatelloDefinition(solution);
        CraftingProcessor.Setup();
        CraftingProcessor.RegisterSolver(definition);
        try
        {
            require(CraftingProcessor.TryAdoptLiveCraft(
                    craft,
                    afterMiracle,
                    allowDonatelloLiveRecovery: true,
                    out var failureReason),
                $"Improved Rivets post-Miracle plugin path must recover with its Raphael incumbent: {failureReason}");
            require(CraftingProcessor.ActiveSolver is DonatelloSolver,
                "Improved Rivets post-Miracle recovery must remain Donatello, never ProgressOnly");

            var forcedConditions = new[]
            {
                Condition.Centered, Condition.Sturdy, Condition.Pliant, Condition.Malleable,
                Condition.Primed, Condition.GoodOmen, Condition.Robust, Condition.Excellent,
                Condition.Poor, Condition.Normal,
            };
            var conditionSchedule = Enumerable.Range(1, 100).ToDictionary(
                actionNumber => actionNumber,
                actionNumber => forcedConditions[(actionNumber - 1) % forcedConditions.Length]);
            var game = new SeededGame(
                craft,
                afterMiracle,
                actionSeed: 41,
                conditionSeed: 43,
                forcedConditions: conditionSchedule);
            var current = afterMiracle;
            StepState? final = null;
            for (var actionNumber = 1; actionNumber <= 100; ++actionNumber)
            {
                var recommendation = await AwaitRecommendation();
                require(!recommendation.IsTerminalFailure
                        && recommendation.Action != VulcanSkill.None
                        && Simulator.CanUseAction(craft, current, recommendation.Action),
                    $"Improved Rivets post-Miracle Donatello must emit a legal quality-preserving action at ordinal {actionNumber}; comment={recommendation.Comment}");
                var actual = game.Execute(recommendation.Action, require);
                if (actual.Progress >= craft.CraftProgress)
                {
                    final = actual;
                    CraftingProcessor.OnCraftFinished(craft, actual, craft.RecipeId, cancelled: false);
                    break;
                }
                require(actual.Durability > 0,
                    $"Improved Rivets post-Miracle path exhausted durability at ordinal {actionNumber}");
                current = ReconcileRecommended(craft, current, recommendation.Action, actual, require);
                CraftingProcessor.OnCraftAdvanced(craft, current, craft.RecipeId);
            }

            require(final != null,
                "Improved Rivets post-Miracle plugin path must complete within 100 actions");
            require(final!.Quality >= postMiracleIncumbent.Quality,
                $"Improved Rivets post-Miracle path must retain the near-99% Raphael quality floor; Raphael={postMiracleIncumbent.Quality}, plugin={final.Quality}");
        }
        finally
        {
            CraftingProcessor.Dispose();
            DonatelloNative.ClearCache();
        }
    }

    private static CraftState Craft()
        => new()
        {
            StatCraftsmanship = 100,
            StatControl = 100,
            StatCP = 500,
            StatLevel = 100,
            CraftLevel = 1,
            CraftDurability = 60,
            CraftProgress = 100,
            CraftQualityMax = 1000,
            CraftProgressDivider = 10,
            CraftProgressModifier = 100,
            CraftQualityDivider = 10,
            CraftQualityModifier = 100,
            CraftHQ = true,
            UnlockedManipulation = true,
            Specialist = true,
            CrafterDelineations = 5,
            RecipeId = 1,
            ConditionFlags = NormalConditions,
            CraftConditionProbabilities = GameStateBuilder.GetConditionProbabilities(
                NormalConditions,
                statLevel: 100,
                craftExpert: false),
            DonatelloOptions = new DonatelloExecutionOptions(
                MaximizeQualityAtCostOfTime: true,
                AllowSpecialistActions: true),
        };

    private sealed class SeededDonatelloDefinition(CachedRaphaelSolution solution) : ISolverDefinition
    {
        public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
        {
            yield return new(this, 0, 1000, "Seeded Donatello");
        }

        public Solver Create(CraftState craft, int flavor)
            => DonatelloSolverDefinition.CreateFromSolution(solution, craft);

        public Solver CreateLive(CraftState craft)
            => new DonatelloSolver(solution, craft);
    }

    private sealed class SeededRaphaelDefinition(CachedRaphaelSolution solution) : ISolverDefinition
    {
        public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
        {
            yield return new(this, 0, 1000, "Seeded Raphael");
        }

        public Solver Create(CraftState craft, int flavor)
            => new RaphaelMacroSolver(solution, craft);
    }

    private static void SetupWithSeededDonatello(params VulcanSkill[] actions)
    {
        CraftingProcessor.Setup();
        CraftingProcessor.RegisterSolver(new SeededDonatelloDefinition(new CachedRaphaelSolution
        {
            ActionIds = (actions.Length == 0 ? [VulcanSkill.BasicSynthesis] : actions)
                .Select(action => (uint)action)
                .ToList(),
        }));
    }

    private static StepState Root(CraftState craft, Condition condition)
        => GameStateBuilder.BuildInitialStepState(craft) with
        {
            Condition = condition,
            CarefulObservationLeft = 3,
            CrafterDelineationsLeft = 5,
        };

    internal static async Task<Solver.Recommendation> AwaitRecommendation(TimeSpan? maximumWait = null)
    {
        var timeout = Stopwatch.StartNew();
        var limit = maximumWait ?? TimeSpan.FromSeconds(10);
        while (timeout.Elapsed < limit)
        {
            CraftingProcessor.Update();
            var recommendation = CraftingProcessor.NextRecommendation;
            if (recommendation.Action != VulcanSkill.None || recommendation.IsTerminalFailure)
                return recommendation;
            await Task.Delay(1);
        }

        throw new TimeoutException($"active plugin solver did not produce a recommendation within {limit}");
    }

    internal static StepState ReconcileRecommended(
        CraftState craft,
        StepState previous,
        VulcanSkill action,
        StepState actual,
        Action<bool, string> require)
    {
        var observed = Observe(previous, actual);
        require(StepStateReconciler.TryReconcileAction(craft, previous, action, observed, out var reconciled),
            $"plugin reconciliation must accept the simulated game outcome for {action}");
        require(StateEquivalent(reconciled, actual),
            $"reconciled plugin state must equal the seeded game state after {action}");
        return reconciled;
    }

    internal static StepState Observe(StepState previous, StepState actual)
        => previous with
        {
            Index = actual.Index,
            Progress = actual.Progress,
            Quality = actual.Quality,
            Durability = actual.Durability,
            RemainingCP = actual.RemainingCP,
            Condition = actual.Condition,
            IQStacks = actual.IQStacks,
            WasteNotLeft = actual.WasteNotLeft,
            ManipulationLeft = actual.ManipulationLeft,
            GreatStridesLeft = actual.GreatStridesLeft,
            InnovationLeft = actual.InnovationLeft,
            VenerationLeft = actual.VenerationLeft,
            MuscleMemoryLeft = actual.MuscleMemoryLeft,
            FinalAppraisalLeft = actual.FinalAppraisalLeft,
            HeartAndSoulActive = actual.HeartAndSoulActive,
            ExpedienceLeft = actual.ExpedienceLeft,
            TrainedPerfectionActive = actual.TrainedPerfectionActive,
            ComboAction = actual.ComboAction,
            MaterialMiracleCharges = actual.MaterialMiracleCharges,
            StellarSteadyHandCharges = actual.StellarSteadyHandCharges,
            StellarSteadyHandLeft = actual.StellarSteadyHandLeft,
        };

    private static bool StateEquivalent(StepState left, StepState right)
        => typeof(StepState)
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .All(field => Equals(field.GetValue(left), field.GetValue(right)));

    private static Condition RollCondition(
        Condition current,
        float[] probabilities,
        float roll,
        bool craftExpert = false)
    {
        if (current == Condition.Excellent)
            return Condition.Poor;
        if (current == Condition.Poor || current == Condition.Good && !craftExpert)
            return Condition.Normal;
        if (current == Condition.GoodOmen)
            return Condition.Good;
        if (current == Condition.Robust)
            return Condition.Sturdy;
        for (var i = 1; i < probabilities.Length; ++i)
        {
            roll -= probabilities[i];
            if (roll < 0)
                return (Condition)i;
        }
        return Condition.Normal;
    }

    internal sealed record TraceEntry(
        int ActionNumber,
        VulcanSkill RecommendedAction,
        VulcanSkill ExecutedAction,
        bool Manual,
        Condition PreviousCondition,
        Condition ResultCondition,
        float ActionRoll,
        float ConditionRoll,
        bool ConditionForced);

    internal sealed class SeededGame
    {
        private readonly CraftState _craft;
        private readonly XorShift32 _actionRng;
        private readonly XorShift32 _conditionRng;
        private readonly IReadOnlyDictionary<int, Condition> _forcedConditions;
        private readonly IReadOnlyDictionary<int, VulcanSkill> _manualActions;
        private int _actionsExecuted;
        private VulcanSkill _recommendedAction;
        private bool _manual;

        public StepState State { get; private set; }
        public List<TraceEntry> Trace { get; } = [];

        public SeededGame(
            CraftState craft,
            StepState initial,
            uint actionSeed,
            uint conditionSeed,
            IReadOnlyDictionary<int, Condition>? forcedConditions = null,
            IReadOnlyDictionary<int, VulcanSkill>? manualActions = null)
        {
            _craft = craft;
            State = initial with { };
            _actionRng = new(actionSeed);
            _conditionRng = new(conditionSeed);
            _forcedConditions = forcedConditions ?? new Dictionary<int, Condition>();
            _manualActions = manualActions ?? new Dictionary<int, VulcanSkill>();
        }

        public VulcanSkill SelectAction(VulcanSkill recommendation, out bool manual)
        {
            _recommendedAction = recommendation;
            manual = _manual = _manualActions.TryGetValue(_actionsExecuted + 1, out var scripted);
            return manual ? scripted : recommendation;
        }

        public StepState Execute(
            VulcanSkill action,
            Action<bool, string> require,
            Simulator.ExecuteResult? expectedResult = null)
        {
            var previous = State;
            var actionNumber = ++_actionsExecuted;
            var actionRoll = _actionRng.NextUnit();
            var advancesCondition = action == VulcanSkill.CarefulObservation || !Simulator.SkipUpdates(action);
            var conditionRoll = advancesCondition ? _conditionRng.NextUnit() : 0f;
            var rolledCondition = advancesCondition
                ? RollCondition(
                    previous.Condition,
                    _craft.CraftConditionProbabilities,
                    conditionRoll,
                    _craft.CraftExpert)
                : previous.Condition;
            var conditionForced = _forcedConditions.TryGetValue(actionNumber, out var forcedCondition);
            var expectedCondition = conditionForced ? forcedCondition : rolledCondition;
            var (result, next) = Simulator.Execute(_craft, previous, action, actionRoll, conditionRoll);
            require(result != Simulator.ExecuteResult.CantUse,
                $"active plugin recommendation {action} must be executable by the game simulator");
            require(expectedResult == null || result == expectedResult,
                $"seeded action outcome for {action} must be {expectedResult}, actual={result}");
            require(conditionForced || next.Condition == rolledCondition,
                $"seeded condition mismatch after {action}: expected={rolledCondition}, actual={next.Condition}");
            next.Condition = expectedCondition;
            State = next;
            Trace.Add(new(
                actionNumber,
                _recommendedAction == VulcanSkill.None ? action : _recommendedAction,
                action,
                _manual,
                previous.Condition,
                next.Condition,
                actionRoll,
                conditionRoll,
                conditionForced));
            _recommendedAction = VulcanSkill.None;
            _manual = false;
            return next;
        }
    }

    private sealed class XorShift32(uint seed)
    {
        private uint _state = seed != 0 ? seed : throw new ArgumentOutOfRangeException(nameof(seed));

        public float NextUnit()
        {
            var value = _state;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            _state = value;
            return (value >> 8) / 16_777_216f;
        }
    }
}
