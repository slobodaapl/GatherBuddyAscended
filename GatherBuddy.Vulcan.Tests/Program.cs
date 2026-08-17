using GatherBuddy.Vulcan;
using GatherBuddy.Vulcan.Vendors;
using GatherBuddy.Crafting;
using GatherBuddy.AutoGather;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.Utility;
using GatherBuddy.Vulcan.Tests;
using Newtonsoft.Json;
using System.Text.Json;
using System.Threading;

var assertions = 0;

typeof(global::GatherBuddy.GatherBuddy)
    .GetProperty(nameof(global::GatherBuddy.GatherBuddy.Log))!
    .GetSetMethod(nonPublic: true)!
    .Invoke(null, [new ElliLib.Log.Logger()]);
typeof(global::GatherBuddy.GatherBuddy)
    .GetProperty(nameof(global::GatherBuddy.GatherBuddy.Config))!
    .GetSetMethod(nonPublic: true)!
    .Invoke(null, [new GatherBuddy.Config.Configuration()]);

void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    assertions++;
}

ExpertConditionSamplerTests.Run(Require);
CollectableTerminalActionAcceptanceTests.Run(Require);
TimedLegendaryGpAcceptanceTests.Run(Require);

static CraftState Craft() => new()
{
    StatCraftsmanship = 100,
    StatControl = 100,
    StatCP = 500,
    StatLevel = 100,
    CraftLevel = 1,
    CraftDurability = 40,
    CraftProgress = 100,
    CraftQualityMax = 1000,
    CraftProgressDivider = 10,
    CraftProgressModifier = 100,
    CraftQualityDivider = 10,
    CraftQualityModifier = 100,
    UnlockedManipulation = true,
};

static StepState Root(Condition condition = Condition.Normal) => new()
{
    Index = 1,
    Durability = 40,
    RemainingCP = 500,
    Condition = condition,
    HeartAndSoulAvailable = true,
    CrafterDelineationsLeft = 2,
    TrainedPerfectionAvailable = true,
};

var deferredResume = new DeferredResumeRequest();
Require(deferredResume.Request(),
    "the first resume click must create a pending request");
Require(!deferredResume.Request(),
    "repeated resume clicks must not create duplicate requests");
Require(!deferredResume.TryComplete(liveStateReady: false) && deferredResume.Requested,
    "an unstable live frame must retain the pending resume request");
Require(!deferredResume.TryComplete(liveStateReady: false) && deferredResume.Requested,
    "repeated unstable live frames must retain the same resume request");
Require(deferredResume.TryComplete(liveStateReady: true) && !deferredResume.Requested,
    "the first stable live frame must consume the pending request exactly once");
Require(!deferredResume.TryComplete(liveStateReady: true),
    "a consumed resume request must not complete again");
deferredResume.Request();
deferredResume.Cancel();
Require(!deferredResume.Requested,
    "pausing or stopping must cancel a stale pending resume request");

if (args is ["--plugin-path-matrix", var corpusPath])
{
    await PluginPathRecipeMatrix.Run(corpusPath, Require);
    Console.WriteLine($"Plugin-path matrix acceptance: {assertions} assertions passed");
    return;
}

if (args is ["--plugin-path-matrix", var expertCorpusPath, "--experts-only"])
{
    await PluginPathRecipeMatrix.Run(expertCorpusPath, Require, expertsOnly: true);
    Console.WriteLine($"Plugin-path expert matrix acceptance: {assertions} assertions passed");
    return;
}

if (args is ["--five-star-plugin-simulation", var seedCountText]
    && int.TryParse(seedCountText, out var fiveStarSeedCount))
{
    await PluginPathSimulationAcceptanceTests.RunFiveStarDistribution(1, fiveStarSeedCount, Require);
    Console.WriteLine($"Five-star plugin-path simulation: {assertions} assertions passed");
    return;
}

if (args is ["--five-star-plugin-simulation", var seedStartText, var rangedSeedCountText]
    && int.TryParse(seedStartText, out var fiveStarSeedStart)
    && int.TryParse(rangedSeedCountText, out var rangedFiveStarSeedCount))
{
    await PluginPathSimulationAcceptanceTests.RunFiveStarDistribution(
        fiveStarSeedStart,
        rangedFiveStarSeedCount,
        Require);
    Console.WriteLine($"Five-star plugin-path simulation: {assertions} assertions passed");
    return;
}

var qualityTimeSettings = new RecipeCraftSettings { MaximizeQualityAtCostOfTime = true };
Require(qualityTimeSettings.HasAnySettings(),
    "the per-item quality/time override must be persisted as a meaningful craft setting");
var qualityRoundingCraft = Craft() with { CraftQualityMax = 2_000, SplendorCosmic = true };
var qualityRoundingRoot = Root(Condition.Excellent) with
{
    IQStacks = 4,
    InnovationLeft = 1,
};
var qualityRoundingResult = Simulator.Execute(
    qualityRoundingCraft,
    qualityRoundingRoot,
    VulcanSkill.BasicTouch,
    0,
    1);
Require(qualityRoundingResult.Item1 == Simulator.ExecuteResult.Succeeded
        && qualityRoundingResult.Item2.Quality == 1_134,
    "quality simulation must use native integer rounding for IQ and Innovation multipliers");
var livePreviewStats = new GameStateBuilder.PlayerStats(5000, 4800, 650, 100, true, true, false, 20);
var savedPreviewStats = livePreviewStats with { Craftsmanship = 4900 };
Require(CraftingContextResolver.SelectValidatorPreviewStats(
            requiredJob: 10,
            currentJob: 10,
            hasConfiguredStatConsumables: false,
            gearsetStats: null,
            () => livePreviewStats) == livePreviewStats
        && CraftingContextResolver.SelectValidatorPreviewStats(
            requiredJob: 10,
            currentJob: 9,
            hasConfiguredStatConsumables: false,
            gearsetStats: null,
            () => livePreviewStats) == null
        && CraftingContextResolver.SelectValidatorPreviewStats(
            requiredJob: 10,
            currentJob: 10,
            hasConfiguredStatConsumables: true,
            gearsetStats: null,
            () => livePreviewStats) == null
        && CraftingContextResolver.SelectValidatorPreviewStats(
            requiredJob: 10,
            currentJob: 10,
            hasConfiguredStatConsumables: false,
            gearsetStats: savedPreviewStats,
            () => livePreviewStats) == savedPreviewStats,
    "recipe-list validation must use live current-job stats when saved gearset reconstruction is unavailable, without inventing off-job or configured-consumable stats");
Require(GearsetStatsReader.TryResolveUncappedSpecialistStat(13, 20, 0, out var specialistCraftsmanship)
        && specialistCraftsmanship == 20
        && !GearsetStatsReader.TryResolveUncappedSpecialistStat(12, 20, 0, out _),
    "saved specialist soul-crystal stats must remain usable without a normal equipment-slot cap table");
Require(CraftingStateBuilder.IsSplendorCosmicTool(90, 4)
        && CraftingStateBuilder.IsSplendorCosmicTool(100, 4)
        && !CraftingStateBuilder.IsSplendorCosmicTool(80, 4)
        && !CraftingStateBuilder.IsSplendorCosmicTool(100, 3),
    "Splendorous/Cosmic tool detection must preserve its level and rarity boundaries");
var requiredQualityCraft = GameStateBuilder.BuildCraftState(
    new GameStateBuilder.RecipeInfo(
        RecipeId: 38247,
        Level: 100,
        Difficulty: 11_250,
        QualityMax: 31_520,
        RequiredQuality: 31_520,
        Durability: 60,
        ProgressDivider: 180,
        ProgressModifier: 100,
        QualityDivider: 180,
        QualityModifier: 100,
        CanHQ: false,
        IsExpert: true,
        Stars: 5,
        IsCollectible: false,
        QualityMin1: 31_520,
        QualityMin2: 31_520,
        QualityMin3: 31_520,
        ConditionFlags: ConditionFlags.Normal,
        HasMaterialMiracle: false,
        CurrentMaterialMiracleCharges: 0,
        HasStellarSteadyHand: false,
        CurrentStellarSteadyHandCharges: 0,
        CollectableMetadataKey: 0,
        IsCosmic: false),
    new GameStateBuilder.PlayerStats(5707, 5110, 700, 100, true, true, true, 2));
Require(requiredQualityCraft.CraftRequiredQuality == 31_520
        && requiredQualityCraft.SplendorCosmic,
    "runtime craft construction must preserve required quality and the equipped-tool Good bonus");
Require(qualityTimeSettings.Clone().MaximizeQualityAtCostOfTime,
    "cloning recipe or crafting-list item settings must preserve the quality/time override");
Require(JsonConvert.DeserializeObject<RecipeCraftSettings>(
        JsonConvert.SerializeObject(qualityTimeSettings))?.MaximizeQualityAtCostOfTime == true,
    "recipe settings JSON must persist the quality/time override");
qualityTimeSettings.DonatelloOptions = new DonatelloExecutionOptions(
    Objective: DonatelloSolveObjective.ProgressOnly);
var qualityTimeOptions = CraftingContextResolver.ResolveDonatelloOptions(qualityTimeSettings);
Require(qualityTimeOptions?.MaximizeQualityAtCostOfTime == true,
    "craft execution context must propagate the per-item quality/time override");
Require(qualityTimeOptions?.Objective == DonatelloSolveObjective.ProgressOnly,
    "the persisted quality/time override must preserve transient Donatello execution options");
var specialistOverrideSettings = new RecipeCraftSettings
{
    SpecialistActionOverride = SpecialistActionOverrideMode.Allow,
};
Require(specialistOverrideSettings.HasAnySettings()
        && specialistOverrideSettings.Clone().SpecialistActionOverride == SpecialistActionOverrideMode.Allow
        && JsonConvert.DeserializeObject<RecipeCraftSettings>(
            JsonConvert.SerializeObject(specialistOverrideSettings))?.SpecialistActionOverride == SpecialistActionOverrideMode.Allow
        && CraftingContextResolver.ResolveDonatelloOptions(specialistOverrideSettings)?.AllowSpecialistActions == true
        && CraftingContextResolver.ResolveDonatelloOptions(new RecipeCraftSettings
        {
            SpecialistActionOverride = SpecialistActionOverrideMode.Disallow,
        })?.AllowSpecialistActions == false,
    "per-item specialist eligibility overrides must persist and reach execution options");
Require(DonatelloSolver.ResolveLiveReplanDeadlineMillis(
        Craft() with { DonatelloOptions = qualityTimeOptions }, 50, 184) == 30_000,
    "the per-item quality/time override must raise the live replan cutoff to 30 seconds");
Require(DonatelloSolver.ResolveLiveReplanDeadlineMillis(Craft(), 50, 184) == 184
        && DonatelloSolver.ResolveLiveReplanDeadlineMillis(Craft(), 250, 184) == 250,
    "live replans must use the larger of the configured optimization threshold and overlapping action delay");
Require(!VulcanSkill.None.IsExecutableAction()
        && !VulcanSkill.TouchCombo.IsExecutableAction()
        && !((VulcanSkill)999_999).IsExecutableAction()
        && VulcanSkill.BasicSynthesis.IsExecutableAction()
        && !Simulator.CanUseAction(Craft(), Root(), (VulcanSkill)999_999),
    "unknown and pseudo action IDs must be rejected before simulation or game execution");
var primedAfterMaterialMiracle = Root(Condition.Primed) with { MaterialMiracleCharges = 0 };
var primedDonatello = new DonatelloSolver(
    new CachedRaphaelSolution { ActionIds = [(uint)VulcanSkill.BasicSynthesis] },
    Craft());
var primedRecommendation = await primedDonatello.SolveUntilReadyAsync(
    Craft(),
    primedAfterMaterialMiracle,
    resume: false);
Require(primedRecommendation.Action != VulcanSkill.None,
    "a Primed root after automatic Material Miracle must wake after its native replan and produce an action");
Require(DonatelloSolverDefinition.TryCreateLiveSolver(
        Craft(),
        out var uncachedLiveDonatello,
        out var liveSolverFailure)
    && uncachedLiveDonatello != null
    && string.IsNullOrEmpty(liveSolverFailure),
    "mid-craft reload recovery must create Donatello directly from live state without an initial cached plan");
var uncachedLiveRecommendation = await uncachedLiveDonatello!.SolveUntilReadyAsync(
    Craft(),
    Root(),
    resume: true);
Require(uncachedLiveRecommendation.Action != VulcanSkill.None,
    "uncached Donatello mid-craft recovery must solve from the observed live root and produce an action");
var cobaltTungstenCraft = new CraftState
{
    RecipeId = 5630,
    StatCraftsmanship = 5688,
    StatControl = 4982,
    StatCP = 573,
    StatLevel = 100,
    CraftLevel = 94,
    CraftDurability = 40,
    CraftProgress = 2400,
    CraftQualityMax = 9400,
    CraftProgressDivider = 152,
    CraftProgressModifier = 100,
    CraftQualityDivider = 132,
    CraftQualityModifier = 100,
    UnlockedManipulation = true,
    SplendorCosmic = true,
};
var cobaltTungstenReloadRoot = new StepState
{
    Index = 11,
    Progress = 1128,
    Quality = 9400,
    Durability = 20,
    RemainingCP = 229,
    Condition = Condition.Normal,
    CrafterDelineationsLeft = 116,
};
Require(DonatelloSolverDefinition.TryCreateLiveSolver(
        cobaltTungstenCraft,
        out var cobaltTungstenRecovery,
        out var cobaltTungstenRecoveryFailure)
    && cobaltTungstenRecovery != null
    && string.IsNullOrEmpty(cobaltTungstenRecoveryFailure),
    "recipe 5630 reload recovery must create a fresh solver without the stale recipe-start Raphael rotation");
var cobaltTungstenState = cobaltTungstenReloadRoot;
var cobaltTungstenResume = true;
for (var recoveryAction = 0; recoveryAction < 12
     && SolverUtils.Status(cobaltTungstenCraft, cobaltTungstenState) == SolverUtils.CraftStatus.InProgress;
     recoveryAction++)
{
    var recommendation = await cobaltTungstenRecovery!.SolveUntilReadyAsync(
        cobaltTungstenCraft,
        cobaltTungstenState,
        cobaltTungstenResume);
    cobaltTungstenResume = false;
    Require(recommendation.Action != VulcanSkill.None && !recommendation.IsTerminalFailure,
        $"recipe 5630 live-root Raphael baseline must produce recovery action {recoveryAction + 1}: {recommendation.Comment}");
    var (result, next) = Simulator.Execute(
        cobaltTungstenCraft,
        cobaltTungstenState,
        recommendation.Action,
        0,
        1);
    Require(result != Simulator.ExecuteResult.CantUse,
        $"recipe 5630 live-root recovery action {recommendation.Action} must be usable");
    cobaltTungstenState = next;
}
Require(SolverUtils.Status(cobaltTungstenCraft, cobaltTungstenState) == SolverUtils.CraftStatus.Complete
        && cobaltTungstenState.Quality == cobaltTungstenCraft.CraftQualityMax,
    "recipe 5630 step-11 reload recovery must complete while preserving its already-maximized quality");
