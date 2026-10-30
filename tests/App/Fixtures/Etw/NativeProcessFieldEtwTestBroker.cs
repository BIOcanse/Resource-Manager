using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using Resource_Manager_APP.Tests.KernelEtwFixtures;

namespace Resource_Manager_APP.Tests;

/// <summary>Preserves the SDK tests' hold counters over a real native resource; never synthesizes event callbacks.</summary>
internal class ProcessFieldEtwTestBroker : IKernelEtwSessionBroker, IDisposable
{
    private readonly NativeKernelEtwTestResource resource;
    private readonly long generationOffset;
    internal int Acquires, Releases;
    internal bool Disposed;
    internal ProcessFieldEtwTestBroker(long firstGeneration = 1)
    {
        if (firstGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(firstGeneration));
        generationOffset = firstGeneration - 1;
        var configuration = NativeKernelEtwTestResource.SmallConfiguration();
        configuration.StartedTickMilliseconds = Environment.TickCount64;
        resource = new(configuration);
    }
    internal KernelTraceEventParser.Keywords Keywords => GetSnapshot().EnabledKeywords;
    internal NativeKernelEtwTestResource NativeResource => resource;
    public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        var held = resource.Acquire(request with { Bind = (source, context) =>
            request.Bind(source, context with { Generation = checked(context.Generation + generationOffset) }) });
        Interlocked.Increment(ref Acquires);
        return new Lease(this, held);
    }
    public KernelEtwSessionSnapshot GetSnapshot()
    {
        var snapshot = resource.GetSnapshot();
        return snapshot with { Generation = checked(snapshot.Generation + generationOffset) };
    }
    internal void PublishAfterWindow() => resource.Publish(checked(Environment.TickCount64 + 2101));
    public void Dispose() { if (Disposed) return; resource.Dispose(); Disposed = true; }
    private sealed class Lease(ProcessFieldEtwTestBroker owner, IKernelEtwSubscription held) : IKernelEtwSubscription
    {
        private int disposed;
        public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            held.Dispose(); Interlocked.Increment(ref owner.Releases);
        }
    }
}
