using GatherBuddy.Vulcan;
using GatherBuddy.Crafting;
using GatherBuddy.AutoGather;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.Utility;
using GatherBuddy.Vulcan.Tests;
using System.Text.Json;

var assertions = 0;

typeof(global::GatherBuddy.GatherBuddy)
    .GetProperty(nameof(global::GatherBuddy.GatherBuddy.Log))!
    .GetSetMethod(nonPublic: true)!
    .Invoke(null, [new ElliLib.Log.Logger()]);

void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    assertions++;
}

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

var oneDelineation = Root() with { QuickInnoLeft = 1, QuickInnoAvailable = true, CrafterDelineationsLeft = 1 };
var afterQuickInnovation = Simulator.Execute(
    craft, oneDelineation, VulcanSkill.QuickInnovation, 0, 1).Item2;
Require(afterQuickInnovation.CrafterDelineationsLeft == 0
        && !Simulator.CanUseAction(craft, afterQuickInnovation, VulcanSkill.HeartAndSoul),
    "Quick Innovation must consume the same delineation pool used by Heart and Soul");

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
        MaxStellarSteadyHandUses: 2,
        MaxMaterialMiracleUses: 3,
        MinimumStepsBeforeMaterialMiracle: 4),
    "ICE Progress Only Solver must map to progress-only Donatello and preserve expert Cosmic limits");
var standardOptions = ArtisanIpcShim.BuildDonatelloOptions(
    "Default", false, 2, 3, 4, 5, 6);
Require(standardOptions == new DonatelloExecutionOptions(
        DonatelloSolveObjective.MaximizeQuality,
        MinimizeSteps: false,
        MaxStellarSteadyHandUses: 0,
        MaxMaterialMiracleUses: 5,
        MinimumStepsBeforeMaterialMiracle: 6),
    "non-expert ICE crafts must use standard Material Miracle limits and never expert Stellar limits");
Require(CraftingGameInterop.CosmicIngredientButtonOrder(progressOnlyOptions) == (39u, 40u)
        && CraftingGameInterop.CosmicIngredientButtonOrder(standardOptions) == (40u, 39u),
    "Cosmic ingredient assignment must prefer NQ for progress-only and HQ for quality-maximizing ICE crafts");

var cacheKeyRequest = new RaphaelSolveRequest(1, 100, 5000, 5000, 600, true, false);
Require(cacheKeyRequest.GetKey() != (cacheKeyRequest with
        { Objective = DonatelloSolveObjective.ProgressOnly }).GetKey()
        && cacheKeyRequest.GetKey() != (cacheKeyRequest with { MinimizeSteps = true }).GetKey()
        && cacheKeyRequest.GetKey() != (cacheKeyRequest with { SplendorCosmic = true }).GetKey()
        && cacheKeyRequest.GetKey() != (cacheKeyRequest with { StellarSteadyHandCharges = 1 }).GetKey()
        && cacheKeyRequest.GetKey() != (cacheKeyRequest with { MaxMaterialMiracleUses = 1 }).GetKey()
        && cacheKeyRequest.GetKey() != (cacheKeyRequest with
        { MinimumStepsBeforeMaterialMiracle = 1 }).GetKey(),
    "Donatello cache keys must isolate every ICE execution objective and Cosmic-use limit");

using var requestJson = JsonDocument.Parse(DonatelloNative.SerializeRequest(
    specialistCraft, specialistRoot, allowSpecialistActions: true, backloadProgress: false));
var nativeRoot = requestJson.RootElement.GetProperty("root");
var expectedRequestFields = new HashSet<string>
{
    "abiVersion", "maxCp", "maxDurability", "maxProgress", "maxQuality", "baseProgress",
    "baseQuality", "jobLevel", "manipulation", "specialist", "backloadProgress",
    "objective", "minimizeSteps", "stellarSteadyHandCharges", "incumbentActionIds", "root",
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
    "Donatello requests must contain exactly the fields required by native ABI v5");