var completedReloadCraft = cobaltTungstenCraft with { CraftProgress = 1128 };
Require(!CraftingGameInterop.RequiresLiveSolver(completedReloadCraft, cobaltTungstenReloadRoot)
        && CraftingGameInterop.RequiresLiveSolver(cobaltTungstenCraft, cobaltTungstenReloadRoot),
    "reload recovery must wait for the game lifecycle when the observed root is already complete, while still solving partial live roots");
qualityTimeSettings.Clear();
Require(!qualityTimeSettings.MaximizeQualityAtCostOfTime && !qualityTimeSettings.HasAnySettings(),
    "clearing per-item settings must clear the quality/time override");
specialistOverrideSettings.Clear();
Require(specialistOverrideSettings.SpecialistActionOverride == SpecialistActionOverrideMode.Inherit
        && !specialistOverrideSettings.HasAnySettings(),
    "clearing per-item settings must restore inherited specialist eligibility");

var hardExpertProgressCraft = new CraftState
{
    StatCraftsmanship = 5474,
    StatControl = 4857,
    StatCP = 573,
    StatLevel = 100,
    CraftLevel = 100,
    CraftDurability = 45,
    CraftProgress = 6900,
    CraftQualityMax = 22100,
    CraftProgressDivider = 170,
    CraftProgressModifier = 100,
    CraftQualityDivider = 150,
    CraftQualityModifier = 100,
    CraftExpert = true,
    UnlockedManipulation = true,
};
var hardExpertProgressState = new StepState
{
    Index = 1,
    Durability = hardExpertProgressCraft.CraftDurability,
    RemainingCP = hardExpertProgressCraft.StatCP,
    Condition = Condition.Normal,
    TrainedPerfectionAvailable = true,
};

StepState ReplayNativePlan(
    CraftState craft,
    StepState root,
    DonatelloNative.SolveResult result,
    string caseName)
{
    var state = root;
    foreach (var action in result.Actions)
    {
        if (SolverUtils.Status(craft, state) != SolverUtils.CraftStatus.InProgress)
            break;
        var (executeResult, next) = Simulator.Execute(craft, state, action, 0, 1);
        Require(executeResult == Simulator.ExecuteResult.Succeeded,
            $"{caseName}: native action {action} must be legal in GatherBuddy at {state}");
        state = next;
    }
    Require(result.FinalState.Complete == (SolverUtils.Status(craft, state) == SolverUtils.CraftStatus.Complete)
            && result.FinalState.Cp == state.RemainingCP
            && result.FinalState.Durability == state.Durability
            && result.FinalState.Progress == state.Progress
            && result.FinalState.Quality == state.Quality,
        $"{caseName}: raphael-sim final {result.FinalState.Progress}/{result.FinalState.Quality}/{result.FinalState.Durability}/{result.FinalState.Cp} must equal GatherBuddy {state.Progress}/{state.Quality}/{state.Durability}/{state.RemainingCP}");
    return state;
}

foreach (var delineations in new[] { 0, 1, 2 })
{
    var crossCraft = hardExpertProgressCraft with
    {
        CraftQualityMax = 0,
        Specialist = true,
        CrafterDelineations = delineations,
    };
    var crossRoot = GameStateBuilder.BuildInitialStepState(crossCraft);
    var native = DonatelloNative.SolveDetailed(
        crossCraft,
        crossRoot,
        allowSpecialistActions: true,
        DonatelloNative.SolveMode.CompleteFastest,
        softDeadlineMillis: 30_000,
        hardDeadlineMillis: 30_000,
        bypassSolutionCache: true);
    var final = ReplayNativePlan(crossCraft, crossRoot, native, $"recipe 38202 specialist d{delineations}");
    Require(SolverUtils.Status(crossCraft, final) == SolverUtils.CraftStatus.Complete,
        $"recipe 38202 specialist d{delineations}: native Complete/Fastest must complete");
}

var (benchmarkCraft, benchmarkBaseline, benchmarkScenarios) =
    DonatelloOptimizationBenchmark.CreateFixtureScenarios();
var benchmarkWarmup = benchmarkScenarios[0];
DonatelloNative.SolveDetailed(
    benchmarkCraft,
    benchmarkWarmup.Root,
    allowSpecialistActions: true,
    DonatelloNative.SolveMode.LiveAdaptive,
    incumbent: benchmarkWarmup.Incumbent,
    softDeadlineMillis: 2000,
    hardDeadlineMillis: 2000,
    bypassSolutionCache: true,
    minimizeSteps: true);
var benchmarkWins = 0;
foreach (var scenario in benchmarkScenarios.Where(scenario =>
             scenario.Root.Condition is Condition.Excellent or Condition.Good or Condition.GoodOmen))
{
    var benchmarkIncumbent = DonatelloPlanEvaluator.Evaluate(benchmarkCraft, scenario.Root, scenario.Incumbent);
    var candidate = DonatelloNative.SolveDetailed(
        benchmarkCraft,
        scenario.Root,
        allowSpecialistActions: true,
        DonatelloNative.SolveMode.LiveAdaptive,
        incumbent: scenario.Incumbent,
        softDeadlineMillis: 100,
        hardDeadlineMillis: 100,
        bypassSolutionCache: true,
        minimizeSteps: true);
    var evaluation = DonatelloPlanEvaluator.Evaluate(benchmarkCraft, scenario.Root, candidate.Actions);
    Require(evaluation.Completes, "known-win benchmark candidate must preserve completion");
    Require(!benchmarkIncumbent.IsStrictlyBetterThan(evaluation),
        "known-win benchmark candidate must not regress its incumbent");
    if (evaluation.IsStrictlyBetterThan(benchmarkIncumbent))
    {
        benchmarkWins++;
        break;
    }
}
Require(benchmarkWins > 0,
    $"recipe 38202 specialist d2 benchmark fixture must produce a strict Donatello win within 100 ms; "
    + $"baseline={DonatelloPlanEvaluator.Evaluate(benchmarkCraft, GameStateBuilder.BuildInitialStepState(benchmarkCraft), benchmarkBaseline)}");

var protectedBoundaryCraft = Craft();
var protectedBoundaryRoot = GameStateBuilder.BuildInitialStepState(protectedBoundaryCraft);
var protectedRaphael = DonatelloNative.SolveDetailed(
    protectedBoundaryCraft,
    protectedBoundaryRoot,
    allowSpecialistActions: false,
    DonatelloNative.SolveMode.OptimizeQuality,
    softDeadlineMillis: 2_000,
    hardDeadlineMillis: 2_000,
    bypassSolutionCache: true);
var protectedRuntimeCandidate = DonatelloNative.SolveDetailed(
    protectedBoundaryCraft,
    protectedBoundaryRoot,
    allowSpecialistActions: false,
    DonatelloNative.SolveMode.LiveAdaptive,
    incumbent: protectedRaphael.Actions,
    softDeadlineMillis: 2_000,
    hardDeadlineMillis: 2_000,
    bypassSolutionCache: true,
    minimizeSteps: true);
var protectedRaphaelScore = DonatelloPlanEvaluator.Evaluate(
    protectedBoundaryCraft, protectedBoundaryRoot, protectedRaphael.Actions);
var protectedRuntimeScore = DonatelloPlanEvaluator.Evaluate(
    protectedBoundaryCraft, protectedBoundaryRoot, protectedRuntimeCandidate.Actions);
var protectedRuntimeAdopted = DonatelloSolver.ShouldAdoptCandidate(
    protectedBoundaryCraft,
    protectedRuntimeScore,
    protectedRaphaelScore,
    stagedProgressPlan: protectedRuntimeCandidate.ProgressBoundary is { Target: "oneShort" });
var protectedSelectedScore = protectedRuntimeAdopted ? protectedRuntimeScore : protectedRaphaelScore;
Require(protectedSelectedScore == protectedRaphaelScore
        || protectedSelectedScore.IsStrictlyBetterThan(protectedRaphaelScore),
    "real native staged solve -> C# admission must never select a normal-craft result worse than Raphael");

var cosmicCraft = new CraftState
{
    StatCraftsmanship = 6000,
    StatControl = 6000,
    StatCP = 600,
    StatLevel = 100,
    UnlockedManipulation = true,
    Specialist = true,
    CrafterDelineations = 2,
    SplendorCosmic = true,
    CraftExpert = true,
    CraftLevel = 100,
    CraftDurability = 80,
    CraftProgress = 10_000,
    CraftQualityMax = 20_000,
    CraftProgressDivider = 60,
    CraftProgressModifier = 100,
    CraftQualityDivider = 62,
    CraftQualityModifier = 100,
};
var cosmicConditions = new[]
{
    Condition.Good,
    Condition.Centered,
    Condition.Sturdy,
    Condition.Pliant,
    Condition.Malleable,
    Condition.Primed,
    Condition.GoodOmen,
    Condition.Robust,
    Condition.Excellent,
    Condition.Poor,
    Condition.Normal,
};
var cosmicState = GameStateBuilder.BuildInitialStepState(cosmicCraft);
IReadOnlyList<VulcanSkill> cosmicIncumbent = [];
for (var step = 0; step < 100 && SolverUtils.Status(cosmicCraft, cosmicState) == SolverUtils.CraftStatus.InProgress; ++step)
{
    cosmicState = cosmicState with { Condition = cosmicConditions[step % cosmicConditions.Length] };
    var incumbentEvaluation = DonatelloPlanEvaluator.Evaluate(cosmicCraft, cosmicState, cosmicIncumbent);
    var native = DonatelloNative.SolveDetailed(
        cosmicCraft,
        cosmicState,
        allowSpecialistActions: true,
        DonatelloNative.SolveMode.LiveAdaptive,
        incumbent: cosmicIncumbent,
        softDeadlineMillis: 1000,
        hardDeadlineMillis: 1000,
        bypassSolutionCache: true);
    Require(native.ElapsedMillis <= 1500,
        $"Cosmic live replan {step} exceeded its 1000ms hard budget: {native.ElapsedMillis}ms");
    Require(native.Actions.Count > 0, $"Cosmic live replan {step} returned no action");
    ReplayNativePlan(cosmicCraft, cosmicState, native, $"Cosmic live replan {step}");
    var candidateEvaluation = DonatelloPlanEvaluator.Evaluate(cosmicCraft, cosmicState, native.Actions);
    Require(!incumbentEvaluation.Completes
            || !incumbentEvaluation.IsStrictlyBetterThan(candidateEvaluation),
        $"Cosmic live replan {step} replaced a completing incumbent with a lexicographically worse plan");
    var (executeResult, next) = Simulator.Execute(cosmicCraft, cosmicState, native.Actions[0], 0, 1);
    Require(executeResult == Simulator.ExecuteResult.Succeeded,
        $"Cosmic live replan {step} first action {native.Actions[0]} must be legal");
    cosmicState = next;
    cosmicIncumbent = native.Actions.Skip(1).ToArray();
}
Require(SolverUtils.Status(cosmicCraft, cosmicState) == SolverUtils.CraftStatus.Complete,
    $"condition-changing Cosmic expert must complete within 100 actions; final: {cosmicState}");

var hardExpertProgressActions = new List<VulcanSkill>();
var hardExpertProgressSolver = new ProgressOnlySolver();
for (var step = 0; step < 100 && SolverUtils.Status(hardExpertProgressCraft, hardExpertProgressState) == SolverUtils.CraftStatus.InProgress; ++step)
{
    var action = hardExpertProgressSolver.Solve(hardExpertProgressCraft, hardExpertProgressState).Action;
    var (result, next) = Simulator.Execute(hardExpertProgressCraft, hardExpertProgressState, action, 0, 1);
    Require(result == Simulator.ExecuteResult.Succeeded,
        $"progress-only emergency action {action} must be legal at {hardExpertProgressState}");
    hardExpertProgressActions.Add(action);
    hardExpertProgressState = next;
}
Require(SolverUtils.Status(hardExpertProgressCraft, hardExpertProgressState) == SolverUtils.CraftStatus.Complete,
    $"native progress-only solver must complete the 6900-difficulty expert recipe; actions: {string.Join(", ", hardExpertProgressActions)}; final: {hardExpertProgressState}");
Console.WriteLine($"Native progress-only 6900 expert: {string.Join(", ", hardExpertProgressActions)} -> {hardExpertProgressState}");

Require(SearchTextNormalizer.Normalize("Ra'Kaznar Ingot").Contains(SearchTextNormalizer.Normalize("rakaznar")),
    "recipe search must ignore apostrophes");
Require(SearchTextNormalizer.Normalize("Crème brûlée") == "cremebrulee",
    "recipe search must remove combining diacritics and separators");
Require(SearchTextNormalizer.Normalize("黒鉄鉱") == "黒鉄鉱",
    "recipe search must preserve non-Latin letters");
Require(FuzzySearch.Score("rakaznaringot", ["razaknar"]) == 2,
    "recipe search fallback must recognize swapped non-adjacent letters");
Require(FuzzySearch.Score("rakaznaringot", ["raakznar"]) == 1,
    "recipe search fallback must recognize adjacent transpositions");
Require(FuzzySearch.Score("rakaznaringot", ["rakazmer"]) == 2,
    "recipe search fallback must recognize limited substitutions");
Require(FuzzySearch.Score("rakaznaringot", ["unrelated"]) == null,
    "recipe search fallback must reject weak matches");
Require(FuzzySearch.Score("raisinbread", ["raisin", "bred"]) == 1,
    "recipe search fallback must match every search term independently");
var allocationQuery = new[] { "razaknar" };
_ = FuzzySearch.Score("rakaznaringot", allocationQuery);
var fuzzyAllocationStart = GC.GetAllocatedBytesForCurrentThread();
for (var iteration = 0; iteration < 100; ++iteration)
    _ = FuzzySearch.Score("rakaznaringot", allocationQuery);
Require(GC.GetAllocatedBytesForCurrentThread() == fuzzyAllocationStart,
    "recipe fuzzy scoring must not allocate managed memory for normal-length names");
var recipeSearchNames = new[]
{
    "Ra'Kaznar Ingot",
    "Raisin Bread",
    "Darksteel Ingot",
};
Require(RecipeSearch.Filter(recipeSearchNames, "rakaznar", name => name).SequenceEqual(["Ra'Kaznar Ingot"]),
    "Recipes-tab search must resolve Ra'Kaznar after removing punctuation");
