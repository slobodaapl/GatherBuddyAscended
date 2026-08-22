using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Linq;

namespace GatherBuddy.FcMesh.Protocol;

/// <summary>
/// Rust/native-authored opaque envelope. All application ordering metadata is
/// taken from this outer value. Relays copy it byte-for-byte.
/// </summary>
public sealed record FcMeshRecord(
    string RecordId,
    string ActualAuthorId,
    ulong? Generation,
    ulong Revision,
    FcHlcTimestamp Hlc,
    string RecordType,
    byte[] Payload,
    string PayloadHash)
{
    /// <summary>Application envelope protocol version supplied by native mesh.</summary>
    public ushort ProtocolVersion { get; init; }

    /// <summary>Native/Iroh signature metadata independently checked at the native envelope boundary.</summary>
    public string Signature { get; init; } = string.Empty;

    /// <summary>Owner encoded in the verified document key.</summary>
    public string DocumentKeyOwnerId { get; init; } = string.Empty;

    /// <summary>Exact document key used by the transport, when available.</summary>
    public string DocumentKey { get; init; } = string.Empty;

    /// <summary>Opaque native/Iroh signature algorithm identifier.</summary>
    public string SignatureAlgorithm { get; init; } = string.Empty;

    private byte[] _originalEnvelopeBytes = Array.Empty<byte>();

    /// <summary>
    /// Exact UTF-8 envelope bytes received from native mesh. The setter and
    /// getter defensively copy so a relayed record cannot lose its signature
    /// or content-hash evidence through an accidental mutation.
    /// </summary>
    [JsonIgnore]
    public byte[] OriginalEnvelopeBytes
    {
        get => _originalEnvelopeBytes.ToArray();
        init => _originalEnvelopeBytes = (value ?? throw new ArgumentNullException(nameof(value))).ToArray();
    }

    [JsonIgnore]
    public string AuthorId => ActualAuthorId;

    [JsonIgnore]
    public FcHlcTimestamp HlcTimestamp => Hlc;

    [JsonIgnore]
    public byte[] OpaquePayload => Payload;

    [JsonIgnore]
    public string KeyOwnerId => DocumentKeyOwnerId;

    [JsonIgnore]
    public string PayloadUtf8 => Encoding.UTF8.GetString(Payload);

    /// <summary>
    /// Builds an unsigned envelope-shaped value for local scheduling/tests.
    /// Native transport must stamp signature metadata and provide trusted
    /// verification context before the value can enter the world store.
    /// </summary>
    public static FcMeshRecord Create<T>(
        T payload,
        string actualAuthorId,
        FcHlcTimestamp hlc,
        ulong? generation,
        ulong revision,
        string recordType,
        string recordId,
        string? documentKey = null)
    {
        var bytes = FcCanonical.SerializeUtf8(payload);
        var hash = FcMeshSignature.HashPayload(bytes);
        var keyOwner = actualAuthorId;
        var resolvedKey = documentKey ?? FcMeshKey.ForRecord(recordType, actualAuthorId, recordId);
        return new FcMeshRecord(
            recordId,
            actualAuthorId,
            generation,
            revision,
            hlc,
            recordType,
            bytes,
            hash)
        {
            DocumentKeyOwnerId = keyOwner,
            DocumentKey = resolvedKey,
        };
    }
}

