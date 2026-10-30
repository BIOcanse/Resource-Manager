using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using ResourceManager.NativeUi;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.NativeUi.SystemIntegration;

// Window enumeration belongs to the interactive UI, never to the Session 0 service.
//
// Publication contract (shared with the backend's InteractiveDesktopObservationStore and with the
// future event-driven resident publisher): the publisher holds one stream open to the backend, and
// while it is open the backend treats its latest line as the session's current observation until the
// next line replaces it; when the stream ends the backend drops it. The publisher therefore writes
// the current observation when a stream opens, then one line within one observation period whenever
// the observed content changes (foreground, the window set and each row's visible/background flags,
// completeness), and nothing while it does not change. ObservedAt alone is not a change.
internal sealed class InteractiveDesktopObservationPublisher : IDisposable, IAsyncDisposable
{
    // The period at which the desktop is observed to detect changes. A stream that ended for any
    // reason is opened again one period later.
    internal static readonly TimeSpan ObservationPeriod = TimeSpan.FromMilliseconds(200);
    private static readonly Lazy<PublisherIdentity> Publisher = new(CreatePublisherIdentity);
    private CancellationTokenSource? lifetime;
    private readonly object lifetimeSync = new();
    private Task? completion;
    private bool disposed;
    private long visibilityWindowHandle;

    internal event Action<IntPtr, bool>? HostWindowVisibilityChanged;

    internal void SetVisibilityWindow(IntPtr handle) =>
        Interlocked.Exchange(ref visibilityWindowHandle, handle.ToInt64());

    internal void Start(BackendServiceSession session)
        => _ = RunAsync(session, CancellationToken.None);

    internal Task RunAsync(BackendServiceSession session, CancellationToken cancellationToken)
    {
        var lastVisibilityWindow = IntPtr.Zero;
        bool? lastVisibility = null;
        var identities = new ProcessIdentityCache();
        return RunAsync(() =>
        {
            var visibilityWindow = new IntPtr(Interlocked.Read(ref visibilityWindowHandle));
            var sample = Capture(visibilityWindow, identities, out var hostWindowVisible);
            if (hostWindowVisible is bool visible
                && (visibilityWindow != lastVisibilityWindow || visible != lastVisibility))
            {
                lastVisibilityWindow = visibilityWindow;
                lastVisibility = visible;
                HostWindowVisibilityChanged?.Invoke(visibilityWindow, visible);
            }
            return sample;
        }, session.RunDesktopPublisherStreamAsync, cancellationToken);
    }

    // Each step of the flow is one publisher stream. The desktop is observed before the stream opens,
    // so an observation that fails opens no stream; the stream then starts with that current sample
    // and writes a line only when the observed content changes. A failed write, a failed observation
    // or the end of the request ends the step, and the next step opens a new stream.
    internal Task RunAsync(Func<InteractiveDesktopObservation> capture,
        Func<Func<Func<InteractiveDesktopObservation, CancellationToken, Task>, CancellationToken, Task>,
            CancellationToken, Task> runStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(runStream);
        return RunAsync(token =>
        {
            var current = capture();
            return runStream((write, streamToken) => WriteChangesAsync(capture, current, write, streamToken), token);
        }, cancellationToken);
    }

    // The body of one stream: the current sample first, then only changed content.
    private static async Task WriteChangesAsync(Func<InteractiveDesktopObservation> capture,
        InteractiveDesktopObservation current, Func<InteractiveDesktopObservation, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        var written = new WrittenDesktopObservation();
        while (true)
        {
            if (!written.Holds(current))
            {
                await write(current, cancellationToken).ConfigureAwait(false);
                written.Accept(current);
            }
            await Task.Delay(ObservationPeriod, cancellationToken).ConfigureAwait(false);
            current = capture();
        }
    }

