using System;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
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
/// Native transport evidence and this class's independent Ed25519/BLAKE3
/// verification must both succeed before a verified context is returned; the
/// class also validates redundant metadata and preserves received bytes.
/// </summary>
public static class FcNativeEnvelopeDecoder
{
    public const string SignatureAlgorithm = "iroh-docs-ed25519";
    private const int AuthorHexLength = 64;
    private const int NodeHexLength = 32;
    private const int SignatureLength = 64;
    private const int MaxEnvelopeBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions RustCanonicalJsonOptions
        = CreateRustCanonicalJsonOptions();

    private static JsonSerializerOptions CreateRustCanonicalJsonOptions()
    {
        // Rust serde_json emits base64 characters without JavaScript/HTML
        // escaping. Clone the source-generated wire metadata so property order,
        // naming, null handling, and byte[] base64 conversion stay unchanged;
        // relax only the encoder used for this already-validated native wire.
        var options = new JsonSerializerOptions(FcJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return options;
    }

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
        if (envelopeBytes.Length > MaxEnvelopeBytes)
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope exceeds 16 MiB maximum.");

        FcNativeEnvelopeWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize(
                envelopeBytes,
                FcJsonContext.Default.FcNativeEnvelopeWire);
        }
        catch (JsonException exception)
        {
            _ = exception;
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope JSON is invalid.");
        }
        catch (NotSupportedException exception)
        {
            _ = exception;
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope JSON is unsupported.");
        }

        if (wire is null || wire.Hlc is null || wire.Payload is null)
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope is incomplete.");

        byte[] canonicalBytes;
        try
        {
            canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(
                wire,
                RustCanonicalJsonOptions);
        }
        catch (Exception exception)
        {
            _ = exception;
            return FcNativeEnvelopeDecodeResult.Invalid("Native envelope canonicalization failed.");
        }

