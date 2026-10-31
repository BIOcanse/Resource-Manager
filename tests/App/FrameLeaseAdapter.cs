using ResourceManager.ReadWrite.Windows.Frames;

namespace Resource_Manager_APP.Tests;

/// <summary>Lets a test frame source that only counts plain leases answer the request overload; requests are not modelled.</summary>
internal sealed class FrameLeaseAdapter(IDisposable inner) : IFrameSubscriptionLease
{
    public void Update(FrameSubscriptionRequest request) { }
    public void Dispose() => inner.Dispose();
}
