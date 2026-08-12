using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GatherBuddy.Crafting;

namespace GatherBuddy.Vulcan;

public sealed class DonatelloSolverDefinition : ISolverDefinition
{
    private readonly RaphaelSolveCoordinator _coordinator;

    public DonatelloSolverDefinition(RaphaelSolveCoordinator coordinator)
        => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
    {
        var unsupported = UnsupportedReason(craft);
        if (unsupported.Length > 0)
        {
            yield return new(this, 0, 100, "Donatello", unsupported);
            yield break;
        }
        var request = RaphaelSolveRequest.FromCraftState(
            craft,
            GatherBuddy.Config.RaphaelSolverConfig.RaphaelAllowSpecialistActions);
        if (_coordinator.TryGetSolution(request, out _))
            yield return new(this, 0, 100, "Donatello (Ready)");
        else if (_coordinator.IsSolveInProgress(request))
            yield return new(this, 0, 50, "Donatello (Generating...)", "Initial Raphael solution is still being generated");
        else
            yield return new(this, 0, 25, "Donatello (Not Ready)", "Initial Raphael solution is unavailable");
    }

    public Solver Create(CraftState craft, int flavor)
    {
        var request = RaphaelSolveRequest.FromCraftState(
            craft,
            GatherBuddy.Config.RaphaelSolverConfig.RaphaelAllowSpecialistActions);
        if (!_coordinator.TryGetSolution(request, out var solution) || solution == null)
            return null!;
        var actions = solution.ActionIds.ConvertAll(id => (VulcanSkill)id);
        var initial = GameStateBuilder.BuildInitialStepState(craft, craft.InitialQuality);
        var evaluation = DonatelloPlanEvaluator.Evaluate(craft, initial, actions);
        if (ShouldUseStaticPlan(craft, evaluation))
        {
            GatherBuddy.Log.Debug("[Donatello] Initial Raphael plan already reaches maximum quality; using static plan");
            return new RaphaelMacroSolver(solution, craft);
        }
        return new DonatelloSolver(solution, craft);
    }

    internal static bool ShouldUseStaticPlan(CraftState craft, DonatelloPlanEvaluation evaluation)
        => evaluation.Completes && evaluation.Quality >= craft.CraftQualityMax;

    private static string UnsupportedReason(CraftState craft)
    {
        var supported = ConditionFlags.Normal | ConditionFlags.Good | ConditionFlags.Excellent
            | ConditionFlags.Poor | ConditionFlags.Centered | ConditionFlags.Sturdy
            | ConditionFlags.Pliant | ConditionFlags.Malleable | ConditionFlags.Primed
            | ConditionFlags.GoodOmen | ConditionFlags.Robust;
        return (craft.ConditionFlags & ~supported) != 0
            ? "Recipe exposes unsupported crafting conditions"
            : string.Empty;
    }
}

public sealed class DonatelloSolver : Solver
{
    private readonly CraftState _craft;
    private List<VulcanSkill> _plan;
    private int _actionIndex;
    private StepState? _expectedState;
    private Task<IReadOnlyList<VulcanSkill>>? _pendingSolve;
    private StepState? _pendingRoot;
    private List<VulcanSkill>? _pendingIncumbent;
    private StepState? _resumeRootAfterPending;
    private string? _handledRoot;
    private IntPtr _pendingInterrupt;
    private DateTime _pendingStartedAt;
    private bool _interruptRequested;
    private bool _progressFallback;
    private readonly ProgressOnlySolver _progressOnlySolver = new();

    public DonatelloSolver(CachedRaphaelSolution initialSolution, CraftState craft)
    {
        _craft = craft ?? throw new ArgumentNullException(nameof(craft));
        _plan = initialSolution?.ActionIds.ConvertAll(id => (VulcanSkill)id)
            ?? throw new ArgumentNullException(nameof(initialSolution));
    }

