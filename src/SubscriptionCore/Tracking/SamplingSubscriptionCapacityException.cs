using ResourceManager.Subscription.Native;

namespace ResourceManager.Subscription;

/// <summary>A bounded subscription table cannot admit the requested demand.</summary>
public sealed class SamplingSubscriptionCapacityException(NativeSamplingSubscriptionStatus reason)
    : InvalidOperationException($"Sampling subscription capacity exceeded: {reason}.")
{
    public NativeSamplingSubscriptionStatus Reason { get; } = reason;
}
