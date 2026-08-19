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
        ValidateManualSynthesisTakeoverPluginPath(require);
        ValidateGabrielCatalog(require);
        await ValidateGabrielScriptedRecovery(require);
        ValidateGabrielPluginPathSimulator(require);
        ValidateZeroStepExpediencePreservation(require);
        await ValidateImprovementQuiescenceLifecycle(require);
        await ValidateImprovementQuiescenceSupersession(require);
        await ValidateNormalRerollAndReactiveReplan(require);
        await ValidateProtectedRaphaelConditionTakeover(require);
        await ValidatePoorPlannerReroll(require);
        ValidateAmbiguousFailedManualActionStops(require);
        await ValidateManualCarefulObservationRecovery(require);
        await ValidateScriptedConditionAndManualActionRecovery(require);
        await ValidateImprovedRivetsMaterialMiracleRecovery(require);
    }

    private static void ValidateManualSynthesisTakeoverPluginPath(Action<bool, string> require)
    {
        var options = CraftingContextResolver.ResolveDonatelloOptions(new RecipeCraftSettings
        {
            MaximizeQualityAtCostOfTime = true,
            DonatelloImprovementQuietSecondsOverride = 9,
            SpecialistActionOverride = SpecialistActionOverrideMode.Disallow,
        });
        var craft = Craft() with
        {
            InitialQuality = 123,
            DonatelloOptions = options,
        };
        var observedRoot = GameStateBuilder.BuildInitialStepState(craft, craft.InitialQuality);
        require(CraftingGameInterop.IsInitialManualSynthesisRoot(craft, observedRoot)
                && craft.DonatelloOptions is
                {
                    MaximizeQualityAtCostOfTime: true,
                    ImprovementQuietPeriodMillis: 9_000,
                    AllowSpecialistActions: false,
                },
            "manual synthesis takeover must carry the exact recipe's resolved Donatello overrides into the observed initial root");

        var result = CraftingPluginPathSimulator.Run(
            craft,
            observedRoot,
            new SeededRaphaelDefinition(new CachedRaphaelSolution
            {
                ActionIds = [(uint)VulcanSkill.BasicSynthesis],
            }),
            liveRecoveryMode: null,
            new PluginPathSimulationScenario(GameSeed: 31));
        require(result is
                {
                    SynthesisCompleted: true,
                    SolverTerminalFailure: false,
                    FailureReason: null,
                    Trace.Count: 1,
                }
                && result.Trace[0].ExecutedAction == VulcanSkill.BasicSynthesis
                && result.Trace[0].State.Quality == 123,
            "manual takeover must traverse active-solver selection, recommendation, execution, and completion without losing observed starting quality");
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
        var donatelloResults = new List<FiveStarRunResult>(seedCount);
        var gabrielResults = new List<FiveStarRunResult>(seedCount);
        try
        {
            for (var seed = seedStart; seed < seedStart + seedCount; ++seed)
            {
                donatelloResults.Add(await RunFiveStarSeed(seed, FiveStarSolver.Donatello, require));
                gabrielResults.Add(await RunFiveStarSeed(seed, FiveStarSolver.Gabriel, require));
            }
        }
        finally
        {
            CraftingProcessor.Dispose();
            DonatelloNative.ClearCache();
            config.DonatelloCacheMemoryMiB = previousCacheMemory;
        }

        ReportFiveStarResults("Donatello", seedStart, seedCount, donatelloResults);
        ReportFiveStarResults("Gabriel", seedStart, seedCount, gabrielResults);
    }

    internal static async Task RunGabrielDistribution(
        int seedStart,
        int seedCount,
        Action<bool, string> require)
    {
        await Task.Yield();
        require(seedStart > 0, "Gabriel plugin-path simulation requires a positive starting seed");
        require(seedCount > 0, "Gabriel plugin-path simulation requires at least one seed");
        var craft = FiveStarCraft();
        var estimate = CraftingPluginPathSimulator.EstimateGabriel(
            craft,
            GameStateBuilder.BuildInitialStepState(craft),
            seedCount,
            (ulong)(uint)seedStart);
        require(estimate.Samples == seedCount
                && estimate.Successes <= estimate.SynthesisCompletions
                && estimate.SynthesisCompletions <= estimate.Samples
                && estimate.SolverTerminalFailures <= estimate.Samples
                && estimate.MinFinalQuality >= 0
                && estimate.MinFinalQuality <= estimate.AverageFinalQuality
                && estimate.AverageFinalQuality <= estimate.MaxFinalQuality
                && estimate.MaxFinalQuality <= craft.CraftQualityMax,
            "Gabriel aggregate must come from internally consistent faithful plugin-path outcomes");
        Console.WriteLine(
            $"five-star Gabriel plugin distribution seed={seedStart} samples={estimate.Samples} "
            + $"successful={estimate.Successes} synthesesCompleted={estimate.SynthesisCompletions} "
            + $"durabilityFailures={estimate.DurabilityFailures} terminalFailures={estimate.SolverTerminalFailures} "
            + $"finalQuality={estimate.MinFinalQuality}/{estimate.AverageFinalQuality:F1}/{estimate.MaxFinalQuality} "
            + $"probability={estimate.Probability:P2} "
            + $"elapsedMs={estimate.ElapsedMillis}");
        foreach (var (reason, count) in estimate.TerminalFailureReasons.OrderByDescending(entry => entry.Value))
            Console.WriteLine($"five-star Gabriel terminalFailure count={count} reason={reason}");
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
        FiveStarSolver solver,
        Action<bool, string> require)
    {
        var craft = FiveStarCraft();
        var root = GameStateBuilder.BuildInitialStepState(craft);
        var solution = new CachedRaphaelSolution
        {
            ActionIds = FiveStarRaphaelSeed.Select(action => (uint)action).ToList(),
        };
        CraftingProcessor.Setup();
        CraftingProcessor.RegisterSolver(solver switch
        {
            FiveStarSolver.Donatello => new SeededDonatelloDefinition(solution),
            FiveStarSolver.Gabriel => new SeededGabrielDefinition((ulong)(uint)seed),
            _ => throw new ArgumentOutOfRangeException(nameof(solver)),
        });
        CraftingProcessor.OnCraftStarted(craft, root, craft.RecipeId, isTrial: false);
        require(solver switch
            {
                FiveStarSolver.Donatello => CraftingProcessor.ActiveSolver is DonatelloSolver,
                FiveStarSolver.Gabriel => CraftingProcessor.ActiveSolver is GabrielSolver,
                _ => false,
            },
            $"five-star simulation must select the real {solver} runtime solver");
        var activeDonatello = CraftingProcessor.ActiveSolver as DonatelloSolver;
        var activeGabriel = CraftingProcessor.ActiveSolver as GabrielSolver;
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
                    solver == FiveStarSolver.Donatello
                        ? TimeSpan.FromMinutes(6)
                        : TimeSpan.FromSeconds(30));
                if (recommendation.IsTerminalFailure || recommendation.Action == VulcanSkill.None)
                {
                    terminalFailure = true;
                    break;
                }

                require(Simulator.CanUseAction(craft, current, recommendation.Action),
                    $"five-star seed {seed}: plugin recommendation {recommendation.Action} must be legal at {current}");
                var executed = game.SelectAction(recommendation.Action, out var manual);
                require(!manual, $"five-star seed {seed}: random distribution run must not inject manual actions");
                var actual = game.Execute(executed, require);

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
            var replans = activeDonatello?.NativeReplanCount
                ?? activeGabriel?.NativeRecommendationCount
                ?? 0;
            return new(completed, successful, current.Quality, replans, terminalFailure);
        }
        finally
        {
            CraftingProcessor.Dispose();
        }
    }

    private enum FiveStarSolver
    {
        Donatello,
        Gabriel,
    }

    private static CraftState FiveStarCraft()
    {
        if (!ExpertConditionProfileCatalog.TryGet(776, out var profile))
            throw new InvalidOperationException("RLT 776 Expert condition profile is missing");
        return new CraftState
        {
            RecipeId = 38247,
            ItemId = 52642,
            StatCraftsmanship = 5792,
            StatControl = 5169,
            StatCP = 700,
            StatLevel = 100,
            UnlockedManipulation = true,
            Specialist = true,
            CrafterDelineations = 2,
            SplendorCosmic = true,
            CraftHQ = false,
            CraftExpert = true,
            CraftStars = 5,
            RecipeLevelTableId = 776,
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
            ConditionFlags = profile.Conditions,
            CraftConditionProbabilities = profile.ToSimulatorProbabilities(),
            CraftConditionProfileCataloged = true,
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

    private static async Task ValidateImprovementQuiescenceLifecycle(Action<bool, string> require)
    {
        const int testQuietMillis = 50;
        var craft = FiveStarCraft() with
        {
            DonatelloOptions = new DonatelloExecutionOptions(
                DonatelloSolveObjective.MaximizeQuality,
                MinimizeSteps: true,
                MaximizeQualityAtCostOfTime: true,
                AllowSpecialistActions: true,
                ImprovementQuietPeriodMillis: testQuietMillis),
        };
        var root = GameStateBuilder.BuildInitialStepState(craft) with { Condition = Condition.Good };
        var solution = new CachedRaphaelSolution
        {
            ActionIds = FiveStarRaphaelSeed.Select(action => (uint)action).ToList(),
        };

        CraftingProcessor.Setup();
        try
        {
            CraftingProcessor.RegisterSolver(new SeededDonatelloDefinition(solution));
            CraftingProcessor.OnCraftStarted(craft, root, craft.RecipeId, isTrial: false);
            var solver = CraftingProcessor.ActiveSolver as DonatelloSolver;
            require(solver != null,
                "improvement-quiescence simulation must select the real active Donatello solver");

            var elapsed = Stopwatch.StartNew();
            while (solver!.NativeReplanCount == 0 && elapsed.Elapsed < TimeSpan.FromSeconds(1))
                await Task.Delay(1);
            require(solver.NativeReplanCount == 1,
                "the improvement-quiescence path must start exactly one native search frontier");

            await Task.Delay(10);
            CraftingProcessor.Update();
            require(CraftingProcessor.NextRecommendation.Action == VulcanSkill.None
                    && !CraftingProcessor.NextRecommendation.IsTerminalFailure,
                "the plugin must not issue an action while the improvement-quiescence search remains active");

            var recommendation = await AwaitRecommendation(TimeSpan.FromSeconds(10));
            require(elapsed.ElapsedMilliseconds >= testQuietMillis
                    && solver.NativeReplanCount == 1,
                "the plugin must retain one native frontier through the configured quiet window");
            require(!recommendation.IsTerminalFailure
                    && recommendation.Action != VulcanSkill.None
                    && Simulator.CanUseAction(craft, root, recommendation.Action),
                "the completed improvement-quiescence search must emit a legal recommendation");

            var game = new SeededGame(craft, root, actionSeed: 17, conditionSeed: 19);
            var actual = game.Execute(recommendation.Action, require);
            ReconcileRecommended(craft, root, recommendation.Action, actual, require);
            CraftingProcessor.OnCraftFinished(craft, actual, craft.RecipeId, cancelled: true);
        }
        finally
        {
            CraftingProcessor.Dispose();
        }
    }

    private static async Task ValidateImprovementQuiescenceSupersession(Action<bool, string> require)
    {
        var craft = FiveStarCraft() with
        {
            DonatelloOptions = new DonatelloExecutionOptions(
                DonatelloSolveObjective.MaximizeQuality,
                MinimizeSteps: true,
                MaximizeQualityAtCostOfTime: true,
                AllowSpecialistActions: true,
                ImprovementQuietPeriodMillis: DonatelloSolver.DefaultImprovementQuietPeriodMillis),
        };
        var root = GameStateBuilder.BuildInitialStepState(craft) with { Condition = Condition.Good };
        var solution = new CachedRaphaelSolution
        {
            ActionIds = FiveStarRaphaelSeed.Select(action => (uint)action).ToList(),
        };

        CraftingProcessor.Setup();
        try
        {
            CraftingProcessor.RegisterSolver(new SeededDonatelloDefinition(solution));
            CraftingProcessor.OnCraftStarted(craft, root, craft.RecipeId, isTrial: false);
            var solver = CraftingProcessor.ActiveSolver as DonatelloSolver;
            require(solver != null,
                "improvement-quiescence supersession must select the real active Donatello solver");
            var startWait = Stopwatch.StartNew();
            while (solver!.NativeReplanCount == 0 && startWait.Elapsed < TimeSpan.FromSeconds(1))
                await Task.Delay(1);
            require(solver.NativeReplanCount == 1,
                "improvement-quiescence supersession must begin with one native frontier");

            var game = new SeededGame(craft, root, actionSeed: 23, conditionSeed: 29);
            var manuallyAdvanced = game.Execute(VulcanSkill.Observe, require);
            var supersession = Stopwatch.StartNew();
            CraftingProcessor.OnCraftAdvanced(craft, manuallyAdvanced, craft.RecipeId);
            while (solver.NativeReplanCount < 2 && supersession.Elapsed < TimeSpan.FromSeconds(1))
            {
                CraftingProcessor.Update();
                await Task.Delay(1);
            }

            require(solver.NativeReplanCount == 2
                    && supersession.Elapsed < TimeSpan.FromSeconds(1),
                "a newer observed root must interrupt the old frontier and promptly start one replacement search");
            require(CraftingProcessor.NextRecommendation.Action == VulcanSkill.None,
                "superseding a pending frontier must never expose its stale recommendation");
        }
        finally
        {
            CraftingProcessor.Dispose();
        }
    }

    private static void ValidateGabrielCatalog(Action<bool, string> require)
    {
        var profiles = ExpertConditionProfileCatalog.All;
        require(profiles.Count == 44
                && profiles.Select(profile => profile.RecipeLevelTableId).Distinct().Count() == 44,
            "Gabriel's embedded catalog must cover all 44 current Expert RecipeLevelTable rows exactly once");
        require(profiles.Select(profile => string.Join(",", profile.BaseProbabilityBasisPoints)).Distinct().Count() == 26,
            "the 44 Expert RecipeLevelTable rows must retain their 26 distinct probability vectors");
        require(profiles.Count(profile => profile.Evidence == ExpertConditionProfileEvidence.ProvisionalPublic) == 43
                && profiles.Count(profile => profile.Evidence == ExpertConditionProfileEvidence.EmpiricallyInferred) == 1
                && profiles.All(profile => !string.IsNullOrWhiteSpace(profile.Provenance)),
            "every cataloged Expert vector must retain its evidence class and source provenance");

        require(ExpertConditionProfileCatalog.TryGet(759, out var oizysProfile)
                && oizysProfile.BaseProbabilityBasisPoints.SequenceEqual(
                    new ushort[] { 2_000, 1_200, 0, 0, 1_500, 800, 1_500, 1_000, 1_000, 0, 1_000 }),
            "RLT 759 must preserve the public Oizys EX+ III profile");
        require(ExpertConditionProfileCatalog.TryGet(773, out var auxesiaProfile)
                && auxesiaProfile.BaseProbabilityBasisPoints.SequenceEqual(
                    new ushort[] { 5_100, 1_200, 0, 0, 800, 300, 700, 500, 500, 0, 900 }),
            "RLT 773 must preserve the public Auxesia Mastery I profile");
        require(ExpertConditionProfileCatalog.TryGet(776, out var profile),
            "Gabriel requires a machine-readable RLT 776 Expert condition profile");
        require(profile.BaseProbabilityBasisPoints.Count == 11
                && profile.BaseProbabilityBasisPoints.Sum(value => value) == 10_000
                && profile.BaseProbabilityBasisPoints.SequenceEqual(
                    new ushort[] { 2_000, 1_000, 0, 0, 1_500, 1_000, 1_500, 1_000, 1_000, 0, 1_000 }),
            "RLT 776 catalog data must preserve the accepted exact basis-point vector");
        require(profile.Evidence == ExpertConditionProfileEvidence.EmpiricallyInferred
                && !string.IsNullOrWhiteSpace(profile.Provenance),
            "cataloged Expert vectors must carry evidence class and provenance");
        require(oizysProfile.Conditions == auxesiaProfile.Conditions
                && auxesiaProfile.Conditions == profile.Conditions,
            "same-flags Expert controls must retain distinct RLT-keyed probability vectors");
        var oizysRuntimeProbabilities = GameStateBuilder.GetConditionProbabilities(
            oizysProfile.Conditions,
            statLevel: 100,
            craftExpert: true,
            recipeLevelTableId: 759);
        require(oizysRuntimeProbabilities.Length == oizysProfile.BaseProbabilityBasisPoints.Count
                && oizysRuntimeProbabilities
                    .Select((probability, index) => Math.Abs(
                        probability - oizysProfile.BaseProbabilityBasisPoints[index] / 10_000f))
                    .All(delta => delta < 0.000001f),
            "the plugin game-state builder must supply the exact cataloged non-776 vector to simulation");
        require(!ExpertConditionProfileCatalog.TryGet(ushort.MaxValue, out _),
            "unknown RecipeLevelTable rows must not receive a fallback condition vector");

        var craft = FiveStarCraft();
        require(GabrielPolicyCatalog.TryResolve(craft, out var policy, out _)
                && policy.Profile == GabrielPolicyProfile.ActorV1,
            "a cataloged Expert recipe must resolve the Gabriel actor profile");
        require(GabrielPolicyCatalog.TryResolve(craft with { RecipeLevelTableId = 759 }, out _, out _),
            "a public catalog profile must make its Expert RecipeLevelTable Gabriel-eligible");
        require(GabrielAssessmentService.FormatReadySummary(0, 0.037) ==
                "Estimated 0.00-3.70% chance to reach full quality."
                && GabrielAssessmentService.FormatReadyDetails(0, 100, 171_109) ==
                "0/100 full quality completions, 171.1s total time spent",
            "Gabriel's user-facing estimate must show its interval and keep diagnostics concise");
        using (var request = System.Text.Json.JsonDocument.Parse(
                   DonatelloNative.SerializeGabrielRequest(
                       craft with { GabrielWorkerThreads = 1 },
                       GameStateBuilder.BuildInitialStepState(craft),
                       decisions: 0,
                       seed: 1,
                       operation: 0,
                       samples: 0,
                       policy,
                       profile)))
        {
            var nativeRoot = request.RootElement.GetProperty("root");
            require(request.RootElement.GetProperty("maxSteps").GetInt32() == 55
                    && request.RootElement.GetProperty("maxDecisions").GetInt32() == 64
                    && request.RootElement.GetProperty("workerThreads").GetInt32() == 1
                    && nativeRoot.GetProperty("carefulObservationCharges").GetInt32() == 3
                    && nativeRoot.GetProperty("quickInnovationAvailable").GetBoolean()
                    && nativeRoot.GetProperty("crafterDelineations").GetInt32() == 2
                    && nativeRoot.GetProperty("heartAndSoulAvailable").GetBoolean(),
                "Gabriel's horizon, worker count, and faithful live specialist resources must reach the native request");
        }
        require(!DonatelloNative.IsValidGabrielRecommendation(VulcanSkill.FinalAppraisal)
                && !DonatelloNative.IsValidGabrielRecommendation(VulcanSkill.CarefulObservation)
                && !DonatelloNative.IsValidGabrielRecommendation(VulcanSkill.QuickInnovation)
                && !DonatelloNative.IsValidGabrielRecommendation(VulcanSkill.StellarSteadyHand)
                && DonatelloNative.IsValidGabrielRecommendation(VulcanSkill.HeartAndSoul),
            "the managed Gabriel boundary must reject all forbidden actions without rejecting Heart and Soul");
        require(DonatelloNative.ResolveGabrielWorkerThreads(0) == 1
                && DonatelloNative.ResolveGabrielWorkerThreads(int.MaxValue)
                    == Math.Min(Math.Max(1, Environment.ProcessorCount), 256),
            "Gabriel worker configuration must remain within the host and native limits");
        using (var request = System.Text.Json.JsonDocument.Parse(
                   DonatelloNative.SerializeGabrielRequest(
                       craft,
                       GameStateBuilder.BuildInitialStepState(craft) with { Index = 56 },
                       decisions: 55,
                       seed: 1,
                       operation: 0,
                       samples: 0,
                       policy,
                       profile)))
        {
            var maxSteps = request.RootElement.GetProperty("maxSteps").GetInt32();
            var maxDecisions = request.RootElement.GetProperty("maxDecisions").GetInt32();
            require(maxSteps > 64 && maxDecisions >= maxSteps,
                "Gabriel's expanded late-craft decision horizon must never be shorter than its step horizon");
        }
        using (var request = System.Text.Json.JsonDocument.Parse(
                   DonatelloNative.SerializeGabrielRequest(
                       craft,
                       GameStateBuilder.BuildInitialStepState(craft) with { Index = 300 },
                       decisions: 300,
                       seed: 1,
                       operation: 0,
                       samples: 0,
                       policy,
                       profile)))
        {
            require(request.RootElement.GetProperty("maxSteps").GetInt32() == byte.MaxValue
                    && request.RootElement.GetProperty("maxDecisions").GetInt32() == byte.MaxValue
                    && request.RootElement.GetProperty("root").GetProperty("step").GetInt32() == byte.MaxValue - 1
                    && request.RootElement.GetProperty("root").GetProperty("decisions").GetInt32() == byte.MaxValue - 1,
                "Gabriel's mechanics-scaled horizon must expand beyond the prototype horizon from a later live root");
        }
        require(GabrielPolicyCatalog.TryResolve(
                    craft with { CraftConditionProfileCataloged = false },
                    out _,
                    out _),
            "the machine-readable catalog, not a redundant runtime flag, must establish vector availability");
        var arbitraryEligibleCraft = craft with
        {
            StatCraftsmanship = 3000,
            StatControl = 2800,
            StatCP = 420,
            StatLevel = 80,
            UnlockedManipulation = false,
            Specialist = false,
            CrafterDelineations = 0,
            SplendorCosmic = false,
            CraftStars = 1,
            CraftDurability = 35,
            CraftProgress = 7500,
            CraftQualityMax = 21000,
            CraftRequiredQuality = 0,
        };
        require(GabrielPolicyCatalog.TryPrepare(
                    arbitraryEligibleCraft,
                    out var prepared,
                    out _,
                    out _)
                && prepared == arbitraryEligibleCraft,
            "Gabriel eligibility must preserve arbitrary stats, recipe geometry, tool, level, and action resources");
        require(!GabrielPolicyCatalog.TryResolve(
                    craft with { RecipeLevelTableId = ushort.MaxValue },
                    out _,
                    out var missingReason)
                && missingReason.Contains("No cataloged Expert condition vector", StringComparison.Ordinal),
            "Gabriel must refuse only an Expert craft whose condition vector is unavailable");
        require(!GabrielPolicyCatalog.TryResolve(
                    craft with { CraftExpert = false },
                    out _,
                    out _),
            "Gabriel must remain Expert-only");
        using (var processor = new CraftingProcessorSession(emitEvents: false, emitDiagnostics: false))
        {
            processor.Setup();
            processor.RegisterSolver(new GabrielSolverDefinition());
            processor.OnCraftStarted(
                arbitraryEligibleCraft,
                GameStateBuilder.BuildInitialStepState(arbitraryEligibleCraft),
                arbitraryEligibleCraft.RecipeId,
                isTrial: false,
                requiredSolverDefinitionType: typeof(GabrielSolverDefinition));
            require(processor.IsActive && processor.ActiveSolver is GabrielSolver,
                "per-item Gabriel must activate for any Expert craft with a cataloged vector");
        }
        require(CraftingContextResolver.ResolveGlobalSolverMode(VulcanSolverMode.Gabriel)
                == VulcanSolverMode.Donatello,
            "Gabriel must never become a global solver selection");
        var executionContext = new CraftingExecutionContext(
            null,
            new CraftingQualityPolicy(1, CraftingQualityOverrideMode.None, false, []),
            VulcanSolverMode.Gabriel,
            ForceProgressOnlyUnlockCraft: false,
            HasCraftedBefore: true,
            UseQuickSynthesis: false,
            SelectedMacroId: null,
            DonatelloOptions: null);
        require(!CraftingContextResolver.UsesRaphaelSolver(executionContext)
                && CraftingContextResolver.UsesSolverAssessment(executionContext),
            "Gabriel must expose chance assessment without enqueueing or waiting for an unused Raphael incumbent");
    }

    private static async Task ValidateGabrielScriptedRecovery(Action<bool, string> require)
    {
        var craft = FiveStarCraft();
        var root = GameStateBuilder.BuildInitialStepState(craft) with
        {
            CarefulObservationLeft = 0,
            CrafterDelineationsLeft = 0,
            HeartAndSoulAvailable = false,
            QuickInnoLeft = 0,
            QuickInnoAvailable = false,
            TrainedPerfectionAvailable = false,
        };
        var game = new SeededGame(
            craft,
            root,
            actionSeed: 0xA11CE,
            conditionSeed: 0xC0FFEE,
            forcedConditions: new Dictionary<int, Condition>
            {
                [1] = Condition.Pliant,
                [2] = Condition.Centered,
            },
            manualActions: new Dictionary<int, VulcanSkill>
            {
                [2] = VulcanSkill.BasicSynthesis,
            });

        CraftingProcessor.Setup();
        CraftingProcessor.RegisterSolver(new GabrielSolverDefinition());
        CraftingProcessor.OnCraftStarted(craft, root, craft.RecipeId, isTrial: false);
        try
        {
            require(CraftingProcessor.ActiveSolver is GabrielSolver,
                "cataloged per-item Gabriel selection must activate the real runtime Gabriel solver");
            var recommendation = await AwaitRecommendation(TimeSpan.FromSeconds(30));
            require(recommendation.Action is VulcanSkill.MuscleMemory or VulcanSkill.Reflect
                    && Simulator.CanUseAction(craft, root, recommendation.Action),
                "Gabriel's first plugin-path action must be Muscle Memory or Reflect");

            var firstAction = game.SelectAction(recommendation.Action, out var firstManual);
            require(!firstManual, "the first scripted Gabriel action must remain the plugin recommendation");
            var actual = game.Execute(firstAction, require);
            require(actual.Condition == Condition.Pliant,
                "scripted Gabriel simulation must force the first post-action condition to Pliant");
            var current = ReconcileRecommended(craft, root, firstAction, actual, require);
            CraftingProcessor.OnCraftAdvanced(craft, current, craft.RecipeId);

            recommendation = await AwaitRecommendation(TimeSpan.FromSeconds(30));
            require(recommendation.Action != VulcanSkill.BasicSynthesis
                    && recommendation.Action != VulcanSkill.HeartAndSoul,
                "Gabriel must not recommend Heart and Soul on Pliant, and the manual-action fixture must differ from its recommendation");
            var manualAction = game.SelectAction(recommendation.Action, out var manual);
            require(manual && manualAction == VulcanSkill.BasicSynthesis,
                "scripted action ordinal two must replace Gabriel's recommendation with Basic Synthesis");
            actual = game.Execute(manualAction, require);
            require(actual.Condition == Condition.Centered,
                "scripted Gabriel simulation must force the manual action result to Centered");
            var observed = Observe(current, actual);
            require(StepStateReconciler.TryReconcileExternalAction(
                    craft,
                    current,
                    observed,
                    out var reconciled,
                    out var externalActionObserved,
                    out var inferredAction)
                    && externalActionObserved
                    && inferredAction == VulcanSkill.BasicSynthesis,
                "manual Basic Synthesis must traverse the real external-action reconciliation boundary");
            require(CraftingProcessor.TryAdoptLiveCraft(
                    craft,
                    reconciled,
                    VulcanSolverMode.Gabriel,
                    out var failureReason),
                $"Gabriel must establish a fresh live policy root after manual intervention: {failureReason}");
            var recovered = await AwaitRecommendation(TimeSpan.FromSeconds(30));
            require(CraftingProcessor.ActiveSolver is GabrielSolver { NativeRecommendationCount: >= 1 }
                    && recovered.Action != VulcanSkill.None
                    && Simulator.CanUseAction(craft, reconciled, recovered.Action),
                "Gabriel must issue a legal native recommendation from the reconciled Centered state");
            require(game.Trace is
                [
                    { Manual: false, ResultCondition: Condition.Pliant },
                    {
                        Manual: true,
                        RecommendedAction: not VulcanSkill.BasicSynthesis,
                        ExecutedAction: VulcanSkill.BasicSynthesis,
                        ResultCondition: Condition.Centered,
                    },
                ],
                "Gabriel scripted trace must preserve forced conditions and recommended/executed divergence");
        }
        finally
        {
            CraftingProcessor.Dispose();
        }
    }

    private static void ValidateGabrielPluginPathSimulator(Action<bool, string> require)
    {
        var craft = FiveStarCraft();
        var root = GameStateBuilder.BuildInitialStepState(craft) with
        {
            TrainedPerfectionAvailable = false,
            StellarSteadyHandCharges = 3,
        };
        require(root.CarefulObservationLeft == 3
                && root.QuickInnoAvailable
                && root.CrafterDelineationsLeft == 2
                && root.StellarSteadyHandCharges == 3,
            "Gabriel specialist-action exclusion must be exercised with every forbidden action available in live state");
        var result = CraftingPluginPathSimulator.Run(
            craft,
            root,
            new GabrielSolverDefinition(policySeed: 7),
            VulcanSolverMode.Gabriel,
            new PluginPathSimulationScenario(
                GameSeed: 11,
                ForcedConditions: new Dictionary<int, Condition>
                {
                    [1] = Condition.Pliant,
                    [2] = Condition.Centered,
                },
                ManualActions: new Dictionary<int, VulcanSkill>
                {
                    [2] = VulcanSkill.BasicSynthesis,
                }));
        require(result.FailureReason == null || result.SolverTerminalFailure,
            $"faithful plugin-path simulator must not fail its own execution/reconciliation contract: {result.FailureReason}");
        require(result.Trace.Count >= 2
                && result.Trace[0] is
                {
                    Manual: false,
                    ExecutedAction: VulcanSkill.MuscleMemory or VulcanSkill.Reflect,
                    ResultCondition: Condition.Pliant,
                }
                && result.Trace[1] is
                {
                    Manual: true,
                    RecommendedAction: not VulcanSkill.HeartAndSoul,
                    ExecutedAction: VulcanSkill.BasicSynthesis,
                    ResultCondition: Condition.Centered,
                },
            "faithful plugin-path simulation must enforce Gabriel's opener and non-Normal Heart and Soul restriction through manual recovery");
        require(result.Trace.All(entry =>
                entry.RecommendedAction is not
                        (VulcanSkill.FinalAppraisal or VulcanSkill.CarefulObservation or VulcanSkill.QuickInnovation or VulcanSkill.StellarSteadyHand)
                    && entry.ExecutedAction is not
                        (VulcanSkill.FinalAppraisal or VulcanSkill.CarefulObservation or VulcanSkill.QuickInnovation or VulcanSkill.StellarSteadyHand)),
            "faithful Gabriel plugin execution must never recommend or execute a forbidden action");

        var heartAndSoulResult = CraftingPluginPathSimulator.Run(
            craft,
            root,
            new GabrielSolverDefinition(policySeed: 29),
            VulcanSolverMode.Gabriel,
            new PluginPathSimulationScenario(
                GameSeed: 31,
                ForcedConditions: new Dictionary<int, Condition>
                {
                    [1] = Condition.Normal,
                },
                ManualActions: new Dictionary<int, VulcanSkill>
                {
                    [2] = VulcanSkill.HeartAndSoul,
                }));
        var heartAndSoulIndices = heartAndSoulResult.Trace
            .Select((entry, index) => (entry, index))
            .Where(pair => pair.entry.ExecutedAction == VulcanSkill.HeartAndSoul)
            .Select(pair => pair.index)
            .ToArray();
        require((heartAndSoulResult.FailureReason == null || heartAndSoulResult.SolverTerminalFailure)
                && heartAndSoulResult.Trace.Count >= 3
                && heartAndSoulResult.Trace[0].ExecutedAction is VulcanSkill.MuscleMemory or VulcanSkill.Reflect
                && heartAndSoulResult.Trace[1] is
                {
                    Manual: true,
                    ExecutedAction: VulcanSkill.HeartAndSoul,
                    PreviousCondition: Condition.Normal,
                    ResultCondition: Condition.Normal,
                }
                && heartAndSoulIndices.Length > 0
                && heartAndSoulIndices.All(index =>
                    index + 1 < heartAndSoulResult.Trace.Count
                    && heartAndSoulResult.Trace[index].PreviousCondition == Condition.Normal
                    && heartAndSoulResult.Trace[index + 1].ExecutedAction is
                        VulcanSkill.TricksOfTrade or VulcanSkill.IntensiveSynthesis or VulcanSkill.PreciseTouch),
            "faithful plugin-path execution must use Heart and Soul only on Normal and immediately consume it with an associated action");

        var estimate = CraftingPluginPathSimulator.EstimateGabriel(
            craft,
            GameStateBuilder.BuildInitialStepState(craft),
            samples: 5,
            seed: 13);
        require(estimate.Samples == 5
                && estimate.Successes <= estimate.SynthesisCompletions
                && estimate.SynthesisCompletions <= estimate.Samples,
            "Gabriel validation estimate must aggregate actual faithful plugin-path outcomes");

        var unrestrictedCraft = craft with
        {
            StatCraftsmanship = 3000,
            StatControl = 2800,
            StatCP = 420,
            StatLevel = 80,
            UnlockedManipulation = false,
            Specialist = false,
            CrafterDelineations = 0,
            SplendorCosmic = false,
            CraftStars = 1,
            CraftDurability = 35,
            CraftProgress = 7500,
            CraftQualityMax = 21000,
            CraftRequiredQuality = 0,
        };
        var unrestrictedRoot = GameStateBuilder.BuildInitialStepState(unrestrictedCraft) with
        {
            Progress = unrestrictedCraft.CraftProgress - 1,
            Quality = unrestrictedCraft.CraftQualityMax,
        };
        var unrestrictedResult = CraftingPluginPathSimulator.Run(
            unrestrictedCraft,
            unrestrictedRoot,
            new GabrielSolverDefinition(policySeed: 17),
            VulcanSolverMode.Gabriel,
            new PluginPathSimulationScenario(GameSeed: 19));
        require(unrestrictedResult is
                {
                    SynthesisCompleted: true,
                    FullQuality: true,
                    SolverTerminalFailure: false,
                    FailureReason: null,
                    Trace.Count: 1,
                }
                && unrestrictedResult.Trace[0].ExecutedAction is not
                    (VulcanSkill.CarefulObservation or VulcanSkill.HeartAndSoul or VulcanSkill.QuickInnovation),
            "faithful plugin execution must use Gabriel with arbitrary geometry/stats/level and no tool, specialist, delineations, or Manipulation");

        var partialQualityResult = CraftingPluginPathSimulator.Run(
            unrestrictedCraft,
            unrestrictedRoot with { Quality = unrestrictedCraft.CraftQualityMax - 1 },
            new SeededRaphaelDefinition(new CachedRaphaelSolution
            {
                ActionIds = [(uint)VulcanSkill.BasicSynthesis],
            }),
            liveRecoveryMode: null,
            new PluginPathSimulationScenario(GameSeed: 23));
        require(partialQualityResult is
                {
                    SynthesisCompleted: true,
                    FullQuality: false,
                    SolverTerminalFailure: false,
                    FailureReason: null,
                },
            "a progress completion below maximum quality must not be reported as a full-quality completion");
    }

    private static void ValidateZeroStepExpediencePreservation(Action<bool, string> require)
    {
        var craft = Craft();
        var root = GameStateBuilder.BuildInitialStepState(craft) with
        {
            ExpedienceLeft = 1,
            PrevComboAction = VulcanSkill.HastyTouch,
        };
        var result = CraftingPluginPathSimulator.Run(
            craft,
            root,
            new SeededRaphaelDefinition(new CachedRaphaelSolution
            {
                ActionIds =
                [
                    (uint)VulcanSkill.FinalAppraisal,
                    (uint)VulcanSkill.BasicSynthesis,
                    (uint)VulcanSkill.BasicSynthesis,
                ],
            }),
            liveRecoveryMode: null,
            new PluginPathSimulationScenario(GameSeed: 37, IsTrial: true));
        require(result is
                {
                    SynthesisCompleted: true,
                    SolverTerminalFailure: false,
                    FailureReason: null,
                    Trace.Count: 3,
                }
                && result.Trace[0] is
                {
                    ExecutedAction: VulcanSkill.FinalAppraisal,
                    State.ExpedienceLeft: 1,
                }
                && result.Trace[0].State.Index == root.Index
                && result.Trace[0].State.Condition == root.Condition,
            "faithful plugin-path execution must preserve Expedience and the craft step across Final Appraisal");
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

    private static async Task ValidateProtectedRaphaelConditionTakeover(Action<bool, string> require)
    {
        var craft = Craft() with
        {
            StatControl = 700,
            CraftDurability = 40,
            CraftQualityMax = 100,
        };
        var solution = new CachedRaphaelSolution
        {
            ActionIds =
            [
                (uint)VulcanSkill.BasicTouch,
                (uint)VulcanSkill.BasicTouch,
                (uint)VulcanSkill.BasicSynthesis,
            ],
        };

        foreach (var condition in new[] { Condition.Good, Condition.Excellent, Condition.Poor })
        {
            CraftingProcessor.Setup();
            CraftingProcessor.RegisterSolver(new SeededDonatelloDefinition(solution));
            try
            {
                var root = Root(craft, Condition.Normal);
                CraftingProcessor.OnCraftStarted(craft, root, craft.RecipeId, isTrial: false);
                require(CraftingProcessor.ActiveSolver is DonatelloProtectedRaphaelSolver,
                    "a guaranteed max-quality Donatello plan must start with the protected Raphael incumbent");

                var recommendation = await AwaitRecommendation();
                require(recommendation.Action == VulcanSkill.BasicTouch,
                    "the protected Raphael incumbent must issue its first static action");
                ((DonatelloSolver)CraftingProcessor.ActiveSolver!).NotifyOpportunisticActionIssued();
                var (_, observed) = Simulator.Execute(craft, root, recommendation.Action, 0, 1);
                observed.Condition = condition;
                CraftingProcessor.OnCraftAdvanced(craft, observed, craft.RecipeId);

                require(CraftingProcessor.ActiveSolver is DonatelloProtectedRaphaelSolver,
                    $"{condition} must keep the protected Raphael incumbent solver");
                var wait = condition is Condition.Excellent or Condition.Poor
                    ? TimeSpan.FromSeconds(30)
                    : TimeSpan.FromSeconds(10);
                var takeoverRecommendation = await AwaitRecommendation(wait);
                require(!takeoverRecommendation.IsTerminalFailure
                        && takeoverRecommendation.Action != VulcanSkill.None
                        && Simulator.CanUseAction(craft, observed, takeoverRecommendation.Action)
                        && CraftingProcessor.ActiveSolver is DonatelloSolver { NativeReplanCount: >= 1 },
                    $"protected Raphael must replan from {condition} without replacing the incumbent solver");

                if (condition == Condition.Good)
                {
                    var active = (DonatelloSolver)CraftingProcessor.ActiveSolver!;
                    require(active.HasPendingOpportunisticReplan,
                        "Good-condition regression must issue the incumbent while its same-root opportunistic replan is still pending");
                    var expectedRemaining = active.RemainingActions.ToArray();
                    require(expectedRemaining.Length > 0,
                        "Good-condition regression requires an incumbent suffix after the issued action");
                    active.NotifyOpportunisticActionIssued();
                    var (_, afterIssuedAction) = Simulator.Execute(
                        craft,
                        observed,
                        takeoverRecommendation.Action,
                        0,
                        1);
                    afterIssuedAction.Condition = Condition.Normal;
                    require(active.WaitForPendingSolve(TimeSpan.FromSeconds(5)),
                        "interrupted old-root opportunistic replan must stop before the next observed node");
                    CraftingProcessor.OnCraftAdvanced(craft, afterIssuedAction, craft.RecipeId);
                    var nextRecommendation = await AwaitRecommendation();
                    require(nextRecommendation.Action == expectedRemaining[0],
                        $"late old-root replan must not rewind the consumed action; expected={expectedRemaining[0]}, actual={nextRecommendation.Action}, comment={nextRecommendation.Comment}");
                }
            }
            finally
            {
                CraftingProcessor.Dispose();
            }
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

    private sealed class SeededGabrielDefinition(ulong policySeed) : ISolverDefinition
    {
        public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
        {
            if (!GabrielPolicyCatalog.TryResolve(craft, out var policy, out var reason))
            {
                yield return new(this, 0, 200, "Seeded Gabriel", reason);
                yield break;
            }
            yield return new(this, (int)policy.Profile, 200, "Seeded Gabriel");
        }

        public Solver Create(CraftState craft, int flavor)
            => new GabrielSolver(craft, policySeed);
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
