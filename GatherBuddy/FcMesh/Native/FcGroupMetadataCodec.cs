using System;
using System.Text;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.Native;

/// <summary>
/// Decoder for the native group's postcard metadata payload. Rust validates
/// this record before emitting InitialSyncCompleted; managed code also checks
/// the exact bytes present in the reconstructed snapshot before Ready.
/// </summary>
internal static class FcGroupMetadataCodec
{
    private const string ExpectedBackendKind = "iroh-docs";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool IsCompatible(
        ReadOnlySpan<byte> payload,
        string? expectedNamespaceHex,
        out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(expectedNamespaceHex)
            || expectedNamespaceHex.Length != 64)
        {
            error = "Native group namespace is unavailable.";
            return false;
        }

        byte[] expectedNamespace;
        try
        {
            expectedNamespace = Convert.FromHexString(expectedNamespaceHex);
        }
        catch (FormatException)
        {
            error = "Native group namespace is malformed.";
            return false;
        }

        var index = 0;
        if (!TryReadVarint(payload, ref index, out var protocolVersion)
            || protocolVersion > ushort.MaxValue
            || !TryReadString(payload, ref index, out var backendKind)
            || !TryReadVarint(payload, ref index, out var storageVersion)
            || storageVersion > ushort.MaxValue
            || payload.Length - index != 32)
        {
            error = "Native group metadata payload is malformed.";
            return false;
        }

        var namespaceBytes = payload.Slice(index, 32);
        if (protocolVersion != (ulong)FcProtocolVersion.Current
            || !string.Equals(backendKind, ExpectedBackendKind, StringComparison.Ordinal)
            || storageVersion != 1
            || !namespaceBytes.SequenceEqual(expectedNamespace))
        {
            error = "Native group metadata is incompatible with this client or namespace.";
            return false;
        }

        return true;
    }

    private static bool TryReadString(
        ReadOnlySpan<byte> payload,
        ref int index,
        out string value)
    {
        value = string.Empty;
        if (!TryReadVarint(payload, ref index, out var length)
            || length > int.MaxValue
            || length > (ulong)(payload.Length - index))
            return false;
        try
        {
            value = StrictUtf8.GetString(payload.Slice(index, (int)length));
            index += (int)length;
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryReadVarint(
        ReadOnlySpan<byte> payload,
        ref int index,
        out ulong value)
    {
        value = 0;
        var shift = 0;
        while (index < payload.Length && shift < 64)
        {
            var current = payload[index++];
            if (shift == 63 && (current & 0x7f) > 1)
                return false;
            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0)
                return true;
            if (shift >= 63)
                return false;
            shift += 7;
        }
        return false;
    }

}
