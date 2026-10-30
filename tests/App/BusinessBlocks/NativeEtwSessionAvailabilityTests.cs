using System.ComponentModel;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task FailedSessionRemainsUnavailableWhileItsSubscriptionFieldIsInUse(int block, Failure failure)
    {
        await using var rig = new Rig(block, failure);
        await rig.ActivateAsync();
        if (rig.Gpu is { } gpu)
        {
            // GPU collection creates its session on the first due capture, after the block is Running.
            await rig.WaitRunningAsync();
            await gpu.CaptureAsync(new Dictionary<int, long>(), TimeSpan.FromSeconds(1), default);
        }
        if (failure == Failure.Release)
        {
            if (rig.Gpu is null) await rig.WaitRunningAsync();
            rig.Session.Complete(new IOException("run failed before release"));
        }
        // Run-entry failures are synchronized by their recorded result since the
        // block may never report Running; GPU failure cases are triggered by the due above.
        var result = await rig.Recorder.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
        await EtwTestRunRecorder.UntilAsync(() => rig.Controller.ReadState(block) == BusinessBlockState.Stopped);
        Assert.False(rig.Authority.Read<bool>(rig.AvailabilityUid).Value);
        Assert.True(rig.Authority.Read<bool>(rig.SubscriptionUid).Value);
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
        // Repeated owner notifications cannot restart an unavailable session.
        for (var index = 0; index < 3; index++)
        {
            await rig.NotifyActiveAsync();
        }
        Assert.Equal(1, rig.Attempts);
        await rig.DeactivateAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GpuRuntimeProviderFailureStopsFurtherDueCaptures(bool permission)
    {
        await using var rig = new Rig(BlockUids.GpuAllocationCollector, null);
        await rig.ActivateAsync();
        await rig.WaitRunningAsync();
        await rig.Gpu!.CaptureAsync(new Dictionary<int, long>(), TimeSpan.FromSeconds(1), default);
        rig.Session.ProviderFailure = permission ? new UnauthorizedAccessException("runtime enable")
            : new InvalidOperationException("runtime wrapper", new COMException("capacity", unchecked((int)0x800705AA)));
        rig.Session.ProviderFailureProvider = GpuAllocationCollector.Provider;
        // A live refresh only reads the retained ledger. A finite due performs the provider enable
        // against the existing session and exercises the runtime failure path.
        await rig.Gpu.CaptureAsync(new Dictionary<int, long>(), TimeSpan.FromSeconds(30), default);
        await rig.Recorder.NextResultAsync();
        await EtwTestRunRecorder.UntilAsync(() => rig.Controller.ReadState(rig.Block) == BusinessBlockState.Stopped);
        var enables = rig.Session.Enables;
        await rig.Gpu.CaptureAsync(new Dictionary<int, long>(), TimeSpan.FromSeconds(1), default);
        Assert.Equal(enables, rig.Session.Enables);
        Assert.Null(rig.Gpu.ReadCurrent(new Dictionary<int, long>()));
        Assert.False(rig.Authority.Read<bool>(rig.AvailabilityUid).Value);
        Assert.Equal(permission ? NativeEtwSessionUnavailableCause.NoPermission : NativeEtwSessionUnavailableCause.Capacity,
            rig.Availability.ReadFailure(rig.Block)!.Cause);
        Assert.Equal(1, rig.Attempts);
        await rig.DeactivateAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresentRuntimeProviderFailureIsHandedToPersistentPublisher(bool permission)
    {
        await using var rig = new Rig(BlockUids.PresentFrameEtwSource, null);
        // Graphics-API activity starts the session without the two display providers.
        await rig.ActivateAsync(graphicsApiOnly: true);
        await rig.WaitRunningAsync();
        rig.Session.ProviderFailure = permission ? new UnauthorizedAccessException("runtime enable")
            : new InvalidOperationException("runtime wrapper", new COMException("capacity", unchecked((int)0x800705AA)));
        using var frames = rig.Present!.AcquireSubscription();
        await rig.Recorder.NextResultAsync();
        await EtwTestRunRecorder.UntilAsync(() => rig.Controller.ReadState(rig.Block) == BusinessBlockState.Stopped);
        Assert.False(rig.Authority.Read<bool>(rig.AvailabilityUid).Value);
        Assert.Equal(permission ? NativeEtwSessionUnavailableCause.NoPermission : NativeEtwSessionUnavailableCause.Capacity,
            rig.Availability.ReadFailure(rig.Block)!.Cause);
        Assert.Equal(1, rig.Attempts);
        frames.Dispose();
        await rig.DeactivateAsync();
    }

    [Fact]
    public async Task PresentTimerReleaseFailureDoesNotChangeWindowsSessionAvailability()
    {
        await using var rig = new Rig(BlockUids.PresentFrameEtwSource, null, new TimerDisposeFailureTimeProvider());
        await rig.ActivateAsync();
        await rig.WaitRunningAsync();
        await rig.DeactivateAsync();
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
        private readonly Failure? failure;
        private readonly NativeBackendBlockFixture fixture;
        private readonly StateReadFunction<bool>? presentActivity;
        private IDisposable? sourceSubscription;
        private Task activityPublication = Task.CompletedTask;
        internal int Block { get; }
        internal int AvailabilityUid { get; }
        internal int SubscriptionUid { get; }
        internal StateAuthority Authority => fixture.Authority;
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
            SubscriptionUid = block == BlockUids.PresentFrameEtwSource ? StateItemUids.GraphicsApiObservationInUse
                : block == BlockUids.GpuAllocationCollector ? StateItemUids.ProcessDedicatedGpuMemoryInUse : StateItemUids.VramResidualInUse;
            EtwTestRunRecorder? recorder = null;
            var registration = new BusinessBlockRegistration(block, _ => recorder!);
            var diagnostics = new BusinessBlockDiagnosticsRegistry([registration], NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
            fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
                new Dictionary<int, Func<IServiceProvider, IBusinessBlock>> { [block] = _ => recorder! });
            Controller = fixture.Scheduler;
            Availability = fixture.Availability;
            IBusinessBlock source;
            if (block == BlockUids.PresentFrameEtwSource)
                source = Present = new(NullLogger<PresentFrameEtwSource>.Instance, Controller, diagnostics, Availability,
                    _ => CreateSession(), timeProvider ?? new PresentFrameEtwManualTimeProvider());
            else if (block == BlockUids.GpuAllocationCollector)
                source = Gpu = new(NullLogger<GpuAllocationCollector>.Instance, new ConfigurationBuilder().Build(), diagnostics,
                    Availability, this, TimeProvider.System);
            else source = VidMm = new(NullLogger<DxgkrnlVidMmEtwTelemetryZone>.Instance, Controller, diagnostics, Availability,
                _ => null, _ => CreateSession());
            Recorder = recorder = new(source, diagnostics, block);
            if (Present is not null)
            {
                presentActivity = Authority.ClaimReadFunction<bool>(SubscriptionUid,
                    () => StateRead<bool>.FromValue(Present.HasActiveSubscriptions));
                Present.SubscriptionsChanged += PresentActivityChanged;
                PresentActivityChanged();
            }
            fixture.InitializeHostPermitsAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
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

        internal async Task ActivateAsync(bool graphicsApiOnly = false)
        {
            if (Present is not null)
            {
                sourceSubscription = graphicsApiOnly
                    ? Present.AcquireGraphicsApiSubscription(new(123, 456, "test", "test.exe"))
                    : Present.AcquireSubscription();
                await activityPublication.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else await fixture.SetNativeSubscriptionInUseAsync(Block, true).WaitAsync(TimeSpan.FromSeconds(10));
        }

        internal Task WaitRunningAsync() => fixture.WaitForStateAsync(Block, BusinessBlockState.Running);

        internal Task NotifyActiveAsync() => presentActivity is not null
            ? presentActivity.NotifyChangedAsync(Reason("subscription-activity"))
            : fixture.SetNativeSubscriptionInUseAsync(Block, true);

        internal async Task DeactivateAsync()
        {
            if (Present is not null)
            {
                sourceSubscription?.Dispose();
                sourceSubscription = null;
                await activityPublication.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else await fixture.SetNativeSubscriptionInUseAsync(Block, false).WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.WaitForStateAsync(Block, BusinessBlockState.Stopped);
        }

        private void PresentActivityChanged()
            => activityPublication = presentActivity!.NotifyChangedAsync(Reason("subscription-activity"));

        private static BusinessBlockReason Reason(string detail)
            => new(BusinessBlockRequestKind.Configuration, nameof(Rig), detail, 0);

        public async ValueTask DisposeAsync()
        {
            await DeactivateAsync();
            await Controller.ShutdownAsync(new(BusinessBlockRequestKind.Dependency, "test", "cleanup", 0)).WaitAsync(TimeSpan.FromSeconds(10));
            if (Present is not null)
            {
                Present.SubscriptionsChanged -= PresentActivityChanged;
                await presentActivity!.UnbindAsync(Reason("cleanup"));
            }
            Present?.Dispose();
            await fixture.DisposeAsync();
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
        private Action<Guid, int, IGpuAllocationPayload, DateTime>? gpuHandler;
        internal Exception? DisposeFailure;
        internal Exception? ProviderFailure;
        internal Guid? ProviderFailureProvider;
        internal int Enables;
        public bool IsProcessing { get; private set; }
        public int EventsLost => 0;
        public void Bind(Action<TraceEvent> handler) { }
        public void Bind(Action<Guid, int, IGpuAllocationPayload, DateTime> handler) => gpuHandler = handler;
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
        public void EnableProvider(Guid provider, TraceEventLevel level, ulong keywords, int[] eventIds, int[]? processIds = null) => EnableProvider(provider);
        public void EnableProvider(Guid provider, ulong keywords = 0, int[]? eventIds = null)
        {
            Interlocked.Increment(ref Enables);
            if (ProviderFailure is { } error && (ProviderFailureProvider is null || ProviderFailureProvider == provider)) throw error;
            if (provider == EventSource.GetGuid(typeof(GpuAllocationBoundary)))
                gpuHandler?.Invoke(provider, 65534,
                    new GpuAllocationNamedPayload(key => throw new InvalidDataException($"GPU manifest has no {key} field.")), DateTime.UtcNow);
        }
        public void DisableProvider(Guid provider) { }
        public void Flush() { }
        public void CaptureState(Guid provider, ulong keywords, int[]? eventIds = null) { }
        public void BeginScan(string sessionName, long scan, long eventsLost)
            => EmitBoundary(2, sessionName, scan, eventsLost);
        public void EndScan(string sessionName, long scan)
            => EmitBoundary(3, sessionName, scan, EventsLost);
        private void EmitBoundary(int id, string sessionName, long scan, long eventsLost)
        {
            var payload = new Dictionary<string, object>
            {
                ["session"] = sessionName, ["scan"] = scan, ["eventsLost"] = eventsLost
            };
            gpuHandler?.Invoke(EventSource.GetGuid(typeof(GpuAllocationBoundary)), id,
                new GpuAllocationNamedPayload(key => payload[key]), DateTime.UtcNow);
        }
        public void Dispose() { Complete(null); if (DisposeFailure is { } error) throw error; }
        public KernelEtwSessionStopResult Stop(TimeSpan timeout)
        {
            Complete(null);
            return DisposeFailure is null ? new(true, true, true, []) : new(false, false, true, [DisposeFailure.Message]);
        }
        public void JoinProcessing() { }
    }
}
