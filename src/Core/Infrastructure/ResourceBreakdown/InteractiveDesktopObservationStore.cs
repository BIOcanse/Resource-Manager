using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.ProcessIdentity;
using ResourceManager.App.Infrastructure.Windows;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed class InteractiveDesktopObservationStore
{
    private static readonly WindowsProcessBasicMetricReader BasicMetricReader = new();
    private readonly object gate = new();
    private readonly Dictionary<int, InteractiveDesktopObservation> sessions = [];
    private readonly Dictionary<int, Publisher> publishers = [];
    private readonly TimeProvider clock;
    private readonly Func<InteractiveDesktopObservation, bool> verifyPublisher;
    private readonly Func<ProcessInstanceKey, int?> readSession;

    public InteractiveDesktopObservationStore() : this(TimeProvider.System, VerifyPublisher, ReadSession) { }

    internal InteractiveDesktopObservationStore(TimeProvider clock,
        Func<InteractiveDesktopObservation, bool> verifyPublisher, Func<ProcessInstanceKey, int?> readSession)
        => (this.clock, this.verifyPublisher, this.readSession) = (clock, verifyPublisher, readSession);

    internal bool SelectPublisher(int sessionId, string connectionId, int processId,
        long creationFileTimeUtc, CancellationToken connectionClosed, CancellationToken keyRevoked = default)
    {
        if (sessionId <= 0 || string.IsNullOrWhiteSpace(connectionId) || processId <= 4
            || creationFileTimeUtc <= 0 || connectionClosed.IsCancellationRequested
            || keyRevoked.IsCancellationRequested) return false;
        lock (gate)
        {
            if (publishers.TryGetValue(sessionId, out var previous))
                return previous.ConnectionId == connectionId && previous.ProcessId == processId
                    && previous.CreationFileTimeUtc == creationFileTimeUtc;
            if (publishers.Count >= 64) return false;
            var publisher = new Publisher(connectionId, processId, creationFileTimeUtc);
            publishers.Add(sessionId, publisher);
            publisher.Closed = connectionClosed.Register(() => ReleasePublisher(sessionId, publisher));
            publisher.Revoked = keyRevoked.Register(() => ReleasePublisher(sessionId, publisher));
            if (publishers.TryGetValue(sessionId, out var selected) && ReferenceEquals(selected, publisher))
                return true;
            publisher.Closed.Unregister();
            publisher.Revoked.Unregister();
            return false;
        }
    }

    private void ReleasePublisher(int sessionId, Publisher publisher)
    {
        lock (gate)
        {
            if (publishers.TryGetValue(sessionId, out var current) && ReferenceEquals(current, publisher))
            {
                publishers.Remove(sessionId);
                sessions.Remove(sessionId);
            }
        }
        publisher.Closed.Unregister();
        publisher.Revoked.Unregister();
    }

    public bool Publish(InteractiveDesktopObservation observation, string connectionId)
    {
        var now = clock.GetUtcNow().UtcTicks;
        if (observation.SessionId <= 0 || observation.PublisherProcessId <= 4
            || observation.PublisherCreationFileTimeUtc <= 0
            || observation.ObservedAtUtcTicks <= 0 || observation.ObservedAtUtcTicks > now
            || observation.Windows is null || observation.Windows.Count > InteractiveDesktopObservation.MaximumWindows
            || observation.Windows.Any(window => window is null || window.ProcessId <= 0 || window.CreationFileTimeUtc < 0)
            || observation.Windows.Select(window => window.ProcessId).Distinct().Count() != observation.Windows.Count
            || observation.ForegroundKnown && observation.Foreground is null
            || observation.Foreground is { } foreground && (foreground.ProcessId <= 0 || foreground.CreationFileTimeUtc < 0)
            || !verifyPublisher(observation)) return false;
        // Own an immutable copy; caller collections cannot alter a published sample.
        observation = observation with { Windows = observation.Windows.ToArray() };
        lock (gate)
        {
            if (!publishers.TryGetValue(observation.SessionId, out var publisher)
                || publisher.ConnectionId != connectionId || publisher.ProcessId != observation.PublisherProcessId
                || publisher.CreationFileTimeUtc != observation.PublisherCreationFileTimeUtc) return false;
            if (sessions.TryGetValue(observation.SessionId, out var previous)
                && observation.ObservedAtUtcTicks < previous.ObservedAtUtcTicks) return false;
            if (!sessions.ContainsKey(observation.SessionId) && sessions.Count >= 64) return false;
            sessions[observation.SessionId] = observation;
        }
        return true;
    }

    internal WindowsProcessRuntimeStateSnapshot Read(IReadOnlyCollection<ProcessInstanceKey> expected,
        IReadOnlyDictionary<int, int>? helperOwnerByProcessId = null)
    {
        Dictionary<int, InteractiveDesktopObservation> current;
        lock (gate) current = sessions.ToDictionary();
        var windows = current.ToDictionary(pair => pair.Key,
            pair => pair.Value.Windows.ToDictionary(window => window.ProcessId));
        var result = new Dictionary<ProcessInstanceKey, ProcessRuntimeStateFact>();
        var verifiedProcesses = new HashSet<ProcessInstanceKey>();
        foreach (var process in expected.Distinct())
        {
            if (process.ProcessId <= 0 || process.StartKey <= 0 || process.StartKey > 2650467743999999999L) continue;
            var session = readSession(process);
            if (session is null) continue;
            if (session == 0)
            {
                verifiedProcesses.Add(process);
                result[process] = new(HostManagerRuntimeStates.BackgroundProcess, false, false, false);
                continue;
            }
            if (!current.TryGetValue(session.Value, out var sample)
                || DateTime.FromFileTimeUtc(process.StartKey).Ticks > sample.ObservedAtUtcTicks) continue;
            if (!sample.ForegroundKnown) continue;
            windows[session.Value].TryGetValue(process.ProcessId, out var window);
            var foreground = sample.Foreground;
            if (window is not null && !SameInstance(window, process)
                || foreground?.ProcessId == process.ProcessId && !SameInstance(foreground, process)) continue;
            verifiedProcesses.Add(process);
            var projected = WindowsProcessRuntimeStateSnapshot.Project([process],
                foreground?.ProcessId == process.ProcessId ? process.ProcessId : null,
                window is null ? new Dictionary<int, ProcessWindowPresence>() : new Dictionary<int, ProcessWindowPresence>
                { [process.ProcessId] = new(window.HasVisibleWindow, window.HasBackgroundWindow) },
                ownershipIncomplete: !sample.Complete, _ => true);
            foreach (var fact in projected.Processes) result[fact.Key] = fact.Value;
        }
        return new WindowsProcessRuntimeStateSnapshot(
            result.Count == 0 ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current, result)
            .InheritHelperStates(expected, helperOwnerByProcessId, verifiedProcesses);
    }

    internal bool? IsForeground(int processId, long creationFileTimeUtc)
        => Read([new(processId, creationFileTimeUtc)]).Processes.TryGetValue(new(processId, creationFileTimeUtc), out var fact)
            ? fact.ForegroundFocused : null;

    // An unverified row carries only the PID; the start-time check above already limits it to the
    // instance that existed when the desktop was observed.
    private static bool SameInstance(DesktopProcessWindowObservation row, ProcessInstanceKey process)
        => row.CreationFileTimeUtc == DesktopProcessWindowObservation.UnverifiedCreationTime
            || row.CreationFileTimeUtc == process.StartKey;

    private static bool VerifyPublisher(InteractiveDesktopObservation observation)
    {
        try
        {
            return NativeMethods.ProcessIdToSessionId(checked((uint)observation.PublisherProcessId), out var session)
                && session == observation.SessionId
                && WindowsProcessImagePathReader.TryRead(
                        observation.PublisherProcessId, observation.PublisherCreationFileTimeUtc) is not null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static int? ReadSession(ProcessInstanceKey expected)
    {
        try
        {
            return NativeMethods.ProcessIdToSessionId(checked((uint)expected.ProcessId), out var session)
                && session <= int.MaxValue
                && BasicMetricReader.ReadCurrentCreationFileTime(expected.ProcessId) == expected.StartKey
                ? checked((int)session) : null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    private sealed class Publisher(string connectionId, int processId, long creationFileTimeUtc)
    {
        internal string ConnectionId { get; } = connectionId;
        internal int ProcessId { get; } = processId;
        internal long CreationFileTimeUtc { get; } = creationFileTimeUtc;
        internal CancellationTokenRegistration Closed;
        internal CancellationTokenRegistration Revoked;
    }
}
