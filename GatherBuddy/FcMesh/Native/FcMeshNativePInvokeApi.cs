using System;
using System.Runtime.InteropServices;

namespace GatherBuddy.FcMesh.Native;

internal sealed class FcMeshNativePInvokeApi : IFcMeshNativeApi, IDisposable
{
    // Use one explicit name across the Windows plugin and Linux managed smoke.
    // The Linux ELF is staged under this name by the justfile test target.
    private const string LibraryName = "gathermesh_ffi.dll";

    public uint AbiVersion() => gbm_abi_version();

    public FcNativeCallResult Create(ReadOnlySpan<byte> configJson, out ulong handle)
    {
        handle = 0;
        using var config = new PinnedBytes(configJson);
        return FcNativeCallResult.From(gbm_create(config.Pointer, config.Length, out handle));
    }

    public FcNativeCallResult Start(ulong handle)
        => FcNativeCallResult.From(gbm_start(handle));

    public FcNativeCallResult CreateGroup(ulong handle)
        => FcNativeCallResult.From(gbm_create_group(handle));

    public FcNativeCallResult JoinGroup(ulong handle, ReadOnlySpan<byte> ticket)
    {
        using var value = new PinnedBytes(ticket);
        return FcNativeCallResult.From(gbm_join_group(handle, value.Pointer, value.Length));
    }

    public FcNativeCallResult LeaveGroup(ulong handle)
        => FcNativeCallResult.From(gbm_leave_group(handle));

    public FcNativeCallResult Put(
        ulong handle,
        ReadOnlySpan<byte> recordId,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> recordType,
        bool hasGeneration,
        ulong generation,
        ulong revision,
        ReadOnlySpan<byte> payload)
    {
        using var recordIdValue = new PinnedBytes(recordId);
        using var keyValue = new PinnedBytes(key);
        using var recordTypeValue = new PinnedBytes(recordType);
        using var payloadValue = new PinnedBytes(payload);
        return FcNativeCallResult.From(gbm_put(
            handle,
            recordIdValue.Pointer,
            recordIdValue.Length,
            keyValue.Pointer,
            keyValue.Length,
            recordTypeValue.Pointer,
            recordTypeValue.Length,
            hasGeneration ? (byte)1 : (byte)0,
            generation,
            revision,
            payloadValue.Pointer,
            payloadValue.Length));
    }

    public FcNativeCallResult PollEvent(ulong handle, out FcMeshNativeEvent? value)
    {
        value = null;
        var native = default(FcNativeEvent);
        var result = FcNativeCallResult.From(gbm_poll_event(handle, out native));
        if (!result.Succeeded)
            return result;
        try
        {
            ValidateStructSize(native.StructSize, Marshal.SizeOf<FcNativeEvent>(), nameof(FcNativeEvent));
            value = new FcMeshNativeEvent(
                native.ProtocolVersion,
                (FcNativeEventKind)native.Kind,
                native.Sequence,
                native.WorldEpoch,
                CopyBuffer(native.Key),
                CopyBuffer(native.Value),
                CopyBuffer(native.ActualAuthor),
                CopyBuffer(native.ContentHash),
                native.Aux);
            return result;
        }
        finally
        {
            gbm_event_free(native);
        }
    }

    public FcNativeCallResult GetStatus(ulong handle, out FcMeshNativeStatus? value)
    {
        value = null;
        var native = default(FcNativeStatus);
        var result = FcNativeCallResult.From(gbm_get_status(handle, out native));
        if (!result.Succeeded)
            return result;
        try
        {
            ValidateStructSize(native.StructSize, Marshal.SizeOf<FcNativeStatus>(), nameof(FcNativeStatus));
            value = new FcMeshNativeStatus(
                (FcNativeLifecycle)native.Lifecycle,
                native.Joined != 0,
                native.PendingCommands,
                native.PendingEvents,
                native.WorldEpoch,
                CopyBuffer(native.EndpointId),
                CopyBuffer(native.NamespaceId),
                native.LastErrorId);
            return result;
        }
        finally
        {
            gbm_buffer_free(native.EndpointId);
            gbm_buffer_free(native.NamespaceId);
        }
    }

    public FcNativeCallResult Shutdown(ulong handle, uint timeoutMs)
        => FcNativeCallResult.From(gbm_shutdown(handle, timeoutMs));

    public FcNativeCallResult Destroy(ulong handle)
        => FcNativeCallResult.From(gbm_destroy(handle));

    public FcNativeCallResult SetCharacterAuthor(ulong handle, ReadOnlySpan<byte> key)
    {
        using var value = new PinnedBytes(key);
        return FcNativeCallResult.From(gbm_set_character_author(handle, value.Pointer, value.Length));
    }

    public FcNativeCallResult RequestSnapshot(ulong handle, ulong requestId)
        => FcNativeCallResult.From(gbm_request_snapshot(handle, requestId));

    public FcNativeCallResult OpenSnapshot(
        ulong handle,
        ulong requestId,
        out ulong snapshot,
        out ulong baseEventSequence)
    {
        snapshot = 0;
        baseEventSequence = 0;
        return FcNativeCallResult.From(gbm_snapshot_open(
            handle,
            requestId,
            out snapshot,
            out baseEventSequence));
    }

