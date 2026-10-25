using Microsoft.Diagnostics.Tracing;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

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
            var dependency = controller.AcquireDependency("present-etw",
                new(BusinessBlockRequestKind.Dependency, "graphics-api-history", "present-graphics-api-subscription", controller.CurrentPlanVersion),
                outputs: ["graphics-api"]);
            var subscription = new GraphicsApiSubscription(this, process, dependency);
            lock (gate) graphicsApiSubscriptions.Add(subscription);
            subscription.ObserveRelease();
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
                if (subscription.Active && !subscription.Released.IsCompleted && subscription.Process.ProcessId == pid
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

    private sealed class GraphicsApiSubscription : IPresentGraphicsApiSubscription, IBusinessBlockDependency
    {
        private PresentFrameEtwSource? owner;
        private readonly IBusinessBlockDependency dependency;
        internal GraphicsApiSubscription(PresentFrameEtwSource owner, GpuPlacementProcessInstance process,
            IBusinessBlockDependency dependency)
        {
            this.owner = owner;
            this.dependency = dependency;
            Process = process;
        }
        internal void ObserveRelease() => _ = dependency.Released.ContinueWith(_ => Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        internal GpuPlacementProcessInstance Process { get; }
        internal bool Active { get; set; } = true;
        internal GpuGraphicsApi? Api { get; set; }
        public Task Ready => dependency.Ready;
        public Task Released => dependency.Released;

        public GpuGraphicsApi? Read()
        {
            var current = Volatile.Read(ref owner);
            if (current is null) return null;
            lock (current.gate) return Active && !Released.IsCompleted ? Api : null;
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is null) return;
            lock (current.lifecycle)
            {
                lock (current.gate) current.graphicsApiSubscriptions.Remove(this);
            }
            dependency.Dispose();
        }

        public async ValueTask DisposeAsync() { Dispose(); await Released.ConfigureAwait(false); }
    }
}
