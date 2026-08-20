using System;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.State;

public interface IFcClock
{
    long UnixMilliseconds { get; }

    DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeMilliseconds(UnixMilliseconds);
}

public sealed class FcSystemClock : IFcClock
{
    public static FcSystemClock Instance { get; } = new();

    public long UnixMilliseconds => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class FcManualClock : IFcClock
{
    public FcManualClock(long unixMilliseconds)
    {
        if (unixMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(unixMilliseconds));
        UnixMilliseconds = unixMilliseconds;
    }

    public long UnixMilliseconds { get; private set; }

    public void Set(long unixMilliseconds)
    {
        if (unixMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(unixMilliseconds));
        UnixMilliseconds = unixMilliseconds;
    }

    public void Advance(TimeSpan amount)
        => Set(checked(UnixMilliseconds + amount.Ticks / TimeSpan.TicksPerMillisecond));
}

public sealed record FcClockOptions(TimeSpan MaxFutureDelta)
{
    public static FcClockOptions Default { get; } = new(TimeSpan.FromMinutes(5));
}

public sealed record FcHlcClockState(FcHlcTimestamp Last, string NodeId);

public sealed class FcHlcClock
{
    private readonly string _nodeId;
    private FcHlcTimestamp _last;

    public FcHlcClock(string nodeId, IFcClock clock, FcHlcClockState? persisted = null)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            throw new ArgumentException("Node ID is required.", nameof(nodeId));
        _nodeId = nodeId;
        if (persisted is not null
            && !string.Equals(persisted.NodeId, nodeId, StringComparison.Ordinal))
            throw new ArgumentException("Persisted HLC belongs to another node.", nameof(persisted));
        _last = persisted?.Last ?? new FcHlcTimestamp(Math.Max(0, clock.UnixMilliseconds), 0, nodeId);
        if (_last.PhysicalUnixMs < 0 || !string.Equals(_last.NodeId, nodeId, StringComparison.Ordinal))
            throw new ArgumentException("Persisted HLC is invalid.", nameof(persisted));
    }

    public FcHlcTimestamp Last => _last;
    public FcHlcClockState ExportState() => new(_last, _nodeId);

    public FcHlcTimestamp Next(IFcClock clock)
    {
        var now = Math.Max(0, clock.UnixMilliseconds);
        var physical = Math.Max(now, _last.PhysicalUnixMs);
        var logical = physical == _last.PhysicalUnixMs ? _last.Logical : 0UL;
        if (physical == _last.PhysicalUnixMs)
        {
            if (logical == ulong.MaxValue)
            {
                physical = checked(physical + 1L);
                logical = 0;
            }
            else
            {
                logical++;
            }
        }
        _last = new FcHlcTimestamp(physical, logical, _nodeId);
        return _last;
    }

    /// <summary>Observe an accepted remote stamp before authoring subsequent local values.</summary>
    public void Observe(FcHlcTimestamp remote, IFcClock clock)
    {
        if (remote.PhysicalUnixMs < 0 || string.IsNullOrWhiteSpace(remote.NodeId))
            throw new ArgumentOutOfRangeException(nameof(remote));
        var now = Math.Max(0, clock.UnixMilliseconds);
        var physical = Math.Max(now, Math.Max(_last.PhysicalUnixMs, remote.PhysicalUnixMs));
        ulong logical;
        if (physical == _last.PhysicalUnixMs && physical == remote.PhysicalUnixMs)
        {
            var maximum = Math.Max(_last.Logical, remote.Logical);
            if (maximum == ulong.MaxValue)
            {
                physical = checked(physical + 1L);
                logical = 0;
            }
            else
            {
                logical = maximum + 1UL;
            }
        }
        else if (physical == _last.PhysicalUnixMs)
        {
            if (_last.Logical == ulong.MaxValue)
            {
                physical = checked(physical + 1L);
                logical = 0;
            }
            else
            {
                logical = _last.Logical + 1UL;
            }
        }
        else if (physical == remote.PhysicalUnixMs)
        {
            if (remote.Logical == ulong.MaxValue)
            {
                physical = checked(physical + 1L);
                logical = 0;
            }
            else
            {
                logical = remote.Logical + 1UL;
            }
        }
        else
            logical = 0UL;
        _last = new FcHlcTimestamp(physical, logical, _nodeId);
    }
}
