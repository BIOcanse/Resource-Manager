using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;

namespace ResourceManager.App.Infrastructure.NativeCore;

// P-session-facts: synchronous, call-scoped projection into the original API's
// final string/uint[] only. No query, ordering, authorization or cache here.
// The service façade/binding retires in P-session-host (H3).
internal static unsafe partial class NativeInteractiveSessionFacts
{
    private const string LibraryName = "ResourceManager.NativeCore";
    private const uint ErrorInvalidHandle = 6; // Win32 error protocol
    private const uint ErrorInvalidSid = 1337;

    internal static string? GetUserSid(uint sessionId)
    {
        using var output = new CallOutput();
        var context = GCHandle.Alloc(output);
        try
        {
            var status = ReadUserSid(sessionId, GCHandle.ToIntPtr(context), &ReceiveSid, out var failure);
            ThrowIfFailed(status, failure, output);
            return output.Sid;
        }
        finally { context.Free(); }
    }

    internal static IReadOnlyList<uint> GetActiveSessionIds()
    {
        using var output = new CallOutput();
        var context = GCHandle.Alloc(output);
        try
        {
            var status = ReadActiveIds(GCHandle.ToIntPtr(context), &AcquireIds, out var failure);
            ThrowIfFailed(status, failure, output);
            return output.Ids;
        }
        finally { context.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint ReceiveSid(nint context, char* text, uint length)
    {
        var output = (CallOutput)GCHandle.FromIntPtr(context).Target!;
        try
        {
            // LocalFree runs only after this final managed string is complete.
            output.Sid = new string(text, 0, checked((int)length));
            return 0;
        }
        catch (Exception error) { output.Failure = ExceptionDispatchInfo.Capture(error); return 1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint* AcquireIds(nint context, uint count)
    {
        var output = (CallOutput)GCHandle.FromIntPtr(context).Target!;
        try
        {
            if (count == 0) return null;
            output.Ids = GC.AllocateUninitializedArray<uint>(checked((int)count));
            output.Pin = GCHandle.Alloc(output.Ids, GCHandleType.Pinned);
            return (uint*)output.Pin.AddrOfPinnedObject();
        }
        catch (Exception error) { output.Failure = ExceptionDispatchInfo.Capture(error); return null; }
    }

    private static void ThrowIfFailed(Status status, Failure failure, CallOutput output)
    {
        output.Failure?.Throw();
        if (status is Status.Ok or Status.NotFound) return;
        if (status == Status.WindowsError)
        {
            var error = unchecked((int)failure.Code);
            if (failure.Operation == Operation.EnumerateSessions)
                throw new Win32Exception(error, $"WTSEnumerateSessionsW failed with Win32 error {error}.");
            if (failure.Operation == Operation.ProcessSession) throw new Win32Exception(error);
            // WindowsIdentity's token reads surface SecurityException, except
            // an invalid token. WTSQueryUserToken failure remains null above.
            if (failure.Operation == Operation.TokenUser && failure.Code == ErrorInvalidHandle)
                throw new ArgumentException("Invalid impersonation token.");
            if (failure.Code == ErrorInvalidSid) throw new ArgumentException("Invalid security identifier.");
            throw new SecurityException(new Win32Exception(error).Message);
        }
        throw new InvalidOperationException($"Interactive session fact ABI returned {status}.");
    }

    // A new scope per call; native never retains this owner/context or its pin.
    private sealed class CallOutput : IDisposable
    {
        internal string? Sid;
        internal uint[] Ids = [];
        internal GCHandle Pin;
        internal ExceptionDispatchInfo? Failure;
        public void Dispose() { if (Pin.IsAllocated) Pin.Free(); }
    }

    private enum Status : uint { Ok, NotFound, WindowsError, ReceiverFailed, InvalidArgument }
    private enum Operation : uint { None, CurrentToken, TokenUser, ProcessSession, SidText, EnumerateSessions }
    [StructLayout(LayoutKind.Sequential)]
    private struct Failure { internal Operation Operation; internal uint Code; }

    [LibraryImport(LibraryName, EntryPoint = "rm_interactive_session_facts_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint AbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "rm_interactive_session_facts_user_sid")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial Status ReadUserSid(uint sessionId, nint context,
        delegate* unmanaged[Cdecl]<nint, char*, uint, uint> receive, out Failure failure);

    [LibraryImport(LibraryName, EntryPoint = "rm_interactive_session_facts_active_ids")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial Status ReadActiveIds(nint context,
        delegate* unmanaged[Cdecl]<nint, uint, uint*> destination, out Failure failure);
}
