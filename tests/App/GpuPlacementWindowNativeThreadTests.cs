using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Infrastructure.GpuPlacement;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed partial class GpuPlacementWindowNativeThreadTests(ITestOutputHelper output)
{
    [Fact]
    public void HiddenWindowProcessesItsOwnPostedMessageBeforeAnyResize()
    {
        using var window = new HiddenWindowThread();
        window.Start();
        window.PostMessageMarker();
        var received = window.MessageMarkerReached.Wait(TimeSpan.FromSeconds(5));
        output.WriteLine("messageOnly=true; received={0}; phase={1}; dequeued={2}",
            received, window.NativePhase, string.Join(";", window.Dequeued));
        Assert.True(received);
        window.Dispose();
        WriteCleanup(window);
    }

    [Fact]
    public async Task SynchronousResizeWaitsForTheActualWindowProcedure()
    {
        using var window = new HiddenWindowThread();
        window.Start();
        var operations = WindowsGpuPlacementWindowOperations.Instance;
        Assert.True(operations.TryRead(window.Handle, out var original));
        Assert.False(original.Visible);
        window.BlockNextResize();
        uint callerThread = 0;
        var call = Task.Factory.StartNew(() =>
        {
            callerThread = GetCurrentThreadId();
            return operations.Resize(window.Handle, original.Width + 1, original.Height);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(window.ChangingEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotEqual(window.NativeThreadId, callerThread);
            Assert.False(call.IsCompleted);
            Assert.Empty(window.Changes);
            output.WriteLine("synchronousCallBlockedInsideTarget=true; hidden=true; callerThread={0}; windowThread={1}",
                callerThread, window.NativeThreadId);
        }
        finally
        {
            window.AllowResize();
            await call.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True((await call.WaitAsync(TimeSpan.FromSeconds(5))).Accepted);
        Assert.True(operations.TryRead(window.Handle, out var changed));
        Assert.Equal(original.Width + 1, changed.Width);
        var restore = await Task.Factory.StartNew(
            () => operations.Resize(window.Handle, original.Width, original.Height),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(restore.Accepted);
        Assert.True(operations.TryRead(window.Handle, out var restored));
        Assert.Equal(original, restored);
        Assert.Equal(new[] { (original.Width + 1, original.Height), (original.Width, original.Height) }, window.Changes);
        window.Dispose();
        WriteCleanup(window);
    }

    [Fact]
    public async Task QueuedResizeReturnDoesNotProveCompletionAndProbeRecoversExplicitly()
    {
        using var window = new HiddenWindowThread();
        window.Start();
        var operations = WindowsGpuPlacementWindowOperations.Instance;
        Assert.True(operations.TryRead(window.Handle, out var original));
        Assert.False(original.Visible);
        window.BlockNextResize();
        var submitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        uint callerThread = 0;
        var caller = Task.Factory.StartNew(() =>
        {
            try
            {
                callerThread = GetCurrentThreadId();
                var first = QueueResize(window.Handle, original.Width + 1, original.Height);
                Assert.True(window.ChangingEntered.Wait(TimeSpan.FromSeconds(5)));
                var second = QueueResize(window.Handle, original.Width, original.Height);
                submitted.SetResult(first && second);
                while (true)
                {
                    var result = GetMessage(out var message, 0, 0, 0);
                    if (result == 0) break;
                    if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    DispatchMessage(in message);
                }
            }
            catch (Exception exception) { submitted.TrySetException(exception); throw; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotEqual(window.NativeThreadId, callerThread);
            Assert.True(operations.TryRead(window.Handle, out var whileBlocked));
            Assert.Equal(original, whileBlocked);
            Assert.Empty(window.Changes);
            Assert.Single(window.Requests);
            window.PostMessageMarker();
            output.WriteLine("asyncCallsReturnedWhileTargetBlocked=true; immediateOriginalBounds=true; completedChanges=0; requestsWhileBlocked=1; nativePause=true");

            window.AllowResize();
            var marker = window.MessageMarkerReached.Wait(TimeSpan.FromSeconds(5));
            Assert.True(operations.TryRead(window.Handle, out var restored));
            output.WriteLine("afterRelease: marker={0}; queuedPairRestored={1}; requests={2}; changes={3}; actualSize={4}x{5}; originalSize={6}x{7}; nativePhase={8}; senderMessageLoop=true",
                marker, restored == original, string.Join(";", window.Requests), string.Join(";", window.Changes),
                restored.Width, restored.Height, original.Width, original.Height, window.NativePhase);
            Assert.True(marker);
            Assert.Equal(2, window.Requests.Length);
            Assert.Contains((original.Width + 1, original.Height), window.Changes);
            var recovery = await Task.Factory.StartNew(
                () => operations.Resize(window.Handle, original.Width, original.Height),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(recovery.Accepted);
            Assert.True(operations.TryRead(window.Handle, out var recovered));
            Assert.Equal(original, recovered);
            output.WriteLine("explicitProbeRecovery=true; productionAsyncResizeEnabled=false");
        }
        finally
        {
            window.AllowResize();
            if (callerThread != 0) PostThreadMessage(callerThread, 0x0012, 0, 0);
            await caller.WaitAsync(TimeSpan.FromSeconds(5));
        }
        window.Dispose();
        WriteCleanup(window);
    }

    private void WriteCleanup(HiddenWindowThread window)
    {
        using var process = Process.GetCurrentProcess();
        output.WriteLine("ownedPid={0}; filetime={1}; normalThreadJoin={2}; windowDestroyed={3}; classUnregistered={4}; timedRelease={5}; visibleWindowUsed=false",
            process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(), window.Joined,
            window.Destroyed, window.ClassUnregistered, window.TimedRelease);
        Assert.True(window.Joined);
        Assert.True(window.Destroyed);
        Assert.True(window.ClassUnregistered);
        Assert.False(window.TimedRelease);
    }

    [Fact]
    public async Task ReadExceptionAfterRealHiddenResizeStillRestoresNativeBounds()
    {
        using var window = new HiddenWindowThread();
        window.Start();
        window.TrackResizes();
        var native = WindowsGpuPlacementWindowOperations.Instance;
        Assert.True(native.TryRead(window.Handle, out var original));
        Assert.False(original.Visible);
        var fault = new HiddenWindowReadFault();
        var action = await Task.Factory.StartNew(
            () => GpuPlacementWindowActions.RequestResize(window.Handle, () => IsWindow(window.Handle), fault),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(action);
        Assert.Null(action.Changed);
        Assert.True(action.Restored);
        Assert.Contains("injected-read-after-native-resize", action.ChangeReadException);
        Assert.Null(action.RestoreException);
        Assert.True(native.TryRead(window.Handle, out var restored));
        Assert.Equal(original, restored);
        Assert.Equal(original.Width + 1, fault.BeforeFault.Width);
        Assert.False(fault.BeforeFault.Visible);
        Assert.Equal(new[] { (original.Width + 1, original.Height), (original.Width, original.Height) }, window.Changes);
        output.WriteLine("realNativeResizeBeforeInjectedReadFailure=true; actualHiddenBoundsRestored=true; eligibilityIsFixtureOnly=true; productionOperationUsed=true");
        window.Dispose();
        WriteCleanup(window);
    }

    private sealed class HiddenWindowReadFault : IGpuPlacementWindowOperations
    {
        private int reads;
        internal GpuPlacementWindowState BeforeFault { get; private set; }
        public bool TryRead(IntPtr window, out GpuPlacementWindowState state)
        {
            var read = WindowsGpuPlacementWindowOperations.Instance.TryRead(window, out state);
            if (++reads == 2 && read)
            {
                BeforeFault = state;
                throw new InvalidOperationException("injected-read-after-native-resize");
            }
            // Only eligibility is supplied by this fixture; no visible user window is needed.
            state = state with { Visible = true };
            return read;
        }
        public GpuPlacementWindowCall Redraw(IntPtr window) => WindowsGpuPlacementWindowOperations.Instance.Redraw(window);
        public GpuPlacementWindowCall Resize(IntPtr window, int width, int height)
            => WindowsGpuPlacementWindowOperations.Instance.Resize(window, width, height);
    }

    [WindowSenderTheory]
    [InlineData(0)]
    [InlineData(0x0046)]
    [InlineData(0x0047)]
    public async Task OwnedSenderExitAndTargetWindowCompletionAreSeparate(uint pauseMessage)
    {
        var pauseTarget = pauseMessage != 0;
        using var window = new HiddenWindowThread();
        window.Start();
        window.TrackResizes();
        var native = WindowsGpuPlacementWindowOperations.Instance;
        Assert.True(native.TryRead(window.Handle, out var original));
        Assert.False(original.Visible);
        if (pauseTarget) window.BlockNextResize(pauseMessage);
        using var target = Process.GetCurrentProcess();
        using var sender = new Process { StartInfo = new(Path.GetFullPath(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_SENDER_PROBE")!))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }};
        sender.StartInfo.ArgumentList.Add(window.Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
        sender.StartInfo.ArgumentList.Add(target.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sender.StartInfo.ArgumentList.Add(target.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(sender.Start());
        var creation = sender.StartTime.ToUniversalTime().ToFileTimeUtc();
        var stderr = sender.StandardError.ReadToEndAsync();
        try
        {
            var line = await sender.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var ready = JsonDocument.Parse(Assert.IsType<string>(line));
            Assert.True(ready.RootElement.GetProperty("ready").GetBoolean());
            Assert.Equal(sender.Id, ready.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal(creation, ready.RootElement.GetProperty("filetime").GetInt64());
            Assert.Equal(target.Id, ready.RootElement.GetProperty("targetPid").GetInt32());
            var remaining = sender.StandardOutput.ReadToEndAsync();
            if (pauseTarget)
            {
                Assert.True(window.ChangingEntered.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(sender.WaitForExit(250));
                Assert.True(native.TryRead(window.Handle, out var paused));
                Assert.Equal(pauseMessage == 0x0047 ? original.Width + 1 : original.Width, paused.Width);
                Assert.Equal(pauseMessage == 0x0047 ? 1 : 0, window.Changes.Length);
                sender.Kill();
                var endedWhilePaused = WaitForSingleObject(sender.SafeHandle, 1000) == 0;
                output.WriteLine("expectedSenderTermination=true; senderExitedWhileTargetPaused={0}; pauseMessage={1:x}; widthWhilePaused={2}",
                    endedWhilePaused, pauseMessage, paused.Width);
                window.AllowResize();
            }
            await sender.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0U, WaitForSingleObject(sender.SafeHandle, 5000));
            var senderOutput = await remaining.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(await stderr.WaitAsync(TimeSpan.FromSeconds(5)));
            if (pauseTarget) Assert.NotEqual(0, sender.ExitCode);
            else
            {
                Assert.Equal(0, sender.ExitCode);
                Assert.Contains("\"restored\":true", senderOutput);
            }
            window.PostMessageMarker();
            Assert.True(window.MessageMarkerReached.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(native.TryRead(window.Handle, out var afterExit));
            Assert.Contains(afterExit.Width, new[] { original.Width, original.Width + 1 });
            if (!pauseTarget) Assert.Equal(original, afterExit);
            output.WriteLine("ownedPid={0}; filetime={1}; senderExit={2}; targetPaused={3}; originalWidth={4}; widthAfterSenderExitAndTargetRelease={5}; changes={6}",
                sender.Id, creation, sender.ExitCode, pauseTarget, original.Width, afterExit.Width, string.Join(";", window.Changes));
            var restore = await Task.Factory.StartNew(
                () => native.Resize(window.Handle, original.Width, original.Height),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(restore.Accepted);
            Assert.True(native.TryRead(window.Handle, out var recovered));
            Assert.Equal(original, recovered);
            output.WriteLine("explicitOwnedTargetRecovery=true; productWorkerImplemented=false");
        }
        finally
        {
            window.AllowResize();
            if (!sender.HasExited)
            {
                sender.Kill();
                await sender.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0U, WaitForSingleObject(sender.SafeHandle, 5000));
                output.WriteLine("unexpectedSenderFailureCleanup=true");
            }
        }
        window.Dispose();
        WriteCleanup(window);
    }

    private static bool QueueResize(IntPtr window, int width, int height)
        => SetWindowPos(window, 0, 0, 0, width, height,
            0x0002 | 0x0004 | 0x0010 | 0x0200 | 0x4000); // NOMOVE, NOZORDER, NOACTIVATE, NOOWNERZORDER, ASYNCWINDOWPOS.

    private sealed class HiddenWindowThread : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly ConcurrentQueue<(int Width, int Height)> _changes = new();
        private readonly ConcurrentQueue<(int Width, int Height, uint Flags)> _requests = new();
        private readonly ConcurrentQueue<string> _dequeued = new();
        private readonly string _className = "ResourceManager.Gpu.Hidden." + Guid.NewGuid().ToString("N");
        private readonly WindowProcedure _procedure;
        private readonly Thread _thread;
        private Exception? _failure;
        private int _blockNext;
        private uint _blockMessage = 0x0046;
        private bool _tracking;
        private bool _started;
        private bool _disposed;

        internal HiddenWindowThread()
        {
            _procedure = Procedure;
            _thread = new Thread(Run) { IsBackground = true, Name = "GPU hidden window probe" };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        internal IntPtr Handle { get; private set; }
        internal uint NativeThreadId { get; private set; }
        internal ManualResetEventSlim ChangingEntered { get; } = new();
        internal ManualResetEventSlim MessageMarkerReached { get; } = new();
        internal (int Width, int Height)[] Changes => _changes.ToArray();
        internal (int Width, int Height, uint Flags)[] Requests => _requests.ToArray();
        internal bool Joined { get; private set; }
        internal bool Destroyed { get; private set; }
        internal bool ClassUnregistered { get; private set; }
        internal bool TimedRelease { get; private set; }
        internal string NativePhase { get; private set; } = "initial";
        internal string[] Dequeued => _dequeued.ToArray();

        internal void Start()
        {
            _started = true;
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(_failure);
            Assert.NotEqual(0, Handle);
        }

        internal void BlockNextResize(uint message = 0x0046)
        {
            _tracking = true;
            _blockMessage = message;
            Interlocked.Exchange(ref _blockNext, 1);
        }

        internal void TrackResizes() => _tracking = true;
        internal void AllowResize() => _release.Set();
        internal void PostMessageMarker() => Assert.True(PostMessage(Handle, 0x8001, 0, 0));

        private void Run()
        {
            var instance = GetModuleHandle(null);
            ushort atom = 0;
            try
            {
                NativeThreadId = GetCurrentThreadId();
                var windowClass = new WindowClass
                {
                    Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = instance,
                    Procedure = Marshal.GetFunctionPointerForDelegate(_procedure), Name = _className
                };
                atom = RegisterClassEx(in windowClass);
                if (atom == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                Handle = CreateWindowEx(0x80, _className, "GPU hidden probe", 0x80000000,
                    0, 0, 80, 60, 0, 0, instance, 0);
                if (Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                _ready.Set();
                while (true)
                {
                    var result = GetMessage(out var message, 0, 0, 0);
                    _dequeued.Enqueue($"{result}:{message.Window:x}:{message.Message:x}");
                    if (result == 0) break;
                    if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    DispatchMessage(in message);
                }
            }
            catch (Exception exception) { _failure ??= exception; }
            finally
            {
                if (Handle != 0 && IsWindow(Handle) && !DestroyWindow(Handle))
                    _failure ??= new Win32Exception(Marshal.GetLastWin32Error());
                Destroyed = Handle != 0 && !IsWindow(Handle);
                if (atom != 0) ClassUnregistered = UnregisterClass(_className, instance);
                GC.KeepAlive(_procedure);
                _ready.Set();
            }
        }

        private IntPtr Procedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            NativePhase = $"enter:{message:x}";
            if (message == 0x0046 && _tracking)
            {
                var position = Marshal.PtrToStructure<WindowPosition>(lParam);
                _requests.Enqueue((position.Width, position.Height, position.Flags));
            }
            if (message == 0x0047 && _tracking)
            {
                var position = Marshal.PtrToStructure<WindowPosition>(lParam);
                _changes.Enqueue((position.Width, position.Height));
            }
            if (message == _blockMessage && Interlocked.Exchange(ref _blockNext, 0) == 1)
            {
                ChangingEntered.Set();
                var wait = WaitForSingleObject(_release.WaitHandle.SafeWaitHandle, 10_000);
                if (wait == 258) TimedRelease = true;
                else if (wait != 0) _failure ??= new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (message == 0x8001) MessageMarkerReached.Set();
            if (message == 0x0002) PostQuitMessage(0);
            NativePhase = $"before-default:{message:x}";
            var result = DefWindowProc(window, message, wParam, lParam);
            NativePhase = $"after-default:{message:x}";
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _release.Set();
            if (_started)
            {
                if (Handle != 0) Assert.True(PostMessage(Handle, 0x0010, 0, 0));
                else if (NativeThreadId != 0) PostThreadMessage(NativeThreadId, 0x0012, 0, 0);
                Joined = _thread.Join(TimeSpan.FromSeconds(5));
                Assert.True(Joined);
            }
            _disposed = true;
            _ready.Dispose();
            _release.Dispose();
            ChangingEntered.Dispose();
            MessageMarkerReached.Dispose();
            Assert.Null(_failure);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background, MenuName;
        public string? Name;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(Microsoft.Win32.SafeHandles.SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(Microsoft.Win32.SafeHandles.SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(in WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetMessage(out WindowMessage message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(in WindowMessage message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
}

internal sealed class WindowSenderTheoryAttribute : TheoryAttribute
{
    public WindowSenderTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_SENDER_PROBE")))
            Skip = "Requires the explicitly supplied owned window sender probe.";
    }
}
