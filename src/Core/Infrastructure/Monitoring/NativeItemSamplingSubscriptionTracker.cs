using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Monitoring;

// Product construction only. The demand table, native planning, settlement and
// workspace replay are the single implementation in SubscriptionCore.
internal sealed class NativeItemSamplingSubscriptionTracker<TRequest> :
    ResourceManager.Subscription.NativeItemSamplingSubscriptionTracker<TRequest, SamplingOwnerToken>
{
    public NativeItemSamplingSubscriptionTracker(HostManagerSamplingSubscriptionOwner owner, int roleId,
        Func<TRequest, string> createSourceKey, Func<TRequest, IEnumerable<string>> getItemIds,
        Func<IReadOnlyList<string>, IReadOnlyList<NativeItemSamplingSubscriptionSourceView<TRequest>>, TRequest?> createRequest,
        bool coalesceItemsIntoLatestCapture,
        Func<ulong, uint, ResourceManager.Subscription.INativeSamplingOutputScratch>? createOutputScratch = null)
        : base(new HostSamplingSessionOwnerAdapter(owner), roleId, createSourceKey, getItemIds, createRequest,
            coalesceItemsIntoLatestCapture, createOutputScratch) { }
}

internal sealed class HostSamplingSessionOwnerAdapter(HostManagerSamplingSubscriptionOwner owner) :
    ISamplingSubscriptionSessionOwner<SamplingOwnerToken>
{
    public ISamplingSubscriptionSessionLease<SamplingOwnerToken> Acquire(int roleId) => owner.Acquire(roleId);
    public ISamplingSubscriptionSessionLease<SamplingOwnerToken>? TryAcquireForCleanup(int roleId) => owner.TryAcquireForCleanup(roleId);
}