Require(RecipeSearch.Filter(recipeSearchNames, "razaknar", name => name).SequenceEqual(["Ra'Kaznar Ingot"]),
    "Recipes-tab search must fall back to the closest typo-tolerant match");
var competingFuzzyNames = new[]
{
    "Ra'Kaznar Ingot",
    "Razaknor Ring",
    "Darksteel Ingot",
};
Require(RecipeSearch.Filter(competingFuzzyNames, "razaknar", name => name).SequenceEqual(["Razaknor Ring"]),
    "Recipes-tab fallback must retain only candidates with the globally closest accepted score");
var autoHomeWarning = AutoHomeNotification.Build("gathering is done");
Require(autoHomeWarning.Contains("gathering is done")
     && autoHomeWarning.Contains("Go home when done")
     && autoHomeWarning.Contains("Show auto-home chat warning"),
    "auto-home warning must explain its trigger, behavior setting, and warning toggle");
Require(CraftingJobTransitionValidator.FindMissingGearsets([13u], 13u, _ => false).Count == 0,
    "current crafting job must not require a saved gearset when no switch is needed");
Require(CraftingJobTransitionValidator.FindMissingGearsets([13u, 9u], 13u, _ => false).SequenceEqual([9u]),
    "preflight must reject the first missing gearset needed by a class transition");
Require(CraftingJobTransitionValidator.FindMissingGearsets([13u, 9u, 13u], 13u, job => job == 9u).SequenceEqual([13u]),
    "preflight must require the original job gearset when queue order switches away and back");
Require(!new CraftingListDefinition().PreferBestClassForMultiRecipeItems,
    "best-class recipe replacement must remain opt-in");
var synthesisValues = new int[18];
synthesisValues[4] = 1;
synthesisValues[6] = 3696;
synthesisValues[8] = 80;
synthesisValues[10] = 18;
synthesisValues[17] = 7320;
var parsedLiveMetrics = SynthesisReader.ReadLiveCraftMetrics(synthesisValues);
Require(parsedLiveMetrics == new SynthesisReader.LiveCraftMetrics(3696, 7320, 80),
    "live-craft recovery must parse max progress, quality, and durability from the raw interleaved Synthesis addon layout without mistaking HQ chance for max quality");
Require(SynthesisReader.MatchesCraftMetrics(Craft(), 100, 1000, 40)
        && !SynthesisReader.MatchesCraftMetrics(Craft(), 101, 1000, 40)
        && !SynthesisReader.MatchesCraftMetrics(Craft(), 100, 999, 40)
        && !SynthesisReader.MatchesCraftMetrics(Craft(), 100, 1000, 41),
    "live-craft metric comparison must detect progress, quality, and durability divergence");
var liveMetricCraft = SynthesisReader.ApplyLiveCraftMetrics(
    Craft(),
    new SynthesisReader.LiveCraftMetrics(321, 654, 70));
Require(liveMetricCraft.CraftProgress == 321
        && liveMetricCraft.CraftQualityMax == 654
        && liveMetricCraft.CraftDurability == 70
        && liveMetricCraft.RecipeId == Craft().RecipeId
        && liveMetricCraft.StatCraftsmanship == Craft().StatCraftsmanship,
    "mid-craft recovery must use authoritative live synthesis dimensions without changing recipe identity or player stats");
Require(ArtisanIpcShim.RemainingRequestedAfterRecovery(100, 100, 3) == 2
        && ArtisanIpcShim.RemainingRequestedAfterRecovery(100, 101, 3) == 3
        && ArtisanIpcShim.RemainingRequestedAfterRecovery(100, 100, 1) == 0
        && ArtisanIpcShim.RemainingRequestedAfterRecovery(100, 100, 3, alreadyQueued: 3) == 0,
    "ICE reload recovery must count the already-running craft exactly once and deduplicate its reissued quantity");
Require(ArtisanIpcShim.ResolveSolverOverride("Progress Only Solver") == SolverOverrideMode.ProgressOnlySolver
        && ArtisanIpcShim.ResolveSolverOverride("Standard Recipe Solver") == SolverOverrideMode.StandardSolver
        && ArtisanIpcShim.ResolveSolverOverride("Raphael Recipe Solver") == SolverOverrideMode.RaphaelSolver
        && ArtisanIpcShim.ResolveSolverOverride("Expert Recipe Solver") == SolverOverrideMode.DonatelloSolver,
    "Artisan solver selections must preserve supported solver semantics instead of silently forcing Donatello");
var unsupportedArtisanSolverRejected = false;
try
{
    ArtisanIpcShim.ResolveSolverOverride("Imaginary Solver");
}
catch (NotSupportedException)
{
    unsupportedArtisanSolverRejected = true;
}
Require(unsupportedArtisanSolverRejected,
    "unsupported Artisan solver semantics must be rejected immediately");
var recoveryTicket = CraftingRecoveryTicket.Capture(
    CraftingAutomationOwner.ArtisanIpc,
    [
        new CraftingListItem(100, 1)
        {
            IsOriginalRecipe = true,
            Options = new ListItemOptions { NQOnly = true },
            CraftSettings = new RecipeCraftSettings
            {
                SolverOverride = SolverOverrideMode.DonatelloSolver,
                DonatelloOptions = new DonatelloExecutionOptions(
                    DonatelloSolveObjective.ProgressOnly,
                    MaxStellarSteadyHandUses: 2),
            },
        },
        new CraftingListItem(101, 1)
        {
            IsOriginalRecipe = true,
            Options = new ListItemOptions { Skipping = true },
        },
    ],
    new CraftingListConsumableSettings { FoodItemId = 42, FoodHQ = true });
var restoredTicket = JsonConvert.DeserializeObject<CraftingRecoveryTicket>(
    JsonConvert.SerializeObject(recoveryTicket))!;
Require(restoredTicket.TryRestore(out var restoredQueue, out _)
        && restoredTicket.Owner == CraftingAutomationOwner.ArtisanIpc
        && restoredQueue.Select(item => item.RecipeId).SequenceEqual([100u, 101u])
        && restoredQueue[0].Options.NQOnly
        && restoredQueue[1].Options.Skipping
        && restoredQueue[0].CraftSettings?.DonatelloOptions
            == new DonatelloExecutionOptions(
                DonatelloSolveObjective.ProgressOnly,
                MaxStellarSteadyHandUses: 2)
        && restoredTicket.ListConsumables?.FoodItemId == 42
        && restoredTicket.ListConsumables.FoodHQ,
    "durable crafting ownership must round-trip queue order, ICE owner, solver objective, and consumable context");
var recoveryPlan = CraftingExecutionPlan.CreateRecovery(restoredQueue);
recoveryPlan.RefreshFromCurrentInventory();
Require(recoveryPlan.QueueView.Select(item => item.RecipeId).SequenceEqual([100u, 101u])
        && recoveryPlan.QueueView[0].Options.NQOnly
        && recoveryPlan.QueueView[1].Options.Skipping
        && recoveryPlan.QueueView[0].CraftSettings?.DonatelloOptions
            == new DonatelloExecutionOptions(
                DonatelloSolveObjective.ProgressOnly,
                MaxStellarSteadyHandUses: 2),
    "recovery preflight refresh must preserve exact queue order and per-craft settings");
Require(CraftingRecoveryTicket.DecideStartupRecovery(
            restoredTicket,
            playerAvailable: true,
            synthesisOpen: true,
            activeRecipeId: 100,
            probeElapsed: TimeSpan.Zero) == CraftingStartupRecoveryDecision.Start
        && CraftingRecoveryTicket.DecideStartupRecovery(
            restoredTicket,
            playerAvailable: true,
            synthesisOpen: true,
            activeRecipeId: 999,
            probeElapsed: TimeSpan.Zero) == CraftingStartupRecoveryDecision.Discard
        && CraftingRecoveryTicket.DecideStartupRecovery(
            restoredTicket,
            playerAvailable: true,
            synthesisOpen: false,
            activeRecipeId: null,
            probeElapsed: TimeSpan.Zero) == CraftingStartupRecoveryDecision.Wait
        && CraftingRecoveryTicket.DecideStartupRecovery(
            restoredTicket,
            playerAvailable: true,
            synthesisOpen: false,
            activeRecipeId: null,
            probeElapsed: CraftingRecoveryTicket.StartupProbeTimeout) == CraftingStartupRecoveryDecision.Discard,
    "startup recovery must resume only an owned matching synthesis and discard stale or mismatched ownership");
Require(CraftingRecoveryTicket.DecideStartupRecovery(
            restoredTicket,
            playerAvailable: true,
            synthesisOpen: true,
            activeRecipeId: null,
            probeElapsed: CraftingRecoveryTicket.StartupProbeTimeout - TimeSpan.FromMilliseconds(1)) == CraftingStartupRecoveryDecision.Wait
        && CraftingRecoveryTicket.DecideStartupRecovery(
            restoredTicket,
            playerAvailable: true,
            synthesisOpen: true,
            activeRecipeId: null,
            probeElapsed: CraftingRecoveryTicket.StartupProbeTimeout) == CraftingStartupRecoveryDecision.Discard,
    "an open synthesis with an unreadable recipe ID must not keep startup recovery alive forever");
Require(CraftingGameInterop.ConservativeRecoveredStellarSteadyHandsUsed(
            new DonatelloExecutionOptions(MaxStellarSteadyHandUses: 3)) == 3
        && CraftingGameInterop.ConservativeRecoveredStellarSteadyHandsUsed(null) == 0,
    "reload recovery must fail closed on unknown prior Stellar Steady Hand uses");
Require(CraftingGatherBridge.RecoveryRequiresBaselineWarning(
            new CraftingExecutionContext(
                null,
                new CraftingQualityPolicy(1, CraftingQualityOverrideMode.None, false, []),
                VulcanSolverMode.Donatello,
                false,
                true,
                false,
                null,
                null))
        && !CraftingGatherBridge.RecoveryRequiresBaselineWarning(
            new CraftingExecutionContext(
                null,
                new CraftingQualityPolicy(1, CraftingQualityOverrideMode.None, false, []),
                VulcanSolverMode.Donatello,
                false,
                true,
                false,
                null,
                new DonatelloExecutionOptions(DonatelloSolveObjective.ProgressOnly))),
    "reload recovery must immediately report quality-mode loss of the pre-reload Raphael baseline");

var cordialInventory = new Dictionary<uint, int>
{
    [CordialSelector.HiCordialItemId] = 1,
    [CordialSelector.CordialItemId] = 1,
    [CordialSelector.CordialItemId + CordialSelector.HqItemOffset] = 1,
    [CordialSelector.WateredCordialItemId] = 1,
    [CordialSelector.WateredCordialItemId + CordialSelector.HqItemOffset] = 1,
};
int CordialCount(uint itemId) => cordialInventory.GetValueOrDefault(itemId);

var strongestCordial = new ConfigPreset.CordialConfig
{
    SelectionMode = ConfigPreset.CordialSelectionMode.StrongestFirst,
    HqPreference = ConfigPreset.CordialHqPreference.HqBeforeNq,
};
Require(CordialSelector.Select(strongestCordial, 0, 1000, CordialCount) == CordialSelector.HiCordialItemId,
    "strongest-first must prefer Hi-Cordial over lower-tier HQ cordials");

cordialInventory[CordialSelector.HiCordialItemId] = 0;
Require(CordialSelector.Select(strongestCordial, 0, 1000, CordialCount)
        == CordialSelector.CordialItemId + CordialSelector.HqItemOffset,
    "HQ-first must prefer HQ within the first available cordial tier");

var weakestCordial = strongestCordial with
{
    SelectionMode = ConfigPreset.CordialSelectionMode.WeakestFirst,
    HqPreference = ConfigPreset.CordialHqPreference.NqBeforeHq,
};
Require(CordialSelector.Select(weakestCordial, 0, 1000, CordialCount) == CordialSelector.WateredCordialItemId,
    "weakest-first with NQ preference must begin with NQ Watered Cordial");

var hqOnlyCordial = strongestCordial with { HqPreference = ConfigPreset.CordialHqPreference.HqOnly };
cordialInventory[CordialSelector.HiCordialItemId] = 1;
Require(CordialSelector.Select(hqOnlyCordial, 0, 1000, CordialCount)
        == CordialSelector.CordialItemId + CordialSelector.HqItemOffset,
    "HQ-only must skip Hi-Cordial because no HQ variant exists");

var nqOnlyCordial = strongestCordial with { HqPreference = ConfigPreset.CordialHqPreference.NqOnly };
cordialInventory[CordialSelector.HiCordialItemId] = 0;
Require(CordialSelector.Select(nqOnlyCordial, 0, 1000, CordialCount) == CordialSelector.CordialItemId,
    "NQ-only must ignore an owned HQ cordial");

var noOvercapCordial = strongestCordial with { PreventGpOvercap = true };
cordialInventory[CordialSelector.HiCordialItemId] = 1;
Require(CordialSelector.Select(noOvercapCordial, 750, 1000, CordialCount)
        == CordialSelector.WateredCordialItemId + CordialSelector.HqItemOffset,
    "overcap prevention must fall through to the strongest cordial whose full restoration fits");
Require(CordialSelector.Select(strongestCordial, 750, 1000, CordialCount) == CordialSelector.HiCordialItemId,
    "disabled overcap prevention must retain legacy threshold-based selection");

var specificCordial = new ConfigPreset.CordialConfig
{
    ItemId = CordialSelector.WateredCordialItemId,
    PreventGpOvercap = true,
};
Require(CordialSelector.Select(specificCordial, 851, 1000, CordialCount) == 0,
    "overcap prevention must also apply to an explicitly selected cordial");

var noSolutionMessage = RaphaelSolveCoordinator.FormatFailureReason(
    "thread 'main' panicked: Failed to solve: NoSolution");
Require(RaphaelSolveCoordinator.IsNoSolutionFailureReason(noSolutionMessage),
    "legacy Raphael NoSolution panics must become a recognized domain outcome");
Require(!noSolutionMessage.Contains("panicked", StringComparison.OrdinalIgnoreCase)
     && !noSolutionMessage.Contains("Exit code", StringComparison.OrdinalIgnoreCase),
    "player-facing NoSolution details must not expose panic or process diagnostics");
var unexpectedFailure = RaphaelSolveCoordinator.FormatFailureReason("sensitive raw native error");
Require(!unexpectedFailure.Contains("sensitive raw stderr", StringComparison.Ordinal),
    "unexpected Raphael failures must direct players to logs instead of rendering raw stderr");
var legacyCachedSolution = JsonConvert.DeserializeObject<CachedRaphaelSolution>("{\"Key\":\"legacy\"}")!;
Require(!RaphaelSolveCoordinator.IsCacheEntryCurrent(legacyCachedSolution),
    "unversioned Raphael cache entries must be rejected after solver semantic changes");
