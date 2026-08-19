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
    uint StellarSteadyHandCharges = 0
)
{
    public static int CanonicalizeCrafterDelineations(bool specialist, int delineations)
        => specialist ? Math.Clamp(delineations, 0, 2) : 0;

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
            CrafterDelineations: CanonicalizeCrafterDelineations(
                allowSpecialistActions && craft.Specialist,
                craft.CrafterDelineations),
            SplendorCosmic: craft.SplendorCosmic,
            Objective: options?.Objective ?? DonatelloSolveObjective.MaximizeQuality,
            MinimizeSteps: options?.MinimizeSteps ?? GatherBuddy.Config.RaphaelSolverConfig.DonatelloMinimizeSteps,
            StellarSteadyHandCharges: stellarCharges
        );
    }

    public string GetKey()
    {
        var delineations = CanonicalizeCrafterDelineations(Specialist, CrafterDelineations);
        var key = $"{RecipeId}/{Level}/{Craftsmanship}/{Control}/{CP}/{(Manipulation ? "1" : "0")}/{(Specialist ? "1" : "0")}/{InitialQuality}/{delineations}/{(SplendorCosmic ? "1" : "0")}/{(int)Objective}/{(MinimizeSteps ? "1" : "0")}/{StellarSteadyHandCharges}";
        return string.IsNullOrEmpty(ValidationContext) ? key : $"{key}/{ValidationContext}";
    }
}

public class CachedRaphaelSolution
{
    public int CacheVersion { get; set; }
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
        CacheVersion = RaphaelSolveCoordinator.SolutionCacheVersion;
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
    Gabriel,         // Per-item-only stochastic Expert solver
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
    public bool DonatelloExperimentalProgressPriority { get; set; } = false;
    public int DonatelloOptimizationThresholdMs { get; set; } = DonatelloSolver.DefaultLiveReplanDeadlineMillis;
    public int DonatelloImprovementQuietSeconds { get; set; } = DonatelloSolver.DefaultImprovementQuietPeriodSeconds;
    public int DonatelloCacheMemoryMiB { get; set; } = 512;
    public int GabrielWorkerThreads { get; set; } = DonatelloNative.DefaultGabrielWorkerThreads;
    public bool AutoClearSolutionCache { get; set; } = true;
    public int SolutionCacheMaxAgeDays { get; set; } = 30;
    public VulcanSolverMode SolverMode { get; set; } = VulcanSolverMode.Donatello;
}
