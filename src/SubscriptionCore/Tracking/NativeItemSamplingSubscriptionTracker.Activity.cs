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
        var now = ActivityNow(snapshot);
        foreach (var payload in snapshot.Sources)
        {
            if (IsInUse(payload, now, settings) && (matches is null || matches(payload.Request))) return true;
        }
        return false;
    }

    /// <summary>Lists the current holders from the published source table. It takes no lease, does not
    /// sample and changes no state; debug and GET use only.</summary>
    [global::ResourceManager.ReadWrite.RuntimeRead(global::ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, nameof(SubscriptionsChanged))]
    public SubscriptionRoleHolders ReadHolders()
    {
        var snapshot = Volatile.Read(ref activitySnapshot);
        var settings = CurrentSettings();
        var now = snapshot.Sources.Length == 0 || settings is null ? 0 : ActivityNow(snapshot);
        var holders = new SubscriptionHolder[snapshot.Sources.Length];
        for (var index = 0; index < holders.Length; index++)
        {
            var payload = snapshot.Sources[index];
            holders[index] = new SubscriptionHolder(
                payload.SourceKey,
                payload.ExplicitInterval is { } interval
                    ? checked((long)Math.Ceiling(interval.TotalMilliseconds))
                    : settings?.DefaultIntervalMilliseconds,
                payload.ExplicitInterval is not null,
                payload.Persistent,
                settings is not null && IsInUse(payload, now, settings),
                payload.ItemIds,
                payload.Since,
                payload.LastSeen);
        }
        Array.Sort(holders, static (left, right) => StringComparer.Ordinal.Compare(left.Holder, right.Holder));
        return new SubscriptionRoleHolders(roleId, typeof(TRequest).Name,
            settings?.DefaultIntervalMilliseconds, settings?.MinimumIntervalMilliseconds, holders);
    }

    private ulong ActivityNow(ActivitySnapshot snapshot)
    {
        var raw = RawTimestamp(clock.GetUtcNow());
        var elapsed = clock.GetElapsedTime(snapshot.CommandTimestamp, clock.GetTimestamp());
        return Math.Max(raw, checked(snapshot.CommandMilliseconds + (ulong)(elapsed.Ticks / TimeSpan.TicksPerMillisecond)));
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