    public FcNativeCallResult PollSnapshot(
        ulong snapshot,
        out FcMeshNativeSnapshotRecord? value,
        out bool done)
    {
        value = null;
        done = false;
        var native = default(FcNativeRecord);
        byte doneValue = 0;
        var result = FcNativeCallResult.From(gbm_snapshot_poll(snapshot, out native, out doneValue));
        if (!result.Succeeded)
            return result;
        try
        {
            ValidateStructSize(native.StructSize, Marshal.SizeOf<FcNativeRecord>(), nameof(FcNativeRecord));
            if (doneValue > 1)
                throw new InvalidOperationException("Native snapshot completion flag is invalid.");
            done = doneValue != 0;
            if (!done)
            {
                value = new FcMeshNativeSnapshotRecord(
                    native.ProtocolVersion,
                    CopyBuffer(native.Key),
                    CopyBuffer(native.Value),
                    CopyBuffer(native.ActualAuthor),
                    CopyBuffer(native.ContentHash),
                    native.HasGeneration != 0 ? native.Generation : null,
                    native.Revision,
                    DecodeUtf8(native.RecordType),
                    native.HlcPhysicalUnixMs,
                    native.HlcLogical,
                    CopyBuffer(native.HlcNodeId));
            }
            return result;
        }
        finally
        {
            FreeRecordBuffers(native);
        }
    }

    public FcNativeCallResult DestroySnapshot(ulong snapshot)
        => FcNativeCallResult.From(gbm_snapshot_destroy(snapshot));

    public string? GetErrorMessage(ulong errorId)
    {
        if (errorId == 0)
            return null;
        var native = default(FcNativeBuffer);
        try
        {
            var result = FcNativeCallResult.From(gbm_error_message(errorId, out native));
            return result.Succeeded ? DecodeUtf8(native) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            gbm_buffer_free(native);
            gbm_error_free(errorId);
        }
    }

    public void Dispose()
    {
    }

    private static byte[] CopyBuffer(FcNativeBuffer buffer)
    {
        if (buffer.Length == 0)
            return Array.Empty<byte>();
        if (buffer.Ptr == IntPtr.Zero)
            throw new InvalidOperationException("Native returned a null pointer for a non-empty buffer.");
        if ((ulong)buffer.Length > int.MaxValue)
            throw new InvalidOperationException("Native returned an oversized buffer.");
        var value = new byte[(int)buffer.Length];
        Marshal.Copy(buffer.Ptr, value, 0, value.Length);
        return value;
    }

    private static void FreeRecordBuffers(FcNativeRecord record)
    {
        gbm_buffer_free(record.Key);
        gbm_buffer_free(record.Value);
        gbm_buffer_free(record.ActualAuthor);
        gbm_buffer_free(record.ContentHash);
        gbm_buffer_free(record.RecordType);
        gbm_buffer_free(record.HlcNodeId);
    }

    private static string DecodeUtf8(FcNativeBuffer buffer)
        => System.Text.Encoding.UTF8.GetString(CopyBuffer(buffer));

    private static void ValidateStructSize(uint actual, int expected, string name)
    {
        if (actual != (uint)expected)
            throw new InvalidOperationException($"Native {name} struct size {actual} != managed {expected}.");
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint gbm_abi_version();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_create(IntPtr configJson, nuint configLen, out ulong handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_start(ulong handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_create_group(ulong handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_join_group(ulong handle, IntPtr ticket, nuint ticketLen);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_leave_group(ulong handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_put(
        ulong handle,
        IntPtr recordId,
        nuint recordIdLen,
        IntPtr key,
        nuint keyLen,
        IntPtr recordType,
        nuint recordTypeLen,
        byte hasGeneration,
        ulong generation,
        ulong revision,
        IntPtr payload,
        nuint payloadLen);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_poll_event(ulong handle, out FcNativeEvent value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_get_status(ulong handle, out FcNativeStatus value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_shutdown(ulong handle, uint timeoutMs);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_destroy(ulong handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_set_character_author(ulong handle, IntPtr key, nuint len);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_request_snapshot(ulong handle, ulong requestId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_snapshot_open(
        ulong handle,
        ulong requestId,
        out ulong snapshot,
        out ulong baseEventSequence);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_snapshot_poll(
        ulong snapshot,
        out FcNativeRecord record,
        out byte done);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_snapshot_destroy(ulong snapshot);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern FcNativeAbiResult gbm_error_message(ulong errorId, out FcNativeBuffer value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gbm_error_free(ulong errorId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gbm_buffer_free(FcNativeBuffer buffer);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void gbm_event_free(FcNativeEvent value);

    private readonly struct PinnedBytes : IDisposable
    {
        private readonly GCHandle _handle;
        private readonly byte[]? _value;

        public PinnedBytes(ReadOnlySpan<byte> value)
        {
            _value = value.ToArray();
            _handle = _value.Length == 0 ? default : GCHandle.Alloc(_value, GCHandleType.Pinned);
        }

        public IntPtr Pointer => _value is { Length: > 0 } ? _handle.AddrOfPinnedObject() : IntPtr.Zero;
        public nuint Length => (nuint)(_value?.Length ?? 0);

        public void Dispose()
        {
            if (_handle.IsAllocated)
                _handle.Free();
        }
    }
}
