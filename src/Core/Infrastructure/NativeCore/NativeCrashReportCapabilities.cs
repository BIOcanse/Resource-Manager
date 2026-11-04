using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.CrashReports;

namespace ResourceManager.App.Infrastructure.NativeCore;

// C0's synchronous original-owner bridge retires in R-crash; the P0 pipe
// adapter can use this same binding without an HttpContext or HTTP JSON.
internal static partial class NativeCrashReportCapabilities
{
    internal enum Operation : uint { Read, Package, IssueLink, Acknowledge, Delete }
    internal enum Outcome : uint { Success, NotFound }
    internal readonly record struct Result(Operation Operation, Outcome Outcome, object? Value);

    // P0 and other native consumers borrow the complete shared field view here;
    // they never reinterpret the selection ABI's opaque token as payload data.
    internal static int Borrow(ICrashReportOwner owner, Operation operation, string? id,
        Func<NativeCrashReportView.BorrowedResult, int> consume)
        => NativeCrashReportView.Borrow(Invoke(owner, operation, id), consume);

    internal static Result Consume(ICrashReportOwner owner, Operation operation, string? id, NativeCrashReportView.ISink sink)
    {
        var result = Invoke(owner, operation, id);
        NativeCrashReportView.Consume(result, sink);
        return result;
    }

    internal static unsafe Result Invoke(ICrashReportOwner owner, Operation operation, string? id = null)
    {
        var borrowed = new BorrowedCall(owner, id);
        var handle = GCHandle.Alloc(borrowed);
        try
        {
            var callbacks = new Callbacks { Read = &Read, Package = &Package, IssueLink = &IssueLink,
                Acknowledge = &Acknowledge, Delete = &Delete };
            // The original UTF-16 string and payload are never converted or
            // copied. Callbacks use the same original string kept in the scope.
            fixed (char* target = id)
            {
                var code = NativeMethods.Invoke((uint)operation, (ushort*)target, checked((nuint)(id?.Length ?? 0)),
                    GCHandle.ToIntPtr(handle), in callbacks, out var result);
                borrowed.Failure?.Throw();
                if (code != 0) throw new InvalidOperationException($"Native crash capability failed ({code}).");
                return new Result(result.Operation, result.Outcome, borrowed.Value);
            }
        }
        finally
        {
            handle.Free();
            GC.KeepAlive(borrowed);
        }
    }

    private sealed class BorrowedCall(ICrashReportOwner owner, string? id)
    {
        internal ICrashReportOwner Owner => owner;
        internal string Id => id!;
        internal object? Value;
        internal ExceptionDispatchInfo? Failure;
        // The opaque native value is a call-scoped reference to this original
        // payload holder. Native code only tests its presence, never dereferences.
        internal nint Capture(nint context, object? value)
        {
            Value = value;
            return value is null ? 0 : context;
        }
    }

    private static BorrowedCall Borrow(nint context) => (BorrowedCall)GCHandle.FromIntPtr(context).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Read(nint context, nint* value)
    {
        var call = Borrow(context);
        try { *value = call.Capture(context, call.Owner.Read()); return 0; }
        catch (Exception error) { call.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Package(nint context, ushort* target, nuint length, nint* value)
    {
        var call = Borrow(context);
        try { *value = call.Capture(context, call.Owner.Package(call.Id)); return 0; }
        catch (Exception error) { call.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int IssueLink(nint context, ushort* target, nuint length, nint* value)
    {
        var call = Borrow(context);
        try { *value = call.Capture(context, call.Owner.IssueLink(call.Id)); return 0; }
        catch (Exception error) { call.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Acknowledge(nint context, ushort* target, nuint length, nint* value)
    {
        var call = Borrow(context);
        try { *value = call.Capture(context, call.Owner.Acknowledge(call.Id)); return 0; }
        catch (Exception error) { call.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Delete(nint context, ushort* target, nuint length, uint* deleted)
    {
        var call = Borrow(context);
        try { *deleted = call.Owner.Delete(call.Id) ? 1U : 0U; return 0; }
        catch (Exception error) { call.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Callbacks
    {
        internal delegate* unmanaged[Cdecl]<nint, nint*, int> Read;
        internal delegate* unmanaged[Cdecl]<nint, ushort*, nuint, nint*, int> Package, IssueLink, Acknowledge;
        internal delegate* unmanaged[Cdecl]<nint, ushort*, nuint, uint*, int> Delete;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeResult { internal Operation Operation; internal Outcome Outcome; internal nint Value; }

    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_crash_capability_invoke")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Invoke(uint operation, ushort* target, nuint length, nint context,
            in Callbacks callbacks, out NativeResult result);
    }
}