var currentCachedSolution = new CachedRaphaelSolution(
    "current",
    new RaphaelSolveRequest(1, 100, 1, 1, 1, false, false))
{
    Optimal = true,
};
Require(RaphaelSolveCoordinator.IsCacheEntryCurrent(currentCachedSolution),
    "proven-optimal Raphael cache entries must carry the current cache version");
var timedOutCachedSolution = new CachedRaphaelSolution(
    "partial",
    new RaphaelSolveRequest(2, 100, 1, 1, 1, false, false))
{
    OptimizationDeadlineReached = true,
    Optimal = false,
    AchievedQuality = 10_458,
    QualityUpperBound = 13_681,
};
Require(!RaphaelSolveCoordinator.IsCacheEntryCurrent(timedOutCachedSolution),
    "a timed-out partial Raphael result must not become a permanent cache hit");
Require(!DonatelloNative.ResolveProgressFirst(
            Craft(),
            DonatelloNative.SolveMode.LiveAdaptive,
            experimentalProgressPriorityEnabled: false)
        && DonatelloNative.ResolveProgressFirst(
            Craft(),
            DonatelloNative.SolveMode.LiveAdaptive,
            experimentalProgressPriorityEnabled: true)
        && !DonatelloNative.ResolveProgressFirst(
            Craft() with { CraftExpert = true },
            DonatelloNative.SolveMode.LiveAdaptive,
            experimentalProgressPriorityEnabled: true)
        && !DonatelloNative.ResolveProgressFirst(
            Craft(),
            DonatelloNative.SolveMode.OptimizeQuality,
            experimentalProgressPriorityEnabled: true),
    "progress-priority replanning must be experimental, default-off, live-adaptive-only, and forbidden for expert recipes");
var validatedRaphaelCraft = Craft();
var validatedRaphaelRoot = Root();
var validatedRaphaelActions = new[] { VulcanSkill.BasicTouch, VulcanSkill.BasicSynthesis };
const int validatedRaphaelQuality = 135;
Require(RaphaelSolveCoordinator.ValidateSolvedPlan(
        validatedRaphaelCraft,
        validatedRaphaelRoot,
        validatedRaphaelActions,
        validatedRaphaelQuality) == null,
    "Raphael cache admission must accept a completing plan whose native and plugin quality agree");
Require(RaphaelSolveCoordinator.ValidateSolvedPlan(
        validatedRaphaelCraft,
        validatedRaphaelRoot,
        validatedRaphaelActions,
        validatedRaphaelQuality + 1,
        validateQuality: false) == null,
    "ProgressOnly Raphael cache admission must accept a completing plan when incidental quality differs");
Require(RaphaelSolveCoordinator.ValidateSolvedPlan(
        validatedRaphaelCraft,
        validatedRaphaelRoot,
        Array.Empty<VulcanSkill>(),
        0) != null
    && RaphaelSolveCoordinator.ValidateSolvedPlan(
        validatedRaphaelCraft,
        validatedRaphaelRoot,
        validatedRaphaelActions,
        validatedRaphaelQuality + 1) != null,
    "Raphael cache admission must reject incomplete plans and native/plugin quality mismatches");

var craft = Craft();
var standardRecommendation = new StandardSolver(new StandardSolverConfig()).Solve(craft, Root());
Require(standardRecommendation.Action != VulcanSkill.None
        && Simulator.CanUseAction(craft, Root(), standardRecommendation.Action),
    "existing Standard Solver must remain operational and return a legal opening action");

var specialistCraft = Craft();
specialistCraft.Specialist = true;
specialistCraft.CrafterDelineations = 1;
specialistCraft.DonatelloOptions = new DonatelloExecutionOptions(
    DonatelloSolveObjective.MaximizeQuality,
    MinimizeSteps: false);
var specialistRoot = GameStateBuilder.BuildInitialStepState(specialistCraft);
Require(specialistRoot.CarefulObservationLeft == 3,
    "specialists must begin with all three Careful Observation uses");
Require(specialistRoot.QuickInnoLeft == 1 && specialistRoot.QuickInnoAvailable,
    "specialists must begin with one available Quick Innovation use");
Require(specialistRoot.CrafterDelineationsLeft == 1,
    "the live Crafter's Delineation inventory must initialize the shared specialist-action pool");
var manyDelineationRequest = RaphaelSolveRequest.FromCraftState(
    specialistCraft with { CrafterDelineations = 111 },
    allowSpecialistActions: true);
Require(manyDelineationRequest.CrafterDelineations == 2,
    "initial Raphael requests must canonicalize large delineation inventories to two usable one-shot specialist actions");

var oneDelineation = Root() with { QuickInnoLeft = 1, QuickInnoAvailable = true, CrafterDelineationsLeft = 1 };
var afterQuickInnovation = Simulator.Execute(
    craft, oneDelineation, VulcanSkill.QuickInnovation, 0, 1).Item2;
Require(afterQuickInnovation.CrafterDelineationsLeft == 0
        && !Simulator.CanUseAction(craft, afterQuickInnovation, VulcanSkill.HeartAndSoul),
    "Quick Innovation must consume the same delineation pool used by Heart and Soul");

var observationCraft = Craft() with
{
    Specialist = true,
    CraftExpert = true,
    DonatelloOptions = new DonatelloExecutionOptions(
        MaximizeQualityAtCostOfTime: true,
        AllowSpecialistActions: true),
};
var observationStep = Root(Condition.Normal) with
{
    IQStacks = 1,
    InnovationLeft = 2,
    CarefulObservationLeft = 3,
    CrafterDelineationsLeft = 5,
    QuickInnoLeft = 1,
    QuickInnoAvailable = true,
    StellarSteadyHandLeft = 2,
    ExpedienceLeft = 1,
    ComboAction = VulcanSkill.BasicTouch,
    PrevComboAction = VulcanSkill.BasicTouch,
    PrevActionFailed = true,
};
Require(DonatelloSolver.ShouldUseCarefulObservation(observationCraft, observationStep)
        && !DonatelloSolver.ShouldUseCarefulObservation(
            observationCraft with
            {
                DonatelloOptions = observationCraft.DonatelloOptions with { AllowSpecialistActions = false },
            },
            observationStep)
        && !DonatelloSolver.ShouldUseCarefulObservation(
            observationCraft,
            observationStep with { CrafterDelineationsLeft = 2 })
        && !DonatelloSolver.ShouldUseCarefulObservation(
            observationCraft,
            observationStep with
            {
                IQStacks = 0,
                InnovationLeft = 0,
                StellarSteadyHandLeft = 0,
                ExpedienceLeft = 0,
            }),
    "Normal-condition rerolls must require explicit quality/time mode, specialist eligibility, surplus delineations, and two preserved buffs");
Require(DonatelloSolver.ShouldPlanCarefulObservation(
        observationCraft,
        observationStep with { Condition = Condition.Poor })
    && !DonatelloSolver.ShouldPlanCarefulObservation(observationCraft, observationStep),
    "an observed Poor root must force one planner decision even when Excellent predicted the Poor transition");
var observationSolver = new DonatelloSolver(
    new CachedRaphaelSolution { ActionIds = [(uint)VulcanSkill.BasicSynthesis] },
    observationCraft);
for (var remainingUses = 3; remainingUses > 0; --remainingUses)
{
    var recommendation = observationSolver.Solve(observationCraft, observationStep);
    Require(recommendation.Action == VulcanSkill.CarefulObservation,
        "Donatello must spend each meaningful Normal-condition Careful Observation before consuming its incumbent plan");
    var observationResult = Simulator.Execute(
        observationCraft,
        observationStep,
        recommendation.Action,
        actionSuccessRoll: 0,
        nextStateRoll: 1);
    Require(observationResult.Item1 == Simulator.ExecuteResult.Succeeded,
        "Careful Observation must remain executable while a surplus delineation exists");
    observationStep = observationResult.Item2;
}
Require(observationSolver.Solve(observationCraft, observationStep).Action == VulcanSkill.BasicSynthesis,
    "Normal-condition rerolls must stop after reserving the remaining Heart and Soul and Quick Innovation delineations");
var poorObservation = Simulator.Execute(
    observationCraft,
    observationStep with
    {
        Condition = Condition.Poor,
        CarefulObservationLeft = 1,
        CrafterDelineationsLeft = 3,
    },
    VulcanSkill.CarefulObservation,
    actionSuccessRoll: 0,
    nextStateRoll: 0).Item2;
Require(poorObservation.Condition == Condition.Normal
        && poorObservation.Index == observationStep.Index
        && poorObservation.IQStacks == observationStep.IQStacks
        && poorObservation.InnovationLeft == observationStep.InnovationLeft
        && poorObservation.ExpedienceLeft == observationStep.ExpedienceLeft
        && poorObservation.StellarSteadyHandLeft == observationStep.StellarSteadyHandLeft
        && poorObservation.ComboAction == observationStep.ComboAction
        && poorObservation.PrevComboAction == observationStep.PrevComboAction
        && poorObservation.PrevActionFailed == observationStep.PrevActionFailed
        && poorObservation.CarefulObservationLeft == 0
        && poorObservation.CrafterDelineationsLeft == 2,
    "Careful Observation must advance Poor to Normal while preserving action state and consuming exactly one charge and delineation");

foreach (var solverName in new[]
         {
             "Default",
             "Standard Recipe Solver",
             "Raphael Recipe Solver",
             "Macro",
             "Expert Recipe Solver",
         })
{
    var options = ArtisanIpcShim.BuildDonatelloOptions(
        solverName, false, 3, 4, 5, 6, 7);
    Require(options.Objective == DonatelloSolveObjective.MaximizeQuality && !options.MinimizeSteps,
        $"ICE solver mode '{solverName}' must map to quality-maximizing Donatello without step minimization");
}
var progressOnlyOptions = ArtisanIpcShim.BuildDonatelloOptions(
    "Progress Only Solver", true, 2, 3, 4, 5, 6);
Require(progressOnlyOptions == new DonatelloExecutionOptions(
        DonatelloSolveObjective.ProgressOnly,
        MinimizeSteps: false,
        MaxStellarSteadyHandUses: 2),
    "ICE Progress Only Solver must map to progress-only Donatello and preserve the expert Stellar limit");
var progressOnlyReactionCraft = Craft() with { DonatelloOptions = progressOnlyOptions };
Require(RaphaelSolveCoordinator.ResolveSolveMode(DonatelloSolveObjective.ProgressOnly)
        == DonatelloNative.SolveMode.CompleteFastest
        && DonatelloSolver.ResolveLiveSolveMode(progressOnlyReactionCraft)
        == DonatelloNative.SolveMode.CompleteFastest,
    "ICE Progress Only Solver must use native Complete/Fastest for both initial and live solves");
Require(RaphaelSolveCoordinator.ResolveSolveMode(DonatelloSolveObjective.MaximizeQuality)
        == DonatelloNative.SolveMode.OptimizeQuality
        && DonatelloSolver.ResolveLiveSolveMode(Craft())
        == DonatelloNative.SolveMode.LiveAdaptive,
    "quality-maximizing Donatello must retain quality and live-adaptive native modes");
Require(DonatelloSolver.ResolvePendingSolveMode(Craft(), 0)
        == DonatelloNative.SolveMode.OptimizeQuality
        && DonatelloSolver.ResolvePendingSolveMode(Craft(), 1)
        == DonatelloNative.SolveMode.LiveAdaptive
        && DonatelloSolver.ResolvePendingSolveMode(progressOnlyReactionCraft, 0)
        == DonatelloNative.SolveMode.CompleteFastest,
    "quality-mode live recovery must establish a Raphael baseline before adaptive replanning; progress-only remains fastest-completion");
var expectedProgressOnlyState = Root(Condition.Normal) with { Quality = 100 };
Require(!DonatelloSolver.RequiresReplan(
        progressOnlyReactionCraft,
        expectedProgressOnlyState,
        expectedProgressOnlyState with { Condition = Condition.Poor, Quality = 250 }),
    "progress-only Donatello must ignore quality-only state and condition changes after Material Miracle");
Require(DonatelloSolver.RequiresReplan(
        progressOnlyReactionCraft,
        expectedProgressOnlyState,
        expectedProgressOnlyState with { Condition = Condition.Good, Quality = 250 })
        && DonatelloSolver.RequiresReplan(
            progressOnlyReactionCraft,
            expectedProgressOnlyState,
            expectedProgressOnlyState with { Condition = Condition.Malleable, Quality = 250 }),
    "progress-only Donatello must still react to conditions that enable or improve progress actions");
var standardOptions = ArtisanIpcShim.BuildDonatelloOptions(
    "Default", false, 2, 3, 4, 5, 6);
Require(standardOptions == new DonatelloExecutionOptions(
        DonatelloSolveObjective.MaximizeQuality,
        MinimizeSteps: false,
        MaxStellarSteadyHandUses: 0),
    "non-expert ICE crafts must never inherit expert Stellar limits or solver-owned Material Miracle policy");
Require(CraftingGameInterop.CosmicIngredientButtonOrder(progressOnlyOptions) == (39u, 40u)
        && CraftingGameInterop.CosmicIngredientButtonOrder(standardOptions) == (40u, 39u),
    "Cosmic ingredient assignment must prefer NQ for progress-only and HQ for quality-maximizing ICE crafts");

var cacheKeyRequest = new RaphaelSolveRequest(1, 100, 5000, 5000, 600, true, false);
Require(cacheKeyRequest.GetKey() != (cacheKeyRequest with
        { Objective = DonatelloSolveObjective.ProgressOnly }).GetKey()
        && cacheKeyRequest.GetKey() != (cacheKeyRequest with { MinimizeSteps = true }).GetKey()
        && cacheKeyRequest.GetKey() != (cacheKeyRequest with { SplendorCosmic = true }).GetKey()
        && cacheKeyRequest.GetKey() != (cacheKeyRequest with { StellarSteadyHandCharges = 1 }).GetKey(),
    "Donatello cache keys must isolate every solver-owned ICE execution objective and Cosmic-use limit");
var manyDelineationKeyRequest = cacheKeyRequest with { Specialist = true, CrafterDelineations = 111 };
Require(manyDelineationKeyRequest.GetKey()
        == (manyDelineationKeyRequest with { CrafterDelineations = 2 }).GetKey()
        && manyDelineationKeyRequest.GetKey()
        != (manyDelineationKeyRequest with { CrafterDelineations = 1 }).GetKey(),
    "Raphael cache keys must distinguish canonical 0/1/2 specialist resources without splitting equivalent inventory counts");

using var requestJson = JsonDocument.Parse(DonatelloNative.SerializeRequest(
    specialistCraft, specialistRoot, allowSpecialistActions: true,
    solveMode: DonatelloNative.SolveMode.OptimizeQuality));
