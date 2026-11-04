using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Launcher;

// Temporary synchronous managed-source bridge. System callbacks leave in
// P-launcher; this binding and LauncherWorkflow.Start leave in W-launcher-entry.
// LauncherWorkflow.Start calls the shared Desktop native export through this bridge.
public static class NativeLauncherWorkflow
{
    // Desktop's one temporary interop library, shared with the other window leaves.
    private const string Library = "ResourceManager.Desktop.Native";

    private enum Result : uint
    {
        Success = 0,
        CallbackFailure = 1,
        ServiceMissing = 2,
        ServiceNotRunning = 3,
        InvalidArguments = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceState
    {
        public byte Present;
        public byte Running;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Callbacks
    {
        public IntPtr Context;
        public IntPtr ReadVerifiedService;
        public IntPtr StartService;
        public IntPtr ElevateService;
        public IntPtr LaunchInterface;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint ReadCallback(IntPtr context, out ServiceState state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint ActionCallback(IntPtr context);

    // Root code pointers, never a service state, path, lease or per-call context.
    private static readonly ReadCallback Read = ReadVerifiedService;
    private static readonly ActionCallback Direct = context => Invoke(context, static source => source.StartService());
    private static readonly ActionCallback Elevate = context => Invoke(context, static source => source.ElevateService());
    private static readonly ActionCallback Launch = context => Invoke(context, static source => source.LaunchInterface());

    public static void Start(bool elevated, bool restart, string backend,
        Func<WindowsServiceState?> readService, Action startService, Action elevateService, Action launchInterface,
        string? failureMessage = null)
    {
        var source = new BorrowedSource(backend, readService, startService, elevateService, launchInterface);
        var handle = GCHandle.Alloc(source);
        try
        {
            var callbacks = new Callbacks
            {
                Context = GCHandle.ToIntPtr(handle),
                ReadVerifiedService = Marshal.GetFunctionPointerForDelegate(Read),
                StartService = Marshal.GetFunctionPointerForDelegate(Direct),
                ElevateService = Marshal.GetFunctionPointerForDelegate(Elevate),
                LaunchInterface = Marshal.GetFunctionPointerForDelegate(Launch)
            };
            var result = StartNative(elevated ? (byte)1 : (byte)0, restart ? (byte)1 : (byte)0, in callbacks);
            if (result == Result.Success) return;
            if (result == Result.CallbackFailure)
            {
                source.Failure?.Throw();
                throw new InvalidOperationException("The launcher callback failed without an exception.");
            }
            throw new InvalidOperationException(result switch
            {
                Result.ServiceMissing => failureMessage ?? "The SYSTEM service did not start.",
                Result.ServiceNotRunning => failureMessage ?? "The SYSTEM service did not reach the running state.",
                _ => "The native launcher workflow returned an invalid result."
            });
        }
        finally
        {
            // Native retains no pointers. The final reader has returned, including
            // on failure; only now may the original callbacks/strings be released.
            handle.Free();
        }
    }

    private static uint ReadVerifiedService(IntPtr context, out ServiceState result)
    {
        result = default;
        BorrowedSource? source = null;
        try
        {
            source = GetSource(context);
            var state = source.ReadService();
            if (state is not null)
            {
                WindowsServiceRegistration.RequireExpectedBinary(state, source.Backend);
                result.Present = 1;
                result.Running = state.Running ? (byte)1 : (byte)0;
            }
            return (uint)Result.Success;
        }
        catch (Exception failure)
        {
            if (source is not null) source.Failure = ExceptionDispatchInfo.Capture(failure);
            return (uint)Result.CallbackFailure;
        }
    }

    private static uint Invoke(IntPtr context, Action<BorrowedSource> action)
    {
        BorrowedSource? source = null;
        try
        {
            source = GetSource(context);
            action(source);
            return (uint)Result.Success;
        }
        catch (Exception failure)
        {
            if (source is not null) source.Failure = ExceptionDispatchInfo.Capture(failure);
            return (uint)Result.CallbackFailure;
        }
    }

    private static BorrowedSource GetSource(IntPtr context) => (BorrowedSource)GCHandle.FromIntPtr(context).Target!;

    // One call's original sources. No mirrored installation or service record.
    private sealed class BorrowedSource(string backend, Func<WindowsServiceState?> readService,
        Action startService, Action elevateService, Action launchInterface)
    {
        public string Backend { get; } = backend;
        public Func<WindowsServiceState?> ReadService { get; } = readService;
        public Action StartService { get; } = startService;
        public Action ElevateService { get; } = elevateService;
        public Action LaunchInterface { get; } = launchInterface;
        public ExceptionDispatchInfo? Failure { get; set; }
    }

    [DllImport(Library, EntryPoint = "rm_launcher_workflow_start", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern Result StartNative(byte elevated, byte restart, in Callbacks callbacks);
}
