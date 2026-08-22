using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;

namespace GatherBuddy.FcMesh.Capabilities;

public enum FcCapabilityEligibility : byte
{
    NqAllowed,
    CapabilityPending,
    EligibleGuaranteed,
    Ineligible,
}

/// <summary>
/// Non-serializable admission proof attached to an FC execution plan. It is
/// a snapshot of the request/session/fingerprint identity only; the queue
/// boundary must revalidate it immediately before starting automation.
/// </summary>
public sealed record FcCapabilityExecutionProof(
    Guid RequestId,
    Guid SessionId,
    ulong SessionGeneration,
    string WorldFingerprint,
    string GameVersion,
    string PlannerFingerprint,
    string GearsetFingerprint,
    string SolverFingerprint,
    FcCapabilityEligibility Eligibility)
{
    public CapabilityRequestRecord? Request { get; init; }
}

public sealed record FcCapabilityFingerprintInput(
    string Kind,
    uint RecipeId,
    uint JobId,
    int Level,
    int Craftsmanship,
    int Control,
    int Cp,
    bool Manipulation,
    bool Specialist,
    bool SplendorCosmic,
    int CrafterDelineations,
    string GameVersion,
    int PlannerSemanticsVersion,
    string PlannerSettings,
    string GearSettings,
    string SolverSettings,
    FcQualityPolicy QualityPolicy);

public sealed record FcSolverFingerprintInput(
    string SchemaVersion,
    VulcanSolverMode? SolverMode,
    bool? RaphaelEnabled,
    int? RaphaelTimeoutMinutes,
    int? RaphaelInitialOptimizationSeconds,
    bool? RaphaelAllowSpecialistActions,
    bool? DonatelloMinimizeSteps,
    bool? DonatelloExperimentalProgressPriority,
    int? DonatelloOptimizationThresholdMs,
    int? DonatelloImprovementQuietSeconds,
    int? GabrielWorkerThreads,
    string RecipeSettingsFingerprint);

public sealed record FcCapabilityCacheKey(
    uint RecipeId,
    bool IsPrecraft,
    string QualityFingerprint,
    string WorldFingerprint,
    string GameVersion,
    string PlannerFingerprint,
    string GearsetFingerprint,
    string SolverFingerprint);

public sealed record FcCapabilityCandidate(
    uint JobId,
    bool CanCraft,
    bool GuaranteesRequiredQuality,
    FcRaphaelAssessmentOutcome Assessment,
    int StepCount,
    long DurationMilliseconds,
    string GearsetFingerprint,
    string SolverFingerprint,
    string Reason)
{
    public static FcCapabilityCandidate Unavailable(uint jobId, string reason)
        => new(
            jobId,
            false,
            false,
            FcRaphaelAssessmentOutcome.None,
            int.MaxValue,
            long.MaxValue,
            string.Empty,
            string.Empty,
            reason);
}

public sealed record FcCapabilityAssessment(
    uint RecipeId,
    bool IsPrecraft,
    bool CanCraft,
    uint? SelectedJobId,
    bool GuaranteesRequiredQuality,
    FcRaphaelAssessmentOutcome Assessment,
    string GearsetFingerprint,
    string SolverFingerprint,
    string GameVersion,
    string PlannerFingerprint,
    string Reason)
{
    public bool IsFullQuality
        => Assessment == FcRaphaelAssessmentOutcome.FullQuality
            && GuaranteesRequiredQuality;

    public CraftCapabilityResult ToWireResult()
        => new(
            RecipeId,
            CanCraft,
            SelectedJobId,
            GuaranteesRequiredQuality,
            Assessment);
}

public sealed record FcCapabilityRequestResult(
    bool Accepted,
    string Message,
    Guid RequestId,
    ulong Revision,
    FcPublicationCommandStatus Status,
    CapabilityRequestRecord? Request)
{
    public static FcCapabilityRequestResult Blocked(string message)
        => new(false, message, Guid.Empty, 0, FcPublicationCommandStatus.Blocked, null);
}

public sealed record FcCapabilityPublicationDiagnostics(
    string State,
    string AuthorScope,
    bool WritesAllowed,
    string LastError,
    int PendingCommands,
    ulong LastReservedRevision,
    Guid? CurrentRequestId,
    int PublishedResponses);

public sealed record FcCapabilityAssessmentContext(
    CapabilityRequestRecord Request,
    RequiredCraftCapability Capability,
    WorkerSessionRecord Session,
    string GameVersion,
    string PlannerFingerprint,
    string WorldFingerprint);

