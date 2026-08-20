using System;
using System.Text.Json.Serialization;

namespace GatherBuddy.FcMesh.Protocol;

/// <summary>
/// Payload metadata. It mirrors the native envelope but never supplies the
/// authoritative HLC, signature, or content hash.
/// </summary>
public sealed record FcRecordHeader(
    int ProtocolVersion,
    int SchemaVersion,
    string RecordType,
    Guid RecordId,
    string OwnerAuthorId,
    ulong Revision)
{
    [JsonIgnore]
    public bool IsCompatible
        => ProtocolVersion == FcProtocolVersion.Current && SchemaVersion == FcProtocolVersion.CurrentSchema;
}
