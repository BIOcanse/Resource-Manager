using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerReportCoordinatorOwnerTests
{
    [Theory]
    [InlineData("no-interrupt-rule")]
    [InlineData("coordinator-stop")]
    [InlineData("last-lease")]
    public async Task InterruptHolderHistoryEndsWhenItsLastRealConsumerLeaves(string withdrawal)
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new TimedInterruptBroker();
        using var source = new EtwSystemInterruptSnapshotSource(broker, sampling.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance);
        var registration = new BusinessBlockRegistration(BackendUids.EtwSystemInterruptSnapshotSource,
            _ => throw new InvalidOperationException())
        { HostedExecution = new(static service => ((EtwSystemInterruptSnapshotSource)service).ExecuteTask, false) };
        var diagnostics = new BusinessBlockDiagnosticsRegistry([registration],
            NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        var registry = new HostedBusinessBlockRegistry(diagnostics, [registration]);
        await using var tree = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, Func<IServiceProvider, IBusinessBlock>>
            {
                [BackendUids.EtwSystemInterruptSnapshotSource] = _ => registry.GetOrAdd(
                    BackendUids.EtwSystemInterruptSnapshotSource, () => source)
            });
        StateRead<bool> ReadActivity() => StateRead<bool>.FromValue(sampling.Owner.Activity.HasInUseSubscriptions(5));
        var publication = tree.Authority.ClaimReadFunction<bool>(BackendItems.SamplingInterruptsInUse, ReadActivity);
        var binding = new SubscriptionActivityFacts.ActivityBinding(ReadActivity,
            () => publication.NotifyChangedAsync(BusinessBlockTestSupport.Reason()));
        var notificationFailures = new ConcurrentQueue<Exception>();
        async Task Notify()
        {
            try { await binding.NotifyAsync(); }
            catch (Exception error) { notificationFailures.Enqueue(error); }
        }
        void Changed(int role) { if (role == 5) _ = Notify(); }
        sampling.Owner.Activity.RoleChanged += Changed;
        await binding.NotifyAsync();
        await tree.InitializeHostPermitsAsync();
        var sources = new ReportDemandSources { InterruptSource = source };
        await using var coordinator = DemandOwner(ReportFamilyPlan(withdrawal == "last-lease" ? 120 : 121), sources, out var provider);
        IDisposable? first = null, second = null;
        try
        {
            Assert.Empty(broker.Holders);
            Assert.Equal(BusinessBlockState.Stopped, tree.Scheduler.ReadState(BackendUids.EtwSystemInterruptSnapshotSource));
            await coordinator.StartAsync(default);
            if (withdrawal == "last-lease")
            {
                await coordinator.RefreshReportsAsync(default);
                Assert.False(ReadActivity().Value);
                Assert.Empty(broker.Holders); // A plan without interrupt facts never starts the source.
                first = source.AcquireSubscription("first", TimeSpan.FromSeconds(10));
                second = source.AcquireSubscription("second", TimeSpan.FromSeconds(10));
            }
            await BusinessBlockTestSupport.Until(() => broker.Holders.Length == 2
                && tree.Scheduler.ReadState(BackendUids.EtwSystemInterruptSnapshotSource) == BusinessBlockState.Running);
            Assert.True(ReadActivity().Value);
            Assert.Collection(broker.Holders.OrderBy(value => value.Id),
                metadata => Assert.Equal(("system-interrupts", KernelTraceEventParser.Keywords.ImageLoad), (metadata.Id, metadata.Keywords)),
                events => Assert.Equal(("system-interrupts-events", KernelTraceEventParser.Keywords.Interrupt
                    | KernelTraceEventParser.Keywords.DeferedProcedureCalls), (events.Id, events.Keywords)));
            if (withdrawal == "no-interrupt-rule")
            {
                provider.Publish(ResourceManager.App.Domain.RuntimeSpecialization.CompiledRuntimePlan.Default with
                { Version = 2, HostManager = ReportFamilyPlan(120) });
                await coordinator.RefreshReportsAsync(default);
            }
            else if (withdrawal == "coordinator-stop") await coordinator.StopAsync(default);
            else
            {
                first!.Dispose(); first = null;
                Assert.True(ReadActivity().Value);
                Assert.Equal(2, broker.Holders.Length);
                second!.Dispose(); second = null;
            }
            await BusinessBlockTestSupport.Until(() => broker.Holders.Length == 0
                && tree.Scheduler.ReadState(BackendUids.EtwSystemInterruptSnapshotSource) == BusinessBlockState.Stopped);
            Assert.False(ReadActivity().Value);
            var history = broker.History.ToArray();
            Assert.Equal(4, history.Length); // Two registrations, followed by their two returns.
            Assert.Equal(new[] { 1, 2, 1, 0 }, history.Select(value => value.Holders.Length));
            Assert.True(history.Zip(history.Skip(1), (left, right) => left.Tick <= right.Tick).All(value => value));
            Assert.Empty(notificationFailures);
        }
        finally
        {
            first?.Dispose(); second?.Dispose();
            await coordinator.StopAsync(default);
            await BusinessBlockTestSupport.Until(() => broker.Holders.Length == 0);
            sampling.Owner.Activity.RoleChanged -= Changed;
        }
    }

    private sealed class TimedInterruptBroker : IKernelEtwSessionBroker
    {
        private readonly object gate = new();
        private readonly List<KernelEtwSubscriptionRequest> held = [];
        internal ConcurrentQueue<(long Tick, KernelEtwSubscriptionRequest[] Holders)> History { get; } = new();
        internal KernelEtwSubscriptionRequest[] Holders { get { lock (gate) return held.ToArray(); } }
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            var source = new KernelEtwEventSource();
            lock (gate) { held.Add(request); History.Enqueue((Stopwatch.GetTimestamp(), held.ToArray())); }
            request.Bind(source, new(1, DateTimeOffset.UtcNow));
            source.Activate();
            return new TimedLease(this, request, source);
        }
        public KernelEtwSessionSnapshot GetSnapshot() => new("Running", "timed-fixture", 1, DateTimeOffset.UtcNow,
            Holders.Aggregate(KernelTraceEventParser.Keywords.None, (all, value) => all | value.Keywords), Holders.Length, 0);
        private sealed class TimedLease(TimedInterruptBroker owner, KernelEtwSubscriptionRequest request, KernelEtwEventSource source) : IKernelEtwSubscription
        {
            private int disposed;
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                source.Cancel();
                lock (owner.gate) { owner.held.Remove(request); owner.History.Enqueue((Stopwatch.GetTimestamp(), owner.held.ToArray())); }
            }
        }
    }
}