var nativeRoot = requestJson.RootElement.GetProperty("root");
var expectedRequestFields = new HashSet<string>
{
    "abiVersion", "maxCp", "maxDurability", "maxProgress", "maxQuality", "baseProgress",
    "baseQuality", "jobLevel", "manipulation", "specialist", "solveMode",
    "allowCarefulObservation",
    "progressFirst", "minimizeSteps", "stellarSteadyHandCharges", "incumbentActionIds",
    "softDeadlineMillis", "hardDeadlineMillis", "bypassSolutionCache", "root",
};
var expectedRootFields = new HashSet<string>
{
    "cp", "durability", "progress", "quality", "innerQuiet", "wasteNot", "manipulation",
    "innovation", "veneration", "greatStrides", "muscleMemory", "finalAppraisal",
    "carefulObservationCharges", "combo", "heartAndSoulActive", "heartAndSoulAvailable",
    "quickInnovationAvailable", "trainedPerfectionActive", "trainedPerfectionAvailable",
    "stellarSteadyHandCharges", "stellarSteadyHand", "splendorCosmic", "expedience", "condition",
    "crafterDelineations",
};
Require(requestJson.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet()
            .SetEquals(expectedRequestFields)
        && nativeRoot.EnumerateObject().Select(property => property.Name).ToHashSet()
            .SetEquals(expectedRootFields),
    "Donatello requests must contain exactly the fields required by native ABI v10");
Require(requestJson.RootElement.GetProperty("abiVersion").GetUInt32() == 10
        && requestJson.RootElement.GetProperty("solveMode").GetInt32() == 0
        && !requestJson.RootElement.GetProperty("allowCarefulObservation").GetBoolean()
        && !requestJson.RootElement.GetProperty("progressFirst").GetBoolean()
        && !requestJson.RootElement.GetProperty("minimizeSteps").GetBoolean()
        && requestJson.RootElement.GetProperty("stellarSteadyHandCharges").GetUInt32() == 0
        && requestJson.RootElement.GetProperty("incumbentActionIds").GetArrayLength() == 0
        && requestJson.RootElement.GetProperty("softDeadlineMillis").GetInt32() == 0
        && requestJson.RootElement.GetProperty("hardDeadlineMillis").GetInt32() == 0
        && !requestJson.RootElement.GetProperty("bypassSolutionCache").GetBoolean()
        && !requestJson.RootElement.TryGetProperty("AbiVersion", out _)
        && nativeRoot.GetProperty("wasteNot").GetInt32() == 0
        && nativeRoot.GetProperty("manipulation").GetInt32() == 0
        && nativeRoot.GetProperty("innovation").GetInt32() == 0
        && nativeRoot.GetProperty("veneration").GetInt32() == 0
        && nativeRoot.GetProperty("greatStrides").GetInt32() == 0
        && nativeRoot.GetProperty("muscleMemory").GetInt32() == 0
        && nativeRoot.GetProperty("crafterDelineations").GetInt32() == 1
        && !nativeRoot.TryGetProperty("manipulationLeft", out _),
    "Donatello requests must match every ABI v10 camelCase field name exactly");

using var poorObservationRequestJson = JsonDocument.Parse(DonatelloNative.SerializeRequest(
    observationCraft,
    observationStep with
    {
        Condition = Condition.Poor,
        CarefulObservationLeft = 3,
        CrafterDelineationsLeft = 5,
    },
    allowSpecialistActions: true,
    solveMode: DonatelloNative.SolveMode.LiveAdaptive));
Require(poorObservationRequestJson.RootElement.GetProperty("allowCarefulObservation").GetBoolean()
        && poorObservationRequestJson.RootElement.GetProperty("root")
            .GetProperty("crafterDelineations").GetInt32() == 5,
    "Poor live replans must expose Careful Observation and all delineations that can fund it plus reserved specialist actions");

var progressOnlyCraft = Craft();
progressOnlyCraft.DonatelloOptions = progressOnlyOptions;
progressOnlyCraft.SplendorCosmic = true;
using var progressOnlyRequestJson = JsonDocument.Parse(DonatelloNative.SerializeRequest(
    progressOnlyCraft,
    Root() with
    {
        Quality = 321,
        StellarSteadyHandCharges = 3,
        StellarSteadyHandLeft = 2,
        StellarSteadyHandsUsed = 1,
    },
    allowSpecialistActions: false,
    solveMode: DonatelloNative.SolveMode.CompleteFastest));
var progressOnlyRoot = progressOnlyRequestJson.RootElement.GetProperty("root");
Require(progressOnlyRequestJson.RootElement.GetProperty("solveMode").GetInt32() == 1
        && progressOnlyRequestJson.RootElement.GetProperty("maxQuality").GetInt32() == 0
        && !progressOnlyRequestJson.RootElement.GetProperty("minimizeSteps").GetBoolean()
        && progressOnlyRequestJson.RootElement.GetProperty("stellarSteadyHandCharges").GetUInt32() == 1
        && progressOnlyRoot.GetProperty("quality").GetInt32() == 0
        && progressOnlyRoot.GetProperty("stellarSteadyHandCharges").GetUInt32() == 1
        && progressOnlyRoot.GetProperty("stellarSteadyHand").GetInt32() == 2
        && progressOnlyRoot.GetProperty("splendorCosmic").GetBoolean(),
    "progress-only native requests must erase quality, disable minimization, and enforce the remaining Stellar-use cap");
using var incumbentRequestJson = JsonDocument.Parse(DonatelloNative.SerializeRequest(
    specialistCraft,
    specialistRoot,
    allowSpecialistActions: true,
    solveMode: DonatelloNative.SolveMode.LiveAdaptive,
    incumbent: [VulcanSkill.BasicSynthesis]));
Require(incumbentRequestJson.RootElement.GetProperty("incumbentActionIds")[0].GetUInt32()
        == (uint)VulcanSkill.BasicSynthesis
        && !incumbentRequestJson.RootElement.GetProperty("progressFirst").GetBoolean(),
    "normal Donatello replans must send the finishing incumbent with experimental progress priority disabled by default");
using var experimentalProgressRequestJson = JsonDocument.Parse(DonatelloNative.SerializeRequest(
    specialistCraft,
    specialistRoot,
    allowSpecialistActions: true,
    solveMode: DonatelloNative.SolveMode.LiveAdaptive,
    incumbent: [VulcanSkill.BasicSynthesis],
    experimentalProgressPriorityEnabled: true));
Require(experimentalProgressRequestJson.RootElement.GetProperty("progressFirst").GetBoolean(),
    "the explicit experimental setting must enable progress-priority live replanning for normal recipes");
using var expertRequestJson = JsonDocument.Parse(DonatelloNative.SerializeRequest(
    specialistCraft with { CraftExpert = true },
    specialistRoot,
    allowSpecialistActions: true,
    solveMode: DonatelloNative.SolveMode.LiveAdaptive));
Require(!expertRequestJson.RootElement.GetProperty("progressFirst").GetBoolean(),
    "expert Donatello replans must retain unrestricted adaptive search");
var qualityRoot = GameStateBuilder.BuildInitialStepState(Craft() with { InitialQuality = 321 }, 321);
Require(qualityRoot.Quality == 321, "initial HQ-material quality must survive root construction");

var robust = Simulator.Execute(craft, Root(Condition.Robust), VulcanSkill.BasicTouch, 0, 1).Item2;
Require(robust.Durability == 35 && robust.Condition == Condition.Sturdy,
    "Robust must halve durability cost and deterministically transition to Sturdy");

var stellarRoot = Root() with { StellarSteadyHandCharges = 1 };
Require(Simulator.CanUseAction(craft, stellarRoot, VulcanSkill.StellarSteadyHand),
    "Stellar Steady Hand must be usable when its Cosmic duty-action charge exists");
var afterStellar = Simulator.Execute(
    craft, stellarRoot, VulcanSkill.StellarSteadyHand, 0, 1).Item2;
Require(afterStellar.StellarSteadyHandCharges == 0
        && afterStellar.StellarSteadyHandLeft == 3
        && afterStellar.StellarSteadyHandsUsed == 1
        && Simulator.GetSuccessRate(afterStellar, VulcanSkill.RapidSynthesis) == 1,
    "Stellar Steady Hand must consume one charge and guarantee the next three risky actions");
var afterZeroStepAction = Simulator.Execute(
    craft, afterStellar, VulcanSkill.FinalAppraisal, 0, 1).Item2;
Require(afterZeroStepAction.StellarSteadyHandLeft == 2,
    "Stellar Steady Hand duration must count zero-step crafting actions, matching Artisan");
var stellarDurationCraft = craft with
{
    MissionHasStellarSteadyHand = true,
    CurrentStellarSteadyHandCharges = 1,
};
var stellarDurationPlan = DonatelloPlanEvaluator.Evaluate(
    stellarDurationCraft,
    Root() with { StellarSteadyHandCharges = 1 },
    [VulcanSkill.StellarSteadyHand, VulcanSkill.BasicSynthesis]);
Require(stellarDurationPlan.Completes && stellarDurationPlan.Duration == 5,
    "C# Donatello admission must use the native 2-second Stellar Steady Hand duration");

var materialMiracleCraft = Craft() with { CurrentMaterialMiracleCharges = 1 };
var materialMiracleRoot = Root() with
{
    Index = 1,
    MaterialMiracleCharges = 1,
};
var fallbackRecommendation = new Solver.Recommendation(VulcanSkill.BasicSynthesis);
var guaranteedMaximumQualityCraft = materialMiracleCraft with { CraftQualityMax = 135 };
var guaranteedMaximumQualitySolution = new CachedRaphaelSolution
{
    ActionIds = [(uint)VulcanSkill.BasicTouch, (uint)VulcanSkill.BasicSynthesis],
    AchievedQuality = 135,
};
Require(DonatelloSolverDefinition.IsGuaranteedMaximumQualitySolution(
            guaranteedMaximumQualitySolution,
            guaranteedMaximumQualityCraft)
        && !DonatelloSolverDefinition.IsGuaranteedMaximumQualitySolution(
            guaranteedMaximumQualitySolution,
            materialMiracleCraft),
    "Material Miracle suppression must require a completing guaranteed-success Raphael plan that reaches the craft's actual maximum quality");
Require(CraftingGameInterop.ShouldDeferMaterialMiracleBootstrap(
            VulcanSolverMode.Donatello,
            donatelloRegistered: true,
            hasGuaranteedMaximumQualityRaphaelPlan: false,
            materialMiracleCraft,
            materialMiracleRoot)
        && !CraftingGameInterop.ShouldDeferMaterialMiracleBootstrap(
            VulcanSolverMode.PureRaphael,
            donatelloRegistered: true,
            hasGuaranteedMaximumQualityRaphaelPlan: false,
            materialMiracleCraft,
            materialMiracleRoot)
        && !CraftingGameInterop.ShouldDeferMaterialMiracleBootstrap(
            VulcanSolverMode.StandardSolver,
            donatelloRegistered: true,
            hasGuaranteedMaximumQualityRaphaelPlan: false,
            materialMiracleCraft,
            materialMiracleRoot)
        && !CraftingGameInterop.ShouldDeferMaterialMiracleBootstrap(
            VulcanSolverMode.Donatello,
            donatelloRegistered: false,
            hasGuaranteedMaximumQualityRaphaelPlan: false,
            materialMiracleCraft,
            materialMiracleRoot)
        && !CraftingGameInterop.ShouldDeferMaterialMiracleBootstrap(
            VulcanSolverMode.Donatello,
            donatelloRegistered: true,
            hasGuaranteedMaximumQualityRaphaelPlan: true,
            materialMiracleCraft,
            materialMiracleRoot),
    "Material Miracle bootstrap must require registered Donatello and no guaranteed maximum-quality Raphael incumbent");
Require(CraftingGameInterop.ResolveExecutionRecommendation(
            materialMiracleCraft,
            materialMiracleRoot,
            fallbackRecommendation,
            materialMiracleBootstrapPending: true).Action == VulcanSkill.MaterialMiracle
        && CraftingGameInterop.ResolveExecutionRecommendation(
            materialMiracleCraft,
            materialMiracleRoot,
            fallbackRecommendation,
            materialMiracleBootstrapPending: false).Action == VulcanSkill.BasicSynthesis
        && CraftingGameInterop.ResolveExecutionRecommendation(
            materialMiracleCraft,
            materialMiracleRoot with { MaterialMiracleCharges = 0 },
            fallbackRecommendation,
            materialMiracleBootstrapPending: true).Action == VulcanSkill.BasicSynthesis,
    "ICE execution must use Material Miracle only for the pending initial bootstrap, then preserve solver ownership");
Require(CraftingQueueProcessor.GetJobSwitchWatchdogFailure(
            TimeSpan.FromSeconds(119), TimeSpan.Zero, 0, busy: true) == null
        && CraftingQueueProcessor.GetJobSwitchWatchdogFailure(
            TimeSpan.FromMinutes(2), TimeSpan.Zero, 0, busy: true) != null
        && CraftingQueueProcessor.GetJobSwitchWatchdogFailure(
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), 2, busy: false) != null
        && CraftingQueueProcessor.GetJobSwitchWatchdogFailure(
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2), 5, busy: false) != null,
    "job switching must have deterministic total and ready-state liveness bounds");
CraftingGameInterop.SetAutomationPaused(true);
Require(CraftingGameInterop.AutomationPaused,
    "pausing a crafting queue must suspend active live-craft action execution");
CraftingGameInterop.SetAutomationPaused(false);
Require(!CraftingGameInterop.AutomationPaused,
    "resuming a crafting queue must re-enable live-craft action execution");
Require(!VendorPurchaseManager.HasPurchaseWatchdogExpired(
            navigating: true,
            TimeSpan.FromMinutes(5))
        && !VendorPurchaseManager.HasPurchaseWatchdogExpired(
            navigating: false,
            TimeSpan.FromSeconds(29))
        && VendorPurchaseManager.HasPurchaseWatchdogExpired(
            navigating: false,
            TimeSpan.FromSeconds(30)),
    "vendor travel must use its movement watchdog while post-navigation purchase phases remain bounded");
Require(!RepairNPCNavigator.HasNavigationWatchdogExpired(
            TimeSpan.FromSeconds(179), TimeSpan.FromMinutes(2), activelyNavigating: false)
        && RepairNPCNavigator.HasNavigationWatchdogExpired(
            TimeSpan.FromMinutes(3), TimeSpan.Zero, activelyNavigating: false)
        && !RepairNPCNavigator.HasNavigationWatchdogExpired(
            TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(19), activelyNavigating: true)
        && RepairNPCNavigator.HasNavigationWatchdogExpired(
            TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(20), activelyNavigating: true),
    "repair travel must permit long moving routes but bound stalled movement and busy-state waits");
