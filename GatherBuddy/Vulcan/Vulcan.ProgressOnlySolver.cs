using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Vulcan;

public class ProgressOnlySolverDefinition : ISolverDefinition
{
    public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
    {
        if (!craft.CraftExpert && !craft.CraftCollectible)
            yield return new(this, 0, 1, "Progress Only Solver");
    }

    public Solver Create(CraftState craft, int flavor) => new ProgressOnlySolver();
}

/// User-facing completion solver backed by Donatello's exact Complete/Fastest frontier.
/// The legacy greedy policy survives only as an emergency path after synthesis has begun.
public class ProgressOnlySolver : Solver
{
    private List<VulcanSkill> _plan = [];
    private int _actionIndex;
    private StepState? _expectedState;
    private readonly GreedyEmergencySolver _emergency = new();

    public override Recommendation Solve(CraftState craft, StepState step)
    {
        if (_expectedState == null || !Equivalent(_expectedState, step) || _actionIndex >= _plan.Count)
        {
            try
            {
                _plan = DonatelloNative.SolveDetailed(
                    craft,
                    step,
                    GatherBuddy.Config.RaphaelSolverConfig.RaphaelAllowSpecialistActions,
                    DonatelloNative.SolveMode.CompleteFastest,
                    hardDeadlineMillis: 30_000).Actions.ToList();
                _actionIndex = 0;
                _expectedState = step with { };
            }
            catch (Exception ex)
            {
                if (step.Index <= 1)
                {
                    GatherBuddy.Log.Error($"[ProgressOnly] Native completion planning failed before craft start: {ex.Message}");
                    return new(VulcanSkill.None, "Native completion planning failed");
                }
                GatherBuddy.Log.Error($"[ProgressOnly] Native completion planning failed mid-craft; using emergency completion: {ex.Message}");
                return _emergency.Solve(craft, step);
            }
        }

        if (_actionIndex >= _plan.Count)
            return new(VulcanSkill.None, "Native completion plan exhausted");
        var action = _plan[_actionIndex++];
        var (result, expected) = Simulator.Execute(craft, step, action, 0, 1);
        if (result == Simulator.ExecuteResult.CantUse)
        {
            if (step.Index <= 1)
                return new(VulcanSkill.None, $"Native completion returned unusable {action}");
            return _emergency.Solve(craft, step);
        }
        _expectedState = expected;
        return new(action, $"Native completion step {_actionIndex}/{_plan.Count}");
    }

    private static bool Equivalent(StepState left, StepState right)
        => left.Progress == right.Progress
            && left.Quality == right.Quality
            && left.Durability == right.Durability
            && left.RemainingCP == right.RemainingCP
            && left.Condition == right.Condition
            && left.IQStacks == right.IQStacks
            && left.WasteNotLeft == right.WasteNotLeft
            && left.ManipulationLeft == right.ManipulationLeft
            && left.InnovationLeft == right.InnovationLeft
            && left.VenerationLeft == right.VenerationLeft
            && left.GreatStridesLeft == right.GreatStridesLeft
            && left.MuscleMemoryLeft == right.MuscleMemoryLeft
            && left.FinalAppraisalLeft == right.FinalAppraisalLeft
            && left.CrafterDelineationsLeft == right.CrafterDelineationsLeft
            && left.HeartAndSoulActive == right.HeartAndSoulActive
            && left.HeartAndSoulAvailable == right.HeartAndSoulAvailable
            && left.QuickInnoAvailable == right.QuickInnoAvailable
            && left.TrainedPerfectionActive == right.TrainedPerfectionActive
            && left.TrainedPerfectionAvailable == right.TrainedPerfectionAvailable
            && left.StellarSteadyHandCharges == right.StellarSteadyHandCharges
            && left.StellarSteadyHandLeft == right.StellarSteadyHandLeft;

    private sealed class GreedyEmergencySolver : Solver
    {
        public override Recommendation Solve(CraftState craft, StepState step)
        {
            if (Simulator.CanUseAction(craft, step, VulcanSkill.MuscleMemory))
                return new(VulcanSkill.MuscleMemory, "Emergency completion");
            if (step.VenerationLeft == 0 && Simulator.CanUseAction(craft, step, VulcanSkill.Veneration))
                return new(VulcanSkill.Veneration, "Emergency completion");

            var synthesis = BestSynthesis(craft, step);
            if (Simulator.GetDurabilityCost(step, synthesis) >= step.Durability)
            {
                if (Simulator.CanUseAction(craft, step, VulcanSkill.ImmaculateMend) && craft.CraftDurability >= 70)
                    return new(VulcanSkill.ImmaculateMend, "Emergency completion");
                if (Simulator.CanUseAction(craft, step, VulcanSkill.MastersMend))
                    return new(VulcanSkill.MastersMend, "Emergency completion");
            }
            return new(synthesis, "Emergency completion");
        }

        private static VulcanSkill BestSynthesis(CraftState craft, StepState step)
        {
            var remainingProgress = craft.CraftProgress - step.Progress;
            if (Simulator.CalculateProgress(craft, step, VulcanSkill.BasicSynthesis) >= remainingProgress)
                return VulcanSkill.BasicSynthesis;
            if (Simulator.CanUseAction(craft, step, VulcanSkill.IntensiveSynthesis))
                return VulcanSkill.IntensiveSynthesis;
            if (Simulator.CanUseAction(craft, step, VulcanSkill.Groundwork)
                && step.Durability > Simulator.GetDurabilityCost(step, VulcanSkill.Groundwork))
                return VulcanSkill.Groundwork;
            if (Simulator.CanUseAction(craft, step, VulcanSkill.PrudentSynthesis))
                return VulcanSkill.PrudentSynthesis;
            if (Simulator.CanUseAction(craft, step, VulcanSkill.CarefulSynthesis))
                return VulcanSkill.CarefulSynthesis;
            return VulcanSkill.BasicSynthesis;
        }
    }
}
