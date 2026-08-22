using System;
using System.Collections.Generic;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;

namespace GatherBuddy.FcMesh.Fulfillment;

/// <summary>
/// Framework-thread adapter for the real GatherBuddy execution surfaces. FC
/// fulfillment enters the same queue processor and active solver as normal
/// crafting; this class contains only the narrow admission and containment
/// calls needed by <see cref="FcFulfillmentController"/>.
/// </summary>
public sealed class FcLiveFulfillmentRuntime : IFcFulfillmentRuntime
{
    public bool ConnectivityAvailable
        => GatherBuddy.FcMeshNative?.Readiness.IsReady == true;

    public bool CraftingActive
        => CraftingGatherBridge.HasActiveQueue
            || CraftingGameInterop.CurrentState is not CraftingGameInterop.CraftState.IdleNormal;

    public bool GatheringInteractionActive
        => GatherBuddy.AutoGather?.IsGathering == true
            || CraftingGatherBridge.WaitingForGatherComplete;

    public bool TryStartCraft(CraftingExecutionPlan plan)
        => plan is not null && CraftingGatherBridge.TryStartFcQueue(plan);

    public bool TryStartGather(IReadOnlyList<uint> targetOrder)
        // A raw target list without the FC plan would bypass the queue's
        // represented-material completion provider. Fail closed.
        => false;

    public bool TryStartGather(CraftingExecutionPlan plan, IReadOnlyList<uint> targetOrder)
        => plan is not null
            && targetOrder is { Count: > 0 }
            && CraftingGatherBridge.TryStartFcQueue(plan, targetOrder);

    public void StopNavigation()
    {
        // Called only after the controller has reached a safe boundary. Stop
        // navigation, never the active craft/gather queue or an in-flight
        // transfer.
        try
        {
            VNavmesh.Path.Stop?.Invoke();
        }
        catch
        {
            // IPC failure cannot justify a queue cancellation or a guessed
            // transfer; the controller's durable session stop remains the
            // authoritative containment action.
        }

        if (GatherBuddy.AutoGather is not null)
            GatherBuddy.AutoGather.Enabled = false;
    }
}
