using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Helpers;
using GatherBuddy.Time;
using System;

namespace GatherBuddy.AutoGather;

public partial class AutoGather
{
    private readonly record struct CollectableGpPlanKey(
        uint ItemId,
        uint LocationId,
        TimeInterval Time,
        uint Quantity,
        uint CompletionItemId,
        string CompletionScope)
    {
        internal static CollectableGpPlanKey From(GatherTarget target)
            => new(
                target.Item.ItemId,
                target.Location.Id,
                target.Time,
                target.Quantity,
                target.CompletionItemId,
                target.CompletionScope);
    }

    private sealed record PendingCollectableGpPlan(
        CollectableGpPlanKey Key,
        int RequiredGp,
        bool ReadyToGather);

    private PendingCollectableGpPlan? _pendingCollectableGpPlan;

    private bool TryBeginCollectableGpWait(int? minimumStartingGp)
    {
        if (minimumStartingGp is not { } requiredGp
         || _currentGatherTarget is not { } target
         || Player.Object is not { } player
         || !CollectableGpWaitPolicy.ShouldWait(
                (int)player.CurrentGp,
                requiredGp,
                target.Time,
                GatherBuddy.Time.ServerTime))
            return false;

        requiredGp = Math.Clamp(requiredGp, 0, (int)player.MaxGp);
        if (requiredGp <= player.CurrentGp)
            return false;

        _pendingCollectableGpPlan = new PendingCollectableGpPlan(
            CollectableGpPlanKey.From(target),
            requiredGp,
            false);
        _currentNodeSessionConfirmed = false;
        CurrentCollectableRotation = null;
        AutoStatus = $"Preparing to wait for solver-required GP ({player.CurrentGp}/{requiredGp})...";
        CloseGatheringAddons();
        return true;
    }

    private int GetSolverRequiredGp(GatherTarget target)
    {
        if (_pendingCollectableGpPlan is not { } pending
         || pending.Key != CollectableGpPlanKey.From(target))
            return 0;

        return pending.RequiredGp;
    }

    private void MarkCollectableGpPlanReady(GatherTarget target)
    {
        if (_pendingCollectableGpPlan is { } pending
         && pending.Key == CollectableGpPlanKey.From(target))
            _pendingCollectableGpPlan = pending with { ReadyToGather = true };
    }

    private bool ConsumeReadyCollectableGpPlan(GatherTarget target)
    {
        if (_pendingCollectableGpPlan is not { ReadyToGather: true } pending
         || pending.Key != CollectableGpPlanKey.From(target))
            return false;

        _pendingCollectableGpPlan = null;
        return true;
    }

    private void DiscardCollectableGpPlanForOtherTarget(GatherTarget target)
    {
        if (_pendingCollectableGpPlan is { } pending
         && pending.Key != CollectableGpPlanKey.From(target))
            _pendingCollectableGpPlan = null;
    }
}
