using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native.Processes;

// P-proc-read internal ABI, not a wire permission enum. Borrow all input handles,
// output span and atomic cancellation flag for this synchronous call only.
// A successful capability is owned by ONE consuming local scope: finally Close.
// R-resource deletes this bridge with its last managed process consumer.
internal static unsafe class NativeProcessReadAccess
{
    internal const uint Version = 0x0001_0000;
    internal const uint ImagePathApi = 1;
    private const string Library = "ResourceManager.NativeCore";
    static NativeProcessReadAccess() => ReadWriteNativeLibraryResolver.EnsureRegistered();

    [DllImport(Library, EntryPoint = "rm_process_read_access_version", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint AbiVersion();
    [DllImport(Library, EntryPoint = "rm_process_read_acquire", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern ProcessReadAccessStatus Acquire(in ProcessReadRequest request, uint* cancelled,
        char* path, uint capacity, ref ProcessReadCapability capability, out ProcessReadResult result);
    [DllImport(Library, EntryPoint = "rm_process_read_query", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern ProcessReadAccessStatus Query(in ProcessReadCapability capability, nuint targetExitObserver, uint* cancelled,
        char* path, uint capacity, out ProcessReadResult result);
    [DllImport(Library, EntryPoint = "rm_process_read_close", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern ProcessReadAccessStatus Close(ref ProcessReadCapability capability, out ProcessReadResult result);
}

internal enum ProcessReadRoute : uint { OpenUnderCaller = 1, CallerHeldHandle = 2 }
internal enum ProcessReadAccessStatus : uint
{
    Ok, InvalidInput, CallerChanged, TargetMismatch, TargetExited, AccessDenied,
    InsufficientImpersonationLevel, ImpersonationFailed, AmbientImpersonation, Cancelled,
    BufferTooSmall, QueryFailed, DuplicateFailed, InvalidHandle, Unsupported, NativeFailure
}
internal enum ProcessReadStage : uint
{
    None, Input, CallerIdentity, TargetIdentity, Token, Impersonate, Open, Duplicate,
    CompareObject, GrantedAccess, Query, Cancellation
}
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessReadRequest
{
    internal nuint CallerProcess, CallerToken, TargetReference, CallerHeldHandle;
    // Raw FILETIME in 100ns since 1601, NOT DateTime UTC ticks since year 1.
    internal ulong CallerCreatedFiletime, TargetCreatedFiletime;
    internal uint CallerPid, TargetPid;
    internal ProcessReadRoute Route;
    internal uint Api;
    // Service observation only; 0 means the owner supplies exit cancellation.
    // These handles NEVER provide target query rights to the caller.
    internal nuint CallerExitObserver, TargetExitObserver;
}
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessReadCapability
{
    internal nuint Handle;
    internal ulong TargetCreatedFiletime;
    internal uint TargetPid, Api, GrantedAccess;
    internal ProcessReadRoute Route;
}
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessReadResult
{
    internal ProcessReadAccessStatus Status;
    internal ProcessReadStage Stage;
    internal uint Win32;
    internal int NtStatus;
    internal uint PathLength, GrantedAccess;
}
