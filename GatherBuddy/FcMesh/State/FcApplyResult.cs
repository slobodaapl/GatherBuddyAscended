namespace GatherBuddy.FcMesh.State;

public enum FcApplyStatus
{
    Accepted,
    Duplicate,
    Rejected,
    Fork,
}

public sealed record FcApplyResult(
    FcApplyStatus Status,
    string Message,
    FcWorldRevision WorldRevision,
    bool WriteBlocked = false)
{
    public bool Accepted => Status == FcApplyStatus.Accepted;

    public static FcApplyResult Reject(FcWorldRevision revision, string message)
        => new(FcApplyStatus.Rejected, message, revision);
}
