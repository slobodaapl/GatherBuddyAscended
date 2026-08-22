using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GatherBuddy.FcMesh.Protocol;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace GatherBuddy.FcMesh.Native;

/// <summary>
/// Independent managed verification of the fixed Rust envelope signature and
/// full-envelope content hash. This is deliberately a fixed-schema encoder:
/// changing the Rust <c>UnsignedEnvelope</c> field order must change the
/// checked-in signing-byte fixture and fail both language gates.
/// </summary>
internal static class FcNativeEnvelopeCrypto
{
    private const int AuthorBytes = 32;
    private const int NodeBytes = 16;
    private const int SignatureBytes = 64;
    private const int ContentHashBytes = 32;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal static bool TryVerify(
        FcNativeEnvelopeWire wire,
        ReadOnlySpan<byte> envelopeBytes,
        ReadOnlySpan<byte> expectedAuthorBytes,
        ReadOnlySpan<byte> expectedContentHashBytes,
        out byte[] managedContentHash,
        out string error)
    {
        managedContentHash = Array.Empty<byte>();
        error = string.Empty;

        if (!TryEncodeSigningBytes(wire, out var signingBytes, out error))
            return false;
        if (!TryDecodeHex(wire.ActualAuthorId, AuthorBytes, out var authorBytes))
        {
            error = "Native envelope author key shape is unsupported.";
            return false;
        }
        if (expectedAuthorBytes.Length != 0
            && (expectedAuthorBytes.Length != AuthorBytes
                || !CryptographicOperations.FixedTimeEquals(expectedAuthorBytes, authorBytes)))
        {
            error = "Native event author does not match the signed envelope.";
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(wire.Signature);
        }
        catch (FormatException)
        {
            error = "Native envelope signature encoding is invalid.";
            return false;
        }
        if (signature.Length != SignatureBytes)
        {
            error = "Native envelope signature length is invalid.";
            return false;
        }

        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(authorBytes, 0));
            verifier.BlockUpdate(signingBytes, 0, signingBytes.Length);
            if (!verifier.VerifySignature(signature))
            {
                error = "Native envelope Ed25519 signature verification failed.";
                return false;
            }
        }
        catch (Exception)
        {
            error = "Native envelope Ed25519 verification is unsupported.";
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingBytes);
            CryptographicOperations.ZeroMemory(signature);
        }

        managedContentHash = HashEnvelope(envelopeBytes);
        if (expectedContentHashBytes.Length != 0
            && (expectedContentHashBytes.Length != ContentHashBytes
                || !CryptographicOperations.FixedTimeEquals(
                    expectedContentHashBytes,
                    managedContentHash)))
        {
            error = "Native event content hash does not match the exact envelope bytes.";
            return false;
        }
        return true;
    }

    internal static byte[] HashEnvelope(ReadOnlySpan<byte> envelopeBytes)
    {
        var digest = new Blake3Digest();
        var input = envelopeBytes.ToArray();
        digest.BlockUpdate(input, 0, input.Length);
        CryptographicOperations.ZeroMemory(input);
        var result = new byte[digest.GetDigestSize()];
        digest.DoFinal(result, 0);
        return result;
    }

    internal static bool TryEncodeSigningBytes(
        FcNativeEnvelopeWire wire,
        out byte[] bytes,
        out string error)
    {
        bytes = Array.Empty<byte>();
        error = string.Empty;
        if (wire is null || wire.Hlc is null)
        {
            error = "Native envelope signing shape is incomplete.";
            return false;
        }

        if (!TryCanonicalGuidBytes(wire.RecordId, out var recordId))
        {
            error = "Native envelope record ID cannot be signed.";
            return false;
        }
        if (!TryDecodeHex(wire.ActualAuthorId, AuthorBytes, out var actualAuthor)
            || !TryDecodeHex(wire.Hlc.NodeId, NodeBytes, out var nodeId))
        {
            error = "Native envelope signing identity shape is unsupported.";
            return false;
        }

        try
        {
            var key = StrictUtf8.GetBytes(wire.DocumentKey);
            var recordType = StrictUtf8.GetBytes(wire.RecordType);
            using var output = new MemoryStream(
                capacity: checked(
                    64
                    + key.Length
                    + recordType.Length
                    + wire.Payload.Length
                    + recordId.Length
                    + actualAuthor.Length
                    + nodeId.Length));
            WriteVarUInt(output, wire.ProtocolVersion);
            WriteBytes(output, key);
            output.Write(recordId, 0, recordId.Length);
            output.Write(actualAuthor, 0, actualAuthor.Length);
            if (wire.Generation is null)
            {
                output.WriteByte(0);
            }
            else
            {
                output.WriteByte(1);
                WriteVarUInt(output, wire.Generation.Value);
            }
            WriteVarUInt(output, wire.Revision);
            WriteBytes(output, recordType);
            WriteVarInt(output, wire.Hlc.PhysicalUnixMs);
            WriteVarUInt(output, wire.Hlc.Logical);
            output.Write(nodeId, 0, nodeId.Length);
            // Rust's HlcWire carries raw in addition to logical. The wire
            // contract requires both words to agree; JSON omits raw.
            WriteVarUInt(output, wire.Hlc.Logical);
            WriteBytes(output, wire.Payload);
            bytes = output.ToArray();
            return true;
        }
        catch (EncoderFallbackException)
        {
            error = "Native envelope signing text is not valid UTF-8.";
            return false;
        }
        catch (OverflowException)
        {
            error = "Native envelope signing bytes exceed the bounded shape.";
            return false;
        }
    }

    private static void WriteBytes(Stream output, byte[] value)
    {
        WriteVarUInt(output, (ulong)value.Length);
        output.Write(value, 0, value.Length);
    }

    private static void WriteVarInt(Stream output, long value)
    {
        var zigZag = unchecked((ulong)((value << 1) ^ (value >> 63)));
        WriteVarUInt(output, zigZag);
    }

    private static void WriteVarUInt(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        output.WriteByte((byte)value);
    }

    private static bool TryCanonicalGuidBytes(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (!Guid.TryParseExact(value, "D", out var guid)
            || guid == Guid.Empty
            || !string.Equals(value, guid.ToString("D"), StringComparison.Ordinal))
            return false;

        var compact = value.Replace("-", string.Empty, StringComparison.Ordinal);
        return TryDecodeHex(compact, 16, out bytes);
    }

    private static bool TryDecodeHex(string? value, int expectedBytes, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value is null || value.Length != expectedBytes * 2)
            return false;
        try
        {
            bytes = Convert.FromHexString(value);
            return bytes.Length == expectedBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