public interface IFcCapabilityAssessmentProvider
{
    IReadOnlyList<FcCapabilityCandidate> Assess(FcCapabilityAssessmentContext context);

    string EnvironmentFingerprint(FcCapabilityAssessmentContext context)
        => string.Empty;
}

/// <summary>
/// Deterministic admission selector shared by live and test assessment
/// providers. It is deliberately independent of any solver implementation.
/// </summary>
public static class FcCapabilityAssessor
{
    public static FcCapabilityAssessment SelectBest(
        FcCapabilityAssessmentContext context,
        IReadOnlyList<FcCapabilityCandidate> candidates,
        Func<uint, uint> recipeJobResolver)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(recipeJobResolver);
        var policy = context.Capability.EffectiveQualityPolicy ?? FcQualityPolicy.Empty;
        var requiresHq = policy.Rules.Any(rule => rule.Quality == FcItemQuality.Hq);
        var requiredJob = recipeJobResolver(context.Capability.RecipeId);
        if (requiredJob == 0)
        {
            return new(
                context.Capability.RecipeId,
                context.Capability.IsPrecraft,
                false,
                null,
                false,
                FcRaphaelAssessmentOutcome.None,
                string.Empty,
                string.Empty,
                context.GameVersion,
                context.PlannerFingerprint,
                $"Recipe {context.Capability.RecipeId} has no resolvable crafting job.");
        }
        var usable = candidates
            .Where(candidate => candidate is not null && candidate.CanCraft)
            .Where(candidate => requiredJob == 0 || candidate.JobId == requiredJob)
            .Where(candidate => !requiresHq
                || candidate.GuaranteesRequiredQuality
                    && candidate.Assessment == FcRaphaelAssessmentOutcome.FullQuality)
            .OrderByDescending(candidate => candidate.GuaranteesRequiredQuality)
            .ThenByDescending(candidate => OutcomeRank(candidate.Assessment))
            .ThenBy(candidate => candidate.StepCount)
            .ThenBy(candidate => candidate.DurationMilliseconds)
            .ThenBy(candidate => candidate.JobId)
            .ToArray();

        if (usable.Length == 0)
        {
            var reason = requiresHq
                ? "No local class has a guaranteed FullQuality Raphael assessment."
                : candidates.Count == 0
                    ? "No local crafting class was available."
                    : candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Reason))?.Reason
                        ?? "No local class can craft this recipe.";
            return new(
                context.Capability.RecipeId,
                context.Capability.IsPrecraft,
                false,
                null,
                false,
                FcRaphaelAssessmentOutcome.None,
                string.Empty,
                string.Empty,
                context.GameVersion,
                context.PlannerFingerprint,
                reason);
        }

        var selected = usable[0];
        return new(
            context.Capability.RecipeId,
            context.Capability.IsPrecraft,
            true,
            selected.JobId,
            selected.GuaranteesRequiredQuality,
            selected.Assessment,
            selected.GearsetFingerprint,
            selected.SolverFingerprint,
            context.GameVersion,
            context.PlannerFingerprint,
            selected.Reason);
    }

    private static int OutcomeRank(FcRaphaelAssessmentOutcome outcome)
        => outcome switch
        {
            FcRaphaelAssessmentOutcome.FullQuality => 4,
            FcRaphaelAssessmentOutcome.MinimumQualityMet => 3,
            FcRaphaelAssessmentOutcome.NoQualityRequired => 2,
            FcRaphaelAssessmentOutcome.PartialQuality => 1,
            _ => 0,
        };
}

/// <summary>
/// Live assessment adapter. It uses the existing gearset reader and Raphael
/// assessment path; no parallel simulator or solver is introduced here.
/// </summary>
public sealed class FcLiveCapabilityAssessmentProvider : IFcCapabilityAssessmentProvider
{
    private readonly Func<uint, uint> _recipeJobResolver;

    public FcLiveCapabilityAssessmentProvider(Func<uint, uint> recipeJobResolver)
        => _recipeJobResolver = recipeJobResolver ?? throw new ArgumentNullException(nameof(recipeJobResolver));

