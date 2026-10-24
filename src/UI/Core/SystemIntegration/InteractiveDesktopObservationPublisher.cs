using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.NativeUi.SystemIntegration;

// Window enumeration belongs to the interactive UI, never to the Session 0 service.
internal sealed class InteractiveDesktopObservationPublisher : IDisposable
{
    private CancellationTokenSource? lifetime;

    internal void Start(BackendServiceSession session)
    {
        Stop();
        var owner = new CancellationTokenSource();
        lifetime = owner;
        _ = RunAsync(session, owner);
    }

    internal void Stop() => Interlocked.Exchange(ref lifetime, null)?.Cancel();
    public void Dispose() => Stop();

    private static async Task RunAsync(BackendServiceSession session, CancellationTokenSource owner)
    {
        try
        {
            while (!owner.IsCancellationRequested)
            {
                try
                {
                    var sample = Capture();
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

    internal static InteractiveDesktopObservation Capture()
    {
        using var publisher = Process.GetCurrentProcess();
        var publisherSession = publisher.SessionId;
        var observedAt = DateTime.UtcNow.Ticks;
        var complete = true;
        var windows = new Dictionary<int, DesktopProcessWindowObservation>();
        DesktopProcessWindowObservation? ReadIdentity(int id)
        {
            Process process;
            try { process = Process.GetProcessById(id); }
            catch (ArgumentException) { return null; } // Exited since enumeration; its windows went with it.
            using (process)
            {
                // Process.SessionId refreshes the full process list; query this PID only.
                if (ProcessIdToSessionId(checked((uint)id), out var session) && session != publisherSession)
                    return null;
                try
                {
                    return new(id, process.StartTime.ToFileTimeUtc(), false, false, false);
                }
                catch (Exception error) when (error is InvalidOperationException
                    or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // A process this user cannot open (a protected game or its anti-cheat) is still
                    // reported by PID; the service matches it against its own process inventory.
                    return new(id, DesktopProcessWindowObservation.UnverifiedCreationTime, false, false, false);
                }
            }
        }
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
            if (windows.Count >= InteractiveDesktopObservation.MaximumWindows) { complete = false; return false; }
            _ = GetWindowThreadProcessId(window, out var pid);
            if (pid is 0 or > int.MaxValue) { complete = false; return true; }
            if (!windows.TryGetValue((int)pid, out var state))
            {
                state = ReadIdentity((int)pid);
                if (state is null) return true;
            }
            var title = GetWindowTextLengthW(window) > 0;
            var visible = IsWindowVisible(window) && !IsIconic(window) && title;
            windows[(int)pid] = state with
            {
                HasVisibleWindow = state.HasVisibleWindow || visible,
                HasBackgroundWindow = state.HasBackgroundWindow || !visible && title,
                HasHiddenWindow = state.HasHiddenWindow || !title
            };
            return true;
        }, IntPtr.Zero);
        return new(publisher.Id, publisher.StartTime.ToFileTimeUtc(), publisherSession,
            observedAt, complete && enumerated, foreground is not null, foreground, windows.Values.ToArray());
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(IntPtr window);
}
