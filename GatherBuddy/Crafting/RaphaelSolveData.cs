using System;
using System.Collections.Generic;
using GatherBuddy.Vulcan;

namespace GatherBuddy.Crafting;

public record RaphaelSolveRequest(
    uint RecipeId,
    int Level,
    int Craftsmanship,
    int Control,
    int CP,
    bool Manipulation,
    bool Specialist,
    int InitialQuality = 0,
    string? ValidationContext = null,
    int CrafterDelineations = 0,
    bool SplendorCosmic = false,
    DonatelloSolveObjective Objective = DonatelloSolveObjective.MaximizeQuality,
    bool MinimizeSteps = false,
    uint StellarSteadyHandCharges = 0,
    uint MaxMaterialMiracleUses = 0,
    uint MinimumStepsBeforeMaterialMiracle = 0
)
{
    public static RaphaelSolveRequest FromCraftState(CraftState craft, bool allowSpecialistActions, string? validationContext = null)
    {
        var options = craft.DonatelloOptions;
        var stellarCharges = options == null
            ? 0u
            : Math.Min(craft.CurrentStellarSteadyHandCharges, options.MaxStellarSteadyHandUses);
        return new(
            RecipeId: craft.RecipeId,
            Level: craft.StatLevel,
            Craftsmanship: craft.StatCraftsmanship,
            Control: craft.StatControl,
            CP: craft.StatCP,
            Manipulation: craft.UnlockedManipulation,
            Specialist: allowSpecialistActions && craft.Specialist,
            InitialQuality: craft.InitialQuality,
            ValidationContext: validationContext,
            CrafterDelineations: allowSpecialistActions && craft.Specialist ? craft.CrafterDelineations : 0,
            SplendorCosmic: craft.SplendorCosmic,
            Objective: options?.Objective ?? DonatelloSolveObjective.MaximizeQuality,
            MinimizeSteps: options?.MinimizeSteps ?? GatherBuddy.Config.RaphaelSolverConfig.DonatelloMinimizeSteps,
            StellarSteadyHandCharges: stellarCharges,
            MaxMaterialMiracleUses: options?.MaxMaterialMiracleUses ?? 0,
            MinimumStepsBeforeMaterialMiracle: options?.MinimumStepsBeforeMaterialMiracle ?? 0
        );
    }

    public string GetKey()
    {
        var key = $"{RecipeId}/{Level}/{Craftsmanship}/{Control}/{CP}/{(Manipulation ? "1" : "0")}/{(Specialist ? "1" : "0")}/{InitialQuality}/{CrafterDelineations}/{(SplendorCosmic ? "1" : "0")}/{(int)Objective}/{(MinimizeSteps ? "1" : "0")}/{StellarSteadyHandCharges}/{MaxMaterialMiracleUses}/{MinimumStepsBeforeMaterialMiracle}";
        return string.IsNullOrEmpty(ValidationContext) ? key : $"{key}/{ValidationContext}";
    }
}

public class CachedRaphaelSolution
{
    public string Key { get; set; } = string.Empty;
    public RaphaelSolveRequest Request { get; set; } = null!;
    public List<uint> ActionIds { get; set; } = new();
    public DateTime GeneratedAt { get; set; }
    public bool IsFailed { get; set; }
    public string? FailureReason { get; set; }
    public bool Optimal { get; set; } = true;
    public bool OptimizationDeadlineReached { get; set; }
    public int AchievedQuality { get; set; }
    public int QualityUpperBound { get; set; }
    public long SolveElapsedMillis { get; set; }

    public CachedRaphaelSolution() { }

    public CachedRaphaelSolution(string key, RaphaelSolveRequest request)
    {
        Key = key;
        Request = request;
        GeneratedAt = DateTime.UtcNow;
    }
}

public enum RaphaelSolvePriority
{
    Background,
    Urgent,
}

public enum VulcanSolverMode
{
    PureRaphael,     // Static Raphael rotations only
    StandardSolver,  // Dynamic standard solver
    ProgressOnly,    // Progress-only solver (no quality actions)
    Donatello,       // Adaptive globally optimizing solver
}

public class RaphaelSolveCoordinatorConfig
{
    public bool RaphaelEnabled { get; set; } = true;
    // Retained for configuration compatibility. Native solves are intentionally serialized.
    public int MaxConcurrentRaphaelProcesses { get; set; } = 1;
    public int RaphaelTimeoutMinutes { get; set; } = 5;
    public int RaphaelInitialOptimizationSeconds { get; set; } = 30;
    public bool RaphaelAllowSpecialistActions { get; set; } = false;
    public bool DonatelloMinimizeSteps { get; set; } = false;
    public int DonatelloOptimizationThresholdMs { get; set; } = DonatelloSolver.DefaultLiveReplanDeadlineMillis;
    public int DonatelloCacheMemoryMiB { get; set; } = 512;
    public bool AutoClearSolutionCache { get; set; } = true;
    public int SolutionCacheMaxAgeDays { get; set; } = 30;
    public VulcanSolverMode SolverMode { get; set; } = VulcanSolverMode.Donatello;
}