    public string EnvironmentFingerprint(FcCapabilityAssessmentContext context)
    {
        var recipe = RecipeManager.GetRecipe(context.Capability.RecipeId);
        if (recipe is not { } resolved)
            return "recipe-unavailable";
        var jobId = _recipeJobResolver(context.Capability.RecipeId);
        if (jobId == 0)
            return "recipe-job-unavailable";
        var stats = GearsetStatsReader.ReadGearsetStatsForJob(jobId);
        if (stats is null)
            return "gearset-unavailable";
        var policy = context.Capability.EffectiveQualityPolicy ?? FcQualityPolicy.Empty;
        var settings = new RecipeCraftSettings
        {
            UseAllNQ = !policy.Rules.Any(rule => rule.Quality == FcItemQuality.Hq),
        };
        return FcCapabilityFingerprints.Gearset(
            context.Capability.RecipeId,
            jobId,
            stats,
            context.GameVersion,
            context.PlannerFingerprint,
            policy,
            resolved.Number == 0 || Dalamud.UnlockState.IsRecipeUnlocked(resolved),
            FcCapabilityFingerprints.CraftSettingsFingerprint(settings))
            + ":"
            + FcCapabilityFingerprints.Solver(
                context.Capability.RecipeId,
                policy,
                FcCapabilityFingerprints.CraftSettingsFingerprint(settings));
    }

    public IReadOnlyList<FcCapabilityCandidate> Assess(FcCapabilityAssessmentContext context)
    {
        var recipe = RecipeManager.GetRecipe(context.Capability.RecipeId);
        if (recipe is not { } resolved)
            return [FcCapabilityCandidate.Unavailable(0, $"Recipe {context.Capability.RecipeId} is unavailable.")];

        var jobId = _recipeJobResolver(context.Capability.RecipeId);
        if (jobId == 0)
            return [FcCapabilityCandidate.Unavailable(0, $"Recipe {context.Capability.RecipeId} has no resolvable crafting job.")];
        if (resolved.Number != 0 && !Dalamud.UnlockState.IsRecipeUnlocked(resolved))
            return [FcCapabilityCandidate.Unavailable(jobId, "Recipe is not unlocked on this character.")];

        var stats = GearsetStatsReader.ReadGearsetStatsForJob(jobId);
        if (stats is null)
            return [FcCapabilityCandidate.Unavailable(jobId, "No saved gearset or current stats are available for the recipe job.")];

        var policy = context.Capability.EffectiveQualityPolicy ?? FcQualityPolicy.Empty;
        var requiresHq = policy.Rules.Any(rule => rule.Quality == FcItemQuality.Hq);
        var settings = new RecipeCraftSettings
        {
            UseAllNQ = !requiresHq,
        };
        if (!RaphaelAssessmentService.TryAssessRecipe(
                context.Capability.RecipeId,
                settings,
                out var assessment))
            return [FcCapabilityCandidate.Unavailable(jobId, assessment.Details)];

        var canCraft = (assessment.State is RaphaelAssessmentState.Ready
            or RaphaelAssessmentState.NotApplicable)
            && assessment.Outcome is not (
                RaphaelAssessmentOutcome.SimulationFailed
                or RaphaelAssessmentOutcome.Incomplete
                or RaphaelAssessmentOutcome.FailedDurability
                or RaphaelAssessmentOutcome.FailedQualityRequirement);
        var guarantees = assessment.State == RaphaelAssessmentState.Ready
            && assessment.Outcome == RaphaelAssessmentOutcome.FullQuality;
        var gearFingerprint = FcCapabilityFingerprints.Gearset(
            context.Capability.RecipeId,
            jobId,
            stats,
            context.GameVersion,
            context.PlannerFingerprint,
            policy,
            resolved.Number == 0 || Dalamud.UnlockState.IsRecipeUnlocked(resolved),
            FcCapabilityFingerprints.CraftSettingsFingerprint(settings));
        var settingsFingerprint = FcCapabilityFingerprints.CraftSettingsFingerprint(settings);
        var solverFingerprint = FcCapabilityFingerprints.Solver(
            context.Capability.RecipeId,
            policy,
            settingsFingerprint);
        return [new FcCapabilityCandidate(
            jobId,
            canCraft,
            guarantees,
            MapOutcome(assessment.Outcome),
            assessment.StepCount,
            0,
            gearFingerprint,
            solverFingerprint,
            assessment.Summary)];
    }

    private static FcRaphaelAssessmentOutcome MapOutcome(RaphaelAssessmentOutcome outcome)
        => Enum.TryParse<FcRaphaelAssessmentOutcome>(outcome.ToString(), out var mapped)
            ? mapped
            : FcRaphaelAssessmentOutcome.None;
}