var afterMaterialMiracle = Simulator.Execute(
    materialMiracleCraft,
    materialMiracleRoot with
    {
        ComboAction = VulcanSkill.BasicTouch,
        PrevComboAction = VulcanSkill.BasicTouch,
        PrevActionFailed = true,
    },
    VulcanSkill.MaterialMiracle,
    0,
    0.5f).Item2;
Require(afterMaterialMiracle.ComboAction == VulcanSkill.BasicTouch
        && afterMaterialMiracle.PrevComboAction == VulcanSkill.BasicTouch
        && afterMaterialMiracle.PrevActionFailed
        && afterMaterialMiracle.MaterialMiracleCharges == 0,
    "Material Miracle execution state must preserve solver-visible combo/failure state while consuming its live charge");
Require(!CraftingGameInterop.MaterialMiracleAcknowledged(
            materialMiracleRoot,
            materialMiracleRoot)
        && CraftingGameInterop.MaterialMiracleAcknowledged(
            materialMiracleRoot,
            materialMiracleRoot with { Condition = Condition.Primed })
        && CraftingGameInterop.MaterialMiracleAcknowledged(
            materialMiracleRoot,
            materialMiracleRoot with { MaterialMiracleCharges = 0 }),
    "automatic Material Miracle must not be acknowledged from an unchanged pre-action frame; condition or charge must change");
var materialMiracleVenerationRoot = materialMiracleRoot with
{
    Durability = 70,
    RemainingCP = 573,
    Condition = Condition.Sturdy,
    MaterialMiracleCharges = 0,
};
materialMiracleCraft.CraftDurability = materialMiracleVenerationRoot.Durability;
var observedMaterialMiracleVeneration = materialMiracleVenerationRoot with
{
    Index = 2,
    RemainingCP = 555,
    VenerationLeft = 4,
    PrevComboAction = VulcanSkill.Veneration,
};
Require(StepStateReconciler.TryReconcileAction(
        materialMiracleCraft,
        materialMiracleVenerationRoot,
        VulcanSkill.Veneration,
        observedMaterialMiracleVeneration,
        out _),
    "Veneration after Material Miracle must reconcile without modeling its condition-only status");

if (args.Contains("--artisan-shim", StringComparer.Ordinal))
{
    Console.WriteLine($"Artisan shim/Donatello acceptance: {assertions} assertions passed");
    return;
}

AcquisitionAcceptanceTests.Run(Require);
SpecialNodeExhaustionAcceptanceTests.Run(Require);
TimedTargetTravelAcceptanceTests.Run(Require);
RepairRoutingAcceptanceTests.Run(Require);
await MarketplaceAcceptanceTests.Run(Require);
Require(VendorAcceptanceTests.Run() > 0, "vendor acceptance harness must execute at least one assertion");
Require(LiveAcquisitionAcceptanceTests.Run() > 0, "live acquisition acceptance harness must execute at least one assertion");
IntegrationAcceptanceTests.Run(Require);
await PluginPathSimulationAcceptanceTests.Run(Require);

// Any completing plan outranks every non-completing plan, regardless of quality.
var incumbent = DonatelloPlanEvaluator.Evaluate(craft, Root(), [VulcanSkill.BasicSynthesis]);
var qualityOnly = DonatelloPlanEvaluator.Evaluate(craft, Root(), [VulcanSkill.BasicTouch]);
Require(incumbent.Completes, "Basic Synthesis must complete the test craft");
Require(!qualityOnly.Completes && qualityOnly.Quality > incumbent.Quality,
    "quality-only regression fixture must have higher quality but no completion");
Require(!qualityOnly.IsStrictlyBetterThan(incumbent),
    "nonfinishing high-quality candidate must never replace completing incumbent");

// Full quality without resources/path to progress remains failure.
var strandedRoot = Root() with { Quality = craft.CraftQualityMax, Durability = 10 };
var stranded = DonatelloPlanEvaluator.Evaluate(craft, strandedRoot, [VulcanSkill.BasicTouch]);
Require(!stranded.Completes && stranded.Quality == craft.CraftQualityMax,
    "100% quality without completion path must score as failure");

// Completion on the action consuming the last durability is successful.
var lastDurability = DonatelloPlanEvaluator.Evaluate(
    craft,
    Root() with { Durability = 10 },
    [VulcanSkill.BasicSynthesis]);
Require(lastDurability.Completes, "progress completion must precede durability failure classification");

// Ties retain incumbent; lower quality never wins through step count.
var same = DonatelloPlanEvaluator.Evaluate(craft, Root(), [VulcanSkill.BasicSynthesis]);
Require(!same.IsStrictlyBetterThan(incumbent), "equivalent candidate must not churn plan");
var lowerQualityShort = new DonatelloPlanEvaluation(true, 10, 1, 3, []);
var higherQualityLong = new DonatelloPlanEvaluation(true, 20, 2, 6, []);
Require(!lowerQualityShort.IsStrictlyBetterThan(higherQualityLong),
    "fewer steps must not outrank higher quality");
Require(same.IsStrictlyBetterThan(
        new DonatelloPlanEvaluation(true, same.Quality, same.Steps + 1, same.Duration + 3, [])),
    "minimize-steps disabled must still adopt a lexicographically better proven plan");
var protectedRaphaelIncumbent = new DonatelloPlanEvaluation(true, 3352, 16, 48, []);
var worseStagedCandidate = new DonatelloPlanEvaluation(true, 2639, 16, 48, []);
var betterCandidate = new DonatelloPlanEvaluation(true, 3353, 20, 60, []);
var riskyCandidate = DonatelloPlanEvaluator.Evaluate(
    craft with { CraftProgress = 500 },
    Root(),
    [VulcanSkill.RapidSynthesis]);
var oneShortBoundary = new DonatelloNative.ProgressBoundary { ActionCount = 1, Target = "oneShort" };
var oneShortEvaluation = new DonatelloPlanEvaluation(
    true,
    100,
    2,
    6,
    [Root(), Root() with { Progress = craft.CraftProgress - 1 }, Root() with { Progress = craft.CraftProgress }]);
Require(DonatelloSolver.ProtectsRaphaelBaseline(craft)
        && !DonatelloSolver.ShouldAdoptCandidate(
            craft,
            worseStagedCandidate,
            protectedRaphaelIncumbent,
            stagedProgressPlan: true)
        && !DonatelloSolver.ShouldAdoptCandidate(
            craft,
            protectedRaphaelIncumbent,
            protectedRaphaelIncumbent,
            stagedProgressPlan: true)
        && !DonatelloSolver.ShouldAdoptCandidate(
            craft,
            riskyCandidate,
            protectedRaphaelIncumbent,
            stagedProgressPlan: true)
        && DonatelloSolver.ShouldAdoptCandidate(
            craft,
            betterCandidate,
            protectedRaphaelIncumbent,
            stagedProgressPlan: true)
        && !DonatelloSolver.ShouldAdoptCandidate(
            craft with { CraftExpert = true },
            worseStagedCandidate,
            protectedRaphaelIncumbent,
            stagedProgressPlan: true)
        && !DonatelloSolver.ShouldAdoptCandidate(
            craft with { SplendorCosmic = true },
            worseStagedCandidate,
            protectedRaphaelIncumbent,
            stagedProgressPlan: true)
        && DonatelloSolver.IsValidOneShortBoundary(craft, oneShortBoundary, oneShortEvaluation)
        && !DonatelloSolver.IsValidOneShortBoundary(
            craft with { CraftExpert = true },
            oneShortBoundary,
            oneShortEvaluation),
    "quality-mode Donatello must reject worse staged candidates against every completing Raphael incumbent, including expert and Cosmic crafts, and accept only a proven strict improvement");
Require(!DonatelloSolver.ProtectsRaphaelBaseline(
            craft with { DonatelloOptions = new DonatelloExecutionOptions(DonatelloSolveObjective.ProgressOnly) })
        && DonatelloSolver.ProtectsRaphaelBaseline(craft with { CraftExpert = true })
        && DonatelloSolver.ProtectsRaphaelBaseline(craft with { SplendorCosmic = true }),
    "Raphael-baseline protection must cover every quality-mode craft; only explicit ProgressOnly may ignore quality");

// A condition-aware replacement can prove useful through the independent Vulcan gate.
var goodRoot = Root(Condition.Good);
var originalNormalSuffix = DonatelloPlanEvaluator.Evaluate(
    craft,
    goodRoot,
    [VulcanSkill.BasicTouch, VulcanSkill.BasicSynthesis]);
var goodCandidate = DonatelloPlanEvaluator.Evaluate(
    craft,
    goodRoot,
    [VulcanSkill.PreciseTouch, VulcanSkill.BasicSynthesis]);
Require(originalNormalSuffix.Completes && goodCandidate.Completes,
    "useful-replan fixture requires two completing suffixes");
Require(goodCandidate.IsStrictlyBetterThan(originalNormalSuffix),
    "Good-condition replan must be independently provable as better than the Normal suffix");
Require(DonatelloSolver.RequiresReplan(craft, null, Root(Condition.Excellent)),
    "an initially observed special condition must trigger replanning");
var expectedPoor = Root(Condition.Poor);
Require(!DonatelloSolver.RequiresReplan(craft, expectedPoor, expectedPoor with { }),
    "the expected Poor successor of Excellent must not trigger a redundant replan");
Require(DonatelloSolver.RequiresReplan(craft, expectedPoor, expectedPoor with { RemainingCP = 499 }),
    "a resource divergence must trigger replanning even when the condition was predicted");
var fullQualityGood = Root(Condition.Good) with { Quality = craft.CraftQualityMax };
Require(!DonatelloSolver.RequiresReplan(craft, null, fullQualityGood),
    "a special condition at full quality must not trigger replanning for a normal craft");
Require(!DonatelloSolver.RequiresReplan(
        craft,
        Root() with { Quality = craft.CraftQualityMax - 1 },
        fullQualityGood),
    "condition-driven quality divergence must not trigger replanning after a normal craft reaches full quality");
Require(DonatelloSolver.RequiresReplan(craft, fullQualityGood, fullQualityGood with { RemainingCP = 499 }),
    "full quality must not suppress replanning for non-quality state divergence");
Require(DonatelloSolver.RequiresReplan(craft with { CraftExpert = true }, null, fullQualityGood),
    "full quality must not suppress expert-craft condition replanning");
var uncappedQualityCompletionRoot = Root() with { Progress = craft.CraftProgress - 1, Quality = craft.CraftQualityMax - 1 };
Require(DonatelloSolver.ShouldReplanBeforeCompletion(
            craft,
            uncappedQualityCompletionRoot,
            VulcanSkill.BasicSynthesis)
        && !DonatelloSolver.ShouldReplanBeforeCompletion(
            craft,
            uncappedQualityCompletionRoot with { Quality = craft.CraftQualityMax },
            VulcanSkill.BasicSynthesis)
        && !DonatelloSolver.ShouldReplanBeforeCompletion(
            craft with { DonatelloOptions = new DonatelloExecutionOptions(DonatelloSolveObjective.ProgressOnly) },
            uncappedQualityCompletionRoot,
            VulcanSkill.BasicSynthesis)
        && !DonatelloSolver.ShouldReplanBeforeCompletion(
            craft with { CraftExpert = true },
            uncappedQualityCompletionRoot,
            VulcanSkill.BasicSynthesis)
        && !DonatelloSolver.ShouldReplanBeforeCompletion(
            craft with { IsCosmic = true },
            uncappedQualityCompletionRoot,
            VulcanSkill.BasicSynthesis),
    "normal quality-mode Donatello must replan before a guaranteed progress-completing action that leaves quality below maximum, while ProgressOnly, Expert, and Cosmic crafts preserve their separate completion contracts");
var maxQualityCompletion = new DonatelloPlanEvaluation(
    true, craft.CraftQualityMax, 10, 30, []);
Require(DonatelloSolverDefinition.ShouldUseStaticPlan(craft, maxQualityCompletion)
        && DonatelloSolverDefinition.ShouldUseStaticPlan(
            craft with { CraftExpert = true }, maxQualityCompletion),
    "an initial max-quality completing Raphael plan must bypass Donatello for normal and expert crafts");

// Explicit known-condition progression, including zero-step preservation.
var excellent = Root(Condition.Excellent);
var zeroStep = DonatelloPlanEvaluator.Evaluate(
    craft,
    excellent,
    [VulcanSkill.HeartAndSoul, VulcanSkill.BasicTouch, VulcanSkill.StandardTouch]);
Require(zeroStep.Trajectory[1].Condition == Condition.Excellent,
    "Heart and Soul must not consume Excellent");
Require(zeroStep.Trajectory[2].Condition == Condition.Poor,
    "first step action under Excellent must produce Poor");
Require(zeroStep.Trajectory[3].Condition == Condition.Normal,
    "step action under Poor must produce Normal");

var quickInnovationRoot = Root(Condition.Excellent);
quickInnovationRoot.QuickInnoLeft = 1;
var quickInnovation = DonatelloPlanEvaluator.Evaluate(
    craft,
    quickInnovationRoot,
    [VulcanSkill.QuickInnovation, VulcanSkill.BasicTouch]);
Require(quickInnovation.Trajectory[1].Condition == Condition.Excellent,
    "Quick Innovation must not consume Excellent");
Require(quickInnovation.Trajectory[1].Index == quickInnovationRoot.Index,
    "Quick Innovation must not advance the step");
Require(quickInnovation.Trajectory[1].InnovationLeft == 1,
    "Quick Innovation must not tick its newly applied Innovation immediately");
var blockedQuickInnovation = Root() with
{
    InnovationLeft = 1,
    QuickInnoLeft = 1,
    QuickInnoAvailable = false,
};
var unblockedQuickInnovation = Simulator.Execute(
    craft, blockedQuickInnovation, VulcanSkill.Observe, 0, 1).Item2;
Require(unblockedQuickInnovation.QuickInnoLeft == 1 && unblockedQuickInnovation.QuickInnoAvailable,
    "Quick Innovation charge must survive temporary blocking by an existing Innovation effect");

var goodOmen = DonatelloPlanEvaluator.Evaluate(
    craft,
    Root(Condition.GoodOmen),
    [VulcanSkill.BasicTouch, VulcanSkill.StandardTouch]);
Require(goodOmen.Trajectory[1].Condition == Condition.Good
    && goodOmen.Trajectory[2].Condition == Condition.Normal,
    "Good Omen prefix must progress through Good to Normal");

// Current expert-condition mechanics.
Require(Simulator.GetSuccessRate(Root(Condition.Centered), VulcanSkill.RapidSynthesis) == 0.75,
    "Centered must add 25 percentage points to Rapid Synthesis success");
