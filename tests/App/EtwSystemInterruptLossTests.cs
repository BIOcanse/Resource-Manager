using Resource_Manager_APP.Tests.KernelEtwFixtures;
using Resource_Manager_APP.Tests.Oracles.KernelEtw;
using System.Buffers.Binary;
using ResourceManager.App.Domain.SystemHealth;
using System.Reflection;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class EtwSystemInterruptLossTests
{
    [Fact]
    public void InternalInterruptRejectionsAreVisibleInThePhysicalSessionDiagnostics()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        var binding = Capture(source, broker);
        RawInterrupt(binding, Start.AddSeconds(1), 2);
        now = Start.AddSeconds(5);
        // A tracked new binding has its own positive floor. This tests stale
        // event rejection without the retired compute-zone freeze transition.
        KernelEtwNativeReaders.ResetInterruptBinding(binding.Native, now.ToUnixTimeMilliseconds());
        broker.Resource.Publish(now.ToUnixTimeMilliseconds());
        RawInterrupt(binding, Start.AddSeconds(4), 2);
        RawInterrupt(binding, now, 0); // A valid integer-QPC record with an invalid zero duration.

        var session = Assert.Single(EtwEventDropDiagnostics.Read().Sessions.Where(value => value.Name == broker.Resource.DiagnosticName));
        var prefix = "holder/system-interrupts/" + SystemInterruptEventKinds.Dpc;
        var total = Assert.Single(session.Kinds.Where(value => value.Kind == prefix));
        Assert.Equal((3L, 2L), (total.Received, total.Dropped));
        foreach (var reason in new[] { "before-window", "invalid-duration" })
        {
            var rejected = Assert.Single(session.Kinds.Where(value => value.Kind == prefix + "/rejected:" + reason));
            Assert.Equal((1L, 1L), (rejected.Received, rejected.Dropped));
        }
        Assert.DoesNotContain(session.Kinds, value => value.Kind == prefix + "/rejected:frozen");
        // Raw ETW has no managed callback generation or floating-point duration.
        // This production dispatcher also proves cancellation admits no new uses.
        binding.Source.Cancel();
        RawInterrupt(binding, now, 2);
        RawInterrupt(binding, now.AddSeconds(1), 2);
        var after = Assert.Single(EtwEventDropDiagnostics.Read().Sessions.Where(value => value.Name == broker.Resource.DiagnosticName));
        var retained = Assert.Single(after.Kinds.Where(value => value.Kind == prefix));
        Assert.Equal((3L, 2L), (retained.Received, retained.Dropped));
        var physical = Assert.Single(after.Kinds.Where(value => value.Kind == "PerfInfoDPC"));
        Assert.Equal((5L, 0L), (physical.Received, physical.Dropped));
        foreach (var reason in new[] { "generation-mismatch", "inactive-binding" })
        {
            var absent = Assert.Single(after.Kinds.Where(value => value.Kind == prefix + "/rejected:" + reason));
            Assert.Equal((0L, 0L), (absent.Received, absent.Dropped));
        }
        now = now.AddSeconds(11);
        Assert.False(Publish(source, broker, now));
        Assert.Empty(source.Read().Drivers);
        Assert.False(source.Read().Available);
    }

    [Fact]
    public void UserModeImagesAreRejectedAndKernelImagesStillOwnRoutines()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        var binding = Capture(source, broker);
        foreach (var opcode in new byte[] { 10, 3, 2 })
            binding.Resource.RawImage(0, 41, 0x7FF6_0000_0000UL, 0x10_0000, "user.dll", opcode);
        binding.Resource.RawImage(0, 0, 0xFFFF_F800_0000_1000UL, 0x100, "driver.sys");
        RawInterrupt(binding, Start.AddSeconds(1), 3);
        now = Start.AddSeconds(11);

        Assert.True(Publish(source, broker, now));
        Assert.Equal("driver.sys", Assert.Single(source.Read().Drivers).ModuleName);
        Assert.Equal(1UL, Assert.IsType<KernelEtwInterruptMetadata>(binding.Native.QueryInterruptMetadata()).ActiveModules);
        var session = Assert.Single(EtwEventDropDiagnostics.Read().Sessions.Where(value => value.Name == broker.Resource.DiagnosticName));
        foreach (var (kind, received, dropped) in new[] { ("ImageLoadOrRundown", 3L, 2L), ("ImageUnload", 1L, 1L) })
        {
            var prefix = "holder/system-interrupts/" + kind;
            var total = Assert.Single(session.Kinds.Where(value => value.Kind == prefix));
            Assert.Equal((received, dropped), (total.Received, total.Dropped));
            var rejected = Assert.Single(session.Kinds.Where(value => value.Kind == prefix + "/rejected:user-mode-image"));
            Assert.Equal((dropped, dropped), (rejected.Received, rejected.Dropped));
        }
    }

    [Fact]
    public void UtcStepsDoNotChangeCoverageAdmissionOrEventExpiry()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        var utc = Start;
        var tick = Start.ToUnixTimeMilliseconds();
        using var source = new EtwSystemInterruptSnapshotSource(broker, fixture.Owner,
            NullLogger<EtwSystemInterruptSnapshotSource>.Instance, () => utc, () => tick);
        broker.ReadTick = () => tick;
        Invoke(source, "EnsureStarted");
        var binding = Capture(source, broker);
        utc = Start.AddDays(1);
        tick += 3000;
        Assert.True(Publish(source, broker, utc));
        Assert.False(source.Read().Available);
        utc = Start.AddDays(-1);
        Interrupt(source, binding, Start.AddSeconds(4), 7);
        tick += 8000;
        Assert.True(Publish(source, broker, utc));
        Assert.True(source.Read().Available);
        Assert.Equal(TimeSpan.FromSeconds(11), source.Read().Window);
        Assert.Equal(1, source.Read().EventCount);
        // A native publication is anchored once at session construction. A UTC
        // query jump cannot relabel that same measured bank as the old caller-
        // stamped implementation did; monotonic elapsed ticks still set coverage.
        Assert.Equal(Start.AddMilliseconds(tick - Start.ToUnixTimeMilliseconds()), source.Read().CapturedAt);
        Assert.NotEqual(utc, source.Read().CapturedAt);
        Assert.Single(source.Read().Buckets);
        tick += 61_000;
        Assert.True(Publish(source, broker, utc));
        Assert.True(source.Read().Available);
        Assert.Equal(0, source.Read().EventCount);
        Assert.Equal(0, source.Read().MaximumSingleDurationMilliseconds);
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PublishedNativeModuleOwnerTracksImagesThroughoutItsRun()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        var binding = Capture(source, broker);
        Image(source, binding, "retained-driver.sys", now.AddSeconds(1));
        now = now.AddSeconds(2);
        Publish(source, broker, now);
        var before = Assert.IsType<KernelEtwInterruptMetadata>(binding.Native.QueryInterruptMetadata());
        Assert.Equal(binding.Context.Generation, before.SessionGeneration);
        Assert.NotEqual(0UL, before.OwnerSerial);
        Assert.Equal(1UL, before.ActiveModules);

        // A normal ImageLoad at the same base replaces the range on the same
        // native owner. No compute-zone mode changes or duration pauses exist.
        now = now.AddSeconds(1);
        Image(source, binding, "replacement-driver.sys", now);
        broker.Resource.Publish(now.ToUnixTimeMilliseconds());
        var changed = Assert.IsType<KernelEtwInterruptMetadata>(binding.Native.QueryInterruptMetadata());
        Assert.Equal(before.SessionGeneration, changed.SessionGeneration);
        Assert.Equal(before.OwnerSerial, changed.OwnerSerial);
        Assert.Equal(1UL, changed.ActiveModules);
        Assert.True(changed.ModuleRevision > before.ModuleRevision);
        Assert.Equal(1, broker.MetadataSubscribeCount);
        Assert.Equal(0, broker.MetadataDisposeCount);

        now = now.AddSeconds(1);
        Interrupt(source, binding, now, 3);
        now = now.AddSeconds(10);
        Assert.True(Publish(source, broker, now));
        Assert.Equal("replacement-driver.sys", Assert.Single(source.Read().Drivers).ModuleName);
        Assert.Equal(1, source.Read().EventCount);
        Assert.Equal(3, source.Read().TotalDurationMilliseconds);
    }


    [Fact]
    public void RequestsBothHardwareInterruptsAndDeferredProcedureCalls()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        using var source = NewSource(broker, fixture, () => Start);
        Invoke(source, "EnsureStarted");
        Assert.True(broker.Request!.Keywords.HasFlag(KernelTraceEventParser.Keywords.Interrupt));
        Assert.True(broker.Request.Keywords.HasFlag(KernelTraceEventParser.Keywords.DeferedProcedureCalls));
        Assert.Equal(KernelTraceEventParser.Keywords.ImageLoad, broker.MetadataRequest!.Keywords);
        Assert.Equal(2, broker.SubscribeCount);
    }

    [Fact]
    public void LossClearsTotalsAndModuleOwnershipThenWarmsOneNewWindow()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        var binding = Capture(source, broker);
        Image(source, binding, "old-driver.sys", Start.AddSeconds(1));
        Interrupt(source, binding, Start.AddSeconds(2), 5);
        now = Start.AddSeconds(11);
        Assert.True(Publish(source, broker, now));
        Assert.Equal("old-driver.sys", Assert.Single(source.Read().Drivers).ModuleName);

        now = Start.AddSeconds(12);
        broker.EventsLost = 2;
        Assert.True(Publish(source, broker, now));
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
        Assert.True(Publish(source, broker, now));

        var recovered = source.Read();
        Assert.True(recovered.Available);
        Assert.Equal(SystemInterruptProviderStates.Running, recovered.ProviderState.State);
        Assert.Equal(2, recovered.EventCount);
        Assert.Equal(13, recovered.TotalDurationMilliseconds);
        Assert.Contains(recovered.Drivers, driver => driver.ModuleName == OracleKernelModuleAddressMap.UnknownModuleName);
        Assert.Null(Assert.Single(recovered.Drivers.Where(driver => driver.ModuleName == OracleKernelModuleAddressMap.UnknownModuleName)).ModulePath);
        Assert.Contains(recovered.Drivers, driver => driver.ModuleName == "new-driver.sys");
        Assert.DoesNotContain(recovered.Drivers, driver => driver.ModuleName.Contains("old", StringComparison.Ordinal));
        now = Start.AddSeconds(24);
        Assert.True(Publish(source, broker, now));
        Assert.Equal(2, source.Read().EventCount);
        Assert.Equal(1, broker.MetadataSubscribeCount);
        now = Start.AddSeconds(73);
        Assert.True(Publish(source, broker, now));
        var rules = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator.Recreate.Rules
            .Where(rule => HostManagerInterruptReportFactProjector.Supports(rule.FactKind)).ToArray();
        Assert.NotEmpty(rules);
        var observation = HostManagerInterruptReportFactProjector.Project(
            rules[0].SourceHandle, rules[0].CoverageScopeHandle, rules, source.Read());
        Assert.Equal(NativeReportSourceStatus.Complete, observation.Status);
        Assert.Equal(rules.Length, observation.Facts.Count);
    }

    [Fact]
    public void ExistingCumulativeLossDoesNotPermanentlyDisableFreshStatistics()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker { EventsLost = 8 };
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        // Apply the run-entry enable command at the run boundary, rather than
        // letting the first IRQ two seconds later become the activation boundary.
        broker.Resource.Publish(now.ToUnixTimeMilliseconds());
        ulong sequence = 0;
        Assert.True(KernelEtwNativeReaders.ReadInterrupts(broker.Resource.Session, ref sequence,
            static view => (view.EventsLost, view.StartedTick), out var initial, out var hasPublication));
        Assert.True(hasPublication);
        Assert.Equal(8UL, initial.EventsLost);
        Assert.Equal(now.ToUnixTimeMilliseconds(), initial.StartedTick);
        var binding = Capture(source, broker);
        Interrupt(source, binding, Start.AddSeconds(2), 3);
        now = Start.AddSeconds(11);

        Assert.True(Publish(source, broker, now));
        Assert.True(source.Read().Available);
        Assert.Equal(1, source.Read().EventCount);
        Assert.Equal(8, source.Read().ProviderState.EventsLost);
        Assert.Equal(1, broker.MetadataSubscribeCount);
    }

    [Fact]
    public void LossBetweenBrokerChecksSuppressesTheCandidateAlreadyBuilt()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        Interrupt(source, Capture(source, broker), Start.AddSeconds(2), 30);
        var changeAt = broker.SnapshotCount + 1;
        broker.AfterSnapshot = count => { if (count == changeAt) broker.EventsLost = 1; };
        now = Start.AddSeconds(11);

        Assert.False(Publish(source, broker, now));
        Assert.Equal(SystemInterruptProviderStates.Warming, source.Read().ProviderState.State);
        Assert.Equal(0, source.Read().EventCount);
        Assert.Empty(source.Read().Drivers);
        Assert.Equal(1, source.Read().ProviderState.EventsLost);
    }

    [Fact]
    public void RebindingRejectsTheOldNativeSessionAndItsWindow()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        var old = Capture(source, broker);
        now = Start.AddSeconds(20);
        // A real replacement closes the previous native owner; there is no live old callback container.
        broker.Rebind();
        var current = Capture(source, broker);
        Assert.False(old.Source.IsActive);
        Assert.Throws<ObjectDisposedException>(() => old.Native.AcquireReadLease());
        Assert.NotEqual(old.Context.Generation, current.Context.Generation);
        Interrupt(source, current, Start.AddSeconds(23), 4);
        now = Start.AddSeconds(31);

        Assert.True(Publish(source, broker, now));
        Assert.Equal(1, source.Read().EventCount);
        Assert.Equal(4, source.Read().TotalDurationMilliseconds);
        Assert.Equal(OracleKernelModuleAddressMap.UnknownModuleName, Assert.Single(source.Read().Drivers).ModuleName);
        Assert.Null(Assert.Single(source.Read().Drivers).ModulePath);
        old.Source.Cancel();
    }

    [Fact]
    public void SameGenerationRebindingRejectsTheOldCandidateWhileItsSourceRemainsActive()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        var old = Capture(source, broker);
        Image(source, old, "old-driver.sys", Start.AddSeconds(1));
        Interrupt(source, old, Start.AddSeconds(2), 50);
        now = Start.AddSeconds(20);
        broker.Resource.Publish(now.ToUnixTimeMilliseconds());
        // Replace the metadata binding after the old native bank was copied,
        // but before its publication is admitted. Neither source generation nor
        // old IsActive can stand in for the current container identity.
        var rebindAt = broker.SnapshotCount + 3;
        broker.AfterSnapshot = count =>
        {
            if (count != rebindAt) return;
            broker.AfterSnapshot = null;
            broker.RebindInSameGeneration();
        };
        Assert.False((bool)Invoke(source, "TryPublishCurrentSnapshot", now, new SamplingOwnerToken(1, 1))!);
        var current = Capture(source, broker);
        Assert.True(old.Source.IsActive);
        Assert.NotSame(old.Source, current.Source);
        Assert.Equal(old.Context.Generation, current.Context.Generation);
        Assert.Same(old.Native, current.Native);
        using (old.Native.AcquireReadLease()) { }
        Assert.Empty(source.Read().Drivers);
        Assert.Equal(0, source.Read().EventCount);

        broker.Resource.Publish(now.ToUnixTimeMilliseconds()); // Apply the new binding's tracked reset.
        Interrupt(source, current, Start.AddSeconds(23), 4);
        now = Start.AddSeconds(31);
        Assert.True(Publish(source, broker, now));
        Assert.Equal(1, source.Read().EventCount);
        Assert.Equal(4, source.Read().TotalDurationMilliseconds);
        Assert.Equal(OracleKernelModuleAddressMap.UnknownModuleName, Assert.Single(source.Read().Drivers).ModuleName);
        Assert.Null(Assert.Single(source.Read().Drivers).ModulePath);
        old.Source.Cancel();
        current.Source.Cancel();
    }

    [Fact]
    public void CancelBeforePublishRejectsNativeStatisticsAndRevokedOwnerCannotReplaceTheEmptyResult()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        var binding = Capture(source, broker);
        binding.Source.Cancel();
        Image(source, binding, "cancelled-driver.sys", Start.AddSeconds(1));
        Interrupt(source, binding, Start.AddSeconds(2), 5);
        now = Start.AddSeconds(11);
        Assert.False(Publish(source, broker, now));
        Assert.True(broker.GetSnapshot().IsRunning); // Canceling a binding did not replace the native resource.
        Assert.Empty(source.Read().Drivers);
        Assert.Equal(0, source.Read().EventCount);
        Assert.False(source.Read().Available);
        var empty = source.Read();
        var revoked = new SamplingOwnerToken(1, 1);
        revoked.Revoke();
        Assert.False((bool)Invoke(source, "TryPublishCurrentSnapshot", now.AddSeconds(1), revoked)!);
        Assert.Same(empty, source.Read());
    }

    [Fact]
    public void UnavailableDurationHoldDoesNotFallBackToRunningMetadata()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        Interrupt(source, Capture(source, broker), Start.AddSeconds(2), 5);
        now = Start.AddSeconds(11);
        Assert.True(Publish(source, broker, now));
        Assert.True(source.Read().Available);
        broker.DurationAvailable = false;
        Assert.True(broker.GetSnapshot().IsRunning);
        Assert.False(Publish(source, broker, now));
        Assert.False(source.Read().Available);
        Assert.Equal(SystemInterruptProviderStates.Unavailable, source.Read().ProviderState.State);
        Assert.Equal("The controlled interrupt duration hold is unavailable.", source.Read().ProviderState.Message);
        Assert.Empty(source.Read().Drivers);
    }

    [Fact]
    public async Task NativeInterruptIntakeDoesNotWaitForTheManagedSnapshotCoordinationGate()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        var binding = Capture(source, broker);
        var eventGate = (object)Field(source, "eventGate");
        Task callback = Task.CompletedTask;
        Monitor.Enter(eventGate);
        try
        {
            callback = Task.Run(() => Interrupt(source, binding, Start.AddSeconds(2), 8));
            Assert.True(callback.Wait(TimeSpan.FromSeconds(5)),
                "Native interrupt intake waited for the managed snapshot coordination gate.");
        }
        finally
        {
            Monitor.Exit(eventGate);
        }
        await callback;

        now = Start.AddSeconds(11);
        Assert.True(Publish(source, broker, now));
        Assert.Equal(1, source.Read().EventCount);
    }

    [Fact]
    public void ReturnedNativeSourceAndRevokedOwnerCannotPublishMoreStatistics()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        var binding = Capture(source, broker);
        Image(source, binding, "returned.sys", Start.AddSeconds(1));
        Interrupt(source, binding, Start.AddSeconds(2), 5);
        now = Start.AddSeconds(11);
        Assert.True(Publish(source, broker, now));
        Assert.Single(source.Read().Drivers);
        var current = source.Read();
        Invoke(source, "StopSession", "explicit fixture run return", SystemInterruptProviderStates.Idle, false);
        Assert.Throws<ObjectDisposedException>(() => binding.Native.AcquireReadLease());
        Assert.False(binding.Source.IsActive);
        var queryCount = broker.SnapshotCount;
        Assert.Same(current, source.Read());
        Assert.Equal(queryCount, broker.SnapshotCount);
        var token = new SamplingOwnerToken(1, 1);
        token.Revoke();

        Assert.False((bool)Invoke(source, "TryPublishCurrentSnapshot", now.AddSeconds(1), token)!);
        Assert.Same(current, source.Read());
    }

    [Fact]
    public void FirstBindingUsesItsOwnTenSecondWindowRatherThanTheKernelStartTime()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker(); // The kernel context predates this consumer.
        var now = Start;
        using var source = NewSource(broker, fixture, () => now);
        Invoke(source, "EnsureStarted");
        now = Start.AddSeconds(9);

        Assert.True(Publish(source, broker, now));
        Assert.False(source.Read().Available);
        Assert.Equal(TimeSpan.FromSeconds(9), source.Read().Window);
        now = Start.AddSeconds(10);
        Assert.True(Publish(source, broker, now));
        Assert.True(source.Read().Available);
    }

    [Fact]
    public void DisposalReleasesTheKernelLeaseAndRejectsNewDemand()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        var source = NewSource(broker, fixture, () => Start);
        var demand = source.AcquireSubscription("report", TimeSpan.FromSeconds(10));
        Invoke(source, "EnsureStarted");
        source.Dispose();
        source.Dispose();

        Assert.Equal(1, broker.MetadataDisposeCount);
        Assert.False(broker.Source.IsActive);
        Assert.Equal(SystemInterruptProviderStates.Idle, source.Read().ProviderState.State);
        Assert.Throws<ObjectDisposedException>(() => source.AcquireSubscription("late", TimeSpan.FromSeconds(10)));
        Invoke(source, "EnsureStarted");
        Assert.Equal(1, broker.MetadataSubscribeCount);
        Assert.Null(Record.Exception(demand.Dispose));
    }

    private static EtwSystemInterruptSnapshotSource NewSource(Broker broker,
        HostManagerSamplingSubscriptionTestFixture fixture, Func<DateTimeOffset> clock)
    {
        broker.ReadTick = () => clock().ToUnixTimeMilliseconds();
        return new(broker, fixture.Owner, NullLogger<EtwSystemInterruptSnapshotSource>.Instance,
            clock, () => broker.ReadTick());
    }
    private static bool Publish(EtwSystemInterruptSnapshotSource source, Broker broker, DateTimeOffset now)
    {
        broker.Resource.Publish(broker.ReadTick());
        return (bool)Invoke(source, "TryPublishCurrentSnapshot", now, new SamplingOwnerToken(1, 1))!;
    }
    private static Binding Capture(EtwSystemInterruptSnapshotSource source, Broker broker)
        => new(broker.Source, broker.Context, broker.Resource.Session, broker.Resource);
    private static void Image(EtwSystemInterruptSnapshotSource source, Binding binding, string name, DateTimeOffset at)
        => binding.Resource.Image(at.ToUnixTimeMilliseconds(), 0xFFFF_F800_0000_1000UL, 0x100, name);
    private static void Interrupt(EtwSystemInterruptSnapshotSource source, Binding binding, DateTimeOffset at, double duration)
        => binding.Resource.Interrupt(at.ToUnixTimeMilliseconds(), 2, duration, 0xFFFF_F800_0000_1010UL);
    private static void RawInterrupt(Binding binding, DateTimeOffset at, int durationMilliseconds)
    {
        var elapsed = at.ToUnixTimeMilliseconds() - binding.Resource.Configuration.StartedTickMilliseconds;
        var qpc = checked((long)((Int128)elapsed * binding.Resource.QpcFrequency / 1000));
        var initial = checked(qpc - (long)((Int128)durationMilliseconds * binding.Resource.QpcFrequency / 1000));
        Span<byte> payload = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(payload, initial);
        BinaryPrimitives.WriteUInt64LittleEndian(payload[8..], 0xFFFF_F800_0000_1010UL);
        binding.Resource.Raw(new Guid("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc"), 68, 2, 8, qpc, 0, 0, 0, payload);
    }
    private static object Field(EtwSystemInterruptSnapshotSource source, string name)
        => typeof(EtwSystemInterruptSnapshotSource).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
    private static object? Invoke(EtwSystemInterruptSnapshotSource source, string name, params object[] arguments)
        => typeof(EtwSystemInterruptSnapshotSource).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(source, arguments);
    private sealed record Binding(KernelEtwEventSource Source, KernelEtwSessionContext Context,
        NativeKernelEtwSession Native, NativeKernelEtwTestResource Resource);

    // Only cold broker status is fault-injectable. All module, interrupt, loss,
    // aging, keyword union, native lifetime and publication work is real.
    private sealed class Broker : RecordingNativeKernelEtwBroker
    {
        private int lost;
        private int subscribeCount;
        internal Func<long> ReadTick = static () => Start.ToUnixTimeMilliseconds();
        internal KernelEtwSubscriptionRequest? Request;
        internal KernelEtwSubscriptionRequest? MetadataRequest;
        internal bool DurationAvailable = true;
        internal KernelEtwSessionContext Context { get; private set; } = new(0, DateTimeOffset.MinValue);
        internal KernelEtwEventSource Source { get; private set; } = null!;
        internal int MetadataSubscribeCount, MetadataDisposeCount, SnapshotCount;
        internal int SubscribeCount => Volatile.Read(ref subscribeCount);
        internal Action<int>? AfterSnapshot;
        internal int EventsLost
        {
            get => lost;
            set
            {
                lost = value;
                if (MetadataSubscribeCount != MetadataDisposeCount)
                {
                    Resource.Loss(ReadTick(), checked((ulong)value)); Resource.Publish(ReadTick());
                }
            }
        }
        internal Broker() : base(NativeKernelEtwTestResource.SmallConfiguration(startedTick: Start.ToUnixTimeMilliseconds())) { }
        public override IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        {
            Request = request;
            var metadata = request.Keywords == KernelTraceEventParser.Keywords.ImageLoad;
            var bind = request.Bind;
            var wrapped = request with { Bind = (source, context) =>
            {
                if (metadata) { Source = source; Context = context; }
                bind(source, context);
            }};
            if (metadata) MetadataRequest = wrapped;
            var lease = base.Acquire(wrapped);
            Interlocked.Increment(ref subscribeCount);
            if (metadata)
            {
                MetadataSubscribeCount++;
                if (lost != 0) { Resource.Loss(ReadTick(), checked((ulong)lost)); Resource.Publish(ReadTick()); }
            }
            return new Held(this, lease, metadata);
        }
        internal void Rebind() => Resource.Rebind();
        internal void RebindInSameGeneration()
        {
            var replacement = new KernelEtwEventSource(Resource.Session);
            MetadataRequest!.Bind(replacement, Context);
            replacement.Activate();
            Sources[MetadataRequest.Id] = replacement;
        }
        public override KernelEtwSessionSnapshot GetSnapshot()
        {
            var receipt = base.GetSnapshot();
            AfterSnapshot?.Invoke(++SnapshotCount);
            return receipt;
        }
        private sealed class Held(Broker owner, IKernelEtwSubscription lease, bool metadata) : IKernelEtwSubscription
        {
            private int disposed;
            public KernelEtwSessionSnapshot GetSnapshot()
            {
                var snapshot = owner.GetSnapshot();
                return !metadata && !owner.DurationAvailable
                    ? snapshot with { State = "Unavailable", Message = "The controlled interrupt duration hold is unavailable." }
                    : snapshot;
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                lease.Dispose(); if (metadata) owner.MetadataDisposeCount++;
            }
        }
    }
}

