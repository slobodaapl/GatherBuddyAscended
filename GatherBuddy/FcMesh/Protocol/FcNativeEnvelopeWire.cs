using System;

namespace GatherBuddy.FcMesh.Protocol;

/// <summary>
/// Exact native JSON field order. This wire DTO is intentionally separate from
/// the semantic managed record: native bytes are canonical evidence and are
/// never regenerated from the managed model for verification.
/// </summary>
public sealed class FcNativeEnvelopeWire
{
    public ushort ProtocolVersion { get; set; }
    public string RecordId { get; set; } = string.Empty;
    public string ActualAuthorId { get; set; } = string.Empty;
    public ulong? Generation { get; set; }
    public ulong Revision { get; set; }
    public FcNativeHlcWire Hlc { get; set; } = new();
    public string RecordType { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public string PayloadHash { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string DocumentKeyOwnerId { get; set; } = string.Empty;
    public string DocumentKey { get; set; } = string.Empty;
    public string SignatureAlgorithm { get; set; } = string.Empty;
}

public sealed class FcNativeHlcWire
{
    public long PhysicalUnixMs { get; set; }
    public ulong Logical { get; set; }
    public string NodeId { get; set; } = string.Empty;
}
