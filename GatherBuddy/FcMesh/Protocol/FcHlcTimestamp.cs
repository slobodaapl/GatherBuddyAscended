using System;

namespace GatherBuddy.FcMesh.Protocol;

/// <summary>
/// Application HLC stamped by the native mesh envelope. Physical time is
/// signed so malformed values can be rejected; valid records require it to be
/// non-negative. Logical time is unsigned and advances monotonically.
/// </summary>
public readonly record struct FcHlcTimestamp(
    long PhysicalUnixMs,
    ulong Logical,
    string NodeId) : IComparable<FcHlcTimestamp>
{
    /// <summary>Compatibility alias for state code that calls the component Physical.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long Physical => PhysicalUnixMs;

    public int CompareTo(FcHlcTimestamp other)
    {
        var physical = PhysicalUnixMs.CompareTo(other.PhysicalUnixMs);
        if (physical != 0)
            return physical;

        var logical = Logical.CompareTo(other.Logical);
        return logical != 0 ? logical : StringComparer.Ordinal.Compare(NodeId, other.NodeId);
    }

    public static FcHlcTimestamp Max(FcHlcTimestamp left, FcHlcTimestamp right)
        => left.CompareTo(right) >= 0 ? left : right;

    public static bool operator <(FcHlcTimestamp left, FcHlcTimestamp right) => left.CompareTo(right) < 0;
    public static bool operator >(FcHlcTimestamp left, FcHlcTimestamp right) => left.CompareTo(right) > 0;
    public static bool operator <=(FcHlcTimestamp left, FcHlcTimestamp right) => left.CompareTo(right) <= 0;
    public static bool operator >=(FcHlcTimestamp left, FcHlcTimestamp right) => left.CompareTo(right) >= 0;
}
