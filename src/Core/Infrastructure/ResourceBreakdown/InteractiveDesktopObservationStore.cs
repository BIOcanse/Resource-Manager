using System.Diagnostics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.App.Infrastructure.Windows;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed class InteractiveDesktopObservationStore
{
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(1);
    private readonly object gate = new();
    private readonly Dictionary<int, InteractiveDesktopObservation> sessions = [];
    private readonly TimeProvider clock;
    private readonly Func<InteractiveDesktopObservation, bool> verifyPublisher;
    private readonly Func<ProcessInstanceKey, int?> readSession;

    public InteractiveDesktopObservationStore() : this(TimeProvider.System, VerifyPublisher, ReadSession) { }

    internal InteractiveDesktopObservationStore(TimeProvider clock,
        Func<InteractiveDesktopObservation, bool> verifyPublisher, Func<ProcessInstanceKey, int?> readSession)
        => (this.clock, this.verifyPublisher, this.readSession) = (clock, verifyPublisher, readSession);

    public bool Publish(InteractiveDesktopObservation observation)
    {
        var now = clock.GetUtcNow().UtcTicks;
        if (observation.SessionId <= 0 || observation.PublisherProcessId <= 4
            || observation.PublisherCreationFileTimeUtc <= 0 || !Fresh(observation, now)
            || observation.Windows is null || observation.Windows.Count > InteractiveDesktopObservation.MaximumWindows
            || observation.Windows.Any(window => window is null || window.ProcessId <= 0 || window.CreationFileTimeUtc <= 0)
            || observation.Windows.Select(window => window.ProcessId).Distinct().Count() != observation.Windows.Count
            || observation.ForegroundKnown && observation.Foreground is null
            || observation.Foreground is { } foreground && (foreground.ProcessId <= 0 || foreground.CreationFileTimeUtc <= 0)
            || !verifyPublisher(observation)) return false;
        // Own an immutable copy; caller collections cannot alter a published sample.
        observation = observation with { Windows = observation.Windows.ToArray() };
        lock (gate)
        {
            if (sessions.TryGetValue(observation.SessionId, out var previous)
                && observation.ObservedAtUtcTicks < previous.ObservedAtUtcTicks) return false;
            foreach (var stale in sessions.Where(pair => !Fresh(pair.Value, now)).Select(pair => pair.Key).ToArray())
                sessions.Remove(stale);
            if (!sessions.ContainsKey(observation.SessionId) && sessions.Count >= 64) return false;
            sessions[observation.SessionId] = observation;
        }
        return true;
    }

    internal WindowsProcessRuntimeStateSnapshot Read(IReadOnlyCollection<ProcessInstanceKey> expected)
    {
        Dictionary<int, InteractiveDesktopObservation> current;
        var now = clock.GetUtcNow().UtcTicks;
        lock (gate) current = sessions.Where(pair => Fresh(pair.Value, now)).ToDictionary();
        var windows = current.ToDictionary(pair => pair.Key,
            pair => pair.Value.Windows.ToDictionary(window => window.ProcessId));
        var result = new Dictionary<ProcessInstanceKey, ProcessRuntimeStateFact>();
        foreach (var process in expected.Distinct())
        {
            if (process.ProcessId <= 0 || process.StartKey <= 0 || process.StartKey > 2650467743999999999L) continue;
            var session = readSession(process);
            if (session is null) continue;
            if (session == 0)
            {
                result[process] = new(HostManagerRuntimeStates.BackgroundProcess, false, false, false, false);
                continue;
            }
            if (!current.TryGetValue(session.Value, out var sample)
                || DateTime.FromFileTimeUtc(process.StartKey).Ticks > sample.ObservedAtUtcTicks) continue;
            if (!sample.ForegroundKnown) continue;
            windows[session.Value].TryGetValue(process.ProcessId, out var window);
            var foreground = sample.Foreground;
            if (window is not null && window.CreationFileTimeUtc != process.StartKey
                || foreground?.ProcessId == process.ProcessId && foreground.CreationFileTimeUtc != process.StartKey) continue;
            var projected = WindowsProcessRuntimeStateSnapshot.Project([process],
                foreground?.ProcessId == process.ProcessId ? process.ProcessId : null,
                window is null ? new Dictionary<int, ProcessWindowPresence>() : new Dictionary<int, ProcessWindowPresence>
                { [process.ProcessId] = new(window.HasVisibleWindow, window.HasBackgroundWindow, window.HasHiddenWindow) },
                ownershipIncomplete: !sample.Complete, _ => true);
            foreach (var fact in projected.Processes) result[fact.Key] = fact.Value;
        }
        return new(result.Count == 0 ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current, result);
    }

    internal bool? IsForeground(int processId, long creationFileTimeUtc)
        => Read([new(processId, creationFileTimeUtc)]).Processes.TryGetValue(new(processId, creationFileTimeUtc), out var fact)
            ? fact.ForegroundFocused : null;

    private static bool Fresh(InteractiveDesktopObservation sample, long now)
        => sample.ObservedAtUtcTicks > 0 && sample.ObservedAtUtcTicks <= now
            && now - sample.ObservedAtUtcTicks <= MaximumAge.Ticks;

    private static bool VerifyPublisher(InteractiveDesktopObservation observation)
    {
        try
        {
            using var process = Process.GetProcessById(observation.PublisherProcessId);
            return NativeMethods.ProcessIdToSessionId(checked((uint)observation.PublisherProcessId), out var session)
                && session == observation.SessionId
                && process.StartTime.ToFileTimeUtc() == observation.PublisherCreationFileTimeUtc
                && StringComparer.OrdinalIgnoreCase.Equals(process.MainModule?.FileName,
                    NativeUiLaunchHostedService.ResolveNativeUiPath());
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static int? ReadSession(ProcessInstanceKey expected)
    {
        try
        {
            using var process = Process.GetProcessById(expected.ProcessId);
            return NativeMethods.ProcessIdToSessionId(checked((uint)expected.ProcessId), out var session)
                && session <= int.MaxValue && process.StartTime.ToFileTimeUtc() == expected.StartKey
                ? checked((int)session) : null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }
}
