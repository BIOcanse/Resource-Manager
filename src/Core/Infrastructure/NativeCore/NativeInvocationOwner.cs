using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

// P0 startup/OS ABI only. No accept loop, registry, CancellationTokenSource or
// dispatch state lives in this bridge. P1 removes platform callbacks; H3 removes
// this managed startup entry. Actual OS/owner callbacks are supplied by their
// original owners at the coordinated product cutover.
internal static unsafe partial class NativeInvocationOwner
{
    internal enum Code : int { Ok, Invalid, Cancelled, Ended, Timeout, Failed }
    internal enum Status : uint
    {
        Ok, NotFound, Unauthorized, Forbidden, Unavailable, InvalidRequest, Cancelled, EffectUnknown
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Bytes { internal byte* Data; internal nuint Length; }

    // Callback must use this borrowed control only while that callback executes.
    // Cancelled must wake a blocked OS operation; no retained delegate/lease.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Control
    {
        internal nint CancelContext;
        internal delegate* unmanaged[Cdecl]<nint, uint> Cancelled;
        internal uint HasDeadline;
        internal ulong DeadlineMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Connection
    {
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, byte*, nuint, Control, nuint*, int> Read, Write;
        internal delegate* unmanaged[Cdecl]<nint, int> VerifyServer;
        internal delegate* unmanaged[Cdecl]<nint, uint> IsEnded;
        internal delegate* unmanaged[Cdecl]<nint, void> Interrupt, Close;
    }

    // Original platform owns these stable tables until their Release callback.
    // The Context is the actual message's pinned caller proof / Ledger lease.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Caller
    {
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, void> Release;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Workspace
    {
        internal byte* Data;
        internal nuint Length;
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, void> Release;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Reply
    {
        internal Status Status;
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, byte*, nuint, nuint*, int> WriteJson;
        internal delegate* unmanaged[Cdecl]<nint, void> Release;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Binding
    {
        internal Bytes Operation, InputSchema, OutputSchema;
        internal uint Kind, Admission;
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, nint, uint, uint*, int> Admit;
        internal delegate* unmanaged[Cdecl]<nint, nint, nint, Control, Reply*, int> Invoke;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Platform
    {
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, Control, Connection**, int> Accept;
        internal delegate* unmanaged[Cdecl]<nint, Connection*, Control, Caller**, int> AttestMessage;
        internal delegate* unmanaged[Cdecl]<nint, Workspace**, int> BorrowWorkspace;
        internal delegate* unmanaged[Cdecl]<nint, Control, int> WaitCapacity;
        internal delegate* unmanaged[Cdecl]<nint, void> SignalCapacity;
        internal delegate* unmanaged[Cdecl]<nint, ulong> NowMilliseconds;
        internal delegate* unmanaged[Cdecl]<nint, uint> Stopping;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Config
    {
        internal uint MaximumFrameBytes;
        internal nuint MaximumConnections, WorkerStackBytes;
        internal uint RequestTimeoutMilliseconds, ResponseTimeoutMilliseconds;
        internal byte* StateMemory;
        internal nuint StateMemoryLength;
    }

    // Synchronous run: local run lease, callback tables and binding text remain
    // live until native joins every connection and returns. This method owns no
    // alternate managed loop. Callback exceptions must be caught by the source.
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_invocation_owner_run")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial Code Run(in Config config, in Platform platform, Binding* bindings, nuint count);

    // Request address is native call-scoped input, never a cross-process value.
    // Only GPU text and crash id get bridge access; caller comes from Attest.
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_invocation_payload_string")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial Code PayloadString(nint request, out Bytes value);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_invocation_crash_target")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial Code CrashTarget(nint request, out Bytes value);
}
