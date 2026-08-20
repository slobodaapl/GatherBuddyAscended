using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GatherBuddy.FcMesh.Native;

public enum FcNativeErrorCode : uint
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    InvalidHandle = 3,
    InvalidState = 4,
    QueueFull = 5,
    NoEvent = 6,
    NotReady = 7,
    Closing = 8,
    AlreadyDestroyed = 9,
    NotFound = 10,
    LimitExceeded = 11,
    InvalidRecord = 12,
    ClockDrift = 13,
    Storage = 14,
    Network = 15,
    Panic = 16,
    Internal = 17,
    SnapshotStale = 18,
}

public enum FcNativeEventKind : uint
{
    Started = 1,
    Stopped = 2,
    Joining = 3,
    Joined = 4,
    Left = 5,
    RecordInserted = 6,
    RecordRemoved = 7,
    InitialSyncCompleted = 8,
    PeerConnected = 9,
    PeerDisconnected = 10,
    PathChanged = 11,
    Warning = 12,
    Error = 13,
    WorldInvalidated = 14,
    SnapshotReady = 15,
}

public enum FcNativeLifecycle : uint
{
    Created = 1,
    Starting = 2,
    Running = 3,
    Closing = 4,
    Closed = 5,
    Destroyed = 6,
}

public static class FcNativeAbi
{
    public const uint Version = 2;
    public const uint ConfigSchemaVersion = 1;
    public const uint EventSchemaVersion = 1;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct FcNativeAbiResult
{
    public readonly uint Code;
    public readonly uint Flags;
    public readonly ulong ErrorId;

    public FcNativeAbiResult(uint code, uint flags, ulong errorId)
    {
        Code = code;
        Flags = flags;
        ErrorId = errorId;
    }

    public bool Succeeded => Code == (uint)FcNativeErrorCode.Ok;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct FcNativeBuffer
{
    public readonly IntPtr Ptr;
    public readonly nuint Length;

    public FcNativeBuffer(IntPtr ptr, nuint length)
    {
        Ptr = ptr;
        Length = length;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct FcNativeEvent
{
    public uint StructSize;
    public uint ProtocolVersion;
    public uint Kind;
    public ulong Sequence;
    public ulong WorldEpoch;
    public FcNativeBuffer Key;
    public FcNativeBuffer Value;
    public FcNativeBuffer ActualAuthor;
    public FcNativeBuffer ContentHash;
    public ulong Aux;
}

[StructLayout(LayoutKind.Sequential)]
public struct FcNativeRecord
{
    public uint StructSize;
    public uint ProtocolVersion;
    public uint Flags;
    public FcNativeBuffer Key;
    public FcNativeBuffer Value;
    public FcNativeBuffer ActualAuthor;
    public FcNativeBuffer ContentHash;
    public ulong Generation;
    public uint HasGeneration;
    public ulong Revision;
    public FcNativeBuffer RecordType;
    public long HlcPhysicalUnixMs;
    public ulong HlcLogical;
    public FcNativeBuffer HlcNodeId;
}

[StructLayout(LayoutKind.Sequential)]
public struct FcNativeStatus
{
    public uint StructSize;
    public uint Lifecycle;
    public uint Flags;
    public uint Joined;
    public ulong PendingCommands;
    public ulong PendingEvents;
    public ulong WorldEpoch;
    public FcNativeBuffer EndpointId;
    public FcNativeBuffer NamespaceId;
    public ulong LastErrorId;
}

public sealed record FcNativeCallResult(uint Code, uint Flags, ulong ErrorId)
{
    public bool Succeeded => Code == (uint)FcNativeErrorCode.Ok;
    public FcNativeErrorCode ErrorCode => (FcNativeErrorCode)Code;

    public static FcNativeCallResult From(FcNativeAbiResult result)
        => new(result.Code, result.Flags, result.ErrorId);
}

public sealed record FcMeshNativeEvent(
    uint ProtocolVersion,
    FcNativeEventKind Kind,
    ulong Sequence,
    ulong WorldEpoch,
    byte[] Key,
    byte[] Value,
    byte[] ActualAuthor,
    byte[] ContentHash,
    ulong Aux)
{
    public FcMeshNativeEvent DeepCopy()
        => this with
        {
            Key = Key.ToArray(),
            Value = Value.ToArray(),
            ActualAuthor = ActualAuthor.ToArray(),
            ContentHash = ContentHash.ToArray(),
        };
}

public sealed record FcMeshNativeSnapshotRecord(
    uint ProtocolVersion,
    byte[] Key,
    byte[] Value,
    byte[] ActualAuthor,
    byte[] ContentHash,
    ulong? Generation,
    ulong Revision,
    string RecordType,
    long HlcPhysicalUnixMs,
    ulong HlcLogical,
    byte[] HlcNodeId)
{
    public FcMeshNativeSnapshotRecord DeepCopy()
        => this with
        {
            Key = Key.ToArray(),
            Value = Value.ToArray(),
            ActualAuthor = ActualAuthor.ToArray(),
            ContentHash = ContentHash.ToArray(),
            HlcNodeId = HlcNodeId.ToArray(),
        };
}

public sealed record FcMeshNativeStatus(
    FcNativeLifecycle Lifecycle,
    bool Joined,
    ulong PendingCommands,
    ulong PendingEvents,
    ulong WorldEpoch,
    byte[] EndpointId,
    byte[] NamespaceId,
    ulong LastErrorId)
{
    public FcMeshNativeStatus DeepCopy()
        => this with { EndpointId = EndpointId.ToArray(), NamespaceId = NamespaceId.ToArray() };
}

public sealed record FcNativeConfiguration(
    string StorageDirectory,
    ulong EventCapacity = 1024,
    ulong CommandCapacity = 256,
    ulong MaxKeyBytes = 4096,
    ulong MaxValueBytes = 16 * 1024 * 1024,
    uint RelayMode = 0,
    IReadOnlyList<string>? RelayUrls = null)
{
    public byte[] ToJsonUtf8()
    {
        var value = new
        {
            schema_version = FcNativeAbi.ConfigSchemaVersion,
            storage_directory = StorageDirectory,
            event_capacity = EventCapacity,
            command_capacity = CommandCapacity,
            max_key_bytes = MaxKeyBytes,
            max_value_bytes = MaxValueBytes,
            relay_mode = RelayMode,
            relay_urls = RelayUrls ?? Array.Empty<string>(),
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
    }
}

internal interface IFcMeshNativeApi : IDisposable
{
    uint AbiVersion();
    FcNativeCallResult Create(ReadOnlySpan<byte> configJson, out ulong handle);
    FcNativeCallResult Start(ulong handle);
    FcNativeCallResult CreateGroup(ulong handle);
    FcNativeCallResult JoinGroup(ulong handle, ReadOnlySpan<byte> ticket);
    FcNativeCallResult LeaveGroup(ulong handle);
    FcNativeCallResult Put(
        ulong handle,
        ReadOnlySpan<byte> recordId,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> recordType,
        bool hasGeneration,
        ulong generation,
        ulong revision,
        ReadOnlySpan<byte> payload);
    FcNativeCallResult PollEvent(ulong handle, out FcMeshNativeEvent? value);
    FcNativeCallResult GetStatus(ulong handle, out FcMeshNativeStatus? value);
    FcNativeCallResult Shutdown(ulong handle, uint timeoutMs);
    FcNativeCallResult Destroy(ulong handle);
    FcNativeCallResult SetCharacterAuthor(ulong handle, ReadOnlySpan<byte> key);
    FcNativeCallResult RequestSnapshot(ulong handle, ulong requestId);
    FcNativeCallResult OpenSnapshot(ulong handle, ulong requestId, out ulong snapshot, out ulong baseEventSequence);
    FcNativeCallResult PollSnapshot(ulong snapshot, out FcMeshNativeSnapshotRecord? value, out bool done);
    FcNativeCallResult DestroySnapshot(ulong snapshot);
    string? GetErrorMessage(ulong errorId);
}

internal sealed class FcMeshSafeHandle : SafeHandle
{
    private readonly Func<ulong, FcNativeCallResult> _destroy;

    public FcMeshSafeHandle(ulong handle, Func<ulong, FcNativeCallResult> destroy)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        _destroy = destroy ?? throw new ArgumentNullException(nameof(destroy));
        if (handle == 0)
            throw new ArgumentOutOfRangeException(nameof(handle));
        SetHandle(new IntPtr(unchecked((long)handle)));
    }

    public ulong Value => unchecked((ulong)handle.ToInt64());
    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        try
        {
            if (!IsInvalid)
                _ = _destroy(Value);
        }
        catch
        {
            // Finalizer/Dispose must never throw across plugin teardown.
        }
        finally
        {
            SetHandleAsInvalid();
        }
        return true;
    }
}