public sealed record FcVerifiedMeshContext(
    string ActualAuthorId,
    string KeyOwnerId,
    string DocumentKey,
    string ContentHash,
    string RecordType,
    string RecordId,
    ulong Revision,
    ulong? Generation,
    bool SignatureValid)
{
    /// <summary>Transport-authenticated author, when the caller has one.</summary>
    public string? TransportAuthorId { get; init; }

    /// <summary>
    /// True only when native transport authenticated the exact envelope bytes.
    /// Generic Phase0 fixtures deliberately remain separate from this path.
    /// </summary>
    public bool NativeVerified { get; internal init; }

    /// <summary>Native-provided SHA-256 payload hash, when content hash is distinct.</summary>
    public string? PayloadHash { get; internal init; }

    public static FcVerifiedMeshContext FromEnvelope(FcMeshRecord envelope)
        => new(
            envelope.ActualAuthorId,
            envelope.DocumentKeyOwnerId,
            envelope.DocumentKey,
            envelope.PayloadHash,
            envelope.RecordType,
            envelope.RecordId,
            envelope.Revision,
            envelope.Generation,
            SignatureValid: false);

    internal static FcVerifiedMeshContext FromNativeVerification(
        FcMeshRecord envelope,
        string contentHash)
        => new(
            envelope.ActualAuthorId,
            envelope.DocumentKeyOwnerId,
            envelope.DocumentKey,
            contentHash,
            envelope.RecordType,
            envelope.RecordId,
            envelope.Revision,
            envelope.Generation,
            SignatureValid: true)
        {
            TransportAuthorId = envelope.ActualAuthorId,
            NativeVerified = true,
            PayloadHash = envelope.PayloadHash,
        };
}

public interface IFcMeshRecordVerifier
{
    bool Verify(FcMeshRecord envelope, FcVerifiedMeshContext verifiedContext);
}

/// <summary>
/// Structural gate for native verification results. Cryptographic verification
/// happens in the strict native-envelope decoder; this gate checks that the
/// resulting evidence still names the exact managed record.
/// </summary>
public sealed class FcVerifiedContextVerifier : IFcMeshRecordVerifier
{
    public bool Verify(FcMeshRecord envelope, FcVerifiedMeshContext verifiedContext)
    {
        if (!verifiedContext.SignatureValid
            || (verifiedContext.TransportAuthorId is not null
                && !string.Equals(verifiedContext.TransportAuthorId, envelope.ActualAuthorId, StringComparison.Ordinal)))
            return false;
        if (verifiedContext.NativeVerified
            && string.IsNullOrWhiteSpace(verifiedContext.ContentHash))
            return false;
        return string.Equals(verifiedContext.ActualAuthorId, envelope.ActualAuthorId, StringComparison.Ordinal)
            && string.Equals(verifiedContext.KeyOwnerId, envelope.ActualAuthorId, StringComparison.Ordinal)
            && string.Equals(envelope.DocumentKeyOwnerId, envelope.ActualAuthorId, StringComparison.Ordinal)
            && (verifiedContext.NativeVerified
                ? string.Equals(verifiedContext.PayloadHash, envelope.PayloadHash, StringComparison.Ordinal)
                : string.Equals(verifiedContext.ContentHash, envelope.PayloadHash, StringComparison.Ordinal))
            && string.Equals(verifiedContext.RecordType, envelope.RecordType, StringComparison.Ordinal)
            && string.Equals(verifiedContext.RecordId, envelope.RecordId, StringComparison.Ordinal)
            && verifiedContext.Revision == envelope.Revision
            && verifiedContext.Generation == envelope.Generation
            && string.Equals(verifiedContext.DocumentKey, envelope.DocumentKey, StringComparison.Ordinal);
    }
}

public static class FcMeshSignature
{
    public static string HashPayload(byte[] payload)
        => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
}

public static class FcMeshKey
{
    public static string ForRecord(string recordType, string owner, string recordId)
        => recordType switch
        {
            "group-metadata" => $"v1/meta/group/{owner}",
            "published-list" => $"v1/lists/{owner}/{recordId}",
            "worker-session" => $"v1/workers/{owner}",
            "chest-snapshot" => $"v1/chest/{owner}",
            "capability-request" => $"v1/capability-requests/{owner}/{recordId}",
            "capability-response" => $"v1/capability-responses/{owner}/{recordId}",
            "inventory-transfer" => $"v1/transfers/{owner}/{recordId}",
            _ => $"v1/unknown/{owner}/{recordId}",
        };
}