Require(Simulator.GetSuccessRate(Root(Condition.Centered), VulcanSkill.HastyTouch) == 0.85,
    "Centered must add 25 percentage points to Hasty Touch success");
var pliant = Simulator.Execute(craft, Root(Condition.Pliant), VulcanSkill.Innovation, 0, 1).Item2;
Require(pliant.RemainingCP == 491, "Pliant must halve 18 CP cost to 9");
var noAdvancedTouchCombo = Root() with
{
    RemainingCP = 100,
    ComboAction = VulcanSkill.None,
};
var activeStandardTouchCombo = noAdvancedTouchCombo with { ComboAction = VulcanSkill.StandardTouch };
Require(Simulator.GetCPCost(activeStandardTouchCombo, VulcanSkill.AdvancedTouch) == 18
        && Simulator.GetCPCost(noAdvancedTouchCombo, VulcanSkill.AdvancedTouch) == 46,
    "Advanced Touch must use the live combo state for its CP discount");
var liveComboCraft = Craft() with
{
    StatControl = 4982,
    StatLevel = 100,
    CraftLevel = 100,
    CraftDurability = 60,
    CraftProgress = 4700,
    CraftQualityMax = 14600,
    CraftQualityDivider = 150,
    CraftQualityModifier = 75,
    SplendorCosmic = true,
};
var liveBasicTouchRoot = Root(Condition.Good) with
{
    Index = 12,
    Progress = 3623,
    Quality = 10396,
    Durability = 55,
    RemainingCP = 249,
    IQStacks = 7,
    InnovationLeft = 3,
    CarefulObservationLeft = 3,
    CrafterDelineationsLeft = 62,
    HeartAndSoulAvailable = false,
    ComboAction = VulcanSkill.None,
    PrevComboAction = VulcanSkill.PrudentTouch,
};
var liveBasicTouchObservation = liveBasicTouchRoot with
{
    Index = 13,
    Quality = 11623,
    Durability = 45,
    RemainingCP = 231,
    Condition = Condition.Normal,
    IQStacks = 8,
    InnovationLeft = 2,
    ComboAction = VulcanSkill.None,
    PrevComboAction = VulcanSkill.BasicTouch,
};
Require(StepStateReconciler.TryReconcileAction(
        liveComboCraft,
        liveBasicTouchRoot,
        VulcanSkill.BasicTouch,
        liveBasicTouchObservation,
        out var reconciledLiveBasicTouch)
    && reconciledLiveBasicTouch.ComboAction == VulcanSkill.BasicTouch
    && reconciledLiveBasicTouch.RemainingCP == 231,
    "the live Basic Touch outcome must reconstruct its unobservable crafting combo state");
var liveStandardTouchObservation = reconciledLiveBasicTouch with
{
    Index = 14,
    Quality = 12551,
    Durability = 35,
    RemainingCP = 213,
    IQStacks = 9,
    InnovationLeft = 1,
    ComboAction = VulcanSkill.None,
    PrevComboAction = VulcanSkill.StandardTouch,
};
Require(StepStateReconciler.TryReconcileAction(
        liveComboCraft,
        reconciledLiveBasicTouch,
        VulcanSkill.StandardTouch,
        liveStandardTouchObservation,
        out var reconciledLiveStandardTouch)
    && reconciledLiveStandardTouch.ComboAction == VulcanSkill.StandardTouch
    && reconciledLiveStandardTouch.RemainingCP == 213,
    "the reconstructed Basic Touch combo must reconcile Standard Touch at its 18 CP combo cost");
var sturdy = Simulator.Execute(craft, Root(Condition.Sturdy), VulcanSkill.BasicTouch, 0, 1).Item2;
Require(sturdy.Durability == 35, "Sturdy must halve 10 durability cost to 5");
var normalProgress = Simulator.CalculateProgress(craft, Root(), VulcanSkill.BasicSynthesis);
var malleableProgress = Simulator.CalculateProgress(craft, Root(Condition.Malleable), VulcanSkill.BasicSynthesis);
Require(malleableProgress == normalProgress * 3 / 2, "Malleable must apply 150% progress");
var primed = Simulator.Execute(craft, Root(Condition.Primed), VulcanSkill.Innovation, 0, 1).Item2;
Require(primed.InnovationLeft == 6, "Primed must extend Innovation from 4 to 6 steps");
var primedAppraisal = Simulator.Execute(craft, Root(Condition.Primed), VulcanSkill.FinalAppraisal, 0, 1).Item2;
Require(primedAppraisal.FinalAppraisalLeft == 7,
    "Primed must extend Final Appraisal from 5 to 7 steps");

// Authoritative UI fields plus inferred external action must reconstruct hidden state.
var manualRoot = Root(Condition.Good);
var manualPrecise = Simulator.Execute(craft, manualRoot, VulcanSkill.PreciseTouch, 0, 0.5f).Item2;
var observedPrecise = manualPrecise with
{
    PrevComboAction = manualRoot.PrevComboAction,
    PrevActionFailed = manualRoot.PrevActionFailed,
};
Require(StepStateReconciler.TryReconcileExternalAction(
        craft,
        manualRoot,
        observedPrecise,
        out var reconciledPrecise,
        out var externalActionObserved,
        out var inferredExternalAction),
    "a unique external step action must be inferred from authoritative live fields");
Require(reconciledPrecise.PrevComboAction == VulcanSkill.PreciseTouch
        && reconciledPrecise.IQStacks == 2
        && externalActionObserved
        && inferredExternalAction == VulcanSkill.PreciseTouch,
    "external-action reconciliation must retain inferred state and identify manual intervention for a fresh live replan");
Require(StepStateReconciler.TryReconcileExternalAction(
        craft,
        manualRoot,
        manualRoot with { },
        out _,
        out var unchangedExternalActionObserved,
        out _)
        && !unchangedExternalActionObserved,
    "an unchanged synthesis snapshot must not continuously replace the active solver");
var manualMiracleRoot = manualRoot with { MaterialMiracleCharges = 1 };
var manualMiracle = Simulator.Execute(
    craft with { CurrentMaterialMiracleCharges = 1 },
    manualMiracleRoot,
    VulcanSkill.MaterialMiracle,
    0,
    0.5f).Item2;
Require(StepStateReconciler.TryReconcileExternalAction(
        craft with { CurrentMaterialMiracleCharges = 1 },
        manualMiracleRoot,
        manualMiracle,
        out _,
        out var externalMiracleObserved,
        out var inferredExternalMiracle)
        && externalMiracleObserved
        && inferredExternalMiracle == VulcanSkill.MaterialMiracle,
    "a manual zero-step duty action must be detected from its live charge even when all normal synthesis fields stay unchanged");

var predictedRoot = Root(Condition.Normal);
var predictedTouch = Simulator.Execute(craft, predictedRoot, VulcanSkill.BasicTouch, 0, 0.5f).Item2;
var staleTouchObservation = predictedRoot with { Condition = Condition.Good };
Require(!StepStateReconciler.TryReconcileAction(
        craft, predictedRoot, VulcanSkill.BasicTouch, staleTouchObservation, out _),
    "a transient pre-action snapshot must not acknowledge the pending action");
var observedTouch = predictedTouch with
{
    Condition = Condition.Good,
    PrevComboAction = predictedRoot.PrevComboAction,
};
Require(StepStateReconciler.TryReconcileAction(
        craft, predictedRoot, VulcanSkill.BasicTouch, observedTouch, out var reconciledTouch),
    "a settled pending-action snapshot must reconcile despite an unpredictable condition");
Require(reconciledTouch.Condition == Condition.Good
        && reconciledTouch.PrevComboAction == VulcanSkill.BasicTouch,
    "pending-action reconciliation must trust the observed condition and retain inferred hidden state");
var missingObservedTouchCombo = observedTouch with { ComboAction = VulcanSkill.None };
Require(StepStateReconciler.TryReconcileAction(
        craft, predictedRoot, VulcanSkill.BasicTouch, missingObservedTouchCombo, out var reconciledHiddenTouchCombo)
        && reconciledHiddenTouchCombo.ComboAction == VulcanSkill.BasicTouch,
    "pending-action reconciliation must retain an inferred combo that live synthesis state does not expose");
var roundedTouchObservation = predictedTouch with
{
    Quality = predictedTouch.Quality + 1,
    Condition = Condition.Good,
    PrevComboAction = predictedRoot.PrevComboAction,
};
Require(StepStateReconciler.TryReconcileAction(
        craft, predictedRoot, VulcanSkill.BasicTouch, roundedTouchObservation, out var reconciledRoundedTouch),
    "a one-point game/simulator quality rounding difference must reconcile");
Require(reconciledRoundedTouch.Quality == roundedTouchObservation.Quality,
    "reconciliation must retain the game's authoritative rounded quality");
var activeCombo = predictedRoot with
{
    ComboAction = VulcanSkill.StandardTouch,
    PrevComboAction = VulcanSkill.StandardTouch,
};
var unobservedActiveCombo = activeCombo with { ComboAction = VulcanSkill.None };
Require(StepStateReconciler.TryReconcileExternalAction(
        craft,
        activeCombo,
        unobservedActiveCombo,
        out var reconciledHiddenActiveCombo,
        out var hiddenComboExternalActionObserved,
        out _)
        && reconciledHiddenActiveCombo.ComboAction == VulcanSkill.StandardTouch
        && !hiddenComboExternalActionObserved,
    "an unavailable live combo field must preserve inferred combo state without inventing an external action");
Require(CraftingGameInterop.RecommendationStateStillCurrent(activeCombo, unobservedActiveCombo),
    "the live execution gate must not reject an inferred combo solely because synthesis state cannot expose it");
var observedComboBreaker = activeCombo with
{
    Index = 2,
    RemainingCP = 482,
    VenerationLeft = 4,
    ComboAction = VulcanSkill.None,
};
Require(StepStateReconciler.TryReconcileExternalAction(
        craft,
        activeCombo,
        observedComboBreaker,
        out var reconciledComboBreaker,
        out var comboBreakerObserved,
        out var inferredComboBreaker)
        && reconciledComboBreaker.ComboAction == VulcanSkill.None
        && reconciledComboBreaker.PrevComboAction == VulcanSkill.Veneration
        && comboBreakerObserved
        && inferredComboBreaker == VulcanSkill.Veneration,
    "an observed combo-breaking action must clear inferred combo state");
var invalidRoundedTouchObservation = roundedTouchObservation with
{
    Quality = predictedTouch.Quality + 2,
};
Require(!StepStateReconciler.TryReconcileAction(
        craft, predictedRoot, VulcanSkill.BasicTouch, invalidRoundedTouchObservation, out _),
    "quality differences larger than one point must remain reconciliation failures");

var finalAppraisalCraft = Craft() with
{
    CraftProgress = 5940,
    CraftQualityMax = 7800,
    CraftDurability = 60,
};
var beforeFinalAppraisal = Root() with
{
    Index = 14,
    Progress = 4860,
    Quality = 4695,
    Durability = 30,
    RemainingCP = 108,
    IQStacks = 8,
    GreatStridesLeft = 3,
    InnovationLeft = 1,
};
var afterFinalAppraisal = Simulator.Execute(
    finalAppraisalCraft,
    beforeFinalAppraisal,
    VulcanSkill.FinalAppraisal,
    0,
    0.5f).Item2;
var observedFinalAppraisal = afterFinalAppraisal with
{
    PrevComboAction = beforeFinalAppraisal.PrevComboAction,
};
Require(StepStateReconciler.TryReconcileAction(
        finalAppraisalCraft,
        beforeFinalAppraisal,
        VulcanSkill.FinalAppraisal,
        observedFinalAppraisal,
        out var reconciledFinalAppraisal)
    && reconciledFinalAppraisal.Quality == 4695
    && reconciledFinalAppraisal.RemainingCP == 107
    && reconciledFinalAppraisal.FinalAppraisalLeft == 5,
    "reload recovery must reconcile Final Appraisal against the authoritative 7800 max quality instead of clamping live quality to the HQ-chance field");

var overcapCraft = Craft() with { CraftQualityMax = 2500 };
var overcapRoot = Root() with
{
    Quality = 2400,
    IQStacks = 3,
    InnovationLeft = 2,
};
var overcapDelicate = Simulator.Execute(
    overcapCraft,
    overcapRoot,
    VulcanSkill.DelicateSynthesis,
    0,
    0.5f).Item2;
Require(overcapDelicate.Quality == overcapCraft.CraftQualityMax,
    "simulated quality must clamp to the recipe maximum reported by the game");
Require(overcapDelicate.IQStacks == 4,
    "a successful quality action must grant Inner Quiet even when visible quality caps");
var observedOvercap = overcapDelicate with
{
    PrevComboAction = overcapRoot.PrevComboAction,
    PrevActionFailed = overcapRoot.PrevActionFailed,
};
Require(StepStateReconciler.TryReconcileAction(
        overcapCraft,
        overcapRoot,
        VulcanSkill.DelicateSynthesis,
        observedOvercap,
        out var reconciledOvercap),
    "a quality-overcapping action must reconcile against the game's capped quality");
Require(reconciledOvercap.Quality == 2500 && reconciledOvercap.IQStacks == 4,
    "quality-overcap reconciliation must preserve the capped quality and inferred Inner Quiet");

var cappedByregotRoot = Root() with
{
    Quality = 2000,
    IQStacks = 6,
    GreatStridesLeft = 3,
    InnovationLeft = 1,
    RemainingCP = 289,
    Durability = 15,
};
var cappedByregot = Simulator.Execute(
    overcapCraft,
    cappedByregotRoot,
    VulcanSkill.ByregotsBlessing,
    0,
    0.5f).Item2;
Require(cappedByregot.Quality == overcapCraft.CraftQualityMax
        && cappedByregot.IQStacks == 0
        && cappedByregot.GreatStridesLeft == 0,
    "a successful quality action must consume Great Strides even when visible quality is capped");
var observedCappedByregot = cappedByregot with
{
    Quality = overcapCraft.CraftQualityMax + 16,
    Condition = Condition.Good,
    PrevComboAction = cappedByregotRoot.PrevComboAction,
    PrevActionFailed = cappedByregotRoot.PrevActionFailed,
};
Require(StepStateReconciler.TryReconcileAction(
        overcapCraft,
        cappedByregotRoot,
        VulcanSkill.ByregotsBlessing,
        observedCappedByregot,
        out var reconciledCappedByregot),
    "a capped Byregot result must reconcile when the live addon exposes raw overcap quality");
Require(reconciledCappedByregot.GreatStridesLeft == 0
        && reconciledCappedByregot.Condition == Condition.Good
        && reconciledCappedByregot.Quality == overcapCraft.CraftQualityMax,
    "capped Byregot reconciliation must retain consumed Great Strides and canonicalize raw quality to the recipe maximum");