        if (!canonicalBytes.AsSpan().SequenceEqual(envelopeBytes))
            return FcNativeEnvelopeDecodeResult.Invalid(
                DescribeCanonicalMismatch(envelopeBytes, canonicalBytes));

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
            || wire.Payload.Length > MaxEnvelopeBytes)
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
        if (!FcNativeEnvelopeCrypto.TryVerify(
                wire,
                envelopeBytes,
                actualAuthorBytes,
                contentHashBytes,
                out var managedContentHash,
                out var cryptoError))
            return FcNativeEnvelopeDecodeResult.Invalid(cryptoError);

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

        var contentHash = contentHashBytes.Length == 0
            ? Convert.ToHexString(managedContentHash).ToLowerInvariant()
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

    private static string DescribeCanonicalMismatch(
        ReadOnlySpan<byte> received,
        ReadOnlySpan<byte> canonical)
    {
        var sharedLength = Math.Min(received.Length, canonical.Length);
        var offset = 0;
        while (offset < sharedLength && received[offset] == canonical[offset])
            offset++;

        var receivedToken = offset < received.Length
            ? JsonTokenClass(received[offset])
            : "end";
        var canonicalToken = offset < canonical.Length
            ? JsonTokenClass(canonical[offset])
            : "end";
        var property = TopLevelPropertyAt(received, offset);
        var receivedByte = offset < received.Length
            ? JsonByteCategory(received[offset])
            : "end";
        var canonicalByte = offset < canonical.Length
            ? JsonByteCategory(canonical[offset])
            : "end";
        var receivedEscape = JsonEscapeForm(received, offset);
        var canonicalEscape = JsonEscapeForm(canonical, offset);
        return $"Native envelope is not canonical compact JSON (offset={offset}, "
            + $"receivedLength={received.Length}, canonicalLength={canonical.Length}, "
            + $"property={property}, receivedToken={receivedToken}, canonicalToken={canonicalToken}, "
            + $"receivedByte={receivedByte}, canonicalByte={canonicalByte}, "
            + $"receivedEscape={receivedEscape}, canonicalEscape={canonicalEscape}).";
    }

    private static string TopLevelPropertyAt(ReadOnlySpan<byte> json, int offset)
    {
        var index = 0;
        SkipJsonWhitespace(json, ref index);
        if (index >= json.Length || json[index] != (byte)'{')
            return "unknown";
        index++;
        while (index < json.Length)
        {
            SkipJsonWhitespace(json, ref index);
            if (index >= json.Length || json[index] == (byte)'}')
                return "unknown";
            if (json[index] != (byte)'"')
                return "unknown";
            var propertyStart = index;
            var propertyEnd = SkipJsonString(json, index);
            if (propertyEnd <= index + 1)
                return "unknown";
            var property = Encoding.UTF8.GetString(
                json.Slice(index + 1, propertyEnd - index - 2));
            index = propertyEnd;
            SkipJsonWhitespace(json, ref index);
            if (index >= json.Length || json[index] != (byte)':')
                return "unknown";
            index++;
            SkipJsonWhitespace(json, ref index);
            var valueStart = index;
            var valueEnd = SkipJsonValue(json, index);
            if (offset >= propertyStart && offset < valueEnd)
                return property;
            if (valueEnd <= valueStart)
                return "unknown";
            index = valueEnd;
            SkipJsonWhitespace(json, ref index);
            if (index >= json.Length || json[index] != (byte)',')
                return "unknown";
            index++;
        }
        return "unknown";
    }

    private static int SkipJsonValue(ReadOnlySpan<byte> json, int index)
    {
        if (index >= json.Length)
            return index;
        if (json[index] == (byte)'"')
            return SkipJsonString(json, index);
        if (json[index] == (byte)'{' || json[index] == (byte)'[')
        {
            var open = json[index];
            var close = open == (byte)'{' ? (byte)'}' : (byte)']';
            var depth = 0;
            var inString = false;
            for (var current = index; current < json.Length; current++)
            {
                var value = json[current];
                if (inString)
                {
                    if (value == (byte)'\\')
                        current++;
                    else if (value == (byte)'"')
                        inString = false;
                    continue;
                }
                if (value == (byte)'"')
                {
                    inString = true;
                    continue;
                }
                if (value == open)
                    depth++;
                else if (value == close && --depth == 0)
                    return current + 1;
            }
            return json.Length;
        }
        while (index < json.Length
            && json[index] != (byte)','
            && json[index] != (byte)'}'
            && json[index] != (byte)']'
            && json[index] != (byte)' '
            && json[index] != (byte)'\t'
            && json[index] != (byte)'\r'
            && json[index] != (byte)'\n')
            index++;
        return index;
    }

    private static int SkipJsonString(ReadOnlySpan<byte> json, int index)
    {
        if (index >= json.Length || json[index] != (byte)'"')
            return index;
        for (var current = index + 1; current < json.Length; current++)
        {
            if (json[current] == (byte)'\\')
            {
                current++;
                continue;
            }
            if (json[current] == (byte)'"')
                return current + 1;
        }
        return index;
    }

    private static void SkipJsonWhitespace(ReadOnlySpan<byte> json, ref int index)
    {
        while (index < json.Length
            && json[index] is (byte)' '
                or (byte)'\t'
                or (byte)'\r'
                or (byte)'\n')
            index++;
    }

    private static string JsonByteCategory(byte value)
        => value is >= 0x20 and <= 0x7E
            ? $"ascii-0x{value:X2}"
            : $"byte-0x{value:X2}";

    private static string JsonEscapeForm(ReadOnlySpan<byte> json, int offset)
    {
        if (offset >= json.Length || json[offset] != (byte)'\\')
            return "literal";
        if (offset + 1 < json.Length && json[offset + 1] == (byte)'u')
            return "unicode-escape";
        return "json-escape";
    }

    private static string JsonTokenClass(byte value)
        => value switch
        {
            (byte)'{' => "object-start",
            (byte)'}' => "object-end",
            (byte)'[' => "array-start",
            (byte)']' => "array-end",
            (byte)':' => "colon",
            (byte)',' => "comma",
            (byte)'"' => "string",
            (byte)'t' or (byte)'f' => "boolean",
            (byte)'n' => "null",
            (byte)'-' => "number",
            >= (byte)'0' and <= (byte)'9' => "number",
            0x20 or 0x09 or 0x0A or 0x0D => "whitespace",
            _ => "other",
        };
}
