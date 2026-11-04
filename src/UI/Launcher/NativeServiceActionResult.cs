using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.Shared.Localization;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.Launcher;

// Temporary execution bridge, retired by P-launcher/result; calling binding by
// W-launcher-entry. No managed read task, child wait, timeout, frame or decision.
// Public ServiceActionResult stays on the accepted old path until maintenance
// supplies the shared Desktop native exports and the product equivalence gate.
internal static unsafe class NativeServiceActionResult
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Options
    {
        internal uint ParentTimeout, ConnectTimeout, MaxFrame, TruncateUnits, InputBuffer, OutputBuffer;
        internal Options(ServiceActionResultParameters value) =>
            (ParentTimeout, ConnectTimeout, MaxFrame, TruncateUnits, InputBuffer, OutputBuffer) =
            (value.ParentTimeoutMilliseconds, value.ConnectTimeoutMilliseconds, value.MaximumFrameBytes,
             value.TruncateUtf16Units, value.InputBufferBytes, value.OutputBufferBytes);
    }
    private enum Status : uint { Ok, BridgeFailure, Transport, Unauthorized, InvalidLength, InvalidUtf8, Eof, Canceled, Timeout, RemoteError, ChildExit, InvalidChannel, OutOfMemory, InvalidOptions, Io }
    [StructLayout(LayoutKind.Sequential)]
    private struct Result { internal Status Status; internal uint OsError, ExitCode; internal char* Text; internal nuint Length; }
    [StructLayout(LayoutKind.Sequential)]
    private struct OwnedHandle { internal nint Raw, Cookie; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Bridge
    {
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, char*, nuint, uint, uint, OwnedHandle*, uint> Server;
        internal delegate* unmanaged[Cdecl]<nint, char*, nuint, uint, OwnedHandle*, uint*, uint> Launch;
        internal delegate* unmanaged[Cdecl]<nint, char*, nuint, uint, OwnedHandle*, uint> Client;
        internal delegate* unmanaged[Cdecl]<nint, OwnedHandle, void> Release;
    }
    private sealed class Context(string? launcher, ToolText? text)
    {
        internal readonly string? Launcher = launcher;
        internal readonly ToolText? Text = text;
        internal ExceptionDispatchInfo? Failure;
        internal uint Capture(Exception error) { Failure ??= ExceptionDispatchInfo.Capture(error); return 1; }
    }

    internal static Task RunElevatedAsync(string launcher, bool restart, ToolText text, ServiceActionResultParameters parameters,
        CancellationToken cancellation = default) => NativeServiceActionResultTasks.Observe(Task.Run(() => Run(launcher, restart, text, parameters, cancellation)));

    private static void Run(string launcher, bool restart, ToolText text, ServiceActionResultParameters parameters, CancellationToken cancellation)
    {
        var context = new Context(launcher, text);
        var pin = GCHandle.Alloc(context);
        var options = new Options(parameters);
        var bridge = Callbacks(GCHandle.ToIntPtr(pin));
        using var cancel = new HandleBorrow(cancellation.CanBeCanceled ? cancellation.WaitHandle.SafeWaitHandle : null);
        Result result = default;
        try
        {
            Methods.Run(in options, in bridge, restart ? 1U : 0U, cancel.Raw, out result);
            context.Failure?.Throw();
            Check(in result, text, cancellation);
        }
        finally { Methods.Release(ref result); pin.Free(); GC.KeepAlive(context); }
    }

    internal static Task<string> ReadAsync(NamedPipeServerStream pipe, int childId, CancellationToken cancellation,
        ServiceActionResultParameters parameters)
    {
        HandleBorrow? borrowed = null;
        HandleBorrow? cancel = null;
        nint session = 0;
        Result result = default;
        try
        {
            borrowed = new HandleBorrow(pipe.SafePipeHandle);
            cancel = new HandleBorrow(cancellation.CanBeCanceled ? cancellation.WaitHandle.SafeWaitHandle : null);
            var options = new Options(parameters);
            // Submit connect/read on native stable storage before returning the Task.
            session = Methods.ReadBegin(in options, borrowed.Raw, checked((uint)childId), cancel.Raw, uint.MaxValue, out result);
            Check(in result, null, cancellation);
            var nativeOwner = session;
            var cancelLease = cancel;
            var task = Task.Run(() => Read(borrowed, cancelLease, nativeOwner, cancellation));
            session = 0; // The task now owns the one native session and both local leases.
            return NativeServiceActionResultTasks.Observe(task);
        }
        catch (Exception error)
        {
            if (session != 0) Methods.ReadAbort(session);
            cancel?.Dispose(); borrowed?.Dispose();
            return NativeServiceActionResultTasks.Observe(Task.FromException<string>(error));
        }
    }
    private static string Read(HandleBorrow pipe, HandleBorrow cancel, nint session, CancellationToken cancellation)
    {
        using (pipe)
        using (cancel)
        {
            Result result = default;
            try
            {
                Methods.ReadFinish(session, out result);
                Check(in result, null, cancellation);
                return Text(in result);
            }
            finally { Methods.Release(ref result); }
        }
    }

    internal static void Write(string name, string error, ServiceActionResultParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(error);
        var context = new Context(null, null);
        var pin = GCHandle.Alloc(context);
        var options = new Options(parameters);
        var bridge = Callbacks(GCHandle.ToIntPtr(pin));
        Result result = default;
        try
        {
            // Pins exist only within the synchronous native invocation, never across await.
            fixed (char* channel = name)
            fixed (char* message = error)
                Methods.Write(in options, in bridge, channel, (nuint)name.Length, message, (nuint)error.Length, out result);
            context.Failure?.Throw();
            Check(in result, null, default);
        }
        finally { Methods.Release(ref result); pin.Free(); GC.KeepAlive(context); }
    }

    private static string Text(in Result result) => result.Length == 0 ? "" : new string(result.Text, 0, checked((int)result.Length));
    private static void Check(in Result result, ToolText? text, CancellationToken cancellation)
    {
        switch (result.Status)
        {
            case Status.Ok: return;
            case Status.RemoteError: throw new InvalidOperationException(Text(in result));
            case Status.ChildExit: throw new InvalidOperationException(text!.Format(text.LauncherServiceExitFormat, unchecked((int)result.ExitCode)));
            case Status.Timeout: throw new TimeoutException(text?.LauncherServiceTimeout);
            case Status.Canceled: throw new OperationCanceledException(cancellation);
            case Status.Unauthorized: throw new UnauthorizedAccessException("The service result came from another process.");
            case Status.InvalidLength: throw new InvalidDataException("Invalid service result length.");
            case Status.InvalidUtf8: throw new DecoderFallbackException();
            case Status.Eof: throw new EndOfStreamException();
            case Status.InvalidChannel: throw new ArgumentException("Invalid service result channel.");
            case Status.OutOfMemory: throw new OutOfMemoryException();
            case Status.InvalidOptions: throw new ArgumentException("Invalid service result parameters.");
            case Status.Transport: throw new Win32Exception(unchecked((int)result.OsError));
            case Status.Io: throw new IOException(new Win32Exception(unchecked((int)result.OsError)).Message,
                unchecked((int)(0x80070000U | (result.OsError & 0xffffU))));
            default: throw new InvalidOperationException("The service result OS bridge failed without an exception.");
        }
    }
    private static Bridge Callbacks(nint context) => new() { Context = context, Server = &CreateServer, Launch = &Launch, Client = &Connect, Release = &ReleaseHandle };
    private static Context Get(nint context) => (Context)GCHandle.FromIntPtr(context).Target!;
    private static void Publish(IDisposable value, SafeHandle handle, OwnedHandle* output)
    {
        GCHandle lease = default;
        try
        {
            lease = GCHandle.Alloc(value);
            *output = new OwnedHandle { Raw = handle.DangerousGetHandle(), Cookie = GCHandle.ToIntPtr(lease) };
        }
        catch { if (lease.IsAllocated) lease.Free(); value.Dispose(); throw; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static uint CreateServer(nint context, char* name, nuint length, uint input, uint output, OwnedHandle* result)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new InvalidOperationException(Get(context).Text!.UserUnknown);
            var pipe = WindowsSessionPipeFactory.Create(new string(name, 0, checked((int)length)),
                $"D:P(A;;GA;;;{sid})(A;;GA;;;BA)(A;;GA;;;SY)", checked((int)input), checked((int)output));
            Publish(pipe, pipe.SafePipeHandle, result);
            return 0;
        }
        catch (Exception error) { return Get(context).Capture(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static uint Launch(nint context, char* channel, nuint length, uint restart, OwnedHandle* result, uint* pid)
    {
        try
        {
            var state = Get(context);
            var start = new ProcessStartInfo(state.Launcher!)
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(state.Launcher!)! };
            start.ArgumentList.Add("--service-action");
            start.ArgumentList.Add("--service-result");
            start.ArgumentList.Add(new string(channel, 0, checked((int)length)));
            if (restart != 0) start.ArgumentList.Add("--restart");
            var child = Process.Start(start) ?? throw new InvalidOperationException(state.Text!.LauncherServiceStartFailed);
            SafeProcessHandle childHandle;
            try { *pid = checked((uint)child.Id); childHandle = child.SafeHandle; }
            catch { child.Dispose(); throw; }
            Publish(child, childHandle, result);
            return 0;
        }
        catch (Exception error) { return Get(context).Capture(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static uint Connect(nint context, char* channel, nuint length, uint timeout, OwnedHandle* result)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", new string(channel, 0, checked((int)length)), PipeDirection.Out,
                PipeOptions.None, TokenImpersonationLevel.Identification);
            pipe.Connect(checked((int)timeout));
            var connected = pipe;
            var handle = connected.SafePipeHandle;
            pipe = null;
            Publish(connected, handle, result);
            return 0;
        }
        catch (Exception error) { pipe?.Dispose(); return Get(context).Capture(error); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void ReleaseHandle(nint context, OwnedHandle owned)
    {
        var handle = GCHandle.FromIntPtr(owned.Cookie);
        try { ((IDisposable)handle.Target!).Dispose(); }
        catch (Exception error) { Get(context).Capture(error); }
        finally { handle.Free(); }
    }
    private sealed class HandleBorrow : IDisposable
    {
        private readonly SafeHandle? handle;
        private bool held;
        internal nint Raw => handle?.DangerousGetHandle() ?? 0;
        internal HandleBorrow(SafeHandle? value) { handle = value; handle?.DangerousAddRef(ref held); }
        public void Dispose() { if (held) { held = false; handle!.DangerousRelease(); } }
    }
    private static class Methods
    {
        private const string Library = "ResourceManager.Desktop.Native.dll";
        [DllImport(Library, EntryPoint = "rm_launcher_result_run", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Run(in Options options, in Bridge bridge, uint restart, nint cancel, out Result result);
        [DllImport(Library, EntryPoint = "rm_launcher_result_read_begin", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint ReadBegin(in Options options, nint pipe, uint pid, nint cancel, uint timeout, out Result result);
        [DllImport(Library, EntryPoint = "rm_launcher_result_read_finish", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ReadFinish(nint session, out Result result);
        [DllImport(Library, EntryPoint = "rm_launcher_result_read_abort", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ReadAbort(nint session);
        [DllImport(Library, EntryPoint = "rm_launcher_result_write", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Write(in Options options, in Bridge bridge, char* name, nuint nameLength, char* text, nuint textLength, out Result result);
        [DllImport(Library, EntryPoint = "rm_launcher_result_release", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Release(ref Result result);
    }
}

// Task.Run deliberately receives no cancellation token, so scheduled native
// cleanup cannot be skipped. Async Task representation preserves the original
// canceled-task semantics of an OperationCanceledException from that invocation.
internal static class NativeServiceActionResultTasks
{
    internal static async Task Observe(Task operation) => await operation.ConfigureAwait(false);
    internal static async Task<T> Observe<T>(Task<T> operation) => await operation.ConfigureAwait(false);
}
