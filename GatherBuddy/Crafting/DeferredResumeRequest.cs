namespace GatherBuddy.Crafting;

/// <summary>
/// Keeps one resume click pending until the live craft reaches a stable state.
/// </summary>
internal sealed class DeferredResumeRequest
{
    public bool Requested { get; private set; }

    public bool Request()
    {
        var newlyRequested = !Requested;
        Requested = true;
        return newlyRequested;
    }

    public bool TryComplete(bool liveStateReady)
    {
        if (!Requested || !liveStateReady)
            return false;

        Requested = false;
        return true;
    }

    public void Cancel()
        => Requested = false;
}
