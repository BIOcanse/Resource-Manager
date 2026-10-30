using System.Threading.Channels;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.ProcessAttribution;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class RuntimeProcessAttributionCatalogAvailabilityTests
{
    [Fact]
    public async Task InitiallyAvailableDoesNotCheckOrCreateATimer()
    {
        var clock = new ManualClock();
        var checks = 0;
        using var availability = new RuntimeProcessAttributionCatalogAvailability(
            _ => { checks++; return Task.CompletedTask; }, new(), clock);

        Assert.True(availability.IsAvailable);
        await availability.CheckAvailabilityAsync();

        Assert.Equal(0, checks);
        Assert.Equal(0, clock.CreatedTimers);
        Assert.Equal(30, new RuntimeProcessAttributionCatalogAvailabilityOptions().RecoveryCheckIntervalSeconds);
    }

    [Fact]
    public async Task ZeroDisablesResourceChecksAfterATransientFailure()
    {
        var clock = new ManualClock();
        var checks = 0;
        using var availability = new RuntimeProcessAttributionCatalogAvailability(
            _ => { checks++; return Task.CompletedTask; },
            new() { RecoveryCheckIntervalSeconds = 0 }, clock);

        availability.ReportFailure(new IOException("inventory temporarily unreadable"));
        await availability.CheckAvailabilityAsync();

        Assert.False(availability.IsAvailable);
        Assert.Equal(0, checks);
        Assert.Equal(0, clock.CreatedTimers);
    }

    [Fact]
    public async Task TransientFailureChecksAfterConfiguredDelayAndStopsAfterRecovery()
    {
        var clock = new ManualClock();
        var checks = 0;
        var changes = 0;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var availability = new RuntimeProcessAttributionCatalogAvailability(
            _ => { Interlocked.Increment(ref checks); return Task.CompletedTask; },
            new() { RecoveryCheckIntervalSeconds = 17 }, clock);
        availability.AvailabilityChanged += () =>
        {
            Interlocked.Increment(ref changes);
            if (availability.IsAvailable) recovered.TrySetResult();
        };

        availability.ReportFailure(new IOException("inventory unavailable"));
        Assert.False(availability.IsAvailable);
        var timer = await clock.NextTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(17), timer.DueTime);
        Assert.Equal(0, checks);

        timer.Fire();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await availability.CheckAvailabilityAsync();

        Assert.True(availability.IsAvailable);
        Assert.Equal(1, checks);
        Assert.Equal(2, changes);
        Assert.Equal(1, clock.CreatedTimers);
    }

    [Fact]
    public async Task FailureRemainsUnavailableUntilAResourceCheckActuallySucceeds()
    {
        var fail = true;
        var checks = 0;
        using var availability = new RuntimeProcessAttributionCatalogAvailability(_ =>
        {
            checks++;
            if (fail) throw new IOException("still unavailable");
            return Task.CompletedTask;
        }, new(), new ManualClock());
        availability.ReportFailure(new IOException("initial failure"));

        await availability.CheckAvailabilityAsync();
        Assert.False(availability.IsAvailable);
        fail = false;
        await availability.CheckAvailabilityAsync();

        Assert.True(availability.IsAvailable);
        Assert.Equal(2, checks);
    }

    public static IEnumerable<object[]> PermanentFailures()
    {
        yield return [new UnauthorizedAccessException()];
        yield return [new DllNotFoundException()];
        yield return [new EntryPointNotFoundException()];
        yield return [new BadImageFormatException()];
        yield return [new NativeSoftwareIdentityException("catalog", NativeSoftwareIdentityStatus.AbiMismatch)];
        yield return [new NativeSoftwareIdentityException("catalog", NativeSoftwareIdentityStatus.Unavailable)];
    }

    [Theory]
    [MemberData(nameof(PermanentFailures))]
    public async Task PermissionAndNativeCompatibilityFailuresAreUnavailableWithoutPolling(Exception error)
    {
        var clock = new ManualClock();
        var checks = 0;
        using var availability = new RuntimeProcessAttributionCatalogAvailability(
            _ => { checks++; return Task.CompletedTask; }, new(), clock);

        availability.ReportFailure(error);
        await availability.CheckAvailabilityAsync();

        Assert.False(availability.IsAvailable);
        Assert.Equal(0, checks);
        Assert.Equal(0, clock.CreatedTimers);
    }

    [Theory]
    [MemberData(nameof(PermanentFailures))]
    public async Task ALaterIoReportCannotRecoverAPermanentFailure(Exception error)
    {
        var clock = new ManualClock();
        var checks = 0;
        using var availability = new RuntimeProcessAttributionCatalogAvailability(
            _ => { checks++; return Task.CompletedTask; }, new(), clock);

        availability.ReportFailure(error);
        availability.ReportFailure(new IOException("later incidental IO failure"));
        await availability.CheckAvailabilityAsync();

        Assert.False(availability.IsAvailable);
        Assert.Equal(0, checks);
        Assert.Equal(0, clock.CreatedTimers);
    }

    public static IEnumerable<object[]> UnknownFailures()
    {
        yield return [new InvalidOperationException("programming defect")];
        yield return [new ArgumentException("bad input")];
        yield return [new OperationCanceledException()];
        yield return [new OutOfMemoryException()];
        yield return [new NativeSoftwareIdentityException("catalog", NativeSoftwareIdentityStatus.OutOfMemory)];
        yield return [new NativeSoftwareIdentityException("catalog", NativeSoftwareIdentityStatus.InvalidArgument)];
    }

    [Theory]
    [MemberData(nameof(UnknownFailures))]
    public async Task UnknownDefectsAndUnprovenAllocationRecoveryDoNotChangeAvailability(Exception error)
    {
        var clock = new ManualClock();
        var changes = 0;
        using var availability = new RuntimeProcessAttributionCatalogAvailability(
            _ => throw new InvalidOperationException("must not check"), new(), clock);
        availability.AvailabilityChanged += () => changes++;

        availability.ReportFailure(error);
        await availability.CheckAvailabilityAsync();

        Assert.True(availability.IsAvailable);
        Assert.Equal(0, changes);
        Assert.Equal(0, clock.CreatedTimers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResourceCheckCannotOverwriteANewerPermanentFailure(bool checkFails)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        using var availability = new RuntimeProcessAttributionCatalogAvailability(async _ =>
        {
            checks++;
            started.SetResult();
            await release.Task;
            if (checkFails) throw new IOException("older resource check");
        }, new(), new ManualClock());
        availability.ReportFailure(new IOException("initial failure"));
        var checking = availability.CheckAvailabilityAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        availability.ReportFailure(new UnauthorizedAccessException());
        release.SetResult();
        await checking;
        await availability.CheckAvailabilityAsync();

        Assert.False(availability.IsAvailable);
        Assert.Equal(1, checks);
    }

    [Fact]
    public async Task DisposeCancelsAPendingRecoveryTimer()
    {
        var clock = new ManualClock();
        var checks = 0;
        using var availability = new RuntimeProcessAttributionCatalogAvailability(
            _ => { checks++; return Task.CompletedTask; }, new(), clock);
        availability.ReportFailure(new IOException());
        var timer = await clock.NextTimerAsync();
        Assert.False(timer.IsDisposed);

        availability.Dispose();
        timer.Fire();

        Assert.True(timer.IsDisposed);
        Assert.Equal(0, checks);
    }

    [Fact]
    public async Task DisposeCancelsTheTimerAndWaitsForAnActiveResourceCheck()
    {
        var clock = new ManualClock();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        using var availability = new RuntimeProcessAttributionCatalogAvailability(async cancellationToken =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { finished = true; }
        }, new(), clock);
        availability.ReportFailure(new IOException());
        var timer = await clock.NextTimerAsync();
        timer.Fire();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        availability.Dispose();

        Assert.True(finished);
        Assert.True(timer.IsDisposed);
    }

    [Fact]
    public async Task OrdinaryAcquisitionStillPropagatesItsFailureWithoutChangingAvailability()
    {
        var failure = new IOException("inventory read failed");
        var registry = new RecoverableSoftwareRegistry { Failure = failure };
        using var provider = RuntimeProcessAttributionCatalogProviderTests.CreateProvider(registry, clock: new ManualClock());

        var thrown = await Assert.ThrowsAsync<IOException>(() => provider.AcquireCatalogAsync(default));

        Assert.Same(failure, thrown);
        Assert.True(provider.IsAvailable);
        provider.ReportFailure(thrown);
        Assert.False(provider.IsAvailable);
    }

    [Fact]
    public async Task OwnerAvailabilityStopsPortableBlockAndRealResourceRecoveryRestartsIt()
    {
        var registry = new RecoverableSoftwareRegistry();
        using var provider = RuntimeProcessAttributionCatalogProviderTests.CreateProvider(registry, clock: new ManualClock());
        var portable = new AvailabilityProbeBlock();
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, IBusinessBlock> { [BlockUids.PortableSoftwareDiscoveryService] = portable });
        var facts = new BackendPreciseRuleTestFacts(fixture.Authority, fixture.Native);
        await facts.InitializeAsync(true);
        var publication = Task.CompletedTask;
        void PublishAvailability() => publication = facts.PublishAsync(
            BackendItems.PortableAttributionCatalogAvailable, provider.IsAvailable);
        provider.AvailabilityChanged += PublishAvailability;
        try
        {
            await fixture.WaitForStateAsync(BlockUids.PortableSoftwareDiscoveryService, BusinessBlockState.Running);
            Assert.Equal(1, portable.Starts);

            registry.Failure = new IOException("catalog inventory unavailable");
            provider.ReportFailure(registry.Failure);
            await publication;
            await fixture.WaitForStateAsync(BlockUids.PortableSoftwareDiscoveryService, BusinessBlockState.Stopped);
            Assert.False(fixture.Authority.Read<bool>(BackendItems.PortableAttributionCatalogAvailable).Value);

            // The enable setting remains true throughout: only the resource fact closes admission.
            await facts.PublishAsync(BackendItems.PortableDiscoveryEnabled, true);
            await provider.CheckAvailabilityAsync();
            await facts.PublishAsync(BackendItems.PortableDiscoveryEnabled, true);
            Assert.Equal(BusinessBlockState.Stopped, fixture.Scheduler.ReadState(BlockUids.PortableSoftwareDiscoveryService));
            Assert.Equal(1, portable.Starts);

            registry.Failure = null;
            await provider.CheckAvailabilityAsync();
            await publication;
            await fixture.WaitForStateAsync(BlockUids.PortableSoftwareDiscoveryService, BusinessBlockState.Running);

            Assert.True(fixture.Authority.Read<bool>(BackendItems.PortableAttributionCatalogAvailable).Value);
            Assert.Equal(2, portable.Starts);
            Assert.Equal(2, registry.Reads);
        }
        finally { provider.AvailabilityChanged -= PublishAvailability; }
    }

    [Fact]
    public async Task OwnerRecoveryReReadsTheInventoryAndReturnsItsCatalogLease()
    {
        var registry = new RecoverableSoftwareRegistry();
        using var provider = RuntimeProcessAttributionCatalogProviderTests.CreateProvider(registry, clock: new ManualClock());
        using var original = await provider.AcquireCatalogAsync(default);
        var pipeline = original.Catalog.Pipeline;
        Assert.True(provider.IsAvailable);
        var changes = 0;
        provider.AvailabilityChanged += () => changes++;

        registry.Failure = new IOException("backing inventory is unavailable");
        provider.ReportFailure(registry.Failure);
        await provider.CheckAvailabilityAsync();
        Assert.False(provider.IsAvailable);
        Assert.Equal(2, registry.Reads);

        registry.Failure = null;
        await provider.CheckAvailabilityAsync();
        Assert.True(provider.IsAvailable);
        Assert.Equal(3, registry.Reads);
        Assert.Equal(2, changes);

        // If the recovery check retained its lease, the original pipeline would survive disposal.
        original.Dispose();
        provider.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pipeline.Match(new(91, null, "example", null, false)));
    }

    private sealed class RecoverableSoftwareRegistry : ISoftwareRegistryView
    {
        internal int Reads;
        internal Exception? Failure;
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken)
        {
            Reads++;
            if (Failure is { } error) throw error;
            return Task.FromResult<IReadOnlyList<SoftwareRecord>>([]);
        }
        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken)
            => GetSoftwareAsync(cancellationToken);
    }

    private sealed class AvailabilityProbeBlock : IBusinessBlock
    {
        private int starts;
        internal int Starts => Volatile.Read(ref starts);
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            Interlocked.Increment(ref starts);
            context.ReportRunning();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly Channel<ManualTimer> timers = Channel.CreateUnbounded<ManualTimer>();
        private int createdTimers;
        internal int CreatedTimers => Volatile.Read(ref createdTimers);
        internal Task<ManualTimer> NextTimerAsync()
            => timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            Interlocked.Increment(ref createdTimers);
            timers.Writer.TryWrite(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private int disposed;
        internal TimeSpan DueTime => dueTime;
        internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
        internal void Fire() { if (!IsDisposed) callback(state); }
        public bool Change(TimeSpan due, TimeSpan period) => !IsDisposed;
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
