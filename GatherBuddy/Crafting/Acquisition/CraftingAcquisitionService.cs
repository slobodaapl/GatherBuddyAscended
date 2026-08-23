using System;
using System.Threading;
using GatherBuddy.Crafting.Acquisition;

namespace GatherBuddy.Crafting;

/// <summary>
/// Integration boundary between a live crafting execution plan and the pure
/// acquisition planner. Capture must run on the Dalamud framework thread;
/// evaluation of the resulting immutable input may run on a worker thread.
/// </summary>
public static class CraftingAcquisitionService
{
    public sealed class PlanningCapture
    {
        public AcquisitionPlanningInputBuilder.BuildResult Snapshot { get; init; } = new();
        public AcquisitionPlanningSettings Settings { get; init; } = new();
    }

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
        return Evaluate(Capture(plan));
    }

    public static PlanningCapture Capture(CraftingExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new PlanningCapture
        {
            Snapshot = AcquisitionPlanningInputBuilder.Build(plan),
            Settings = plan.PlanningSnapshot.GetAcquisitionSettings(),
        };
    }

    public static Evaluation Evaluate(
        PlanningCapture capture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!capture.Snapshot.IsReady)
            return new Evaluation { Snapshot = capture.Snapshot };

        var planning = AcquisitionPlanner.Plan(
            capture.Snapshot.Input,
            capture.Settings,
            cancellationToken);
        return new Evaluation
        {
            Snapshot = capture.Snapshot,
            Planning = planning,
        };
    }

    public static string FormatFailure(Evaluation evaluation)
        => string.IsNullOrWhiteSpace(evaluation.Status)
            ? "Automatic acquisition could not produce a complete plan."
            : evaluation.Status;
}
