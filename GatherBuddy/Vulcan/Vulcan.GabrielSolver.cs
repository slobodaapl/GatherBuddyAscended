using System;
using System.Collections.Generic;
using GatherBuddy.Crafting;

namespace GatherBuddy.Vulcan;

public sealed class GabrielSolverDefinition : ISolverDefinition
{
    private readonly ulong? _policySeed;

    internal ulong? PolicySeed => _policySeed;

    public GabrielSolverDefinition()
    {
    }

    internal GabrielSolverDefinition(ulong policySeed)
    {
        _policySeed = policySeed;
    }

    public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
    {
        if (!GabrielPolicyCatalog.TryResolve(craft, out var policy, out var reason))
        {
            yield return new(this, 0, 200, "Gabriel", reason);
            yield break;
        }
        yield return new(this, (int)policy.Profile, 200, "Gabriel");
    }

    public Solver Create(CraftState craft, int flavor)
    {
        if (!GabrielPolicyCatalog.TryResolve(craft, out var policy, out var reason))
            throw new InvalidOperationException(reason);
        if (flavor != (int)policy.Profile)
            throw new InvalidOperationException($"Gabriel flavor {flavor} does not match {policy.Profile}");
        return _policySeed.HasValue
            ? new GabrielSolver(craft, _policySeed.Value)
            : new GabrielSolver(craft);
    }

    internal static bool TryCreateLiveSolver(
        CraftState craft,
        ulong? policySeed,
        out GabrielSolver? solver,
        out string failureReason)
    {
        if (!GabrielPolicyCatalog.TryResolve(craft, out _, out failureReason))
        {
            solver = null;
            return false;
        }
        solver = policySeed.HasValue
            ? new GabrielSolver(craft, policySeed.Value)
            : new GabrielSolver(craft);
        failureReason = string.Empty;
        return true;
    }
}

public sealed class GabrielSolver : Solver
{
    private readonly ulong _policySeed;
    private StepState? _lastSolvedRoot;
    private bool _hasRecommended;
    private int _decisions;

    public int NativeRecommendationCount { get; private set; }

    public GabrielSolver(CraftState craft)
        : this(craft, CreatePolicySeed(craft))
    {
    }

    internal GabrielSolver(CraftState craft, ulong policySeed)
    {
        if (!GabrielPolicyCatalog.TryResolve(craft, out _, out var reason))
            throw new InvalidOperationException(reason);
        _policySeed = policySeed;
    }

    public override Recommendation Solve(CraftState craft, StepState step)
    {
        if (!GabrielPolicyCatalog.TryResolve(craft, out var policy, out var reason))
            return new(VulcanSkill.None, reason, IsTerminalFailure: true);
        if (_lastSolvedRoot != null && !Equivalent(_lastSolvedRoot, step) && _hasRecommended)
            _decisions++;
        _lastSolvedRoot = step with { };

        try
        {
            var seed = MixSeed(_policySeed, _decisions);
            var recommendation = DonatelloNative.RecommendGabriel(craft, step, _decisions, seed);
            NativeRecommendationCount++;
            _hasRecommended = true;
            return new(
                recommendation.Action,
                $"Gabriel: {(recommendation.FailureClosure ? "failed-attempt closure" : recommendation.Planned ? $"{recommendation.CandidateCount} candidates, {recommendation.RolloutCount} rollouts" : "actor policy")} ({recommendation.ElapsedMillis} ms)");
        }
        catch (Exception exception)
        {
            return new(
                VulcanSkill.None,
                $"Gabriel could not produce a usable action: {exception.Message}",
                IsTerminalFailure: true);
        }
    }

    private static ulong CreatePolicySeed(CraftState craft)
    {
        var random = (ulong)Random.Shared.NextInt64();
        return random
            ^ ((ulong)craft.RecipeId << 32)
            ^ (uint)craft.StatCraftsmanship
            ^ ((ulong)(uint)craft.StatControl << 16)
            ^ (uint)craft.StatCP;
    }

    private static ulong MixSeed(ulong seed, int decision)
    {
        var value = seed + ((ulong)(uint)decision + 1) * 0x9E37_79B9_7F4A_7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return value ^ (value >> 31);
    }

    private static bool Equivalent(StepState left, StepState right)
        => left.Index == right.Index
            && left.Progress == right.Progress
            && left.Quality == right.Quality
            && left.Durability == right.Durability
            && left.RemainingCP == right.RemainingCP
            && left.Condition == right.Condition
            && left.IQStacks == right.IQStacks
            && left.WasteNotLeft == right.WasteNotLeft
            && left.ManipulationLeft == right.ManipulationLeft
            && left.GreatStridesLeft == right.GreatStridesLeft
            && left.InnovationLeft == right.InnovationLeft
            && left.VenerationLeft == right.VenerationLeft
            && left.MuscleMemoryLeft == right.MuscleMemoryLeft
            && left.FinalAppraisalLeft == right.FinalAppraisalLeft
            && left.CarefulObservationLeft == right.CarefulObservationLeft
            && left.CrafterDelineationsLeft == right.CrafterDelineationsLeft
            && left.HeartAndSoulActive == right.HeartAndSoulActive
            && left.HeartAndSoulAvailable == right.HeartAndSoulAvailable
            && left.PrevActionFailed == right.PrevActionFailed
            && left.ExpedienceLeft == right.ExpedienceLeft
            && left.QuickInnoLeft == right.QuickInnoLeft
            && left.QuickInnoAvailable == right.QuickInnoAvailable
            && left.TrainedPerfectionAvailable == right.TrainedPerfectionAvailable
            && left.TrainedPerfectionActive == right.TrainedPerfectionActive
            && left.ComboAction == right.ComboAction
            && left.PrevComboAction == right.PrevComboAction
            && left.MaterialMiracleCharges == right.MaterialMiracleCharges
            && left.StellarSteadyHandCharges == right.StellarSteadyHandCharges
            && left.StellarSteadyHandLeft == right.StellarSteadyHandLeft
            && left.StellarSteadyHandsUsed == right.StellarSteadyHandsUsed
            && left.ObserveCounter == right.ObserveCounter;
}
