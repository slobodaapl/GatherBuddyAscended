using System;
using System.Collections.Generic;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.State;

public sealed record FcLivenessOptions(
    TimeSpan Horizon,
    TimeSpan MaxClockDelta,
    TimeSpan IdleRefreshInterval)
{
    public FcLivenessOptions(TimeSpan horizon, TimeSpan maxClockDelta)
        : this(horizon, maxClockDelta, TimeSpan.FromSeconds(60))
    {
    }

    public static FcLivenessOptions Default { get; } = new(
        TimeSpan.FromSeconds(300),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromSeconds(60));
}

public sealed class FcLivenessTracker
{
    private readonly FcLivenessOptions _options;
    private readonly Dictionary<string, WorkerSessionRecord> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FcHlcTimestamp> _latestHlc = new(StringComparer.Ordinal);
    private readonly HashSet<string> _forked = new(StringComparer.Ordinal);

    public FcLivenessTracker(FcLivenessOptions? options = null)
    {
        _options = options ?? FcLivenessOptions.Default;
    }

    public bool Accept(WorkerSessionRecord record, FcHlcTimestamp authoredHlc)
    {
        if (record is null
            || record.Header is null
            || authoredHlc.PhysicalUnixMs < 0
            || string.IsNullOrWhiteSpace(authoredHlc.NodeId))
            return false;
        var owner = record.Header.OwnerAuthorId;
        if (!_latest.TryGetValue(owner, out var previous))
        {
            _latest[owner] = record;
            _latestHlc[owner] = authoredHlc;
            return true;
        }

        var hash = FcCanonical.SemanticHash(record);
        var previousHash = FcCanonical.SemanticHash(previous);
        if (record.Header.Revision > previous.Header.Revision)
        {
            if (record.SessionGeneration < previous.SessionGeneration
                || (record.SessionGeneration == previous.SessionGeneration
                    && record.SessionId != previous.SessionId))
            {
                _forked.Add(owner);
                return false;
            }
            if (_latestHlc.TryGetValue(owner, out var previousHlc) && authoredHlc < previousHlc)
                return false;
            _forked.Remove(owner);
            _latest[owner] = record;
            _latestHlc[owner] = authoredHlc;
            return true;
        }
        if (record.Header.Revision == previous.Header.Revision
            && hash == previousHash
            && _latestHlc.TryGetValue(owner, out var currentHlc)
            && authoredHlc == currentHlc)
        {
            return true;
        }
        if (record.Header.Revision == previous.Header.Revision)
        {
            _forked.Add(owner);
            return false;
        }

        return false;
    }

    public bool IsActive(WorkerSessionRecord record, FcHlcTimestamp authoredHlc, IFcClock clock)
        => IsActive(record, authoredHlc, clock.UnixMilliseconds);

    public bool IsActive(WorkerSessionRecord record, FcHlcTimestamp authoredHlc, long nowUnixMilliseconds)
    {
        if (record is null || record.Header is null || authoredHlc.PhysicalUnixMs < 0
            || string.IsNullOrWhiteSpace(authoredHlc.NodeId))
            return false;
        if (_forked.Contains(record.Header.OwnerAuthorId))
            return false;
        if (record.State == FcWorkerState.Unsubscribed)
            return false;
        if (record.State is not (FcWorkerState.Active or FcWorkerState.Waiting))
            return false;

        var horizonMs = checked((long)_options.Horizon.TotalMilliseconds);
        var maxDeltaMs = checked((long)_options.MaxClockDelta.TotalMilliseconds);
        var age = nowUnixMilliseconds - authoredHlc.PhysicalUnixMs;
        return age < horizonMs && age >= -maxDeltaMs;
    }

    public bool IsActive(string ownerAuthorId, IFcClock clock)
        => !_forked.Contains(ownerAuthorId)
            && _latest.TryGetValue(ownerAuthorId, out var record)
            && _latestHlc.TryGetValue(ownerAuthorId, out var hlc)
            && IsActive(record, hlc, clock);

    public IReadOnlyDictionary<string, WorkerSessionRecord> Latest => _latest;
    public IReadOnlySet<string> ForkedWorkers => _forked;
}
