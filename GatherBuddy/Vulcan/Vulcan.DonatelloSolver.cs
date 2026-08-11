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
        return _coordinator.TryGetSolution(request, out var solution) && solution != null
            ? new DonatelloSolver(solution, craft)
            : null!;
    }

    private static string UnsupportedReason(CraftState craft)
    {
        if (craft.IsCosmic || craft.SplendorCosmic || craft.MissionHasMaterialMiracle)
            return "Donatello does not yet model Cosmic/Material Miracle mechanics";
        var supported = ConditionFlags.Normal | ConditionFlags.Good | ConditionFlags.Excellent
            | ConditionFlags.Poor | ConditionFlags.Centered | ConditionFlags.Sturdy
            | ConditionFlags.Pliant | ConditionFlags.Malleable | ConditionFlags.Primed
            | ConditionFlags.GoodOmen;
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
    private string? _handledRoot;
    private IntPtr _pendingInterrupt;
    private DateTime _pendingStartedAt;
    private bool _interruptRequested;

    public DonatelloSolver(CachedRaphaelSolution initialSolution, CraftState craft)
    {
        _craft = craft ?? throw new ArgumentNullException(nameof(craft));
        _plan = initialSolution?.ActionIds.ConvertAll(id => (VulcanSkill)id)
            ?? throw new ArgumentNullException(nameof(initialSolution));
    }

    public override Recommendation Solve(CraftState craft, StepState step)
    {
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
        }

        var rootKey = Fingerprint(step);
        if (RequiresReplan(_expectedState, step) && rootKey != _handledRoot)
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

        var action = _plan[_actionIndex++];
        var (result, expected) = Simulator.Execute(_craft, step, action, 0, 1);
        _expectedState = result == Simulator.ExecuteResult.CantUse ? null : expected;
        return new(action, $"Donatello step {_actionIndex}/{_plan.Count}");
    }

    private void StartReplan(StepState root, string rootKey)
    {
        var liveRoot = root with { };
        _pendingRoot = liveRoot;
        _pendingIncumbent = _plan.Skip(_actionIndex).ToList();
        _handledRoot = rootKey;
        _pendingStartedAt = DateTime.UtcNow;
        _interruptRequested = false;
        _pendingInterrupt = DonatelloNative.CreateInterrupt();
        var interrupt = _pendingInterrupt;
        _pendingSolve = Task.Run(() =>
        {
            try
            {
                return DonatelloNative.Solve(_craft, liveRoot, interrupt);
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
            if (candidateScore.Completes && candidateScore.IsStrictlyBetterThan(incumbentScore))
            {
                _plan = candidate;
                GatherBuddy.Log.Debug($"[Donatello] Adopted strict improvement: quality={candidateScore.Quality}, steps={candidateScore.Steps}");
            }
            else
            {
                _plan = incumbent;
                GatherBuddy.Log.Debug("[Donatello] Retained incumbent; candidate did not prove a strict improvement");
            }
        }
        catch (Exception ex)
        {
            _plan = incumbent;
            GatherBuddy.Log.Warning($"[Donatello] Replan failed; retaining incumbent: {ex.Message}");
        }
        _actionIndex = 0;
        _expectedState = null;
        _pendingSolve = null;
        _pendingRoot = null;
        _pendingIncumbent = null;
        _pendingInterrupt = IntPtr.Zero;
        _interruptRequested = false;
    }

    private static bool CanRepresentLiveRoot(StepState step, out string reason)
    {
        if (step.Condition < Condition.Normal || step.Condition > Condition.GoodOmen)
        {
            reason = "unknown crafting condition";
            return false;
        }
        if (step.MaterialMiracleActive || step.MaterialMiracleCharges > 0)
        {
            reason = "Material Miracle state is not represented by the native solver";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static bool Equivalent(StepState expected, StepState actual)
        => expected.Index == actual.Index
            && expected.Progress == actual.Progress
            && expected.Quality == actual.Quality
            && expected.Durability == actual.Durability
            && expected.RemainingCP == actual.RemainingCP
            && expected.Condition == actual.Condition
            && expected.IQStacks == actual.IQStacks
            && expected.WasteNotLeft == actual.WasteNotLeft
            && expected.ManipulationLeft == actual.ManipulationLeft
            && expected.GreatStridesLeft == actual.GreatStridesLeft
            && expected.InnovationLeft == actual.InnovationLeft
            && expected.VenerationLeft == actual.VenerationLeft
            && expected.MuscleMemoryLeft == actual.MuscleMemoryLeft
            && expected.FinalAppraisalLeft == actual.FinalAppraisalLeft
            && expected.CarefulObservationLeft == actual.CarefulObservationLeft
            && expected.HeartAndSoulActive == actual.HeartAndSoulActive
            && expected.HeartAndSoulAvailable == actual.HeartAndSoulAvailable
            && expected.ExpedienceLeft == actual.ExpedienceLeft
            && expected.QuickInnoLeft == actual.QuickInnoLeft
            && expected.QuickInnoAvailable == actual.QuickInnoAvailable
            && expected.TrainedPerfectionAvailable == actual.TrainedPerfectionAvailable
            && expected.TrainedPerfectionActive == actual.TrainedPerfectionActive
            && expected.PrevComboAction == actual.PrevComboAction
            && expected.MaterialMiracleCharges == actual.MaterialMiracleCharges
            && expected.MaterialMiracleActive == actual.MaterialMiracleActive;

    internal static bool RequiresReplan(StepState? expected, StepState actual)
        => expected == null ? actual.Condition != Condition.Normal : !Equivalent(expected, actual);

    private static string Fingerprint(StepState step)
        => $"{step.Index}/{step.Progress}/{step.Quality}/{step.Durability}/{step.RemainingCP}/{(int)step.Condition}/"
            + $"{step.IQStacks}/{step.WasteNotLeft}/{step.ManipulationLeft}/{step.GreatStridesLeft}/"
            + $"{step.InnovationLeft}/{step.VenerationLeft}/{step.MuscleMemoryLeft}/{step.FinalAppraisalLeft}/"
            + $"{step.CarefulObservationLeft}/{(int)step.PrevComboAction}/{step.HeartAndSoulActive}/"
            + $"{step.HeartAndSoulAvailable}/{step.ExpedienceLeft}/{step.QuickInnoLeft}/"
            + $"{step.QuickInnoAvailable}/{step.TrainedPerfectionActive}/{step.TrainedPerfectionAvailable}/"
            + $"{step.MaterialMiracleCharges}/{step.MaterialMiracleActive}";

    public override Solver Clone()
        => new DonatelloSolver(
            new CachedRaphaelSolution { ActionIds = _plan.Select(action => (uint)action).ToList() },
            _craft);
}
