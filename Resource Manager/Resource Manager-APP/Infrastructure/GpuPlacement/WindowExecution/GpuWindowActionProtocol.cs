using System.Buffers.Binary;
using ResourceManager.App.Application.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;

internal static class GpuWindowActionProtocol
{
    internal const int MaximumMessageBytes = 80;
    private const uint Version = 2;
    internal enum Kind : uint { Prepare = 1, Prepared, Execute, Rejected, Completed, Parent }

    internal static byte[] Parent(ulong handle)
    {
        if (handle == 0 || handle > long.MaxValue) throw new ArgumentOutOfRangeException(nameof(handle));
        var data = Header(Kind.Parent, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), handle);
        return data;
    }

    internal static byte[] Prepare(GpuWindowActionRequest request)
    {
        request.Validate();
        var data = Header(Kind.Prepare, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), checked((uint)request.ProcessId));
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(12), checked((ulong)request.CreationFileTimeUtc));
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(20), request.Window);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), (uint)request.Method);
        return data;
    }

    internal static byte[] Execute() => Header(Kind.Execute, 8);

    internal static Kind ReadKind(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(data) != Version)
            throw new InvalidDataException("Invalid window message header.");
        var kind = (Kind)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var size = kind switch { Kind.Prepare => 32, Kind.Prepared => 56, Kind.Execute => 8, Kind.Rejected => 16, Kind.Completed => 80, Kind.Parent => 16, _ => 0 };
        if (data.Length != size) throw new InvalidDataException("Invalid window message kind or exact length.");
        return kind;
    }

    internal static GpuWindowPreparation ReadPreparation(ReadOnlySpan<byte> data, GpuWindowActionRequest request)
    {
        RequireKind(data, Kind.Prepared);
        if (!data.Slice(8, 24).SequenceEqual(Prepare(request).AsSpan(8)))
            throw new InvalidDataException("The preparation belongs to a different target or action.");
        var threadId = BinaryPrimitives.ReadUInt32LittleEndian(data[32..]);
        var before = ReadWindow(data[36..]);
        if (threadId == 0 || !before.Visible || before.Iconic || before.Width <= 0 || before.Height <= 0 || before.Width == int.MaxValue)
            throw new InvalidDataException("The prepared window is not eligible.");
        return new(request, threadId, before);
    }

    internal static GpuWindowRejection ReadRejection(ReadOnlySpan<byte> data)
    {
        RequireKind(data, Kind.Rejected);
        var reason = (GpuWindowRejectionReason)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (!Enum.IsDefined(reason)) throw new InvalidDataException("Unknown window rejection.");
        return new(reason, Error(data[12..]));
    }

    internal static GpuWindowCompletion ReadCompletion(ReadOnlySpan<byte> data, GpuWindowActionMethod method)
    {
        RequireKind(data, Kind.Completed);
        var result = new GpuWindowCompletion(ReadCall(data[8..]), ReadObservation(data[16..]), ReadCall(data[44..]), ReadObservation(data[52..]));
        ValidateCompletion(result, method);
        return result;
    }

    internal static void ValidateCompletion(GpuWindowCompletion result, GpuWindowActionMethod method)
    {
        ValidateCall(result.Change);
        ValidateCall(result.Restore);
        ValidateObservation(result.AfterChange);
        ValidateObservation(result.AfterRestore);
        if (result.Change.State is not (GpuWindowCallState.Accepted or GpuWindowCallState.Rejected)
            || result.AfterChange.State == GpuWindowReadState.NotRead)
            throw new InvalidDataException("A completed action must report its actual change call and observation.");
        if (method == GpuWindowActionMethod.Redraw)
        {
            if (result.Restore.State != GpuWindowCallState.NotAttempted || result.AfterRestore.State != GpuWindowReadState.NotRead)
                throw new InvalidDataException("A redraw cannot report a resize restoration.");
        }
        else if (result.Restore.State == GpuWindowCallState.NotAttempted || result.AfterRestore.State == GpuWindowReadState.NotRead)
            throw new InvalidDataException("A resize must report restoration or target loss.");
    }

    private static GpuWindowCall ReadCall(ReadOnlySpan<byte> data)
    {
        var state = (GpuWindowCallState)BinaryPrimitives.ReadUInt32LittleEndian(data);
        var error = Error(data[4..]);
        return new(state, error);
    }

    private static GpuWindowRead ReadObservation(ReadOnlySpan<byte> data)
    {
        var state = (GpuWindowReadState)BinaryPrimitives.ReadUInt32LittleEndian(data);
        var error = Error(data[4..]);
        var value = ReadWindow(data[8..]);
        if (state != GpuWindowReadState.Available && value != new GpuWindowState(0, 0, 0, 0, false, false, false))
            throw new InvalidDataException("Invalid native window observation.");
        return new(state, error, state == GpuWindowReadState.Available ? value : null);
    }

    private static void ValidateCall(GpuWindowCall call)
    {
        if (call is null || !Enum.IsDefined(call.State)
            || ((call.State is GpuWindowCallState.NotAttempted or GpuWindowCallState.Accepted) && call.NativeError.HasValue))
            throw new InvalidDataException("Invalid native call observation.");
    }

    private static void ValidateObservation(GpuWindowRead observation)
    {
        if (observation is null || !Enum.IsDefined(observation.State)
            || (observation.State != GpuWindowReadState.Unavailable && observation.NativeError.HasValue)
            || ((observation.State == GpuWindowReadState.Available) != (observation.Value is not null))
            || (observation.Value is { } value && (value.Width < 0 || value.Height < 0)))
            throw new InvalidDataException("Invalid native window observation.");
    }

    private static GpuWindowState ReadWindow(ReadOnlySpan<byte> data)
    {
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        if ((flags & ~7U) != 0) throw new InvalidDataException("Unknown window state flag.");
        return new(BinaryPrimitives.ReadInt32LittleEndian(data), BinaryPrimitives.ReadInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[8..]), BinaryPrimitives.ReadInt32LittleEndian(data[12..]),
            (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0);
    }

    private static uint? Error(ReadOnlySpan<byte> data)
    {
        var error = BinaryPrimitives.ReadUInt32LittleEndian(data);
        return error == 0 ? null : error;
    }

    private static byte[] Header(Kind kind, int size)
    {
        var data = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(data, Version);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)kind);
        return data;
    }

    private static void RequireKind(ReadOnlySpan<byte> data, Kind expected)
    {
        if (ReadKind(data) != expected) throw new InvalidDataException("Unexpected window message in this step.");
    }
}
