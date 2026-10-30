using Microsoft.Diagnostics.Tracing;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed partial class PresentFrameEtwSource : IPresentGraphicsApiObservationSource
{
    public event Action? ObservationChanged;
    private readonly List<GraphicsApiSubscription> graphicsApiSubscriptions = [];

    public IPresentGraphicsApiSubscription AcquireGraphicsApiSubscription(GpuPlacementProcessInstance process)
    {
        if (process.ProcessId <= 4 || process.ProcessStartKey is 0 or > long.MaxValue)
            throw new ArgumentException("An exact process instance is required.", nameof(process));
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var wasActive = HasActiveSubscriptions;
            var subscription = new GraphicsApiSubscription(this, process, ++nextHolderId);
            lock (gate) graphicsApiSubscriptions.Add(subscription);
            UpdateOutputProviders(frameLeases);
            PublishSubscriptionActivity(wasActive);
            return subscription;
        }
    }

    internal static GpuGraphicsApi? MapPresentGraphicsApi(Guid provider, int eventId)
        => provider == DxgiProvider && eventId is DxgiPresentStart or DxgiMultiplaneOverlayPresentStart
            ? GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12
            : provider == Direct3D9Provider && eventId == Direct3D9PresentStart ? GpuGraphicsApi.D3D9 : null;

    private bool ApplyGraphicsApiEvent(TraceEvent data)
    {
        if (graphicsApiSubscriptions.Count == 0) return false;
        if (data.ProviderGuid == KernelProcessProvider)
        {
            if ((int)data.ID == ProcessStart && data.PayloadByName("CreateTime") is DateTime created)
                return ObserveGraphicsApiProcessLifetimeCore(Convert.ToInt32(data.PayloadByName("ProcessID")), created.ToFileTimeUtc());
            else if ((int)data.ID == ProcessStop)
                return ObserveGraphicsApiProcessLifetimeCore(Convert.ToInt32(data.PayloadByName("ProcessID")), null);
            return false;
        }
        return ObserveGraphicsApiPresentCore(data.ProviderGuid, (int)data.ID, data.ProcessID, data.TimeStamp.ToUniversalTime());
    }

    internal bool ObserveGraphicsApiPresent(Guid provider, int eventId, int pid, DateTime timestampUtc)
    {
        bool used;
        lock (gate) used = ObserveGraphicsApiPresentCore(provider, eventId, pid, timestampUtc);
        if (used) SynchronizeGraphicsApiUse();
        return used;
    }

    private bool ObserveGraphicsApiPresentCore(Guid provider, int eventId, int pid, DateTime timestampUtc)
    {
        var api = MapPresentGraphicsApi(provider, eventId);
        if (api is null) return false;
        var used = false;
        lock (gate)
        {
            foreach (var subscription in graphicsApiSubscriptions)
                if (subscription.NeedsEvents && subscription.Process.ProcessId == pid
                    && timestampUtc.ToFileTimeUtc() >= (long)subscription.Process.ProcessStartKey)
                {
                    // Single-use identity check, only for a matching Present.
                    // PID reuse/inaccessible identity completes the old attempt;
                    // it must never archive the replacement's graphics API.
                    if (!isCurrentGraphicsApiProcess(subscription.Process))
                    {
                        subscription.Active = false;
                        subscription.Api = null;
                        used = true;
                        continue;
                    }
                    subscription.Api = api;
                    subscription.ObservedApi = api;
                    used = true;
                }
        }
        return used;
    }

    internal bool ObserveGraphicsApiProcessLifetime(int pid, long? startKey)
    {
        bool used;
        lock (gate) used = ObserveGraphicsApiProcessLifetimeCore(pid, startKey);
        if (used) SynchronizeGraphicsApiUse();
        return used;
    }

    private bool ObserveGraphicsApiProcessLifetimeCore(int pid, long? startKey)
    {
        var used = false;
        lock (gate)
        {
            foreach (var subscription in graphicsApiSubscriptions)
                if (subscription.Active && subscription.Process.ProcessId == pid
                    && startKey != (long)subscription.Process.ProcessStartKey)
                {
                    used = true;
                    subscription.Active = false;
                    subscription.Api = null;
                }
        }
        return used;
    }

    private void SynchronizeGraphicsApiUse()
    {
        lock (lifecycle)
        {
            var wasActive = HasActiveSubscriptions;
            UpdateOutputProviders(frameLeases);
            PublishSubscriptionActivity(wasActive);
        }
        ObservationChanged?.Invoke();
    }

    internal PresentHolder[] ReadHolders()
    {
        lock (lifecycle)
        lock (gate)
            return [.. frameSubscriptions.Select(item => new PresentHolder(item.Id, "frames", null, null, null, true, hasFrameCurrent)),
                .. graphicsApiSubscriptions.Select(item => new PresentHolder(item.Id, "graphics-api-history",
                    item.Process.ProcessId, item.Process.ProcessStartKey, item.Process.ExecutablePath, item.NeedsEvents, item.Active && item.Api is not null))];
    }

    internal sealed record PresentHolder(string Id, string Kind, int? ProcessId, ulong? ProcessStartKey,
        string? ExecutablePath, bool NeedsEvents, bool HasCurrent);

    private sealed class GraphicsApiSubscription(PresentFrameEtwSource source, GpuPlacementProcessInstance process, long id)
        : IPresentGraphicsApiSubscription
    {
        private PresentFrameEtwSource? owner = source;
        internal GpuPlacementProcessInstance Process { get; } = process;
        internal string Id { get; } = $"gpu-placement-api-history:{id}";
        internal bool Active { get; set; } = true;
        internal GpuGraphicsApi? Api { get; set; }
        // Historical image evidence survives process exit; Read still rejects
        // a stopped/reused live instance, while Complete must not call it empty.
        internal GpuGraphicsApi? ObservedApi { get; set; }
        internal bool NeedsEvents => Active && Api is null;
        public bool IsComplete
        {
            get
            {
                var current = Volatile.Read(ref owner);
                if (current is null) return true;
                lock (current.gate) return !ReferenceEquals(Volatile.Read(ref owner), current) || !NeedsEvents;
            }
        }
        public GpuGraphicsApi? Read()
        {
            var current = Volatile.Read(ref owner);
            if (current is null) return null;
            lock (current.gate) return ReferenceEquals(Volatile.Read(ref owner), current) && Active ? Api : null;
        }
        public void Dispose() => _ = Complete();
        public GpuGraphicsApi? Complete()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is null) return null;
            lock (current.lifecycle)
            {
                var wasActive = current.HasActiveSubscriptions;
                GpuGraphicsApi? result;
                lock (current.gate)
                {
                    result = ObservedApi;
                    current.graphicsApiSubscriptions.Remove(this);
                }
                current.UpdateOutputProviders(current.frameLeases);
                current.PublishSubscriptionActivity(wasActive);
                return result;
            }
        }
    }
}
