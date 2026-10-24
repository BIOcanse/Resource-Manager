using ResourceManager.App.Domain.SystemHealth;
using System.Reflection;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class EtwSystemInterruptLossTests
{
    [Fact]
    public void UtcStepsDoNotChangeCoverageAdmissionOrEventExpiry()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        var utc = Start;
        var tick = Start.ToUnixTimeMilliseconds();
        using var source = new EtwSystemInterruptSnapshotSource(broker, fixture.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance, () => utc, () => tick);
        Invoke(source, "EnsureStarted");
        var binding = Capture(source, broker);
        utc = Start.AddDays(1);
        tick += 3000;
        Assert.True(Publish(source, utc));
        Assert.False(source.Read().Available);
        utc = Start.AddDays(-1);
        Interrupt(source, binding, Start.AddSeconds(4), 7);
        tick += 8000;
        Assert.True(Publish(source, utc));
        Assert.True(source.Read().Available);
        Assert.Equal(TimeSpan.FromSeconds(11), source.Read().Window);
        Assert.Equal(1, source.Read().EventCount);
        Assert.Equal(utc, source.Read().CapturedAt);
        Assert.Single(source.Read().Buckets);
        tick += 61_000;
        Assert.True(Publish(source, utc));
        Assert.True(source.Read().Available);
        Assert.Equal(0, source.Read().EventCount);
        Assert.Equal(0, source.Read().MaximumSingleDurationMilliseconds);
        source.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RequestsBothHardwareInterruptsAndDeferredProcedureCalls()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        using var source = NewSource(broker, fixture, () => Start);
        Invoke(source, "EnsureStarted");
        Assert.True(broker.Request!.Keywords.HasFlag(KernelTraceEventParser.Keywords.Interrupt));
        Assert.True(broker.Request.Keywords.HasFlag(KernelTraceEventParser.Keywords.DeferedProcedureCalls));
        source.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
    }

    [Fact]
    public void LossClearsTotalsAndModuleOwnershipThenWarmsOneNewWindow()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        var binding = Capture(source, broker);
        Image(source, binding, "old-driver.sys", Start.AddSeconds(1));
        Interrupt(source, binding, Start.AddSeconds(2), 5);
        now = Start.AddSeconds(11);
        Assert.True(Publish(source, now));
        Assert.Equal("old-driver.sys", Assert.Single(source.Read().Drivers).ModuleName);

        now = Start.AddSeconds(12);
        broker.EventsLost = 2;
        Assert.True(Publish(source, now));
        Assert.False(source.Read().Available);
        Assert.Equal(SystemInterruptProviderStates.Warming, source.Read().ProviderState.State);
        Assert.Empty(source.Read().Drivers);
        Assert.Equal(0, source.Read().EventCount);
        Assert.Equal(2, source.Read().ProviderState.EventsLost);
        // Deliveries captured before the reset cannot restore stale identity or totals.
        Image(source, binding, "late-old-driver.sys", Start.AddSeconds(11));
        Interrupt(source, binding, Start.AddSeconds(11), 200);
        Interrupt(source, binding, Start.AddSeconds(13), 6);
        Image(source, binding, "new-driver.sys", Start.AddSeconds(14));
        Interrupt(source, binding, Start.AddSeconds(15), 7);
        now = Start.AddSeconds(23);
        Assert.True(Publish(source, now));

        var recovered = source.Read();
        Assert.True(recovered.Available);
        Assert.Equal(SystemInterruptProviderStates.Running, recovered.ProviderState.State);
        Assert.Equal(2, recovered.EventCount);
        Assert.Equal(13, recovered.TotalDurationMilliseconds);
        Assert.Contains(recovered.Drivers, driver => driver.ModuleName == KernelModuleAddressMap.UnknownModuleName);
        Assert.Contains(recovered.Drivers, driver => driver.ModuleName == "new-driver.sys");
        Assert.DoesNotContain(recovered.Drivers, driver => driver.ModuleName.Contains("old", StringComparison.Ordinal));
        now = Start.AddSeconds(24);
        Assert.True(Publish(source, now));
        Assert.Equal(2, source.Read().EventCount);
        Assert.Equal(1, broker.SubscribeCount);
        now = Start.AddSeconds(73);
        Assert.True(Publish(source, now));
        var rules = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator.Recreate.Rules
            .Where(rule => HostManagerInterruptReportFactProjector.Supports(rule.FactKind)).ToArray();
        Assert.NotEmpty(rules);
        var observation = HostManagerInterruptReportFactProjector.Project(
            rules[0].SourceHandle, rules[0].CoverageScopeHandle, rules, source.Read());
        Assert.Equal(NativeReportSourceStatus.Complete, observation.Status);
        Assert.Equal(rules.Length, observation.Facts.Count);
        source.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
    }

    [Fact]
    public void ExistingCumulativeLossDoesNotPermanentlyDisableFreshStatistics()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker { EventsLost = 8 };
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        var binding = Capture(source, broker);
        Interrupt(source, binding, Start.AddSeconds(2), 3);
        now = Start.AddSeconds(11);

        Assert.True(Publish(source, now));
        Assert.True(source.Read().Available);
        Assert.Equal(1, source.Read().EventCount);
        Assert.Equal(8, source.Read().ProviderState.EventsLost);
        Assert.Equal(1, broker.SubscribeCount);
        source.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
    }

    [Fact]
    public void LossBetweenBrokerChecksSuppressesTheCandidateAlreadyBuilt()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        Interrupt(source, Capture(source, broker), Start.AddSeconds(2), 30);
        var changeAt = broker.SnapshotCount + 1;
        broker.AfterSnapshot = count => { if (count == changeAt) broker.EventsLost = 1; };
        now = Start.AddSeconds(11);

        Assert.False(Publish(source, now));
        Assert.Equal(SystemInterruptProviderStates.Warming, source.Read().ProviderState.State);
        Assert.Equal(0, source.Read().EventCount);
        Assert.Empty(source.Read().Drivers);
        Assert.Equal(1, source.Read().ProviderState.EventsLost);
        source.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
    }

    [Fact]
    public void RebindingInTheSameGenerationRejectsTheOldContainers()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        var old = Capture(source, broker);
        now = Start.AddSeconds(20);
        // Deliberately leave the old source active to isolate the container guard.
        broker.Rebind();
        var current = Capture(source, broker);
        Assert.True(old.Source.IsActive);
        Image(source, old, "old-driver.sys", Start.AddSeconds(21));
        Interrupt(source, old, Start.AddSeconds(22), 50);
        Interrupt(source, current, Start.AddSeconds(23), 4);
        now = Start.AddSeconds(31);

        Assert.True(Publish(source, now));
        Assert.Equal(1, source.Read().EventCount);
        Assert.Equal(4, source.Read().TotalDurationMilliseconds);
        Assert.Equal(KernelModuleAddressMap.UnknownModuleName, Assert.Single(source.Read().Drivers).ModuleName);
        old.Source.Cancel();
        source.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
    }

    [Fact]
    public void CancelledSourceAndRevokedOwnerCannotPublishMoreStatistics()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        var binding = Capture(source, broker);
        binding.Source.Cancel();
        Image(source, binding, "cancelled.sys", Start.AddSeconds(1));
        Interrupt(source, binding, Start.AddSeconds(2), 5);
        now = Start.AddSeconds(11);
        Assert.True(Publish(source, now));
        Assert.Empty(source.Read().Drivers);
        var current = source.Read();
        var queryCount = broker.SnapshotCount;
        Assert.Same(current, source.Read());
        Assert.Equal(queryCount, broker.SnapshotCount);
        var token = new SamplingOwnerToken(1, 1);
        token.Revoke();

        Assert.False((bool)Invoke(source, "TryPublishCurrentSnapshot", now.AddSeconds(1), token)!);
        Assert.Same(current, source.Read());
        source.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
    }

    [Fact]
    public void FirstBindingUsesItsOwnTenSecondWindowRatherThanTheKernelStartTime()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker(); // The kernel context predates this consumer.
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        now = Start.AddSeconds(9);

        Assert.True(Publish(source, now));
        Assert.False(source.Read().Available);
        Assert.Equal(TimeSpan.FromSeconds(9), source.Read().Window);
        now = Start.AddSeconds(10);
        Assert.True(Publish(source, now));
        Assert.True(source.Read().Available);
        source.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
    }

    [Fact]
    public void DisposalReleasesTheKernelLeaseAndRejectsNewDemand()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        var source = NewSource(broker, fixture, () => Start);
        var demand = source.AcquireSubscription("report", TimeSpan.FromSeconds(10));
        Invoke(source, "EnsureStarted");
        source.Dispose();
        source.Dispose();

        Assert.Equal(1, broker.DisposeCount);
        Assert.False(broker.Source.IsActive);
        Assert.Equal(SystemInterruptProviderStates.Idle, source.Read().ProviderState.State);
        Assert.Throws<ObjectDisposedException>(() => source.AcquireSubscription("late", TimeSpan.FromSeconds(10)));
        Invoke(source, "EnsureStarted");
        Assert.Equal(1, broker.SubscribeCount);
        Assert.Null(Record.Exception(demand.Dispose));
    }

    private static EtwSystemInterruptSnapshotSource NewSource(Broker broker,
        HostManagerSamplingSubscriptionTestFixture fixture, Func<DateTimeOffset> clock)
        => new(broker, fixture.Owner, NullLogger<EtwSystemInterruptSnapshotSource>.Instance, clock, () => clock().ToUnixTimeMilliseconds());

    private static bool Publish(EtwSystemInterruptSnapshotSource source, DateTimeOffset now)
        => (bool)Invoke(source, "TryPublishCurrentSnapshot", now, new SamplingOwnerToken(1, 1))!;

    private static Binding Capture(EtwSystemInterruptSnapshotSource source, Broker broker)
        => new(broker.Source, broker.Context,
            (KernelModuleAddressMap)Field(source, "modules"),
            (SystemInterruptWindowAggregator)Field(source, "aggregator"));

    private static void Image(EtwSystemInterruptSnapshotSource source, Binding binding,
        string name, DateTimeOffset at)
        => Invoke(source, "ObserveBoundImage", binding.Source, binding.Context, binding.Modules,
            0x1000UL, 0x100, name, false, at.ToUnixTimeMilliseconds());

    private static void Interrupt(EtwSystemInterruptSnapshotSource source, Binding binding,
        DateTimeOffset at, double duration)
        => Invoke(source, "ObserveBoundInterrupt", binding.Source, binding.Context,
            binding.Modules, binding.Aggregator, SystemInterruptEventKinds.Dpc, duration, 0x1010UL, at.ToUnixTimeMilliseconds());

    private static object Field(EtwSystemInterruptSnapshotSource source, string name)
        => typeof(EtwSystemInterruptSnapshotSource).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(source)!;

    private static object? Invoke(EtwSystemInterruptSnapshotSource source, string name, params object[] arguments)
        => typeof(EtwSystemInterruptSnapshotSource).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(source, arguments);

    private sealed record Binding(KernelEtwEventSource Source, KernelEtwSessionContext Context,
        KernelModuleAddressMap Modules, SystemInterruptWindowAggregator Aggregator);

    private sealed class Broker : IKernelEtwSessionBroker
    {
        internal KernelEtwSubscriptionRequest? Request;
        internal KernelEtwSessionContext Context { get; } = new(73, DateTimeOffset.UnixEpoch);
        internal KernelEtwEventSource Source { get; private set; } = new();
        internal int EventsLost;
        internal int SubscribeCount;
        internal int DisposeCount;
        internal int SnapshotCount;
        internal Action<int>? AfterSnapshot;
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            Request = request;
            SubscribeCount++;
            Rebind();
            return new Lease(this);
        }

        internal void Rebind()
        {
            Source = new KernelEtwEventSource();
            Request!.Bind(Source, Context);
            Source.Activate();
        }

        public KernelEtwSessionSnapshot GetSnapshot()
        {
            var snapshot = new KernelEtwSessionSnapshot("Running", "fixture", Context.Generation,
                Context.StartedAt, Request?.Keywords ?? KernelTraceEventParser.Keywords.None,
                1, EventsLost);
            AfterSnapshot?.Invoke(++SnapshotCount);
            return snapshot;
        }

        private sealed class Lease(Broker broker) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => broker.GetSnapshot();

            private bool disposed;
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                broker.DisposeCount++;
                broker.Source.Cancel();
            }
        }
    }
}
