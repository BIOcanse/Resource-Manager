namespace ResourceManager.Subscription;

public partial class NativeItemSamplingSubscriptionTracker<TRequest, TContext>
{
    private ActivitySnapshot activitySnapshot = new([], 0, 0);
    private readonly List<ActivityView> activityViews = [];
    public event Action? SubscriptionsChanged;

    public bool HasInUseSubscriptions => HasInUseSubscriptionsFor(null);

    /// <summary>Classifies requests on writes, returning an allocation-free current activity read.</summary>
    public Func<bool> CreateActivityRead(Func<TRequest, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        lock (gate)
        {
            var view = new ActivityView(matches);
            activityViews.Add(view);
            PublishActivityView(view, activitySnapshot);
            return () => ReadActivity(Volatile.Read(ref view.Snapshot), null);
        }
    }

    public bool HasInUseSubscriptionsFor(Func<TRequest, bool>? matches)
    {
        return ReadActivity(Volatile.Read(ref activitySnapshot), matches);
    }

    private bool ReadActivity(ActivitySnapshot snapshot, Func<TRequest, bool>? matches)
    {
        if (snapshot.Sources.Length == 0) return false;
        var settings = CurrentSettings();
        if (settings is null) return false;
        var raw = RawTimestamp(clock.GetUtcNow());
        var elapsed = clock.GetElapsedTime(snapshot.CommandTimestamp, clock.GetTimestamp());
        var now = Math.Max(raw, checked(snapshot.CommandMilliseconds + (ulong)(elapsed.Ticks / TimeSpan.TicksPerMillisecond)));
        foreach (var payload in snapshot.Sources)
        {
            if (IsInUse(payload, now, settings) && (matches is null || matches(payload.Request))) return true;
        }
        return false;
    }

    private ISubscriptionRoleSettings? CurrentSettings()
    {
        if (owner is ISamplingSubscriptionSettingsSource settings) return settings.GetRoleSettings(roleId);
        return Volatile.Read(ref lastRoleSettings);
    }

    private static bool IsInUse(SourcePayload payload, ulong now, ISubscriptionRoleSettings settings)
        => payload.Persistent || now <= checked(payload.NativeObservedAtMilliseconds + (ulong)settings.ActiveTtlMilliseconds);

    private void RefreshActivity()
    {
        var snapshot = new ActivitySnapshot(sourcePayloads.Values.ToArray(), lastCommandMilliseconds, lastCommandTimestamp);
        Volatile.Write(ref activitySnapshot, snapshot);
        foreach (var view in activityViews) PublishActivityView(view, snapshot);
        SubscriptionsChanged?.Invoke();
    }

    private static void PublishActivityView(ActivityView view, ActivitySnapshot snapshot)
        => Volatile.Write(ref view.Snapshot, new ActivitySnapshot(
            snapshot.Sources.Where(payload => view.Matches(payload.Request)).ToArray(),
            snapshot.CommandMilliseconds, snapshot.CommandTimestamp));

    private sealed record ActivitySnapshot(SourcePayload[] Sources, ulong CommandMilliseconds, long CommandTimestamp);
    private sealed class ActivityView(Func<TRequest, bool> matches)
    {
        internal readonly Func<TRequest, bool> Matches = matches;
        internal ActivitySnapshot Snapshot = new([], 0, 0);
    }

    /// <summary>Returns per-run native output storage while preserving live subscriptions.</summary>
    internal void ReleaseRunResources()
    {
        lock (gate)
        {
            if (pendingPlanEpoch != 0 || outputOperationActive)
                throw new InvalidOperationException("Sampling must settle its current plan before returning storage.");
            EndOutputUse();
            ClosePendingOutputScratch();
            outputScratch?.Dispose();
            outputScratch = null;
            outputRows = default;
            dueBuffer = [];
            sourceBuffer = [];
            expiredBuffer = [];
            triggerBuffer = [];
        }
    }
}
