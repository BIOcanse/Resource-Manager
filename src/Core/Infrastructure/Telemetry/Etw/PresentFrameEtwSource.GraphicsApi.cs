using Microsoft.Diagnostics.Tracing;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed partial class PresentFrameEtwSource : IPresentGraphicsApiObservationSource
{
    private readonly List<GraphicsApiSubscription> graphicsApiSubscriptions = [];

    public IPresentGraphicsApiSubscription AcquireGraphicsApiSubscription(GpuPlacementProcessInstance process)
    {
        if (process.ProcessId <= 4 || process.ProcessStartKey is 0 or > long.MaxValue)
            throw new ArgumentException("An exact process instance is required.", nameof(process));
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var subscription = new GraphicsApiSubscription(this, process);
            lock (gate) graphicsApiSubscriptions.Add(subscription);
            leases++;
            ReconcileDemand();
            return subscription;
        }
    }

    internal static GpuGraphicsApi? MapPresentGraphicsApi(Guid provider, int eventId)
        => provider == DxgiProvider && eventId is DxgiPresentStart or DxgiPresentStop or DxgiMultiplaneOverlayPresentStart or DxgiMultiplaneOverlayPresentStop
            ? GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12
            : provider == Direct3D9Provider && eventId is Direct3D9PresentStart or Direct3D9PresentStop ? GpuGraphicsApi.D3D9 : null;

    private void ApplyGraphicsApiEvent(TraceEvent data)
    {
        if (graphicsApiSubscriptions.Count == 0) return;
        if (data.ProviderGuid == KernelProcessProvider)
        {
            if ((int)data.ID == ProcessStart && data.PayloadByName("CreateTime") is DateTime created)
                ObserveGraphicsApiProcessLifetime(Convert.ToInt32(data.PayloadByName("ProcessID")), created.ToFileTimeUtc());
            else if ((int)data.ID == ProcessStop)
                ObserveGraphicsApiProcessLifetime(Convert.ToInt32(data.PayloadByName("ProcessID")), null);
            return;
        }
        ObserveGraphicsApiPresent(data.ProviderGuid, (int)data.ID, data.ProcessID, data.TimeStamp.ToUniversalTime());
    }

    internal void ObserveGraphicsApiPresent(Guid provider, int eventId, int pid, DateTime timestampUtc)
    {
        var api = MapPresentGraphicsApi(provider, eventId);
        if (api is null) return;
        lock (gate)
        {
            foreach (var subscription in graphicsApiSubscriptions)
                if (subscription.Active && subscription.Process.ProcessId == pid
                    && timestampUtc.ToFileTimeUtc() >= (long)subscription.Process.ProcessStartKey)
                    subscription.Api ??= api;
        }
    }

    internal void ObserveGraphicsApiProcessLifetime(int pid, long? startKey)
    {
        lock (gate)
        {
            foreach (var subscription in graphicsApiSubscriptions)
                if (subscription.Process.ProcessId == pid
                    && startKey != (long)subscription.Process.ProcessStartKey)
                {
                    subscription.Active = false;
                    subscription.Api = null;
                }
        }
    }

    private sealed class GraphicsApiSubscription(PresentFrameEtwSource owner, GpuPlacementProcessInstance process)
        : IPresentGraphicsApiSubscription
    {
        private PresentFrameEtwSource? owner = owner;
        internal GpuPlacementProcessInstance Process { get; } = process;
        internal bool Active { get; set; } = true;
        internal GpuGraphicsApi? Api { get; set; }

        public GpuGraphicsApi? Read()
        {
            var current = Volatile.Read(ref owner);
            if (current is null) return null;
            lock (current.lifecycle)
            {
                current.ReconcileDemand();
                lock (current.gate) return !current.disposed && !current.failed && Active ? Api : null;
            }
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is null) return;
            lock (current.lifecycle)
            {
                lock (current.gate) current.graphicsApiSubscriptions.Remove(this);
                if (current.leases > 0 && --current.leases == 0) current.Stop();
            }
        }
    }
}
