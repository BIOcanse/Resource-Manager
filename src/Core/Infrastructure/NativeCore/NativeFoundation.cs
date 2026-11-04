using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

// Temporary C3/C4/C41 bridge. NativeCore owns OS calls; each caller owns its
// domain state and scopes. Remove with the last managed foundation consumer.
internal static unsafe class NativeFoundation
{
    private const string Library = "ResourceManager.NativeCore";

    [StructLayout(LayoutKind.Sequential)]
    internal struct ClockSample
    {
        internal ulong UtcTicks;
        internal ulong MonotonicTicks;
        internal ulong Frequency;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Identity
    {
        internal ulong InstanceId;
        internal ulong ProcessCreatedUtcTicks;
        internal uint ProcessId;
        internal uint Reserved;
    }

    internal static ClockSample ReadClock()
    {
        Check(Clock(out var result));
        return result;
    }

    internal static Identity CaptureIdentity()
    {
        Check(Capture(out var result));
        return result;
    }

    internal static void AppendLog(string path, string archive, long rotateBytes, ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rotateBytes);
        fixed (char* pathPointer = path)
        fixed (char* archivePointer = archive)
        fixed (byte* payload = bytes)
            Check(Append(pathPointer, checked((uint)path.Length), archivePointer,
                checked((uint)archive.Length), checked((ulong)rotateBytes), payload, (nuint)bytes.Length));
    }

    internal static void RotateLog(string path, string archive, long rotateBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rotateBytes);
        fixed (char* pathPointer = path)
        fixed (char* archivePointer = archive)
            Check(Rotate(pathPointer, checked((uint)path.Length), archivePointer,
                checked((uint)archive.Length), checked((ulong)rotateBytes)));
    }

    private static void Check(uint code)
    {
        if (code != 0) throw new IOException("Native foundation operation failed.", new Win32Exception(checked((int)code)));
    }

    [DllImport(Library, EntryPoint = "rm_foundation_clock", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint Clock(out ClockSample result);
    [DllImport(Library, EntryPoint = "rm_foundation_identity", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint Capture(out Identity result);
    [DllImport(Library, EntryPoint = "rm_foundation_log_append", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint Append(char* path, uint pathLength, char* archive, uint archiveLength,
        ulong rotateBytes, byte* bytes, nuint byteLength);
    [DllImport(Library, EntryPoint = "rm_foundation_log_rotate", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint Rotate(char* path, uint pathLength, char* archive, uint archiveLength, ulong rotateBytes);
}

// Also usable by DI once the integrating writer applies the registration patch.
// Base timers retain the existing System.Threading.Timer cancellation semantics;
// this bridge does not create a second timer loop or durable time authority.
internal sealed class NativeFoundationTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(checked((long)NativeFoundation.ReadClock().UtcTicks), TimeSpan.Zero);
    public override long GetTimestamp() => checked((long)NativeFoundation.ReadClock().MonotonicTicks);
    public override long TimestampFrequency => checked((long)NativeFoundation.ReadClock().Frequency);
}
