using FanControlCore;
using System.Text.Json;

namespace ResourceManager.App.Infrastructure.Control.Writers;

public sealed partial class FanControlCoreClient
{
    private readonly object factSync = new();
    private enum Fact { Availability, RunNeeded, TemperatureKinds }
    private readonly Queue<(Fact Item, IReadOnlyList<string>? Kinds)> factChanges = new();
    private bool publishingFacts;
    private volatile bool softwareCurveRunNeeded;
    private volatile bool available = true;
    private volatile string? openFailureReason;
    private IFanControlCoreSession? observedSession;
    private Action<bool>? runNeededObserver;
    private long runNeededNotifications;
    private IReadOnlyList<string> softwareCurveTemperatureKinds = Array.Empty<string>();
    private Action<IReadOnlyList<string>>? temperatureKindsObserver;
    private long temperatureKindsNotifications;

    internal bool SoftwareCurveRunNeeded => softwareCurveRunNeeded;
    internal bool IsAvailable => available;
    internal event Action? SoftwareCurveRunNeededChanged;
    internal event Action? AvailabilityChanged;
    internal IReadOnlyList<string> SoftwareCurveTemperatureKinds => Volatile.Read(ref softwareCurveTemperatureKinds);
    internal event Action<IReadOnlyList<string>>? SoftwareCurveTemperatureKindsChanged;

    private void ObserveSession(IFanControlCoreSession opened)
    {
        long initialVersion, initialKindsVersion;
        lock (factSync)
        {
            observedSession = opened;
            initialVersion = runNeededNotifications;
            initialKindsVersion = temperatureKindsNotifications;
            runNeededObserver = value =>
            {
                lock (factSync)
                {
                    if (!ReferenceEquals(observedSession, opened)) return;
                    runNeededNotifications++;
                    SetRunNeededLocked(value);
                }
                PublishFactChanges();
            };
            temperatureKindsObserver = value =>
            {
                lock (factSync)
                {
                    if (!ReferenceEquals(observedSession, opened)) return;
                    temperatureKindsNotifications++;
                    SetTemperatureKindsLocked(value);
                }
                PublishFactChanges();
            };
        }
        opened.SoftwareCurveRunNeededChanged += runNeededObserver;
        opened.SoftwareCurveTemperatureKindsChanged += temperatureKindsObserver;
        // Subscribe before reading. A concurrent notification takes precedence over
        // this initial snapshot, so initialization cannot overwrite a newer event.
        var initial = opened.SoftwareCurveRunNeeded;
        lock (factSync)
            if (ReferenceEquals(observedSession, opened) && initialVersion == runNeededNotifications)
                SetRunNeededLocked(initial);
        var initialKinds = opened.SoftwareCurveTemperatureKinds;
        lock (factSync)
            if (ReferenceEquals(observedSession, opened) && initialKindsVersion == temperatureKindsNotifications)
                SetTemperatureKindsLocked(initialKinds);
        PublishFactChanges();
    }

    private void ForgetSession(IFanControlCoreSession? closing)
    {
        lock (factSync)
        {
            if (!ReferenceEquals(observedSession, closing)) return;
            if (closing is not null) closing.SoftwareCurveRunNeededChanged -= runNeededObserver;
            if (closing is not null) closing.SoftwareCurveTemperatureKindsChanged -= temperatureKindsObserver;
            observedSession = null;
            runNeededObserver = null;
            temperatureKindsObserver = null;
            SetRunNeededLocked(false);
            SetTemperatureKindsLocked(Array.Empty<string>());
        }
        PublishFactChanges();
    }

    private void SetRunNeededLocked(bool value)
    {
        if (softwareCurveRunNeeded == value) return;
        softwareCurveRunNeeded = value;
        factChanges.Enqueue((Fact.RunNeeded, null));
    }

    private void SetTemperatureKindsLocked(IReadOnlyList<string> value)
    {
        if (softwareCurveTemperatureKinds.SequenceEqual(value)) return;
        Volatile.Write(ref softwareCurveTemperatureKinds, value);
        factChanges.Enqueue((Fact.TemperatureKinds, value));
    }

    private void ObserveOpen(JsonElement description)
    {
        // Only an explicit successful Open or the SDK's merged, typed evidence
        // changes persistent availability. Unknown/mixed failures do not guess.
        if (!description.TryGetProperty("available", out var result)) return;
        var success = result.ValueKind == JsonValueKind.True;
        var recognised = Enum.TryParse<FanOpenFailureKind>(Text(description, "openFailureKind"), out var kind)
            && kind is FanOpenFailureKind.NoSupportedController or FanOpenFailureKind.DriverMissing or FanOpenFailureKind.AccessDenied;
        if (!success && !recognised) return;
        lock (factSync)
        {
            openFailureReason = success ? null : Text(description, "reason") ?? kind.ToString();
            if (available != success)
            {
                available = success;
                factChanges.Enqueue((Fact.Availability, null));
            }
        }
        PublishFactChanges();
    }

    // One drainer preserves mutation order, while all consumer callbacks run
    // outside the owner lock (the HardwareBridgeProcessOwner publication pattern).
    private void PublishFactChanges()
    {
        lock (factSync)
        {
            if (publishingFacts) return;
            publishingFacts = true;
        }
        while (true)
        {
            (Fact Item, IReadOnlyList<string>? Kinds) change;
            lock (factSync)
            {
                if (factChanges.Count == 0) { publishingFacts = false; return; }
                change = factChanges.Dequeue();
            }
            Delegate? observers = change.Item switch
            {
                Fact.Availability => AvailabilityChanged,
                Fact.RunNeeded => SoftwareCurveRunNeededChanged,
                _ => SoftwareCurveTemperatureKindsChanged
            };
            foreach (var observer in observers?.GetInvocationList() ?? [])
            {
                try
                {
                    if (change.Item == Fact.TemperatureKinds)
                        ((Action<IReadOnlyList<string>>)observer)(change.Kinds!);
                    else ((Action)observer)();
                }
                catch (Exception error) { logger?.LogWarning(error, "Fan owner fact observer failed."); }
            }
        }
    }

    private JsonElement UnavailableCommand()
        => JsonSerializer.SerializeToElement(new { ok = false, error = openFailureReason ?? "fan-channel-missing" });

    private async Task<IFanControlCoreSession?> WaitForRunAsync(Task<IFanControlCoreSession?> ready, CancellationToken stop)
    {
        if (ready.IsCompleted) return await ready.WaitAsync(stop).ConfigureAwait(false);
        var unavailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() { if (!IsAvailable) unavailable.TrySetResult(); }
        AvailabilityChanged += Changed;
        try
        {
            // Discovery may recognise a failure before the controller ever starts
            // a core run. Wake already admitted commands as well as new commands.
            Changed();
            if (await Task.WhenAny(ready, unavailable.Task).WaitAsync(stop).ConfigureAwait(false) == unavailable.Task)
                return null;
            return await ready.ConfigureAwait(false);
        }
        finally { AvailabilityChanged -= Changed; }
    }

    /// <summary>Borrow the parent's ready session; never open or acquire a holder.</summary>
    internal async Task RunSoftwareCurveAsync(CancellationToken stop)
    {
        IFanControlCoreSession current;
        lock (sessionSync)
        {
            if (sessionStopped || !runReady.Task.IsCompletedSuccessfully || runReady.Task.Result is not { } held)
                throw new InvalidOperationException("The parent fan run has no ready session.");
            current = held;
            if (activeCalls++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { await current.RunSoftwareCurveAsync(stop).ConfigureAwait(false); }
        finally { EndCall(); }
    }
}
