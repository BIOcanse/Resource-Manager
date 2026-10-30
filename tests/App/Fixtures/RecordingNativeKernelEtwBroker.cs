using ResourceManager.App.Infrastructure.Telemetry.Etw;
using Resource_Manager_APP.Tests.KernelEtwFixtures;

namespace Resource_Manager_APP.Tests;

internal interface INativeKernelEtwTestResourceBroker
{
    NativeKernelEtwTestResource Resource { get; }
}

internal class RecordingNativeKernelEtwBroker : IKernelEtwSessionBroker, INativeKernelEtwTestResourceBroker, IDisposable
{
    public NativeKernelEtwTestResource Resource { get; }
    internal Dictionary<string, int> Binds { get; } = [];
    internal Dictionary<string, KernelEtwEventSource> Sources { get; } = [];
    internal RecordingNativeKernelEtwBroker(NativeKernelEtwSessionConfig? configuration = null)
        => Resource = new(configuration ?? NativeKernelEtwTestResource.SmallConfiguration());
    public virtual IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
    {
        var bind = request.Bind;
        return Resource.Acquire(request with { Bind = (source, context) =>
        {
            Binds[request.Id] = Binds.GetValueOrDefault(request.Id) + 1;
            Sources[request.Id] = source;
            bind(source, context);
        }});
    }
    public virtual KernelEtwSessionSnapshot GetSnapshot() => Resource.GetSnapshot();
    public void Dispose() => Resource.Dispose();
}
