using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Results.Explicit;

internal sealed partial class WindowsRawInputCaptureHost
{
    private const uint WmInput = 0x00FF;
    private const uint WmInputDeviceChange = 0x00FE;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmTimer = 0x0113;
    private const uint RimInput = 0;
    private const uint GidcRemoval = 2;
    private const uint RidevRemove = 0x00000001;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDeviceNotify = 0x00002000;
    private const ushort GenericDesktopPage = 0x01;
    private const ushort MouseUsage = 0x02;
    private const ushort KeyboardUsage = 0x06;
    private const int MaximumRegisteredDevices = 4096;
    internal static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<IntPtr, WindowsRawInputCaptureHost> ByWindow = new();
    private static readonly WndProcDelegate WindowProcedure = StaticWindowProcedure;

    private readonly object listenerGate = new();
    private readonly List<Listener> listeners = [];
    private readonly WindowsRawInputDeviceKind kind;
    private readonly string className = $"ResourceManager.RawInput.{Guid.NewGuid():N}";
    private readonly TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private IntPtr window;
    private IntPtr instance;
    private ushort classAtom;
    private uint threadId;
    private UIntPtr ownershipTimerId = new(1);
    private long nextListenerId;
    private uint maximumPacketBytes = 64;
    private int registered;
    private int stopRequested;
    private int timerActive;
    private Exception? workerFailure;

    internal WindowsRawInputCaptureHost(WindowsRawInputDeviceKind kind)
    {
        this.kind = kind;
        thread = new(WorkerMain) { IsBackground = true, Name = $"RawInput-{kind}-{Guid.NewGuid():N}" };
    }

    internal int ListenerCount { get { lock (listenerGate) return listeners.Count; } }

    internal void Start()
    {
        thread.Start();
        started.Task.GetAwaiter().GetResult();
    }

