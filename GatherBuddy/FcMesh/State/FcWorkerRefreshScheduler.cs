using System;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.State;

/// <summary>
/// Produces absolute worker no-op revisions. The scheduler owns only the local
/// publication cadence; liveness remains a receiver-side HLC projection.
/// </summary>
public class FcWorkerRefreshScheduler
{
    private readonly FcHlcClock _hlcClock;
    private readonly TimeSpan _interval;
    private ulong _revisionHighWater;
    private long _nextRefreshAt;
    private WorkerSessionRecord _current;

    public FcWorkerRefreshScheduler(
        WorkerSessionRecord initial,
        FcHlcClock hlcClock,
        IFcClock clock,
        TimeSpan? interval = null)
    {
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
        _hlcClock = hlcClock ?? throw new ArgumentNullException(nameof(hlcClock));
        if (clock is null)
            throw new ArgumentNullException(nameof(clock));
        _interval = interval ?? TimeSpan.FromSeconds(60);
        if (_interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        _revisionHighWater = initial.Header.Revision;
        _nextRefreshAt = checked(clock.UnixMilliseconds + (long)_interval.TotalMilliseconds);
    }

    public WorkerSessionRecord Current => _current;
    public ulong RevisionHighWater => _revisionHighWater;
    public long NextRefreshAtUnixMilliseconds => _nextRefreshAt;

    public void ObserveMeaningfulUpdate(WorkerSessionRecord record, IFcClock clock)
    {
        ValidateRevision(record);
        _current = record;
        _revisionHighWater = record.Header.Revision;
        _nextRefreshAt = checked(clock.UnixMilliseconds + (long)_interval.TotalMilliseconds);
    }

    public FcMeshRecord? TryCreateIdleRefresh(IFcClock clock)
    {
        if (_current.State == FcWorkerState.Unsubscribed || clock.UnixMilliseconds < _nextRefreshAt)
            return null;

        var nextRevision = checked(_revisionHighWater + 1UL);
        var header = _current.Header with { Revision = nextRevision };
        var record = _current with { Header = header };
        var hlc = _hlcClock.Next(clock);
        _current = record;
        _revisionHighWater = nextRevision;
        _nextRefreshAt = checked(clock.UnixMilliseconds + (long)_interval.TotalMilliseconds);
        return FcMeshRecord.Create(
            record,
            record.Header.OwnerAuthorId,
            hlc,
            record.SessionGeneration,
            nextRevision,
            FcRecordTypes.WorkerSession,
            record.Header.RecordId.ToString("D"));
    }

    public WorkerSessionRecord StartNewGeneration(Guid sessionId, IFcClock clock)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        var nextGeneration = checked(_current.SessionGeneration + 1UL);
        var nextRevision = checked(_revisionHighWater + 1UL);
        _current = _current with
        {
            Header = _current.Header with { Revision = nextRevision },
            SessionId = sessionId,
            SessionGeneration = nextGeneration,
            State = FcWorkerState.Active,
        };
        _revisionHighWater = nextRevision;
        _nextRefreshAt = checked(clock.UnixMilliseconds + (long)_interval.TotalMilliseconds);
        return _current;
    }

    private void ValidateRevision(WorkerSessionRecord record)
    {
        if (record.Header.Revision <= _revisionHighWater)
            throw new InvalidOperationException("Worker revision high-water mark would regress or be reused.");
        if (record.SessionGeneration < _current.SessionGeneration)
            throw new InvalidOperationException("Worker session generation would regress.");
        if (record.SessionGeneration == _current.SessionGeneration
            && record.SessionId != _current.SessionId)
            throw new InvalidOperationException("Worker session identity would change without a new generation.");
    }
}

public sealed class FcWorkerIdleRefreshScheduler : FcWorkerRefreshScheduler
{
    public FcWorkerIdleRefreshScheduler(
        WorkerSessionRecord initial,
        FcHlcClock hlcClock,
        IFcClock clock,
        TimeSpan? interval = null)
        : base(initial, hlcClock, clock, interval)
    {
    }
}
