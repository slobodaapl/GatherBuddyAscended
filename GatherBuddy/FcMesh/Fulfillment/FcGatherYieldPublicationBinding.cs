using System;
using GatherBuddy.FcMesh.Sessions;

namespace GatherBuddy.FcMesh.Fulfillment;

/// <summary>
/// Managed production seam between the gather bridge's interaction event and
/// the author-scoped worker ledger. The bridge supplies an absolute checked
/// yield; this binding performs the one worker update and never polls game
/// inventory or emits a second mesh record.
/// </summary>
public sealed class FcGatherYieldPublicationBinding
{
    private readonly FcWorkerSessionService _worker;

    public FcGatherYieldPublicationBinding(FcWorkerSessionService worker)
        => _worker = worker ?? throw new ArgumentNullException(nameof(worker));

    public FcWorkerSessionResult Publish(GatherYieldObserved observed)
    {
        if (observed is null || observed.Quantity is null)
            return FcWorkerSessionResult.Blocked("Gather yield observation is incomplete.");
        return _worker.RecordGatherYield(observed.Quantity);
    }
}