    internal long AddListener(ulong handle, uint maxBytes,
        Action<WindowsRawInputNativeEvent> onInput, Action<WindowsRawInputCaptureFault> onFault)
    {
        lock (listenerGate)
        {
            if (stopped.Task.IsCompleted)
                throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                    "The Raw Input message window has stopped.");
            var id = checked(++nextListenerId);
            listeners.Add(new(id, handle, maxBytes, onInput, onFault));
            maximumPacketBytes = Math.Max(maximumPacketBytes, maxBytes);
            return id;
        }
    }

    internal bool RemoveListener(long id)
    {
        lock (listenerGate)
        {
            listeners.RemoveAll(item => item.Id == id);
            return listeners.Count == 0;
        }
    }

    internal void VerifyRegistrationOwnership()
    {
        if (window == IntPtr.Zero || Volatile.Read(ref registered) == 0 || !IsRegisteredToWindow(window))
            throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.RegistrationOwnershipLost,
                "Raw Input registration ownership changed while the explicit session was active.");
    }

    internal void NotifyAllFault(WindowsRawInputCaptureFault fault)
    {
        foreach (var listener in SnapshotListeners()) InvokeFaultIfRegistered(listener, fault);
    }

    internal void Stop()
    {
        Volatile.Write(ref stopRequested, 1);
        if (thread.IsAlive)
        {
            WindowsRawInputCaptureException? closeFailure = null;
            var stopWindow = window;
            if (stopWindow != IntPtr.Zero)
            {
                if (!PostMessageW(stopWindow, WmClose, UIntPtr.Zero, IntPtr.Zero))
                    closeFailure = NativeFailure(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                        "PostMessageW(WM_CLOSE) failed during Raw Input shutdown.");
            }
            if (stopWindow == IntPtr.Zero || closeFailure is not null)
            {
                if (!PostThreadMessageW(threadId, 0x0012, UIntPtr.Zero, IntPtr.Zero))
                {
                    var quitFailure = NativeFailure(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                        "PostThreadMessageW(WM_QUIT) failed during Raw Input shutdown.");
                    throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                        "Raw Input shutdown could not post a stop message.", quitFailure.NativeError,
                        closeFailure is null ? quitFailure : new AggregateException(closeFailure, quitFailure));
                }
                if (closeFailure is not null) Trace.TraceWarning("Raw Input shutdown used WM_QUIT after: {0}", closeFailure);
            }
        }
        try { stopped.Task.WaitAsync(ShutdownTimeout).GetAwaiter().GetResult(); }
        catch (TimeoutException error)
        {
            throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                $"Raw Input shutdown did not complete within {ShutdownTimeout.TotalSeconds} seconds.", innerException: error);
        }
    }

    internal void StopAfterFailedStart()
    {
        try { Stop(); }
        catch (Exception error) { Trace.TraceError("Raw Input cleanup after failed start failed: {0}", error); }
    }

    private void WorkerMain()
    {
        threadId = GetCurrentThreadId();
        try
        {
            CreateWindowAndRegister();
            started.TrySetResult(true);
            MessageLoop();
            if (Volatile.Read(ref stopRequested) == 0)
                throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                    "The Raw Input message loop stopped while a session still owned a registration lease.");
        }
        catch (Exception exception)
        {
            workerFailure = exception;
            if (!started.Task.IsCompleted) started.TrySetException(exception);
            else NotifyAllFault(ToFault(exception));
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (Volatile.Read(ref timerActive) != 0 && window != IntPtr.Zero)
            {
                if (!KillTimer(window, ownershipTimerId) && cleanupFailure is null)
                    cleanupFailure = NativeFailure(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                        "KillTimer failed during Raw Input window cleanup.");
                Volatile.Write(ref timerActive, 0);
            }
            try
            {
                if (Volatile.Read(ref registered) != 0)
                {
                    if (window != IntPtr.Zero && IsRegisteredToWindow(window)) RemoveRegistration();
                    else cleanupFailure = new WindowsRawInputCaptureException(
                        WindowsRawInputCaptureFailureCode.RegistrationOwnershipLost,
                        "The Raw Input registration was no longer owned by the session window during cleanup.");
                }
            }
            catch (Exception exception) { cleanupFailure = exception; }

            if (window != IntPtr.Zero)
            {
                ByWindow.TryRemove(window, out _);
                if (IsWindow(window) && !DestroyWindow(window) && cleanupFailure is null)
                    cleanupFailure = NativeFailure(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                        "DestroyWindow failed.");
                window = IntPtr.Zero;
            }

            if (classAtom != 0 && instance != IntPtr.Zero && !UnregisterClassW(className, instance)
                && cleanupFailure is null)
                cleanupFailure = NativeFailure(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                    "UnregisterClassW failed.");

            workerFailure ??= cleanupFailure;
            if (workerFailure is null) stopped.TrySetResult(true);
            else stopped.TrySetException(workerFailure);
        }
    }

    private void CreateWindowAndRegister()
    {
        instance = GetModuleHandleW(null);
        if (instance == IntPtr.Zero)
            throw NativeFailure(WindowsRawInputCaptureFailureCode.RegistrationFailure, "GetModuleHandleW failed.");

        var windowClass = new NativeWindowClassEx
        {
            Size = checked((uint)Marshal.SizeOf<NativeWindowClassEx>()),
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
            Instance = instance,
            ClassName = className
        };
        classAtom = RegisterClassExW(ref windowClass);
        if (classAtom == 0)
            throw NativeFailure(WindowsRawInputCaptureFailureCode.RegistrationFailure, "RegisterClassExW failed.");

        window = CreateWindowExW(0, className, string.Empty, 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, instance, IntPtr.Zero);
        if (window == IntPtr.Zero)
            throw NativeFailure(WindowsRawInputCaptureFailureCode.RegistrationFailure, "CreateWindowExW failed.");
        if (!ByWindow.TryAdd(window, this))
            throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                "The Raw Input message window could not be attached to its session owner.");

        EnsureNoExistingRegistration();
        var request = new NativeRawInputDevice
        {
            UsagePage = GenericDesktopPage,
            Usage = UsageFor(kind),
            Flags = RidevInputSink | RidevDeviceNotify,
            TargetWindow = window
        };
        if (!RegisterRawInputDevices(ref request, 1,
                checked((uint)Marshal.SizeOf<NativeRawInputDevice>())))
            throw NativeFailure(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                "RegisterRawInputDevices failed.");
        Volatile.Write(ref registered, 1);
        VerifyRegistrationOwnership();
        ownershipTimerId = SetTimer(window, new UIntPtr(1), 1000, IntPtr.Zero);
        if (ownershipTimerId == UIntPtr.Zero)
            throw NativeFailure(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                "SetTimer could not establish bounded Raw Input ownership checks.");
        Volatile.Write(ref timerActive, 1);
    }

    private void EnsureNoExistingRegistration()
    {
        foreach (var existing in ReadRegisteredDevices())
        {
            if (existing.UsagePage != GenericDesktopPage) continue;
            var pageOnly = (existing.Flags & 0x00000020) != 0;
            if (pageOnly || existing.Usage == UsageFor(kind))
                throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.RegistrationConflict,
                    "This process already has a Raw Input registration for the selected device class.");
        }
    }

    private void RemoveRegistration()
    {
        var request = new NativeRawInputDevice
        {
            UsagePage = GenericDesktopPage,
            Usage = UsageFor(kind),
            Flags = RidevRemove,
            TargetWindow = IntPtr.Zero
        };
        if (!RegisterRawInputDevices(ref request, 1,
                checked((uint)Marshal.SizeOf<NativeRawInputDevice>())))
            throw NativeFailure(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                "RegisterRawInputDevices(RIDEV_REMOVE) failed.");
        Volatile.Write(ref registered, 0);
    }

    private bool IsRegisteredToWindow(IntPtr hwnd)
        => ReadRegisteredDevices().Any(item => item.UsagePage == GenericDesktopPage
            && item.Usage == UsageFor(kind) && item.TargetWindow == hwnd);

    private void MessageLoop()
    {
        while (true)
        {
            var result = GetMessageW(out var message, IntPtr.Zero, 0, 0);
            if (result == -1)
                throw NativeFailure(WindowsRawInputCaptureFailureCode.NativeReadFailure, "GetMessageW failed.");
            if (result == 0) return;
            _ = TranslateMessage(ref message);
            _ = DispatchMessageW(ref message);
        }
    }

    private IntPtr ProcessWindowMessage(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == WmInput)
        {
            NativeRawInputHeader? knownHeader = null;
            try
            {
                if (lParam == IntPtr.Zero)
                    throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.NativeReadFailure,
                        "WM_INPUT did not contain an HRAWINPUT handle.");
                var header = ReadHeader(lParam);
                knownHeader = header;
                var handle = WindowsRawInputNativeRuntime.ToPublicHandle(header.Device);
                if (!HasListener(handle)) return IntPtr.Zero;
                DispatchPacket(ReadFullPacket(lParam, header, MaximumPacketBytesForListeners()));
            }
            catch (Exception exception)
            {
                var handle = knownHeader is { } header ? WindowsRawInputNativeRuntime.ToPublicHandle(header.Device) : 0;
                var fault = ToFault(exception, handle);
                if (handle == 0) NotifyAllFault(fault);
                else NotifyFaultForDevice(handle, fault);
            }
            finally
            {
                if (wParam.ToUInt64() == RimInput)
                    _ = DefWindowProcW(hwnd, message, wParam, lParam);
            }
            return IntPtr.Zero;
        }

        if (message == WmInputDeviceChange && wParam.ToUInt64() == GidcRemoval)
        {
            var handle = WindowsRawInputNativeRuntime.ToPublicHandle(lParam);
            NotifyFaultForDevice(handle, new(WindowsRawInputCaptureFailureCode.DeviceRemoved,
                "The selected Raw Input device was removed during the explicit session.", DeviceHandle: handle));
            return IntPtr.Zero;
        }
        if (message == WmTimer && wParam == ownershipTimerId)
        {
            try
            {
                if (IsRegisteredToWindow(hwnd)) return IntPtr.Zero;
            }
            catch { }

            Volatile.Write(ref registered, 0);
            Volatile.Write(ref stopRequested, 1);
            Volatile.Write(ref timerActive, 0);
            NotifyAllFault(new(WindowsRawInputCaptureFailureCode.RegistrationOwnershipLost,
                "The process Raw Input class registration no longer targets this capture window."));
            _ = DestroyWindow(hwnd);
            return IntPtr.Zero;
        }
        if (message == WmClose)
        {
            _ = DestroyWindow(hwnd);
            return IntPtr.Zero;
        }
        if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private uint MaximumPacketBytesForListeners()
    {
        lock (listenerGate) return maximumPacketBytes;
    }

    private bool HasListener(ulong handle)
    {
        lock (listenerGate) return listeners.Any(item => item.DeviceHandle == handle);
    }

    private void DispatchPacket(WindowsRawInputNativeEvent packet)
    {
        foreach (var listener in SnapshotListeners())
        {
            if (listener.DeviceHandle == packet.DeviceHandle) InvokeInputIfRegistered(listener, packet);
        }
    }

    private void NotifyFaultForDevice(ulong handle, WindowsRawInputCaptureFault fault)
    {
        foreach (var listener in SnapshotListeners())
            if (listener.DeviceHandle == handle) InvokeFaultIfRegistered(listener, fault);
    }

    private Listener[] SnapshotListeners()
    {
        lock (listenerGate) return [.. listeners];
    }

    private static void InvokeFault(Listener listener, WindowsRawInputCaptureFault fault)
    {
        try { listener.OnFault(fault); }
        catch { }
    }

    private void InvokeInputIfRegistered(Listener listener, WindowsRawInputNativeEvent packet)
    {
        lock (listenerGate)
        {
            if (!listeners.Any(item => item.Id == listener.Id)) return;
            if (packet.InputSizeBytes > listener.MaximumRawInputBytes)
            {
                InvokeFault(listener, new(WindowsRawInputCaptureFailureCode.PacketTooLarge,
                    "The selected packet exceeded this session's configured byte bound.",
                    DeviceHandle: packet.DeviceHandle));
                return;
            }
            try { listener.OnInput(packet); }
            catch (Exception exception) { InvokeFault(listener, ToFault(exception, packet.DeviceHandle)); }
        }
    }

    private void InvokeFaultIfRegistered(Listener listener, WindowsRawInputCaptureFault fault)
    {
        lock (listenerGate)
        {
            if (listeners.Any(item => item.Id == listener.Id)) InvokeFault(listener, fault);
        }
    }

    private static WindowsRawInputCaptureFault ToFault(Exception exception, ulong handle = 0)
        => exception is WindowsRawInputCaptureException raw
            ? new(raw.Code, raw.Message, raw.NativeError, handle)
            : new(WindowsRawInputCaptureFailureCode.NativeReadFailure, exception.Message,
                exception is Win32Exception win32 ? win32.NativeErrorCode : 0, handle);

    private static WindowsRawInputCaptureException NativeFailure(
        WindowsRawInputCaptureFailureCode code, string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new(code, $"{operation} Win32 error {error}.", error, new Win32Exception(error));
    }

    private static ushort UsageFor(WindowsRawInputDeviceKind kind) => kind switch
    {
        WindowsRawInputDeviceKind.Mouse => MouseUsage,
        WindowsRawInputDeviceKind.Keyboard => KeyboardUsage,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static IntPtr StaticWindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
        => ByWindow.TryGetValue(hwnd, out var host)
            ? host.ProcessWindowMessage(hwnd, message, wParam, lParam)
            : DefWindowProcW(hwnd, message, wParam, lParam);

    private sealed record Listener(long Id, ulong DeviceHandle, uint MaximumRawInputBytes,
        Action<WindowsRawInputNativeEvent> OnInput, Action<WindowsRawInputCaptureFault> OnFault);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
}
