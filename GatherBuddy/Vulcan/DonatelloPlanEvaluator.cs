using System.Collections.Generic;

namespace GatherBuddy.Vulcan;

public sealed record DonatelloPlanEvaluation(
    bool Completes,
    int Quality,
    int Steps,
    int Duration,
    IReadOnlyList<StepState> Trajectory)
{
    public bool IsStrictlyBetterThan(DonatelloPlanEvaluation incumbent)
    {
        if (Completes != incumbent.Completes)
            return Completes;
        if (Quality != incumbent.Quality)
            return Quality > incumbent.Quality;
        if (Steps != incumbent.Steps)
            return Steps < incumbent.Steps;
        return Duration < incumbent.Duration;
    }
}

public static class DonatelloPlanEvaluator
{
    public static DonatelloPlanEvaluation Evaluate(
        CraftState craft,
        StepState root,
        IReadOnlyList<VulcanSkill> actions)
    {
        var state = root;
        var trajectory = new List<StepState>(actions.Count + 1) { state };
        var executed = 0;
        var duration = 0;
        foreach (var action in actions)
        {
            if (SolverUtils.Status(craft, state) != SolverUtils.CraftStatus.InProgress)
                break;
            if (Simulator.GetSuccessRate(state, action) < 1.0)
                return Failed(craft, state, executed, duration, trajectory);
            var (result, next) = Simulator.Execute(craft, state, action, 0, 1);
            if (result != Simulator.ExecuteResult.Succeeded)
                return Failed(craft, state, executed, duration, trajectory);
            state = next;
            trajectory.Add(state);
            executed++;
            duration += Duration(action);
        }
        var completes = SolverUtils.Status(craft, state) == SolverUtils.CraftStatus.Complete;
        return new(
            completes,
            System.Math.Min(state.Quality, craft.CraftQualityMax),
            executed,
            duration,
            trajectory);
    }

    private static DonatelloPlanEvaluation Failed(
        CraftState craft,
        StepState state,
        int steps,
        int duration,
        IReadOnlyList<StepState> trajectory)
        => new(false, System.Math.Min(state.Quality, craft.CraftQualityMax), steps, duration, trajectory);

    private static int Duration(VulcanSkill action) => action switch
    {
        VulcanSkill.WasteNot or VulcanSkill.WasteNot2 or VulcanSkill.Veneration
            or VulcanSkill.Innovation or VulcanSkill.GreatStrides or VulcanSkill.Manipulation
            or VulcanSkill.StellarSteadyHand => 2,
        _ => 3,
    };
}