Require(requestJson.RootElement.GetProperty("abiVersion").GetUInt32() == 5
        && requestJson.RootElement.GetProperty("objective").GetInt32() == 0
        && !requestJson.RootElement.GetProperty("minimizeSteps").GetBoolean()
        && requestJson.RootElement.GetProperty("stellarSteadyHandCharges").GetUInt32() == 0
        && requestJson.RootElement.GetProperty("incumbentActionIds").GetArrayLength() == 0
        && !requestJson.RootElement.TryGetProperty("AbiVersion", out _)
        && nativeRoot.GetProperty("wasteNot").GetInt32() == 0
        && nativeRoot.GetProperty("manipulation").GetInt32() == 0
        && nativeRoot.GetProperty("innovation").GetInt32() == 0
        && nativeRoot.GetProperty("veneration").GetInt32() == 0
        && nativeRoot.GetProperty("greatStrides").GetInt32() == 0
        && nativeRoot.GetProperty("muscleMemory").GetInt32() == 0
        && nativeRoot.GetProperty("crafterDelineations").GetInt32() == 1
        && !nativeRoot.TryGetProperty("manipulationLeft", out _),
    "Donatello requests must match every ABI v5 camelCase field name exactly");

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
    backloadProgress: false));
var progressOnlyRoot = progressOnlyRequestJson.RootElement.GetProperty("root");
Require(progressOnlyRequestJson.RootElement.GetProperty("objective").GetInt32() == 1
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
    backloadProgress: false,
    incumbent: [VulcanSkill.BasicSynthesis]));
Require(incumbentRequestJson.RootElement.GetProperty("incumbentActionIds")[0].GetUInt32()
        == (uint)VulcanSkill.BasicSynthesis,
    "Donatello replans must send the current finishing suffix as the native search incumbent");
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

var materialMiracleCraft = Craft();
materialMiracleCraft.StatLevel = 101;
materialMiracleCraft.DonatelloOptions = new DonatelloExecutionOptions(
    DonatelloSolveObjective.MaximizeQuality,
    MaxMaterialMiracleUses: 2,
    MinimumStepsBeforeMaterialMiracle: 4);
var materialMiracleRoot = Root() with { Index = 4, MaterialMiracleCharges = 1 };
Require(!DonatelloSolver.ShouldUseMaterialMiracle(
        materialMiracleCraft, materialMiracleRoot with { Index = 3 })
        && DonatelloSolver.ShouldUseMaterialMiracle(materialMiracleCraft, materialMiracleRoot)
        && !DonatelloSolver.ShouldUseMaterialMiracle(
            materialMiracleCraft, materialMiracleRoot with { MaterialMiracleActive = true })
        && !DonatelloSolver.ShouldUseMaterialMiracle(
            materialMiracleCraft, materialMiracleRoot with { MaterialMiraclesUsed = 2 }),
    "Material Miracle policy must enforce minimum step, active-effect exclusion, and maximum uses");

if (args.Contains("--artisan-shim", StringComparer.Ordinal))
{
    Console.WriteLine($"Artisan shim/Donatello acceptance: {assertions} assertions passed");
    return;
}

AcquisitionAcceptanceTests.Run(Require);
await MarketplaceAcceptanceTests.Run(Require);
Require(VendorAcceptanceTests.Run() > 0, "vendor acceptance harness must execute at least one assertion");
Require(LiveAcquisitionAcceptanceTests.Run() > 0, "live acquisition acceptance harness must execute at least one assertion");
IntegrationAcceptanceTests.Run(Require);

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
Require(!same.IsStrictlyBetterThan(
        new DonatelloPlanEvaluation(true, same.Quality, same.Steps + 1, same.Duration + 3, []),
        minimizeSteps: false),
    "quality-only Donatello mode must retain equal-quality incumbents regardless of steps");

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
        craft, manualRoot, observedPrecise, out var reconciledPrecise),
    "a unique external step action must be inferred from authoritative live fields");
Require(reconciledPrecise.PrevComboAction == VulcanSkill.PreciseTouch
        && reconciledPrecise.IQStacks == 2,
    "external-action reconciliation must retain inferred combo and effect state");

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

var observationRoot = Root(Condition.Normal) with { CarefulObservationLeft = 3 };
var observedObservation = observationRoot with { Condition = Condition.Good };
Require(StepStateReconciler.TryReconcileExternalAction(
        craft, observationRoot, observedObservation, out var reconciledObservation),
    "a zero-step external Careful Observation must be inferred");
Require(reconciledObservation.CarefulObservationLeft == 2
        && reconciledObservation.Condition == Condition.Good,
    "Careful Observation reconciliation must consume one charge and trust the observed condition");

Console.WriteLine($"Donatello Vulcan acceptance: {assertions} assertions passed");