    // One step at a time; the next step starts one period after the previous one ended.
    internal Task RunAsync(Func<CancellationToken, Task> observeAsync, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observeAsync);
        lock (lifetimeSync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completion is { IsCompleted: false }) return completion;
            var owner = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lifetime = owner;
            completion = RunCoreAsync(observeAsync, owner);
            return completion;
        }
    }

    internal void Stop() => _ = StopAsync();

    internal Task StopAsync()
    {
        lock (lifetimeSync)
        {
            lifetime?.Cancel();
            return completion ?? Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        lock (lifetimeSync)
        {
            disposed = true;
            lifetime?.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunCoreAsync(Func<CancellationToken, Task> observeAsync, CancellationTokenSource owner)
    {
        try
        {
            while (!owner.IsCancellationRequested)
            {
                try
                {
                    await observeAsync(owner.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception
                    or IOException)
                { Trace.WriteLine($"Interactive desktop observation unavailable: {error.Message}"); }
                await Task.Delay(ObservationPeriod, owner.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
        finally
        {
            lock (lifetimeSync)
            {
                if (ReferenceEquals(lifetime, owner)) lifetime = null;
                owner.Dispose();
            }
        }
    }

    private static DesktopProcessWindowObservation? ReadIdentity(int id, int publisherSessionId)
    {
        if (ProcessIdToSessionId(checked((uint)id), out var session)
            && session != checked((uint)publisherSessionId))
            return null;

        var processIdentity = NativeProcessCreationTimeReader.Read(
            id,
            WindowsProcessCreationTimeNativeCalls.Instance);
        if (processIdentity.HandleCloseFailed || processIdentity.Error is not null)
            Trace.WriteLine($"NativeUi could not read or release process identity for PID {id}: {processIdentity.Win32Error} {processIdentity.Error}");
        if (processIdentity.Status == ProcessCreationTimeReadStatus.Exited) return null;

        // A process this user cannot query (for example a protected game or anti-cheat) is
        // still reported by PID; the service matches it against its own process inventory.
        return new DesktopProcessWindowObservation(
            id,
            processIdentity.Status == ProcessCreationTimeReadStatus.Success
                ? processIdentity.CreationFileTimeUtc ?? DesktopProcessWindowObservation.UnverifiedCreationTime
                : DesktopProcessWindowObservation.UnverifiedCreationTime,
            false,
            false);
    }

    private static InteractiveDesktopObservation Capture(IntPtr visibilityWindow, ProcessIdentityCache cache,
        out bool? hostWindowVisible)
    {
        var publisher = Publisher.Value;
        var observedAt = DateTime.UtcNow.Ticks;
        var complete = true;
        var flags = new Dictionary<int, (bool Visible, bool Background)>();
        cache.BeginObservation();
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
        var foregroundProcessId = 0;
        if (foregroundWindow != IntPtr.Zero)
        {
            _ = GetWindowThreadProcessId(foregroundWindow, out var pid);
            if (pid is > 0 and <= int.MaxValue) foregroundProcessId = (int)pid;
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
            if (flags.Count >= InteractiveDesktopObservation.MaximumWindows) { complete = false; return false; }
            _ = GetWindowThreadProcessId(window, out var pid);
            if (pid is 0 or > int.MaxValue) { complete = false; return true; }
            cache.RecordWindow(window, (int)pid);
            flags.TryGetValue((int)pid, out var state);
            var visible = !minimized;
            flags[(int)pid] = (state.Visible || visible, state.Background || !visible);
            return true;
        }, IntPtr.Zero);
        // Identities are resolved after enumeration, once per PID, so the cache can tell whether the
        // PID still shows a window it showed last time.
        var windows = new List<DesktopProcessWindowObservation>(flags.Count);
        DesktopProcessWindowObservation? foreground = null;
        foreach (var (id, state) in flags)
        {
            var identity = cache.Resolve(id, publisher.SessionId);
            if (identity is null) continue;
            windows.Add(identity with { HasVisibleWindow = state.Visible, HasBackgroundWindow = state.Background });
            if (id == foregroundProcessId) foreground = identity;
        }
        cache.EndObservation();
        if (foregroundProcessId != 0 && !flags.ContainsKey(foregroundProcessId))
            foreground = ReadIdentity(foregroundProcessId, publisher.SessionId);
        if (foregroundWindow != IntPtr.Zero && foreground is null) complete = false;
        if (visibilityWindow != IntPtr.Zero && targetShown
            && (!targetBoundsAvailable || !enumerated || !coverageComplete || !visibilityWindowFound))
            observedHostWindowVisible = true;
        else if (visibilityWindow != IntPtr.Zero && !targetShown)
            observedHostWindowVisible = false;
        hostWindowVisible = observedHostWindowVisible;
        return new(publisher.ProcessId, publisher.StartTimeFileTimeUtc, checked((int)publisher.SessionId),
            observedAt, complete && enumerated, foreground is not null, foreground, windows.ToArray());
    }

    // Process identities (creation times) carried across observations by PID, so an unchanged desktop
    // does not reopen every windowed process each period. An entry is kept only for a PID that shows a
    // window in the current observation, and is reused only while that PID still shows at least one
    // window it showed last time: a PID whose windows were all replaced may now be another process
    // (PID reuse) and is read again.
    private sealed class ProcessIdentityCache
    {
        private Dictionary<int, DesktopProcessWindowObservation> identities = [];
        private Dictionary<int, DesktopProcessWindowObservation> nextIdentities = [];
        private Dictionary<IntPtr, int> windowOwners = [];
        private Dictionary<IntPtr, int> nextWindowOwners = [];
        private readonly HashSet<int> continued = [];

        internal void BeginObservation()
        {
            nextIdentities.Clear();
            nextWindowOwners.Clear();
            continued.Clear();
        }

        internal void RecordWindow(IntPtr window, int processId)
        {
            nextWindowOwners[window] = processId;
            if (windowOwners.TryGetValue(window, out var owner) && owner == processId) continued.Add(processId);
        }

        internal DesktopProcessWindowObservation? Resolve(int processId, int publisherSessionId)
        {
            var identity = continued.Contains(processId) && identities.TryGetValue(processId, out var cached)
                ? cached : ReadIdentity(processId, publisherSessionId);
            if (identity is not null) nextIdentities[processId] = identity;
            return identity;
        }

        // PIDs not resolved in this observation are dropped here.
        internal void EndObservation()
        {
            (identities, nextIdentities) = (nextIdentities, identities);
            (windowOwners, nextWindowOwners) = (nextWindowOwners, windowOwners);
            nextIdentities.Clear();
            nextWindowOwners.Clear();
        }
    }

    // The last observation written to the current stream, compared by content.
    private sealed class WrittenDesktopObservation
    {
        private InteractiveDesktopObservation? accepted;
        private readonly Dictionary<int, DesktopProcessWindowObservation> acceptedWindows = [];

        internal bool Holds(InteractiveDesktopObservation sample)
        {
            if (accepted is not { } previous
                || previous.PublisherProcessId != sample.PublisherProcessId
                || previous.PublisherCreationFileTimeUtc != sample.PublisherCreationFileTimeUtc
                || previous.SessionId != sample.SessionId
                || previous.Complete != sample.Complete
                || previous.ForegroundKnown != sample.ForegroundKnown
                || !Equals(previous.Foreground, sample.Foreground)
                || acceptedWindows.Count != sample.Windows.Count) return false;
            foreach (var window in sample.Windows)
                if (!acceptedWindows.TryGetValue(window.ProcessId, out var held) || held != window) return false;
            return true;
        }

        internal void Accept(InteractiveDesktopObservation sample)
        {
            accepted = sample;
            acceptedWindows.Clear();
            foreach (var window in sample.Windows) acceptedWindows[window.ProcessId] = window;
        }
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
