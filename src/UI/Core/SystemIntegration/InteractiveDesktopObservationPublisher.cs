using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using ResourceManager.NativeUi;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.NativeUi.SystemIntegration;

// Window enumeration belongs to the interactive UI, never to the Session 0 service.
internal sealed class InteractiveDesktopObservationPublisher : IDisposable
{
    private static readonly Lazy<PublisherIdentity> Publisher = new(CreatePublisherIdentity);
    private CancellationTokenSource? lifetime;
    private long visibilityWindowHandle;

    internal event Action<IntPtr, bool>? HostWindowVisibilityChanged;

    internal void SetVisibilityWindow(IntPtr handle) =>
        Interlocked.Exchange(ref visibilityWindowHandle, handle.ToInt64());

    internal void Start(BackendServiceSession session)
    {
        Stop();
        var owner = new CancellationTokenSource();
        lifetime = owner;
        _ = RunAsync(session, owner);
    }

    internal void Stop() => Interlocked.Exchange(ref lifetime, null)?.Cancel();
    public void Dispose() => Stop();

    private async Task RunAsync(BackendServiceSession session, CancellationTokenSource owner)
    {
        var lastVisibilityWindow = IntPtr.Zero;
        bool? lastVisibility = null;
        try
        {
            while (!owner.IsCancellationRequested)
            {
                try
                {
                    var visibilityWindow = new IntPtr(Interlocked.Read(ref visibilityWindowHandle));
                    var sample = Capture(visibilityWindow, out var hostWindowVisible);
                    if (hostWindowVisible is bool visible
                        && (visibilityWindow != lastVisibilityWindow || visible != lastVisibility))
                    {
                        lastVisibilityWindow = visibilityWindow;
                        lastVisibility = visible;
                        HostWindowVisibilityChanged?.Invoke(visibilityWindow, visible);
                    }
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(owner.Token);
                    request.CancelAfter(TimeSpan.FromSeconds(1));
                    await session.PublishDesktopAsync(sample, request.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { Trace.WriteLine($"Interactive desktop observation unavailable: {error.Message}"); }
                await Task.Delay(TimeSpan.FromMilliseconds(200), owner.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
        finally { owner.Dispose(); }
    }

    internal static InteractiveDesktopObservation Capture() => Capture(IntPtr.Zero, out _);

    private static InteractiveDesktopObservation Capture(IntPtr visibilityWindow, out bool? hostWindowVisible)
    {
        var publisher = Publisher.Value;
        var observedAt = DateTime.UtcNow.Ticks;
        var complete = true;
        var windows = new Dictionary<int, DesktopProcessWindowObservation>();
        var identities = new Dictionary<int, DesktopProcessWindowObservation?>();
        DesktopProcessWindowObservation? ReadIdentity(int id)
        {
            if (identities.TryGetValue(id, out var cached)) return cached;
            if (ProcessIdToSessionId(checked((uint)id), out var session)
                && session != checked((uint)publisher.SessionId))
            {
                identities[id] = null;
                return null;
            }

            var processIdentity = NativeProcessCreationTimeReader.Read(
                id,
                WindowsProcessCreationTimeNativeCalls.Instance);
            if (processIdentity.HandleCloseFailed || processIdentity.Error is not null)
                Trace.WriteLine($"NativeUi could not read or release process identity for PID {id}: {processIdentity.Win32Error} {processIdentity.Error}");
            if (processIdentity.Status == ProcessCreationTimeReadStatus.Exited)
            {
                identities[id] = null;
                return null;
            }

            // A process this user cannot query (for example a protected game or anti-cheat) is
            // still reported by PID; the service matches it against its own process inventory.
            var identity = new DesktopProcessWindowObservation(
                id,
                processIdentity.Status == ProcessCreationTimeReadStatus.Success
                    ? processIdentity.CreationFileTimeUtc ?? DesktopProcessWindowObservation.UnverifiedCreationTime
                    : DesktopProcessWindowObservation.UnverifiedCreationTime,
                false,
                false);
            identities[id] = identity;
            return identity;
        }
        var visibilityWindowFound = false;
        var coverageComplete = true;
        var coverageWindowCount = 0;
        bool? observedHostWindowVisible = null;
        hostWindowVisible = null;
        var targetShown = visibilityWindow != IntPtr.Zero
            && IsWindowVisible(visibilityWindow)
            && !IsIconic(visibilityWindow)
            && !IsWindowCloaked(visibilityWindow);
        var targetBounds = Rectangle.Empty;
        var targetBoundsAvailable = targetShown && TryGetWindowBounds(visibilityWindow, out targetBounds);
        List<Rectangle>? occluders = targetBoundsAvailable ? new List<Rectangle>() : null;
        if (visibilityWindow != IntPtr.Zero && !targetShown)
            observedHostWindowVisible = false;
        else if (targetShown && !targetBoundsAvailable)
            observedHostWindowVisible = true;
        var foregroundWindow = GetForegroundWindow();
        DesktopProcessWindowObservation? foreground = null;
        if (foregroundWindow != IntPtr.Zero)
        {
            _ = GetWindowThreadProcessId(foregroundWindow, out var pid);
            foreground = pid is > 0 and <= int.MaxValue ? ReadIdentity((int)pid) : null;
            if (foreground is null) complete = false;
        }
        var enumerated = EnumWindows((window, parameter) =>
        {
            var shown = IsWindowVisible(window);
            var minimized = IsIconic(window);
            if (visibilityWindow != IntPtr.Zero && !visibilityWindowFound)
            {
                if (window == visibilityWindow)
                {
                    visibilityWindowFound = true;
                    if (targetBoundsAvailable && coverageComplete)
                        observedHostWindowVisible = !NativeWindowOcclusion.IsFullyCovered(targetBounds, occluders!);
                }
                else if (targetBoundsAvailable && coverageComplete)
                {
                    if (coverageWindowCount >= InteractiveDesktopObservation.MaximumWindows)
                    {
                        coverageComplete = false;
                    }
                    else
                    {
                        coverageWindowCount++;
                        if (shown && !minimized && CanOcclude(window)
                            && TryGetWindowBounds(window, out var coveringBounds))
                            occluders!.Add(coveringBounds);
                    }
                }
            }
            if (!shown) return true;
            if (windows.Count >= InteractiveDesktopObservation.MaximumWindows) { complete = false; return false; }
            _ = GetWindowThreadProcessId(window, out var pid);
            if (pid is 0 or > int.MaxValue) { complete = false; return true; }
            if (!windows.TryGetValue((int)pid, out var state))
            {
                state = ReadIdentity((int)pid);
                if (state is null) return true;
            }
            var visible = !minimized;
            windows[(int)pid] = state with
            {
                HasVisibleWindow = state.HasVisibleWindow || visible,
                HasBackgroundWindow = state.HasBackgroundWindow || shown && !visible
            };
            return true;
        }, IntPtr.Zero);
        if (visibilityWindow != IntPtr.Zero && targetShown
            && (!targetBoundsAvailable || !enumerated || !coverageComplete || !visibilityWindowFound))
            observedHostWindowVisible = true;
        else if (visibilityWindow != IntPtr.Zero && !targetShown)
            observedHostWindowVisible = false;
        hostWindowVisible = observedHostWindowVisible;
        return new(publisher.ProcessId, publisher.StartTimeFileTimeUtc, checked((int)publisher.SessionId),
            observedAt, complete && enumerated, foreground is not null, foreground, windows.Values.ToArray());
    }

    private static PublisherIdentity CreatePublisherIdentity()
    {
        var processId = Environment.ProcessId;
        if (!ProcessIdToSessionId(checked((uint)processId), out var sessionId))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        using var process = Process.GetCurrentProcess();
        return new(processId, process.StartTime.ToFileTimeUtc(), checked((int)sessionId));
    }

    private static bool TryGetWindowBounds(IntPtr window, out Rectangle bounds)
    {
        if (GetWindowRect(window, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
        {
            bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return true;
        }
        bounds = Rectangle.Empty;
        return false;
    }

    private static bool CanOcclude(IntPtr window)
    {
        var extendedStyle = GetWindowLongW(window, GwlExStyle);
        if ((extendedStyle & (WsExTransparent | WsExLayered | WsExNoRedirectionBitmap)) != 0)
            return false;
        return !IsWindowCloaked(window);
    }

    private static bool IsWindowCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private sealed record PublisherIdentity(int ProcessId, long StartTimeFileTimeUtc, int SessionId);

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoRedirectionBitmap = 0x00200000;
    private const int DwmwaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLongW(IntPtr window, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int valueSize);
}
