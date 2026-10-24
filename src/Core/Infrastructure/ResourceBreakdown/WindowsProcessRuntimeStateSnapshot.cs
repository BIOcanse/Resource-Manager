using System.Diagnostics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

// A window that is not shown is not tray evidence (ATTR-10) and is not observed. No source
// proves a tray icon today, so window observation never yields tray_only.
internal sealed record ProcessRuntimeStateFact(
    string RuntimeState,
    bool ForegroundFocused,
    bool HasVisibleWindow,
    bool HasBackgroundWindow);

internal sealed record ProcessWindowPresence(
    bool HasVisibleWindow,
    bool HasBackgroundWindow);

internal sealed record WindowsProcessRuntimeStateSnapshot(
    SamplingObservationStatus Status,
    IReadOnlyDictionary<ProcessInstanceKey, ProcessRuntimeStateFact> Processes)
{
    internal static WindowsProcessRuntimeStateSnapshot NotRequested { get; } =
        new(
            SamplingObservationStatus.NotRequested,
            new Dictionary<ProcessInstanceKey, ProcessRuntimeStateFact>());

    // Both the host and helper must have passed this capture's original instance/session checks.
    internal WindowsProcessRuntimeStateSnapshot InheritHelperStates(
        IReadOnlyCollection<ProcessInstanceKey> expectedProcesses,
        IReadOnlyDictionary<int, int>? helperOwnerByProcessId,
        IReadOnlySet<ProcessInstanceKey> verifiedProcesses)
    {
        if (helperOwnerByProcessId is null || helperOwnerByProcessId.Count == 0) return this;
        var instances = expectedProcesses.GroupBy(process => process.ProcessId)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        var result = Processes.ToDictionary();
        foreach (var (helperId, hostId) in helperOwnerByProcessId)
        {
            if (!instances.TryGetValue(helperId, out var helper)) continue;
            if (verifiedProcesses.Contains(helper) && instances.TryGetValue(hostId, out var host)
                && host.StartKey <= helper.StartKey && Processes.TryGetValue(host, out var state))
                result[helper] = state;
            else
                result.Remove(helper);
        }
        return this with { Processes = result };
    }

    internal static WindowsProcessRuntimeStateSnapshot Capture(
        IReadOnlyCollection<ProcessInstanceKey> expectedProcesses,
        InteractiveDesktopObservationStore? desktops = null,
        IReadOnlyDictionary<int, int>? helperOwnerByProcessId = null)
    {
        ArgumentNullException.ThrowIfNull(expectedProcesses);
        if (desktops is not null) return desktops.Read(expectedProcesses, helperOwnerByProcessId);
        using (var current = Process.GetCurrentProcess())
            if (current.SessionId == 0) return Unavailable;
        try
        {
            var expectedByPid = new Dictionary<int, ProcessInstanceKey>();
            var ambiguousPids = new HashSet<int>();
            foreach (var expected in expectedProcesses)
            {
                if (expected.ProcessId <= 0 || expected.StartKey <= 0)
                {
                    continue;
                }
                if (!expectedByPid.TryAdd(expected.ProcessId, expected))
                {
                    ambiguousPids.Add(expected.ProcessId);
                }
            }
            foreach (var processId in ambiguousPids)
            {
                expectedByPid.Remove(processId);
            }

            var foregroundWindow = NativeMethods.GetForegroundWindow();
            int? foregroundProcessId = null;
            var ownershipIncomplete = false;
            if (foregroundWindow != IntPtr.Zero)
            {
                var threadId = NativeMethods.GetWindowThreadProcessId(
                    foregroundWindow,
                    out var processId);
                if (threadId == 0 || processId == 0 || processId > int.MaxValue)
                {
                    ownershipIncomplete = true;
                }
                else
                {
                    foregroundProcessId = checked((int)processId);
                }
            }

            var mutable = new Dictionary<int, MutableProcessWindowState>();
            var enumerated = NativeMethods.EnumWindows((window, _) =>
            {
                var threadId = NativeMethods.GetWindowThreadProcessId(
                    window,
                    out var processId);
                if (threadId == 0 || processId == 0 || processId > int.MaxValue)
                {
                    ownershipIncomplete = true;
                    return true;
                }

                var pid = checked((int)processId);
                if (!expectedByPid.ContainsKey(pid))
                {
                    return true;
                }
                if (!mutable.TryGetValue(pid, out var state))
                {
                    state = new MutableProcessWindowState();
                    mutable.Add(pid, state);
                }

                var visible = NativeMethods.IsWindowVisible(window);
                var iconic = NativeMethods.IsIconic(window);
                if (visible && !iconic)
                {
                    state.HasVisibleWindow = true;
                }
                else if (visible && iconic)
                {
                    state.HasBackgroundWindow = true;
                }
                return true;
            }, IntPtr.Zero);
            if (!enumerated)
            {
                return Unavailable;
            }

            var windowsByPid = mutable.ToDictionary(
                static pair => pair.Key,
                static pair => new ProcessWindowPresence(
                    pair.Value.HasVisibleWindow,
                    pair.Value.HasBackgroundWindow));
            return Project(
                expectedByPid.Values,
                foregroundProcessId,
                windowsByPid,
                ownershipIncomplete,
                IsSameProcessInstance,
                helperOwnerByProcessId);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException
                or OverflowException)
        {
            return Unavailable;
        }
    }

    internal static WindowsProcessRuntimeStateSnapshot Project(
        IReadOnlyCollection<ProcessInstanceKey> expectedProcesses,
        int? foregroundProcessId,
        IReadOnlyDictionary<int, ProcessWindowPresence> windowsByPid,
        bool ownershipIncomplete,
        Func<ProcessInstanceKey, bool> isSameProcessInstance,
        IReadOnlyDictionary<int, int>? helperOwnerByProcessId = null)
    {
        ArgumentNullException.ThrowIfNull(expectedProcesses);
        ArgumentNullException.ThrowIfNull(windowsByPid);
        ArgumentNullException.ThrowIfNull(isSameProcessInstance);
        var result = new Dictionary<ProcessInstanceKey, ProcessRuntimeStateFact>();
        var verifiedProcesses = new HashSet<ProcessInstanceKey>();
        foreach (var expected in expectedProcesses)
        {
            if (!isSameProcessInstance(expected))
            {
                continue;
            }
            verifiedProcesses.Add(expected);

            windowsByPid.TryGetValue(expected.ProcessId, out var windows);
            var foreground = foregroundProcessId == expected.ProcessId;
            if (ownershipIncomplete && !foreground && windows is null)
            {
                continue;
            }

            var visible = foreground || windows?.HasVisibleWindow == true;
            var background = windows?.HasBackgroundWindow == true;
            var runtimeState = foreground
                ? HostManagerRuntimeStates.ForegroundFocused
                : visible
                    ? HostManagerRuntimeStates.ForegroundUnfocused
                    : background
                        ? HostManagerRuntimeStates.BackgroundWindow
                        : HostManagerRuntimeStates.BackgroundProcess;
            result.Add(
                expected,
                new ProcessRuntimeStateFact(
                    runtimeState,
                    foreground,
                    visible,
                    background));
        }

        return new WindowsProcessRuntimeStateSnapshot(
            SamplingObservationStatus.Current,
            result).InheritHelperStates(expectedProcesses, helperOwnerByProcessId, verifiedProcesses);
    }

    private static bool IsSameProcessInstance(ProcessInstanceKey expected)
    {
        try
        {
            using var process = Process.GetProcessById(expected.ProcessId);
            return process.StartTime.ToFileTimeUtc() == expected.StartKey;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
            return false;
        }
    }

    private static WindowsProcessRuntimeStateSnapshot Unavailable { get; } =
        new(
            SamplingObservationStatus.Unavailable,
            new Dictionary<ProcessInstanceKey, ProcessRuntimeStateFact>());

    private sealed class MutableProcessWindowState
    {
        public bool HasVisibleWindow { get; set; }

        public bool HasBackgroundWindow { get; set; }
    }
}