    public override Recommendation Solve(CraftState craft, StepState step)
    {
        if (_progressFallback)
            return _progressOnlySolver.Solve(craft, step) with { Comment = "Donatello completion fallback" };

        if (_pendingSolve == null && ShouldUseMaterialMiracle(_craft, step))
        {
            var (miracleResult, miracleExpected) = Simulator.Execute(_craft, step, VulcanSkill.MaterialMiracle, 0, 1);
            if (miracleResult != Simulator.ExecuteResult.CantUse)
            {
                _expectedState = miracleExpected;
                _handledRoot = null;
                return new(VulcanSkill.MaterialMiracle, "Donatello Material Miracle policy");
            }
        }

        if (_pendingSolve != null)
        {
            if (!_pendingSolve.IsCompleted)
            {
                var timeout = TimeSpan.FromMinutes(
                    GatherBuddy.Config.RaphaelSolverConfig.RaphaelTimeoutMinutes);
                if (!_interruptRequested && DateTime.UtcNow - _pendingStartedAt >= timeout)
                {
                    _interruptRequested = true;
                    DonatelloNative.Interrupt(_pendingInterrupt);
                    GatherBuddy.Log.Warning("[Donatello] Replan timed out; interrupting native search and retaining incumbent");
                }
                return new(VulcanSkill.None, "Donatello re-optimizing remaining craft");
            }
            CompleteReplan();
            if (_resumeRootAfterPending != null)
            {
                var resumeRoot = _resumeRootAfterPending;
                _resumeRootAfterPending = null;
                return StartResumeReplan(resumeRoot);
            }
        }

        var rootKey = Fingerprint(step);
        if (RequiresReplan(_craft, _expectedState, step) && rootKey != _handledRoot)
        {
            if (CanRepresentLiveRoot(step, out var unsupportedReason))
            {
                StartReplan(step, rootKey);
                return new(VulcanSkill.None, "Donatello re-optimizing remaining craft");
            }
            _handledRoot = rootKey;
            GatherBuddy.Log.Warning($"[Donatello] Replan skipped; retaining incumbent: {unsupportedReason}");
        }

        if (_actionIndex >= _plan.Count)
            return new(VulcanSkill.None, "Donatello plan complete");

        var action = _plan[_actionIndex];
        var (result, expected) = Simulator.Execute(_craft, step, action, 0, 1);
        if (result == Simulator.ExecuteResult.CantUse)
        {
            _progressFallback = true;
            GatherBuddy.Log.Error($"[Donatello] Refused unusable planned action {action}; switching to completion fallback");
            return _progressOnlySolver.Solve(craft, step) with { Comment = "Donatello completion fallback" };
        }
        _actionIndex++;
        _expectedState = expected;
        return new(action, $"Donatello step {_actionIndex}/{_plan.Count}");
    }

    internal Recommendation ResumeFromLiveState(StepState step)
    {
        if (_pendingSolve != null)
        {
            _resumeRootAfterPending = step with { };
            if (!_interruptRequested)
            {
                _interruptRequested = true;
                DonatelloNative.Interrupt(_pendingInterrupt);
            }
            return new(VulcanSkill.None, "Donatello re-optimizing remaining craft");
        }

        return StartResumeReplan(step);
    }

    private Recommendation StartResumeReplan(StepState step)
    {
        var rootKey = Fingerprint(step);
        if (!CanRepresentLiveRoot(step, out var unsupportedReason))
        {
            ActivateCompletionFallback($"cannot resume from the live state: {unsupportedReason}");
            return _progressOnlySolver.Solve(_craft, step) with { Comment = "Donatello completion fallback" };
        }

        _progressFallback = false;
        StartReplan(step, rootKey);
        return new(VulcanSkill.None, "Donatello re-optimizing resumed craft from live state");
    }

    private void StartReplan(StepState root, string rootKey)
    {
        var liveRoot = root with { };
        var incumbent = _plan.Skip(_actionIndex).ToList();
        _pendingRoot = liveRoot;
        _pendingIncumbent = incumbent;
        _handledRoot = rootKey;
        _pendingStartedAt = DateTime.UtcNow;
        _interruptRequested = false;
        _pendingInterrupt = DonatelloNative.CreateInterrupt();
        var interrupt = _pendingInterrupt;
        _pendingSolve = Task.Run(() =>
        {
            try
            {
                return DonatelloNative.Solve(_craft, liveRoot, interrupt, incumbent);
            }
            finally
            {
                DonatelloNative.FreeInterrupt(interrupt);
            }
        });
    }

    private void CompleteReplan()
    {
        var root = _pendingRoot!;
        var incumbent = _pendingIncumbent!;
        try
        {
            var candidate = _pendingSolve!.GetAwaiter().GetResult().ToList();
            var incumbentScore = DonatelloPlanEvaluator.Evaluate(_craft, root, incumbent);
            var candidateScore = DonatelloPlanEvaluator.Evaluate(_craft, root, candidate);
            var minimizeSteps = _craft.DonatelloOptions?.MinimizeSteps
                ?? GatherBuddy.Config.RaphaelSolverConfig.DonatelloMinimizeSteps;
            if (candidateScore.Completes && candidateScore.IsStrictlyBetterThan(incumbentScore, minimizeSteps))
            {
                _plan = candidate;
                GatherBuddy.Log.Debug($"[Donatello] Adopted strict improvement: quality={candidateScore.Quality}, steps={candidateScore.Steps}");
            }
            else if (incumbentScore.Completes)
            {
                _plan = incumbent;
                GatherBuddy.Log.Debug("[Donatello] Retained incumbent; candidate did not prove a strict improvement");
            }
            else
            {
                ActivateCompletionFallback("neither native candidate nor incumbent completes from the live state");
            }
        }
        catch (Exception ex)
        {
            if (DonatelloPlanEvaluator.Evaluate(_craft, root, incumbent).Completes)
            {
                _plan = incumbent;
                GatherBuddy.Log.Warning($"[Donatello] Replan failed; retaining validated incumbent: {ex.Message}");
            }
            else
            {
                ActivateCompletionFallback($"native replan failed and incumbent does not complete: {ex.Message}");
            }
        }
        _actionIndex = 0;
        _expectedState = null;
        _pendingSolve = null;
        _pendingRoot = null;
        _pendingIncumbent = null;
        _pendingInterrupt = IntPtr.Zero;
        _interruptRequested = false;
    }

