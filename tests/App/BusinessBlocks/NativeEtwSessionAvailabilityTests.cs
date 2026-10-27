using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.StateItems;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

public sealed class NativeEtwSessionAvailabilityTests
{
    public static IEnumerable<object[]> Failures()
    {
        foreach (var block in new[] { BlockUids.PresentFrameEtwSource, BlockUids.GpuAllocationCollector, BlockUids.DxgkrnlVidMmEtwTelemetryZone })
            foreach (var failure in Enum.GetValues<Failure>()) yield return [block, failure];
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task FailedSessionRemainsUnavailableWhileDemandIsHeld(int block, Failure failure)
    {
        await using var rig = new Rig(block, failure);
        var lease = rig.Subscribe();
        if (failure == Failure.Release)
        {
            await lease.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            rig.Session.Complete(new IOException("run failed before release"));
        }
        // A failed startup can finish before Ready reads the current run. Ready
        // deliberately waits for the next run in that case; the recorded failed
        // result is the completion signal for this test's injected startup.
        var result = await rig.Recorder.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        await EtwTestRunRecorder.UntilAsync(() => rig.Controller.ReadState(block) == BusinessBlockState.Stopped);
        Assert.False(rig.Authority.Read<bool>(rig.AvailabilityUid).Value);
        Assert.Single(rig.Controller.Read().Demands);
        var expected = failure is Failure.PermissionCreate or Failure.PermissionProvider
            ? NativeEtwSessionUnavailableCause.NoPermission
            : failure is Failure.CapacityCreate or Failure.CapacityProvider
                ? NativeEtwSessionUnavailableCause.Capacity : NativeEtwSessionUnavailableCause.ReleaseUnconfirmed;
        var recorded = Assert.IsType<NativeEtwSessionFailure>(rig.Availability.ReadFailure(block));
        Assert.Equal(expected, recorded.Cause);
        if (expected == NativeEtwSessionUnavailableCause.Capacity)
        {
            var providers = block == BlockUids.PresentFrameEtwSource ? 5 : block == BlockUids.GpuAllocationCollector ? 2 : 1;
            Assert.Equal(providers, recorded.Providers.Count);
            Assert.DoesNotContain(Guid.Empty, recorded.Providers);
            Assert.Contains(PresentFrameEtwSource.GraphicsKernelProvider, recorded.Providers);
        }
        // Explicit fresh alignments prove held demand cannot start another native session.
        for (var index = 0; index < 3; index++)
        {
            await rig.Controller.SetDemandAsync("availability-test:" + index, block,
                new(BusinessBlockRequestKind.Dependency, "availability-test", "held", index));
        }
        Assert.Equal(1, rig.Attempts);
        await lease.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GpuRuntimeProviderFailureStopsFurtherDueCaptures(bool permission)
    {
        await using var rig = new Rig(BlockUids.GpuAllocationCollector, null);
        var lease = rig.Subscribe();
        await lease.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Session.ProviderFailure = permission ? new UnauthorizedAccessException("runtime enable")
            : new InvalidOperationException("runtime wrapper", new COMException("capacity", unchecked((int)0x800705AA)));
        await rig.Gpu!.CaptureAsync(new Dictionary<int, long>(), default);
        await rig.Recorder.NextResultAsync();
        await EtwTestRunRecorder.UntilAsync(() => rig.Controller.ReadState(rig.Block) == BusinessBlockState.Stopped);
        var enables = rig.Session.Enables;
        await rig.Gpu.CaptureAsync(new Dictionary<int, long>(), default);
        Assert.Equal(enables, rig.Session.Enables);
        Assert.Null(rig.Gpu.ReadCurrent(new Dictionary<int, long>()));
        Assert.False(rig.Authority.Read<bool>(rig.AvailabilityUid).Value);
        Assert.Equal(1, rig.Attempts);
        await lease.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresentRuntimeProviderFailureIsHandedToPersistentPublisher(bool permission)
    {
        await using var rig = new Rig(BlockUids.PresentFrameEtwSource, null);
        // Graphics-API demand starts the session without the two display providers.
        var api = rig.Present!.AcquireGraphicsApiSubscription(new(123, 456, "test", "test.exe"));
        await ((IBusinessBlockDependency)api).Ready.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Session.ProviderFailure = permission ? new UnauthorizedAccessException("runtime enable")
            : new InvalidOperationException("runtime wrapper", new COMException("capacity", unchecked((int)0x800705AA)));
        var frames = rig.Present.AcquireSubscription();
        await rig.Recorder.NextResultAsync();
        await EtwTestRunRecorder.UntilAsync(() => rig.Controller.ReadState(rig.Block) == BusinessBlockState.Stopped);
        Assert.False(rig.Authority.Read<bool>(rig.AvailabilityUid).Value);
        Assert.Equal(permission ? NativeEtwSessionUnavailableCause.NoPermission : NativeEtwSessionUnavailableCause.Capacity,
            rig.Availability.ReadFailure(rig.Block)!.Cause);
        Assert.Equal(1, rig.Attempts);
        await ((IBusinessBlockDependency)frames).DisposeAsync();
        await ((IBusinessBlockDependency)api).DisposeAsync();
    }

    [Fact]
    public async Task PresentTimerReleaseFailureDoesNotChangeWindowsSessionAvailability()
    {
        await using var rig = new Rig(BlockUids.PresentFrameEtwSource, null, new TimerDisposeFailureTimeProvider());
        var lease = rig.Subscribe();
        await lease.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        await lease.DisposeAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, (await rig.Recorder.NextResultAsync()).Outcome);
        Assert.True(rig.Authority.Read<bool>(rig.AvailabilityUid).Value);
        Assert.Null(rig.Availability.ReadFailure(rig.Block));
        Assert.Equal(1, rig.Attempts);
    }

    [Fact]
    public void PublisherOwnsInitialTrueValuesAndKeepsPermissionFailureThroughCleanup()
    {
        var authority = new StateAuthority(StateRegistry.Items, StateRegistry.Blocks);
        var publisher = new NativeEtwSessionAvailability(authority);
        foreach (var uid in new[] { StateItemUids.PresentNativeSessionAvailable, StateItemUids.GpuAllocationNativeSessionAvailable, StateItemUids.VidMmNativeSessionAvailable })
        {
            Assert.True(authority.Read<bool>(uid).Value);
            Assert.Throws<InvalidOperationException>(() => authority.ClaimCell<bool>(uid));
        }
        var error = new UnauthorizedAccessException("denied");
        publisher.Report(BlockUids.PresentFrameEtwSource, NativeEtwSessionUnavailableCause.NoPermission, error);
        publisher.Report(BlockUids.PresentFrameEtwSource, NativeEtwSessionUnavailableCause.ReleaseUnconfirmed, new IOException("cleanup"));
        Assert.Same(error, publisher.ReadFailure(BlockUids.PresentFrameEtwSource)!.Exception);
        Assert.False(authority.Read<bool>(StateItemUids.PresentNativeSessionAvailable).Value);
        Assert.Null(NativeEtwSessionAvailability.Classify(new IOException("ordinary provider error")));
        Assert.Equal(NativeEtwSessionUnavailableCause.Capacity, NativeEtwSessionAvailability.Classify(new Win32Exception(1450)));
        Assert.Equal(NativeEtwSessionUnavailableCause.NoPermission,
            NativeEtwSessionAvailability.Classify(new KernelEtwSessionCreationCleanupException(error, new IOException("cleanup"))));
    }

    public enum Failure { Release, CreationCleanup, PermissionCreate, PermissionProvider, CapacityCreate, CapacityProvider }

    private sealed class Rig : IAsyncDisposable, IGpuAllocationSessionFactory
    {
        private readonly ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        private readonly Failure? failure;
        internal int Block { get; }
        internal int AvailabilityUid { get; }
        internal StateAuthority Authority { get; } = new(StateRegistry.Items, StateRegistry.Blocks);
        internal NativeEtwSessionAvailability Availability { get; }
        internal BusinessBlockScheduler Controller { get; }
        internal EtwTestRunRecorder Recorder { get; }
        internal Session Session { get; } = new();
        internal PresentFrameEtwSource? Present { get; }
        internal GpuAllocationCollector? Gpu { get; }
        private DxgkrnlVidMmEtwTelemetryZone? VidMm { get; }
        internal int Attempts;

        internal Rig(int block, Failure? failure, TimeProvider? timeProvider = null)
        {
            Block = block;
            this.failure = failure;
            AvailabilityUid = block == BlockUids.PresentFrameEtwSource ? StateItemUids.PresentNativeSessionAvailable
                : block == BlockUids.GpuAllocationCollector ? StateItemUids.GpuAllocationNativeSessionAvailable : StateItemUids.VidMmNativeSessionAvailable;
            Availability = new(Authority);
            EtwTestRunRecorder? recorder = null;
            BusinessBlockRegistration[] declarations = [new(block, [], _ => recorder!)];
            var diagnostics = new BusinessBlockDiagnosticsRegistry(declarations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
            StateItemKey demand = new(StateItemUids.BlockDemand, block);
            Controller = new(services, declarations, Options.Create(new BusinessBlockOptions()), NullLogger<BusinessBlockScheduler>.Instance,
                Authority, decisions: [new(BlockUids.Program, [demand, AvailabilityUid],
                    input => input.Read<bool>(demand).GetValueOrDefault(false) && input.Read<bool>(AvailabilityUid).GetValueOrDefault(false) ? [block] : [])]);
            IBusinessBlock source;
            if (block == BlockUids.PresentFrameEtwSource)
                source = Present = new(NullLogger<PresentFrameEtwSource>.Instance, Controller, diagnostics, Availability,
                    _ => CreateSession(), timeProvider ?? new PresentFrameEtwManualTimeProvider());
            else if (block == BlockUids.GpuAllocationCollector)
                source = Gpu = new(NullLogger<GpuAllocationCollector>.Instance, new ConfigurationBuilder().Build(), Controller, diagnostics,
                    Availability, this, TimeProvider.System);
            else source = VidMm = new(NullLogger<DxgkrnlVidMmEtwTelemetryZone>.Instance, Controller, diagnostics, Availability,
                _ => null, _ => CreateSession());
            Recorder = recorder = new(source, diagnostics, block);
        }

        private Session CreateSession()
        {
            Interlocked.Increment(ref Attempts);
            if (failure == Failure.CreationCleanup) throw new KernelEtwSessionCreationCleanupException(new IOException("create"), new IOException("cleanup"));
            if (failure == Failure.PermissionCreate) throw new UnauthorizedAccessException("create");
            if (failure == Failure.CapacityCreate) throw new COMException("create capacity", unchecked((int)0x800705AA));
            Session.DisposeFailure = failure == Failure.Release ? new IOException("release unconfirmed") : null;
            Session.ProviderFailure = failure == Failure.PermissionProvider ? new InvalidOperationException("provider wrapper", new UnauthorizedAccessException("enable"))
                : failure == Failure.CapacityProvider ? new InvalidOperationException("provider wrapper", new COMException("enable capacity", unchecked((int)0x800705AA))) : null;
            // VidMm enables its sole provider inside construction.
            if (Block == BlockUids.DxgkrnlVidMmEtwTelemetryZone) Session.EnableProvider(PresentFrameEtwSource.GraphicsKernelProvider);
            return Session;
        }
        public IGpuAllocationSession Create(string name) => CreateSession();
        internal IBusinessBlockDependency Subscribe() => Present is not null ? (IBusinessBlockDependency)Present.AcquireSubscription()
            : Gpu is not null ? Gpu.AcquireSubscription(TimeSpan.FromSeconds(1)) : VidMm!.AcquireSubscription("test", TimeSpan.FromSeconds(1));
        public async ValueTask DisposeAsync()
        {
            await Controller.ShutdownAsync(new(BusinessBlockRequestKind.Dependency, "test", "cleanup", 0)).WaitAsync(TimeSpan.FromSeconds(10));
            Present?.Dispose();
            await Controller.DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private sealed class TimerDisposeFailureTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new Timer();
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.FromException(new UnauthorizedAccessException("in-process timer cleanup"));
        }
    }

    private sealed class Session : IPresentFrameEtwSession, IVidMmEtwSession, IGpuAllocationSession
    {
        private readonly TaskCompletionSource<Exception?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<Exception?>? completed;
        internal Exception? DisposeFailure;
        internal Exception? ProviderFailure;
        internal int Enables;
        public bool IsProcessing { get; private set; }
        public int EventsLost => 0;
        public void Bind(Action<TraceEvent> handler) { }
        public void Bind(Action<Guid, int, IGpuAllocationPayload, DateTime> handler) { }
        public void Process()
        {
            var error = completion.Task.GetAwaiter().GetResult();
            if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
        public void StartProcessing(Action<Exception?> callback) { completed = callback; IsProcessing = true; }
        internal void Complete(Exception? error)
        {
            IsProcessing = false;
            completion.TrySetResult(error);
            completed?.Invoke(error);
        }
        public void EnableProvider(Guid provider, TraceEventLevel level, ulong keywords, int[] eventIds) => EnableProvider(provider);
        public void EnableProvider(Guid provider, ulong keywords = 0, int[]? eventIds = null)
        {
            Interlocked.Increment(ref Enables);
            if (ProviderFailure is { } error) throw error;
        }
        public void DisableProvider(Guid provider) { }
        public void Flush() { }
        public void CaptureState(Guid provider, ulong keywords) { }
        public void BeginScan(string sessionName, long scan, long eventsLost) { }
        public void EndScan(string sessionName, long scan) { }
        public void Dispose() { Complete(null); if (DisposeFailure is { } error) throw error; }
        public KernelEtwSessionStopResult Stop(TimeSpan timeout)
        {
            Complete(null);
            return DisposeFailure is null ? new(true, true, true, []) : new(false, false, true, [DisposeFailure.Message]);
        }
        public void JoinProcessing() { }
    }
}
