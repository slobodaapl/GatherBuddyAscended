using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.Native;

public sealed record FcNativeEnvelopeDecodeResult(
    bool IsValid,
    string Error,
    FcMeshRecord? Record,
    FcVerifiedMeshContext? VerifiedContext)
{
    public static FcNativeEnvelopeDecodeResult Invalid(string error)
        => new(false, error, null, null);

    public static FcNativeEnvelopeDecodeResult Valid(
        FcMeshRecord record,
        FcVerifiedMeshContext context)
        => new(true, string.Empty, record, context);
}

/// <summary>
/// Strict boundary decoder for the native ABI's canonical JSON envelope.
/// Native signature verification happens before this context is trusted; this
/// class validates all redundant metadata and preserves the received bytes.
/// </summary>
public static class FcNativeEnvelopeDecoder
{
    public const string SignatureAlgorithm = "iroh-docs-ed25519";
    private const int AuthorHexLength = 64;
    private const int NodeHexLength = 32;
    private const int SignatureLength = 64;

    public static FcNativeEnvelopeDecodeResult Decode(ReadOnlySpan<byte> envelopeBytes)
    {
        var decoded = Decode(
            envelopeBytes,
            ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty);
        return decoded.IsValid && decoded.Record is not null
            ? FcNativeEnvelopeDecodeResult.Valid(
                decoded.Record,
                FcVerifiedMeshContext.FromEnvelope(decoded.Record))
            : decoded;
    }

    internal static FcNativeEnvelopeDecodeResult Decode(
        ReadOnlySpan<byte> envelopeBytes,
        ReadOnlySpan<byte> actualAuthorBytes,
        ReadOnlySpan<byte> contentHashBytes,
        ReadOnlySpan<byte> keyBytes)
    {
        if (envelopeBytes.IsEmpty)
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope is empty.");

        FcNativeEnvelopeWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize(
                envelopeBytes,
                FcJsonContext.Default.FcNativeEnvelopeWire);
        }
        catch (JsonException exception)
        {
            return FcNativeEnvelopeDecodeResult.Invalid($"Native envelope JSON is invalid: {exception.Message}");
        }
        catch (NotSupportedException exception)
        {
            return FcNativeEnvelopeDecodeResult.Invalid($"Native envelope JSON is unsupported: {exception.Message}");
        }

        if (wire is null || wire.Hlc is null || wire.Payload is null)
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope is incomplete.");

        byte[] canonicalBytes;
        try
        {
            canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(
                wire,
                FcJsonContext.Default.FcNativeEnvelopeWire);
        }
        catch (Exception exception)
        {
            return FcNativeEnvelopeDecodeResult.Invalid($"Native envelope canonicalization failed: {exception.Message}");
        }

        if (!canonicalBytes.AsSpan().SequenceEqual(envelopeBytes))
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope is not canonical compact JSON.");

        if (wire.ProtocolVersion != FcProtocolVersion.Current)
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope protocol version is unsupported.");
        if (!TryCanonicalGuid(wire.RecordId, out _))
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope record ID is not lowercase canonical GUID-D.");
        if (!IsLowerHex(wire.ActualAuthorId, AuthorHexLength)
            || !IsLowerHex(wire.DocumentKeyOwnerId, AuthorHexLength))
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope author identity is not lowercase 32-byte hex.");
        if (!IsLowerHex(wire.Hlc.NodeId, NodeHexLength))
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope HLC node identity is not lowercase 16-byte hex.");
        if (wire.Revision == 0
            || wire.Hlc.PhysicalUnixMs < 0
            || string.IsNullOrWhiteSpace(wire.RecordType)
            || wire.RecordType.Length > 128
            || wire.RecordType.Contains('\0')
            || wire.Payload.Length > 16 * 1024 * 1024)
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope ordering metadata is invalid.");
        if (!string.Equals(wire.SignatureAlgorithm, SignatureAlgorithm, StringComparison.Ordinal))
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope signature algorithm is unsupported.");

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(wire.Signature);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope signature is not padded base64.");
        }
        if (signature.Length != SignatureLength
            || !string.Equals(Convert.ToBase64String(signature), wire.Signature, StringComparison.Ordinal))
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope signature encoding is not canonical 64-byte base64.");

        if (!string.Equals(
                wire.PayloadHash,
                FcMeshSignature.HashPayload(wire.Payload),
                StringComparison.Ordinal)
            || !IsLowerHex(wire.PayloadHash, 64))
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope payload hash is invalid.");

        var expectedDocumentKey = FcMeshKey.ForRecord(wire.RecordType, wire.ActualAuthorId, wire.RecordId);
        if (!string.Equals(wire.DocumentKeyOwnerId, wire.ActualAuthorId, StringComparison.Ordinal)
            || !string.Equals(wire.DocumentKey, expectedDocumentKey, StringComparison.Ordinal)
            || wire.DocumentKey.Length == 0
            || wire.DocumentKey.Length > 4096
            || wire.DocumentKey.Contains('\0'))
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope document key ownership or identity is invalid.");

        if (actualAuthorBytes.Length != 0
            && (!TryLowerHexBytes(wire.ActualAuthorId, out var expectedAuthorBytes)
                || !actualAuthorBytes.SequenceEqual(expectedAuthorBytes)))
            return FcNativeEnvelopeDecodeResult.Invalid("Native event author bytes do not match the envelope author.");
        if (keyBytes.Length != 0
            && !keyBytes.SequenceEqual(Encoding.UTF8.GetBytes(wire.DocumentKey)))
            return FcNativeEnvelopeDecodeResult.Invalid("Native event key does not match the envelope document key.");
        if (contentHashBytes.Length != 0 && contentHashBytes.Length != 32)
            return FcNativeEnvelopeDecodeResult.Invalid("Native event content hash must be 32 bytes.");

        var record = new FcMeshRecord(
            wire.RecordId,
            wire.ActualAuthorId,
            wire.Generation,
            wire.Revision,
            new FcHlcTimestamp(wire.Hlc.PhysicalUnixMs, wire.Hlc.Logical, wire.Hlc.NodeId),
            wire.RecordType,
            wire.Payload.ToArray(),
            wire.PayloadHash)
        {
            ProtocolVersion = wire.ProtocolVersion,
            Signature = wire.Signature,
            DocumentKeyOwnerId = wire.DocumentKeyOwnerId,
            DocumentKey = wire.DocumentKey,
            SignatureAlgorithm = wire.SignatureAlgorithm,
            OriginalEnvelopeBytes = envelopeBytes.ToArray(),
        };

        // BLAKE3 full-envelope verification remains native-owned. Managed code
        // retains and exposes the exact native hash but does not invent a
        // second cryptographic implementation at this boundary.
        var contentHash = contentHashBytes.Length == 0
            ? string.Empty
            : Convert.ToHexString(contentHashBytes).ToLowerInvariant();
        var context = FcVerifiedMeshContext.FromNativeVerification(record, contentHash);
        return FcNativeEnvelopeDecodeResult.Valid(record, context);
    }

    private static bool TryCanonicalGuid(string value, out Guid guid)
    {
        if (!Guid.TryParseExact(value, "D", out guid) || guid == Guid.Empty)
            return false;
        return string.Equals(value, guid.ToString("D"), StringComparison.Ordinal);
    }

    private static bool IsLowerHex(string? value, int length)
        => value is not null
            && value.Length == length
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryLowerHexBytes(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }
}