    private void ActivateCompletionFallback(string reason)
    {
        _plan = [];
        _progressFallback = true;
        GatherBuddy.Log.Error($"[Donatello] {reason}; switching to completion fallback");
    }

    private static bool CanRepresentLiveRoot(StepState step, out string reason)
    {
        if (step.Condition < Condition.Normal || step.Condition > Condition.Robust)
        {
            reason = "unknown crafting condition";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    internal static bool ShouldUseMaterialMiracle(CraftState craft, StepState step)
    {
        var options = craft.DonatelloOptions;
        return options is { MaxMaterialMiracleUses: > 0 }
            && step.MaterialMiraclesUsed < options.MaxMaterialMiracleUses
            && step.Index >= options.MinimumStepsBeforeMaterialMiracle
            && !step.MaterialMiracleActive
            && Simulator.CanUseAction(craft, step, VulcanSkill.MaterialMiracle);
    }

    private static bool Equivalent(StepState expected, StepState actual, bool ignoreQualityAndCondition)
        => expected.Index == actual.Index
            && expected.Progress == actual.Progress
            && (ignoreQualityAndCondition || expected.Quality == actual.Quality)
            && expected.Durability == actual.Durability
            && expected.RemainingCP == actual.RemainingCP
            && (ignoreQualityAndCondition || expected.Condition == actual.Condition)
            && expected.IQStacks == actual.IQStacks
            && expected.WasteNotLeft == actual.WasteNotLeft
            && expected.ManipulationLeft == actual.ManipulationLeft
            && expected.GreatStridesLeft == actual.GreatStridesLeft
            && expected.InnovationLeft == actual.InnovationLeft
            && expected.VenerationLeft == actual.VenerationLeft
            && expected.MuscleMemoryLeft == actual.MuscleMemoryLeft
            && expected.FinalAppraisalLeft == actual.FinalAppraisalLeft
            && expected.CarefulObservationLeft == actual.CarefulObservationLeft
            && expected.CrafterDelineationsLeft == actual.CrafterDelineationsLeft
            && expected.HeartAndSoulActive == actual.HeartAndSoulActive
            && expected.HeartAndSoulAvailable == actual.HeartAndSoulAvailable
            && expected.ExpedienceLeft == actual.ExpedienceLeft
            && expected.QuickInnoLeft == actual.QuickInnoLeft
            && expected.QuickInnoAvailable == actual.QuickInnoAvailable
            && expected.TrainedPerfectionAvailable == actual.TrainedPerfectionAvailable
            && expected.TrainedPerfectionActive == actual.TrainedPerfectionActive
            && expected.PrevComboAction == actual.PrevComboAction
            && expected.MaterialMiracleCharges == actual.MaterialMiracleCharges
            && expected.MaterialMiracleActive == actual.MaterialMiracleActive
            && expected.MaterialMiraclesUsed == actual.MaterialMiraclesUsed
            && expected.StellarSteadyHandCharges == actual.StellarSteadyHandCharges
            && expected.StellarSteadyHandLeft == actual.StellarSteadyHandLeft
            && expected.StellarSteadyHandsUsed == actual.StellarSteadyHandsUsed;

    internal static bool RequiresReplan(CraftState craft, StepState? expected, StepState actual)
    {
        var fullQualityNormalCraft = !craft.CraftExpert && actual.Quality >= craft.CraftQualityMax;
        return expected == null
            ? !fullQualityNormalCraft && actual.Condition != Condition.Normal
            : !Equivalent(expected, actual, fullQualityNormalCraft);
    }

    private static string Fingerprint(StepState step)
        => $"{step.Index}/{step.Progress}/{step.Quality}/{step.Durability}/{step.RemainingCP}/{(int)step.Condition}/"
            + $"{step.IQStacks}/{step.WasteNotLeft}/{step.ManipulationLeft}/{step.GreatStridesLeft}/"
            + $"{step.InnovationLeft}/{step.VenerationLeft}/{step.MuscleMemoryLeft}/{step.FinalAppraisalLeft}/"
            + $"{step.CarefulObservationLeft}/{step.CrafterDelineationsLeft}/{(int)step.PrevComboAction}/{step.HeartAndSoulActive}/"
            + $"{step.HeartAndSoulAvailable}/{step.ExpedienceLeft}/{step.QuickInnoLeft}/"
            + $"{step.QuickInnoAvailable}/{step.TrainedPerfectionActive}/{step.TrainedPerfectionAvailable}/"
            + $"{step.MaterialMiracleCharges}/{step.MaterialMiracleActive}/{step.MaterialMiraclesUsed}/"
            + $"{step.StellarSteadyHandCharges}/{step.StellarSteadyHandLeft}/{step.StellarSteadyHandsUsed}";

    public override Solver Clone()
        => new DonatelloSolver(
            new CachedRaphaelSolution { ActionIds = _plan.Select(action => (uint)action).ToList() },
            _craft);
}