public static class FcCapabilityFingerprints
{
    public static string Gearset(
        uint recipeId,
        uint jobId,
        GameStateBuilder.PlayerStats stats,
        string gameVersion,
        string plannerFingerprint,
        FcQualityPolicy policy,
        bool recipeUnlocked = false,
        string consumableFingerprint = "")
        => Hash(new FcCapabilityFingerprintInput(
            "gearset-v1",
            recipeId,
            jobId,
            stats.Level,
            stats.Craftsmanship,
            stats.Control,
            stats.CP,
            stats.Manipulation,
            stats.Specialist,
            stats.SplendorCosmic,
            stats.CrafterDelineations,
            gameVersion,
            FcPublishedListMapper.CurrentPlannerSemanticsVersion,
            plannerFingerprint,
            $"job:{jobId};level:{stats.Level};craftsmanship:{stats.Craftsmanship};control:{stats.Control};cp:{stats.CP};manipulation:{stats.Manipulation};specialist:{stats.Specialist};splendor:{stats.SplendorCosmic};delineations:{stats.CrafterDelineations};recipe-unlocked:{recipeUnlocked};consumables:{consumableFingerprint}",
            string.Empty,
            policy));

    public static string Solver(
        uint recipeId,
        FcQualityPolicy policy,
        string settingsFingerprint = "")
    {
        var solverConfig = global::GatherBuddy.GatherBuddy.Config?.RaphaelSolverConfig;
        var solverSettings = new FcSolverFingerprintInput(
            "raphael-solver-settings-v1",
            solverConfig?.SolverMode,
            solverConfig?.RaphaelEnabled,
            solverConfig?.RaphaelTimeoutMinutes,
            solverConfig?.RaphaelInitialOptimizationSeconds,
            solverConfig?.RaphaelAllowSpecialistActions,
            solverConfig?.DonatelloMinimizeSteps,
            solverConfig?.DonatelloExperimentalProgressPriority,
            solverConfig?.DonatelloOptimizationThresholdMs,
            solverConfig?.DonatelloImprovementQuietSeconds,
            solverConfig?.GabrielWorkerThreads,
            settingsFingerprint);

        return Hash(new FcCapabilityFingerprintInput(
            "solver-v1",
            recipeId,
            0,
            0,
            0,
            0,
            0,
            false,
            false,
            false,
            0,
            "",
            FcPublishedListMapper.CurrentPlannerSemanticsVersion,
            FcCanonical.Serialize(solverSettings),
            string.Empty,
            "raphael-solver-settings-v1",
            policy));
    }

    public static string Planner(int semanticsVersion, string gameVersion)
        => Hash(new FcCapabilityFingerprintInput(
            "planner-v1",
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            false,
            false,
            0,
            gameVersion,
            semanticsVersion,
            "published-final-precraft-quality-policy",
            string.Empty,
            string.Empty,
            FcQualityPolicy.Empty));

    public static string Quality(FcQualityPolicy policy)
        => Hash(policy ?? FcQualityPolicy.Empty);

    public static string CraftSettingsFingerprint(RecipeCraftSettings settings)
        => string.Join(",", [
            $"food:{settings.FoodMode}:{settings.FoodItemId}:{settings.FoodHQ}",
            $"medicine:{settings.MedicineMode}:{settings.MedicineItemId}:{settings.MedicineHQ}",
            $"manual:{settings.ManualMode}:{settings.ManualItemId}",
            $"squadron:{settings.SquadronManualMode}:{settings.SquadronManualItemId}",
            $"nq:{settings.UseAllNQ}",
            $"macro:{settings.MacroMode}:{settings.SelectedMacroId}",
            $"solver:{settings.SolverOverride}",
            $"maximize:{settings.MaximizeQualityAtCostOfTime}",
            $"quiet:{settings.DonatelloImprovementQuietSecondsOverride}",
            $"specialist:{settings.SpecialistActionOverride}",
            $"donatello:{settings.DonatelloOptions?.Objective}:{settings.DonatelloOptions?.MinimizeSteps}:{settings.DonatelloOptions?.MaxStellarSteadyHandUses}:{settings.DonatelloOptions?.MaximizeQualityAtCostOfTime}:{settings.DonatelloOptions?.AllowSpecialistActions}:{settings.DonatelloOptions?.ReplanDeadlineMillis}:{settings.DonatelloOptions?.ImprovementQuietPeriodMillis}",
            "ingredients:" + string.Join(";", settings.IngredientPreferences
                .OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key}={pair.Value}")),
        ]);

    private static string Hash<T>(T value)
        => Convert.ToHexString(SHA256.HashData(FcCanonical.SerializeUtf8(value))).ToLowerInvariant();
}
