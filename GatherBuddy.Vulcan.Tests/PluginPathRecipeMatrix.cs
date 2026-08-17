using System.Text.Json;
using System.Text.Json.Serialization;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;

namespace GatherBuddy.Vulcan.Tests;

internal static class PluginPathRecipeMatrix
{
    private static readonly int[] BracketLevels = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
    private static readonly IReadOnlyDictionary<int, Condition> ScriptedMatrixConditions =
        Enumerable.Range(1, 100).ToDictionary(
            actionNumber => actionNumber,
            actionNumber => actionNumber == 1 ? Condition.Good : Condition.Normal);

    public static async Task Run(string corpusPath, Action<bool, string> require, bool expertsOnly = false)
    {
        var corpus = JsonSerializer.Deserialize<Corpus>(
            File.ReadAllText(corpusPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("plugin-path recipe corpus was empty");
        require(corpus.Version == 1, "plugin-path recipe corpus version must be 1");

        var normalCases = corpus.Cases
            .Where(testCase => testCase.Expert == false
                && testCase.Name.StartsWith('L')
                && testCase.RecipeId != null
                && testCase.ItemId != null
                && !testCase.Name.Contains("-specialist-", StringComparison.Ordinal))
            .ToList();
        var expertCases = corpus.Cases
            .Where(testCase => testCase.Expert == true
                && testCase.JobLevel == 100
                && testCase.RecipeId != null
                && testCase.ItemId != null
                && !testCase.Name.Contains("-specialist-", StringComparison.Ordinal))
            .ToList();
        require(normalCases.Count == 100,
            $"real recipe corpus must contain 100 base normal cases, actual={normalCases.Count}");
        require(expertCases.Count == 10,
            $"real recipe corpus must contain 10 base level-100 expert cases, actual={expertCases.Count}");
        foreach (var bracket in BracketLevels)
        {
            require(normalCases.Count(testCase => testCase.JobLevel == bracket) == 10,
                $"level bracket ending at {bracket} must contain exactly 10 normal recipes");
        }

        var config = global::GatherBuddy.GatherBuddy.Config.RaphaelSolverConfig;
        var previousDeadline = config.DonatelloOptimizationThresholdMs;
        var previousCache = config.DonatelloCacheMemoryMiB;
        var previousTimeout = config.RaphaelTimeoutMinutes;
        config.DonatelloOptimizationThresholdMs = 25;
        config.DonatelloCacheMemoryMiB = 64;
        config.RaphaelTimeoutMinutes = 1;

        var ties = 0;
        var improvements = 0;
        var nativeReplanCases = 0;
        try
        {
            var cases = expertsOnly ? expertCases : normalCases.Concat(expertCases).ToList();
            for (var index = 0; index < cases.Count; ++index)
            {
                var result = await RunCase(cases[index], require);
                if (result.PluginQuality == result.RaphaelQuality)
                    ties++;
                else
                    improvements++;
                if (result.NativeReplans > 0)
                    nativeReplanCases++;

                if ((index + 1) % 10 == 0 || index + 1 == cases.Count)
                {
                    Console.WriteLine(
                        $"Plugin-path matrix: {index + 1}/{cases.Count}; ties={ties}, improvements={improvements}, native-replan-cases={nativeReplanCases}");
                }
            }
        }
        finally
        {
            CraftingProcessor.Dispose();
            DonatelloNative.ClearCache();
            config.DonatelloOptimizationThresholdMs = previousDeadline;
            config.DonatelloCacheMemoryMiB = previousCache;
            config.RaphaelTimeoutMinutes = previousTimeout;
        }

        require(nativeReplanCases > 0,
            "real recipe matrix must exercise at least one active Donatello native replan");
        Console.WriteLine(
            $"Plugin-path recipe matrix passed: {ties + improvements}/{ties + improvements} completed; {ties} ties; {improvements} improvements; {nativeReplanCases} cases invoked native replanning");
    }

    private static async Task<CaseResult> RunCase(MatrixCase testCase, Action<bool, string> require)
    {
        var craft = BuildCraft(testCase);
        require(Simulator.BaseProgress(craft) == testCase.BaselineSettings.BaseProgress
                && Simulator.BaseQuality(craft) == testCase.BaselineSettings.BaseQuality,
            $"{testCase.Name}: C# live craft reconstruction must match the real-data native base stats");
        var root = GameStateBuilder.BuildInitialStepState(craft);

        DonatelloNative.ClearCache();
        var baselineDeadlineMillis = testCase.Expert == true ? 5_000 : 500;
        var raphael = DonatelloNative.SolveDetailed(
            craft,
            root,
            allowSpecialistActions: false,
            DonatelloNative.SolveMode.OptimizeQuality,
            incumbent: null,
            softDeadlineMillis: baselineDeadlineMillis,
            hardDeadlineMillis: baselineDeadlineMillis,
            bypassSolutionCache: true);
        require(raphael.Actions.Count > 0,
            $"{testCase.Name}: pure Raphael must return a non-empty incumbent");
        var raphaelFinal = ExecuteMacro(craft, root, raphael.Actions, testCase, require);
        require(raphaelFinal.Progress >= craft.CraftProgress,
            $"{testCase.Name}: pure Raphael incumbent must complete in the scripted game");

        var solution = new CachedRaphaelSolution
        {
            ActionIds = raphael.Actions.Select(action => (uint)action).ToList(),
        };
        var definition = new MatrixSolverDefinition(solution);
        CraftingProcessor.Setup();
        CraftingProcessor.RegisterSolver(definition);
        CraftingProcessor.OnCraftStarted(craft, root, testCase.RecipeId!.Value, isTrial: false);
        var game = new PluginPathSimulationAcceptanceTests.SeededGame(
            craft,
            root,
            actionSeed: 0xA11CEu,
            conditionSeed: 0xC0FFEEu,
            forcedConditions: ScriptedMatrixConditions);
        StepState? pluginFinal = null;
        var current = root;
        try
        {
            for (var actionNumber = 1; actionNumber <= 100; ++actionNumber)
            {
                var recommendation = await PluginPathSimulationAcceptanceTests.AwaitRecommendation();
                require(!recommendation.IsTerminalFailure
                        && recommendation.Action != VulcanSkill.None
                        && Simulator.CanUseAction(craft, current, recommendation.Action),
                    $"{testCase.Name}: plugin must emit a legal non-terminal recommendation at action {actionNumber}");
                var executed = game.SelectAction(recommendation.Action, out var manual);
                require(!manual, $"{testCase.Name}: matrix has no manual action at ordinal {actionNumber}");
                var actual = game.Execute(executed, require);
                if (actual.Progress >= craft.CraftProgress)
                {
                    pluginFinal = actual;
                    CraftingProcessor.OnCraftFinished(craft, actual, testCase.RecipeId, cancelled: false);
                    break;
                }
                require(actual.Durability > 0,
                    $"{testCase.Name}: plugin exhausted durability at action {actionNumber}");
                current = PluginPathSimulationAcceptanceTests.ReconcileRecommended(
                    craft,
                    current,
                    executed,
                    actual,
                    require);
                CraftingProcessor.OnCraftAdvanced(craft, current, testCase.RecipeId);
            }
        }
        finally
        {
            CraftingProcessor.Dispose();
        }

        require(pluginFinal != null,
            $"{testCase.Name}: plugin path must complete within 100 actions");
        require(pluginFinal!.Quality >= raphaelFinal.Quality,
            $"{testCase.Name}: plugin quality regression: Raphael={raphaelFinal.Quality}, plugin={pluginFinal.Quality}");
        var nativeReplans = definition.CreatedSolver is DonatelloSolver donatello
            ? donatello.NativeReplanCount
            : 0;
        return new(raphaelFinal.Quality, pluginFinal.Quality, nativeReplans);
    }

    private static StepState ExecuteMacro(
        CraftState craft,
        StepState root,
        IReadOnlyList<VulcanSkill> actions,
        MatrixCase testCase,
        Action<bool, string> require)
    {
        var game = new PluginPathSimulationAcceptanceTests.SeededGame(
            craft,
            root,
            actionSeed: 0xA11CEu,
            conditionSeed: 0xC0FFEEu,
            forcedConditions: ScriptedMatrixConditions);
        foreach (var action in actions)
        {
            var actual = game.Execute(action, require);
            if (actual.Progress >= craft.CraftProgress)
                return actual;
            require(actual.Durability > 0,
                $"{testCase.Name}: pure Raphael incumbent exhausted durability before completion");
        }
        return game.State;
    }

    private static CraftState BuildCraft(MatrixCase testCase)
    {
        var stats = testCase.CrafterStats
            ?? throw new InvalidOperationException($"{testCase.Name}: missing crafter stats");
        var recipeLevel = testCase.RecipeLevel
            ?? throw new InvalidOperationException($"{testCase.Name}: missing recipe-level data");
        var settings = testCase.BaselineSettings;
        var expert = testCase.Expert == true;
        var conditionFlags = expert
            ? ConditionFlags.Normal | ConditionFlags.Good | ConditionFlags.Excellent
                | ConditionFlags.Poor | ConditionFlags.Centered | ConditionFlags.Sturdy
                | ConditionFlags.Pliant | ConditionFlags.Malleable | ConditionFlags.Primed
                | ConditionFlags.GoodOmen | ConditionFlags.Robust
            : ConditionFlags.Normal | ConditionFlags.Good | ConditionFlags.Excellent | ConditionFlags.Poor;
        return new CraftState
        {
            RecipeId = testCase.RecipeId!.Value,
            ItemId = testCase.ItemId!.Value,
            StatCraftsmanship = stats.Craftsmanship,
            StatControl = stats.Control,
            StatCP = stats.Cp,
            StatLevel = stats.Level,
            UnlockedManipulation = stats.Manipulation,
            Specialist = false,
            CraftHQ = true,
            CraftExpert = expert,
            CraftLevel = recipeLevel.JobLevel,
            CraftDurability = settings.MaxDurability,
            CraftProgress = settings.MaxProgress,
            CraftQualityMax = settings.MaxQuality,
            CraftProgressDivider = recipeLevel.ProgressDiv,
            CraftProgressModifier = recipeLevel.ProgressMod,
            CraftQualityDivider = recipeLevel.QualityDiv,
            CraftQualityModifier = recipeLevel.QualityMod,
            ConditionFlags = conditionFlags,
            CraftConditionProbabilities = GameStateBuilder.GetConditionProbabilities(
                conditionFlags,
                stats.Level,
                expert),
            DonatelloOptions = new DonatelloExecutionOptions(),
        };
    }

    private sealed class MatrixSolverDefinition(CachedRaphaelSolution solution) : ISolverDefinition
    {
        public Solver? CreatedSolver { get; private set; }

        public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
        {
            yield return new(this, 0, 1000, "Matrix Donatello");
        }

        public Solver Create(CraftState craft, int flavor)
            => CreatedSolver = DonatelloSolverDefinition.CreateFromSolution(solution, craft);
    }

    private sealed record CaseResult(int RaphaelQuality, int PluginQuality, int NativeReplans);

    private sealed class Corpus
    {
        public int Version { get; set; }
        public List<MatrixCase> Cases { get; set; } = [];
    }

    private sealed class MatrixCase
    {
        public string Name { get; set; } = string.Empty;
        public int? JobLevel { get; set; }
        public bool? Expert { get; set; }
        public uint? RecipeId { get; set; }
        public uint? ItemId { get; set; }
        public CrafterStats? CrafterStats { get; set; }
        public RecipeLevel? RecipeLevel { get; set; }
        public NativeSettings BaselineSettings { get; set; } = new();
    }

    private sealed class CrafterStats
    {
        [JsonPropertyName("craftsmanship")]
        public int Craftsmanship { get; set; }
        [JsonPropertyName("control")]
        public int Control { get; set; }
        [JsonPropertyName("cp")]
        public int Cp { get; set; }
        [JsonPropertyName("level")]
        public int Level { get; set; }
        [JsonPropertyName("manipulation")]
        public bool Manipulation { get; set; }
    }

    private sealed class RecipeLevel
    {
        [JsonPropertyName("job_level")]
        public int JobLevel { get; set; }
        [JsonPropertyName("progress_div")]
        public int ProgressDiv { get; set; }
        [JsonPropertyName("quality_div")]
        public int QualityDiv { get; set; }
        [JsonPropertyName("progress_mod")]
        public int ProgressMod { get; set; }
        [JsonPropertyName("quality_mod")]
        public int QualityMod { get; set; }
    }

    private sealed class NativeSettings
    {
        [JsonPropertyName("max_cp")]
        public int MaxCp { get; set; }
        [JsonPropertyName("max_durability")]
        public int MaxDurability { get; set; }
        [JsonPropertyName("max_progress")]
        public int MaxProgress { get; set; }
        [JsonPropertyName("max_quality")]
        public int MaxQuality { get; set; }
        [JsonPropertyName("base_progress")]
        public int BaseProgress { get; set; }
        [JsonPropertyName("base_quality")]
        public int BaseQuality { get; set; }
    }
}
