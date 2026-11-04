using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Endpoints.Transport;

// HTTP header/PipeWriter plumbing retires in H1. Shared CLR field access now
// lives only in NativeCrashReportView and retires in R-crash.
internal static partial class NativeCrashReportResponse
{
    internal enum Operation : uint { List, Package, Acknowledge, Delete }

    internal static Task Send(HttpContext context, NativeCrashReportCapabilities.Result result)
        => SendCore(context, (Operation)result.Operation, result.Value,
            result.Outcome == NativeCrashReportCapabilities.Outcome.Success);

    private static Task SendCore(HttpContext context, Operation operation, object? snapshot, bool found)
    {
        // All source access and pins end before returning this asynchronous flush.
        // PipeWriter owns the final output segments throughout the pending send.
        if (!Encode(context, operation, snapshot, found)) return Task.CompletedTask;
        return FlushAsync(context.Response.BodyWriter, context.RequestAborted);
    }

    private static async Task FlushAsync(PipeWriter output, CancellationToken cancellationToken)
    {
        var result = await output.FlushAsync(cancellationToken);
        if (result.IsCanceled) throw new OperationCanceledException(cancellationToken);
    }

    private static unsafe bool Encode(HttpContext context, Operation operation, object? snapshot, bool found)
    {
        Check(NativeMethods.Describe((uint)operation, found ? 1U : 0U, out var response));
        context.Response.StatusCode = checked((int)response.Status);
        context.Response.ContentType = response.ContentType switch
        {
            0 => null,
            1 => "application/json; charset=utf-8",
            2 => "application/problem+json",
            _ => throw new InvalidOperationException("Unknown native crash-report content type.")
        };
        if (response.ContentType == 0) return false;

        using var borrowed = new BorrowedOutput(context.Response.BodyWriter);
        var handle = GCHandle.Alloc(borrowed);
        try
        {
            var output = new OutputCallbacks { Reserve = &Reserve, Commit = &Commit };
            var outputContext = GCHandle.ToIntPtr(handle);
            var code = NativeCrashReportView.Borrow(new NativeCrashReportCapabilities.Result(
                (NativeCrashReportCapabilities.Operation)operation,
                found ? NativeCrashReportCapabilities.Outcome.Success : NativeCrashReportCapabilities.Outcome.NotFound, snapshot),
                input => NativeMethods.EncodeViewStream(in input, outputContext, in output, out _));
            borrowed.Failure?.Throw();
            Check(code);
            return true;
        }
        finally
        {
            handle.Free();
            GC.KeepAlive(snapshot);
        }
    }

    private static void Check(int code)
    {
        if (code != 0) throw new InvalidOperationException($"Native crash-report encoding failed ({code}).");
    }

    private static BorrowedOutput Borrow(nint context) => (BorrowedOutput)GCHandle.FromIntPtr(context).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Reserve(nint context, nuint completed, byte** pointer, nuint* capacity)
    {
        var borrowed = Borrow(context);
        try
        {
            borrowed.Commit(completed);
            var memory = borrowed.Output.GetMemory();
            borrowed.Pin = memory.Pin();
            borrowed.Pinned = true;
            borrowed.Capacity = memory.Length;
            *pointer = (byte*)borrowed.Pin.Pointer;
            *capacity = checked((nuint)memory.Length);
            return 0;
        }
        catch (Exception error) { borrowed.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Commit(nint context, nuint completed)
    {
        var borrowed = Borrow(context);
        try { borrowed.Commit(completed); return 0; }
        catch (Exception error) { borrowed.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    // A call-scoped owner of a single output-segment pin. No Ledger lease or
    // native pointer is retained by an endpoint, queue or asynchronous closure.
    private sealed class BorrowedOutput(PipeWriter output) : IDisposable
    {
        internal PipeWriter Output => output;
        internal MemoryHandle Pin;
        internal bool Pinned;
        internal int Capacity;
        internal ExceptionDispatchInfo? Failure;

        internal void Commit(nuint completed)
        {
            if (completed > checked((nuint)Capacity)) throw new InvalidOperationException("Native segment extent exceeds its borrow.");
            if (Pinned)
            {
                // Release the address before Advance lets PipeWriter reuse it.
                Pin.Dispose();
                Pinned = false;
                Capacity = 0;
                output.Advance(checked((int)completed));
            }
            else if (completed != 0) throw new InvalidOperationException("No output segment is borrowed.");
        }

        public void Dispose()
        {
            if (Pinned) { Pin.Dispose(); Pinned = false; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct OutputCallbacks
    {
        internal delegate* unmanaged[Cdecl]<nint, nuint, byte**, nuint*, int> Reserve;
        internal delegate* unmanaged[Cdecl]<nint, nuint, int> Commit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Response { internal ulong Length; internal uint Status; internal uint ContentType; }

    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_crash_reports_encode_view_stream")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int EncodeViewStream(in NativeCrashReportView.BorrowedResult result, nint context,
            in OutputCallbacks output, out Response response);

        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_crash_reports_describe")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Describe(uint operation, uint found, out Response response);

    }
}
