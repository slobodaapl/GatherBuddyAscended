using System;
using GatherBuddy.Crafting.Acquisition;

namespace GatherBuddy.Crafting;

/// <summary>
/// Integration boundary between a live crafting execution plan and the pure
/// acquisition planner. It is intentionally synchronous: all game reads happen
/// on the Dalamud framework thread, while Universalis lookups are queued by the
/// marketboard service and observed on the next update.
/// </summary>
public static class CraftingAcquisitionService
{
    public sealed class Evaluation
    {
        public AcquisitionPlanningInputBuilder.BuildResult Snapshot { get; init; } = new();
        public AcquisitionPlanningResult? Planning { get; init; }
        public bool IsLoading => Snapshot.IsLoading;
        public string Status
            => IsLoading
                ? Snapshot.LoadingReason
                : !string.IsNullOrWhiteSpace(Snapshot.ErrorReason)
                    ? Snapshot.ErrorReason
                    : Planning?.Blockers.Count > 0
                        ? Planning.Blockers[0].Reason
                        : string.Empty;
    }

    public static Evaluation Evaluate(CraftingExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var snapshot = AcquisitionPlanningInputBuilder.Build(plan);
        if (!snapshot.IsReady)
            return new Evaluation { Snapshot = snapshot };

        var planning = AcquisitionPlanner.Plan(
            snapshot.Input,
            plan.PlanningSnapshot.GetAcquisitionSettings());
        return new Evaluation
        {
            Snapshot = snapshot,
            Planning = planning,
        };
    }

    public static string FormatFailure(Evaluation evaluation)
        => string.IsNullOrWhiteSpace(evaluation.Status)
            ? "Automatic acquisition could not produce a complete plan."
            : evaluation.Status;
}