var cappedPreparatoryCraft = overcapCraft with { CraftDurability = 80 };
var cappedPreparatoryRoot = Root() with
{
    Quality = overcapCraft.CraftQualityMax,
    IQStacks = 8,
    GreatStridesLeft = 2,
    InnovationLeft = 4,
    RemainingCP = 179,
    Durability = 80,
};
var cappedPreparatory = Simulator.Execute(
    cappedPreparatoryCraft,
    cappedPreparatoryRoot,
    VulcanSkill.PreparatoryTouch,
    0,
    0.5f).Item2;
Require(cappedPreparatory.Quality == cappedPreparatoryCraft.CraftQualityMax
        && cappedPreparatory.IQStacks == 10
        && cappedPreparatory.GreatStridesLeft == 0
        && cappedPreparatory.InnovationLeft == 3
        && cappedPreparatory.RemainingCP == 139
        && cappedPreparatory.Durability == 60,
    "capped Preparatory Touch must consume Great Strides and retain its other observable state changes");
var observedCappedPreparatory = cappedPreparatory with
{
    PrevComboAction = cappedPreparatoryRoot.PrevComboAction,
    PrevActionFailed = cappedPreparatoryRoot.PrevActionFailed,
};
Require(StepStateReconciler.TryReconcileAction(
        cappedPreparatoryCraft,
        cappedPreparatoryRoot,
        VulcanSkill.PreparatoryTouch,
        observedCappedPreparatory,
        out var reconciledCappedPreparatory),
    "the exact capped Preparatory Touch trace must reconcile after Great Strides is consumed in-game");
Require(reconciledCappedPreparatory.GreatStridesLeft == 0
        && reconciledCappedPreparatory.IQStacks == 10,
    "capped Preparatory Touch reconciliation must retain consumed Great Strides and gained Inner Quiet");

var observationRoot = Root(Condition.Normal) with { CarefulObservationLeft = 3 };
var observedObservation = observationRoot with { Condition = Condition.Good };
Require(StepStateReconciler.TryReconcileExternalAction(
        craft, observationRoot, observedObservation, out var reconciledObservation),
    "a zero-step external Careful Observation must be inferred");
Require(reconciledObservation.CarefulObservationLeft == 2
        && reconciledObservation.Condition == Condition.Good,
    "Careful Observation reconciliation must consume one charge and trust the observed condition");

var ingredientSelection = new IngredientSelectionSequencer();
ingredientSelection.BeginEquipment(123, true, 1_000);
Require(ingredientSelection.Phase == EquipmentIngredientSelectionPhase.WaitingForMenu
        && ingredientSelection.ItemId == 123
        && ingredientSelection.HighQuality,
    "equipment ingredient selection must retain the pending item while waiting for its menu");
Require(!ingredientSelection.IsReady(1_149) && ingredientSelection.IsReady(1_150),
    "equipment ingredient menu selection must resume after 150 ms without blocking the framework thread");
ingredientSelection.MarkMenuSelectionComplete(1_150);
Require(ingredientSelection.Phase == EquipmentIngredientSelectionPhase.WaitingForAssignment
        && !ingredientSelection.IsReady(1_349)
        && ingredientSelection.IsReady(1_350),
    "equipment assignment verification must resume 200 ms later without blocking the framework thread");
ingredientSelection.CompleteEquipmentAssignment();
Require(ingredientSelection.Phase == EquipmentIngredientSelectionPhase.None
        && ingredientSelection.ItemId == 0,
    "completed equipment assignment must clear pending state");
ingredientSelection.DelayNormalAssignment(2_000);
Require(!ingredientSelection.IsReady(2_049) && ingredientSelection.IsReady(2_050),
    "normal material assignment verification must resume after 50 ms without blocking the framework thread");

var frameworkThreadId = Environment.CurrentManagedThreadId;
var backgroundSolver = new ThreadRecordingSolver();
CraftingProcessor.Setup();
CraftingProcessor.RegisterSolver(new ThreadRecordingSolverDefinition(backgroundSolver));
CraftingProcessor.OnCraftStarted(Craft(), Root(), 1, false);
Require(CraftingProcessor.NextRecommendation.Action == VulcanSkill.None,
    "craft start must submit solver work without blocking for a recommendation");
Require(SpinWait.SpinUntil(() => backgroundSolver.SolveThreadId != 0, TimeSpan.FromSeconds(5)),
    "background solver must start promptly");
Require(backgroundSolver.SolveThreadId != frameworkThreadId,
    "solver work must not run on the submitting framework thread");
var newerSolverStep = Root(Condition.Good) with { Index = 2 };
CraftingProcessor.OnCraftAdvanced(Craft(), newerSolverStep, 1);
backgroundSolver.Release();
Require(SpinWait.SpinUntil(() =>
    {
        CraftingProcessor.Update();
        return CraftingProcessor.NextRecommendation.Comment == "step 2";
    }, TimeSpan.FromSeconds(5)),
    "the latest queued craft snapshot must replace the stale in-flight result");
Require(backgroundSolver.Calls == 2,
    "the bounded crafting worker must run one in-flight request plus one latest queued request");
CraftingProcessor.OnCraftFinished(Craft(), newerSolverStep, 1, false);
CraftingProcessor.Dispose();

var terminalFailureSolver = new TerminalFailureSolver();
CraftingProcessor.Setup();
CraftingProcessor.RegisterSolver(new TerminalFailureSolverDefinition(terminalFailureSolver));
CraftingProcessor.OnCraftStarted(Craft(), Root(), 1, false);
Require(SpinWait.SpinUntil(() =>
    {
        CraftingProcessor.Update();
        return CraftingProcessor.FaultReason == "deterministic terminal failure";
    }, TimeSpan.FromSeconds(5)),
    "a terminal solver result must become an explicit automation fault");
CraftingProcessor.OnCraftAdvanced(Craft(), Root(), 1);
Thread.Sleep(20);
CraftingProcessor.Update();
Require(terminalFailureSolver.Calls == 1,
    "an unchanged terminal solver failure must not be resubmitted or spin forever");
CraftingProcessor.OnCraftFinished(Craft(), Root(), 1, true);
Require(CraftingProcessor.FaultReason.Length == 0,
    "finishing or cancelling a craft must clear its terminal solver fault");
CraftingProcessor.Dispose();

var staleLiveRecoverySolver = new ThreadRecordingSolver();
CraftingProcessor.Setup();
CraftingProcessor.RegisterSolver(new ThreadRecordingSolverDefinition(staleLiveRecoverySolver));
CraftingProcessor.OnCraftStarted(Craft(), Root(), 1, false);
Require(SpinWait.SpinUntil(() => staleLiveRecoverySolver.SolveThreadId != 0, TimeSpan.FromSeconds(5)),
    "the stale solver must be in flight before testing manual-intervention recovery");
CraftingProcessor.RegisterSolver(new SeededLiveDonatelloDefinition(
    new CachedRaphaelSolution { ActionIds = [(uint)VulcanSkill.BasicSynthesis] }));
Require(CraftingProcessor.TryAdoptLiveCraft(
        craft,
        reconciledPrecise,
        allowDonatelloLiveRecovery: true,
        out var manualRecoveryFailure)
        && string.IsNullOrEmpty(manualRecoveryFailure),
    "manual intervention must replace the active solver with a fresh live-root solver");
Require(SpinWait.SpinUntil(() =>
    {
        CraftingProcessor.Update();
        return CraftingProcessor.NextRecommendation.Action != VulcanSkill.None;
    }, TimeSpan.FromSeconds(5)),
    "manual-intervention live recovery must produce a quality-mode action without waiting behind stale solver work");
staleLiveRecoverySolver.Release();
CraftingProcessor.OnCraftFinished(craft, reconciledPrecise, 1, false);
CraftingProcessor.Dispose();

CraftingProcessor.Setup();
Require(CraftingProcessor.TryAdoptLiveCraft(
        cobaltTungstenCraft,
        cobaltTungstenReloadRoot,
        allowDonatelloLiveRecovery: true,
        out var cobaltProcessorRecoveryFailure)
    && string.IsNullOrEmpty(cobaltProcessorRecoveryFailure),
    "recipe 5630 must enter processor-level live recovery from its observed root");
for (var duplicateFrame = 0; duplicateFrame < 20; duplicateFrame++)
    CraftingProcessor.OnCraftAdvanced(cobaltTungstenCraft, cobaltTungstenReloadRoot, 5630);
Require(SpinWait.SpinUntil(() =>
    {
        CraftingProcessor.Update();
        return CraftingProcessor.NextRecommendation.Action != VulcanSkill.None
            || CraftingProcessor.FaultReason.Length > 0;
    }, TimeSpan.FromSeconds(10))
    && CraftingProcessor.FaultReason.Length == 0
    && CraftingProcessor.NextRecommendation.Action == VulcanSkill.Groundwork,
    "identical normal frames during live-root Raphael generation must not discard and consume the resumed Groundwork recommendation");
CraftingProcessor.OnCraftFinished(cobaltTungstenCraft, cobaltTungstenReloadRoot, 5630, false);
CraftingProcessor.Dispose();

CraftingProcessor.Setup();
Require(CraftingProcessor.TryAdoptLiveCraft(
        craft,
        reconciledPrecise,
        allowDonatelloLiveRecovery: true,
        out var freshRootRecoveryFailure)
    && string.IsNullOrEmpty(freshRootRecoveryFailure)
    && CraftingProcessor.ActiveSolver is DonatelloSolver,
    "quality-mode live recovery must establish a fresh Raphael baseline from the observed root without a recipe-start cache");
CraftingProcessor.OnCraftFinished(craft, reconciledPrecise, 1, false);
CraftingProcessor.Dispose();

CraftingProcessor.Setup();
var progressOnlyRecoveryCraft = Craft() with
{
    DonatelloOptions = new DonatelloExecutionOptions(DonatelloSolveObjective.ProgressOnly),
};
Require(CraftingProcessor.TryAdoptLiveCraft(
        progressOnlyRecoveryCraft,
        Root(),
        allowDonatelloLiveRecovery: true,
        out var progressOnlyRecoveryFailure)
    && string.IsNullOrEmpty(progressOnlyRecoveryFailure)
    && CraftingProcessor.ActiveSolver is DonatelloSolver,
    "explicit ProgressOnly may recover from a live root without a quality incumbent");
CraftingProcessor.OnCraftFinished(progressOnlyRecoveryCraft, Root(), 1, false);
CraftingProcessor.Dispose();

CraftingProcessor.Setup();
CraftingProcessor.RegisterSolver(new StandardSolverDefinition());
var standardRecoveryCraft = craft with { CraftHQ = true };
Require(CraftingProcessor.TryAdoptLiveCraft(
        standardRecoveryCraft,
        reconciledPrecise,
        allowDonatelloLiveRecovery: false,
        out var standardRecoveryFailure)
        && string.IsNullOrEmpty(standardRecoveryFailure)
        && CraftingProcessor.ActiveSolverName == "Standard Recipe Solver",
    $"live recovery must preserve Standard solver ownership instead of silently switching to Donatello; active={CraftingProcessor.ActiveSolverName}, failure={standardRecoveryFailure}");
CraftingProcessor.OnCraftFinished(standardRecoveryCraft, reconciledPrecise, 1, false);
CraftingProcessor.Dispose();

var staleSolver = new ThreadRecordingSolver();
var replacementSolver = new ThreadRecordingSolver(blockFirstCall: false);
CraftingProcessor.Setup();
var replacingDefinition = new ReplacingSolverDefinition(staleSolver, replacementSolver);
CraftingProcessor.RegisterSolver(replacingDefinition);
CraftingProcessor.OnCraftStarted(Craft(), Root(), 1, false);
Require(SpinWait.SpinUntil(() => staleSolver.SolveThreadId != 0, TimeSpan.FromSeconds(5)),
    "the stale craft solver must start before testing craft replacement");
CraftingProcessor.OnCraftFinished(Craft(), Root(), 1, true);
CraftingProcessor.OnCraftStarted(Craft(), Root(Condition.Good), 2, false);
Require(SpinWait.SpinUntil(() =>
    {
        CraftingProcessor.Update();
        return CraftingProcessor.NextRecommendation.Comment == "step 1";
    }, TimeSpan.FromSeconds(5)),
    "a new craft must start solving immediately instead of waiting behind obsolete in-flight work");
staleSolver.Release();
CraftingProcessor.OnCraftFinished(Craft(), Root(), 2, false);
CraftingProcessor.Dispose();

Console.WriteLine($"Donatello Vulcan acceptance: {assertions} assertions passed");

sealed class ThreadRecordingSolverDefinition(ThreadRecordingSolver solver) : ISolverDefinition
{
    public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
    {
        yield return new(this, 0, 1000, "Thread recording solver");
    }

    public Solver Create(CraftState craft, int flavor) => solver;
}

sealed class SeededLiveDonatelloDefinition(CachedRaphaelSolution solution) : ISolverDefinition
{
    public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
    {
        yield return new(this, 0, 2000, "Seeded live Donatello");
    }

    public Solver Create(CraftState craft, int flavor)
        => DonatelloSolverDefinition.CreateFromSolution(solution, craft);

    public Solver CreateLive(CraftState craft)
        => new DonatelloSolver(solution, craft);
}

sealed class TerminalFailureSolverDefinition(TerminalFailureSolver solver) : ISolverDefinition
{
    public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
    {
        yield return new(this, 0, 1000, "Terminal failure solver");
    }

    public Solver Create(CraftState craft, int flavor) => solver;
}

sealed class TerminalFailureSolver : Solver
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public override Recommendation Solve(CraftState craft, StepState step)
    {
        Interlocked.Increment(ref _calls);
        return new(VulcanSkill.None, "deterministic terminal failure", IsTerminalFailure: true);
    }
}

sealed class ReplacingSolverDefinition(params ThreadRecordingSolver[] solvers) : ISolverDefinition
{
    private int _nextSolver;

    public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
    {
        yield return new(this, 0, 1000, "Replacing solver");
    }

    public Solver Create(CraftState craft, int flavor) => solvers[_nextSolver++];
}

sealed class ThreadRecordingSolver(bool blockFirstCall = true) : Solver
{
    private readonly ManualResetEventSlim _release = new(false);
    private int _calls;
    private int _solveThreadId;

    public int Calls => Volatile.Read(ref _calls);
    public int SolveThreadId => Volatile.Read(ref _solveThreadId);

    public void Release() => _release.Set();

    public override Recommendation Solve(CraftState craft, StepState step)
    {
        Volatile.Write(ref _solveThreadId, Environment.CurrentManagedThreadId);
        if (Interlocked.Increment(ref _calls) == 1 && blockFirstCall)
            _release.Wait();
        return new(VulcanSkill.BasicSynthesis, $"step {step.Index}");
    }
}
