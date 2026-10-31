using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests.KernelEtwFixtures;

/// <summary>Holder/status assertions around a real native owner; no managed renderer history or publication fabrication.</summary>
internal sealed class NativeRendererTestBroker : IKernelEtwSessionBroker, IDisposable
{
    private readonly NativeKernelEtwTestResource resource = new(NativeKernelEtwTestResource.SmallConfiguration(qpcFrequency: 1000));
    private readonly bool publishOnAcquire;
    private IKernelEtwSubscription? sibling;
    private string? state;
    private KernelTraceEventParser.Keywords? keywords;
    private long tick;
    private int acquires, releases;
    internal KernelEtwSubscriptionRequest? Request { get; private set; }
    internal KernelEtwEventSource Source { get; private set; } = null!;
    internal int SubscribeCount => Volatile.Read(ref acquires);
    internal int DisposeCount => Volatile.Read(ref releases);
    internal int Acquires => SubscribeCount;
    internal int Releases => DisposeCount;
    internal NativeKernelEtwTestResource Resource => resource;
    internal ulong PublishedSequence
    {
        get
        {
            var cursor = new KernelEtwNativeNotifications.Cursor();
            KernelEtwNativeNotifications.Read(resource.Session, ref cursor, static (_, _) => { });
            return cursor.Publication;
        }
    }

    internal NativeRendererTestBroker(bool publishOnAcquire = true, bool keepSiblingLease = false)
    {
        this.publishOnAcquire = publishOnAcquire;
        if (keepSiblingLease)
            sibling = resource.Acquire(new("renderer-test-sibling", KernelTraceEventParser.Keywords.Process
                | KernelTraceEventParser.Keywords.ImageLoad,
                static (_, _) => { }) { NativeOutputs = KernelEtwNativeOutputs.Renderer });
    }

    public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
    {
        Request = request;
        var lease = resource.Acquire(request with
        {
            Bind = (source, context) =>
            {
                Source = source;
                request.Bind(source, context);
            }
        });
        Interlocked.Increment(ref acquires);
        if (publishOnAcquire) resource.Publish(tick);
        return new Held(this, lease);
    }

    public KernelEtwSessionSnapshot GetSnapshot()
    {
        var actual = resource.GetSnapshot();
        return actual with { State = state ?? actual.State, EnabledKeywords = keywords ?? actual.EnabledKeywords };
    }

    internal void ProcessChanged(KernelEtwEventSource source, int pid, bool processStart = false)
    {
        if (!ReferenceEquals(source, Source) || !source.IsActive || pid <= 0) return;
        var at = ++tick;
        if (processStart) resource.RawProcess(at, checked((uint)pid), 0, "renderer.exe", checked((ulong)at));
        else resource.RawImage(at, checked((uint)pid), 0x10000, 0x1000, @"C:\Apps\renderer.dll");
        resource.Publish(at);
    }

    internal void SetEventsLost(int value)
    {
        resource.Loss(++tick, checked((ulong)value));
        resource.Publish(tick);
    }
    internal void ResetHistory() => SetEventsLost(checked(resource.GetSnapshot().EventsLost + 1));
    internal void Publish() => resource.Publish(++tick);
    internal void SetState(string value) => state = value == "Running" ? null : value;
    internal void SetKeywords(KernelTraceEventParser.Keywords value) => keywords = value;
    internal void Rebind(long generation)
    {
        resource.Rebind();
        if (resource.Generation != generation) throw new InvalidOperationException("The test expected a different real native generation.");
        resource.Publish(++tick);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref sibling, null)?.Dispose();
        resource.Dispose();
    }

    private sealed class Held(NativeRendererTestBroker owner, IKernelEtwSubscription lease) : IKernelEtwSubscription
    {
        private int disposed;
        public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { lease.Dispose(); }
            finally { Interlocked.Increment(ref owner.releases); }
        }
    }
}
