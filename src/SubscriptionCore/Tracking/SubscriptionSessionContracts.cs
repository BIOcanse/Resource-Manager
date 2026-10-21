using ResourceManager.Subscription.Native;

namespace ResourceManager.Subscription;

/// <summary>Values supplied by the caller's compiled role policy.</summary>
public interface ISubscriptionRoleSettings
{
    int RoleId { get; }
    long DefaultIntervalMilliseconds { get; }
    long MinimumIntervalMilliseconds { get; }
    long ActiveTtlMilliseconds { get; }
    long MaximumFutureSkewMilliseconds { get; }
    long FreshnessGraceMilliseconds { get; }
}

public sealed record SubscriptionRoleSettings(int RoleId, long DefaultIntervalMilliseconds,
    long MinimumIntervalMilliseconds, long ActiveTtlMilliseconds, long MaximumFutureSkewMilliseconds,
    long FreshnessGraceMilliseconds) : ISubscriptionRoleSettings;

/// <summary>The owner keeps the native session alive until the lease is disposed.
/// OwnerToken is an opaque typed publication context; the tracker does not mutate it.</summary>
public interface ISamplingSubscriptionSessionLease<out TContext> : IDisposable
{
    NativeSamplingSubscriptionSession Session { get; }
    ISubscriptionRoleSettings HotPublish { get; }
    ulong WorkspaceIncarnation { get; }
    TContext OwnerToken { get; }
}

public interface ISamplingSubscriptionSessionOwner<TContext>
{
    ISamplingSubscriptionSessionLease<TContext> Acquire(int roleId);
    ISamplingSubscriptionSessionLease<TContext>? TryAcquireForCleanup(int roleId);
}
