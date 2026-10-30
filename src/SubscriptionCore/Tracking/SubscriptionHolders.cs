namespace ResourceManager.Subscription;

/// <summary>One current subscription source of a tracker, read from the tracker's published source table.
/// Holder is the source key: the named holder for persistent subscriptions, the request cache key for
/// anonymous reads. IntervalMilliseconds is the explicit interval, or the role default when the holder
/// gave none (null before the role policy is published). InUse is false for an expired non-persistent
/// source that the next plan has not removed yet. ItemIds are the requested item identities.</summary>
public sealed record SubscriptionHolder(
    string Holder,
    long? IntervalMilliseconds,
    bool ExplicitInterval,
    bool Persistent,
    bool InUse,
    IReadOnlyList<string> ItemIds,
    DateTimeOffset Since,
    DateTimeOffset LastSeen);

/// <summary>Every current holder of one sampling role. Live debug read; it may be stale by the time it is used.</summary>
public sealed record SubscriptionRoleHolders(
    int RoleId,
    string RequestType,
    long? DefaultIntervalMilliseconds,
    long? MinimumIntervalMilliseconds,
    IReadOnlyList<SubscriptionHolder> Holders);
