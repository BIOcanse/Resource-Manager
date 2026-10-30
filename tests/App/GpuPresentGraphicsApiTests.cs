using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuPresentGraphicsApiTests
{
    [Fact]
    public async Task AnImageNegativeCannotDiscardAFinalPresentFromAnAlreadyLiveAttempt()
    {
        using var files = new Files();
        var source = new Presents();
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        var history = new HistoryReadHook(store);
        using var owner = Owner(files, history, source);
        await owner.ReconcileAsync(Snapshot(Process(files)), Registered, 0, default);
        var lease = Assert.Single(source.Leases);
        // A sibling can finish empty while this PID still has a live attempt.
        await store.SaveNoPresentAsync("software", "Software", files.Instance(), DateTimeOffset.UtcNow.AddDays(1), default);
        history.AfterRead = () => lease.Api = GpuGraphicsApi.D3D9;
        await owner.ReconcileAsync(Snapshot(Process(files)), Registered, 1, default);
        var saved = Assert.Single((await store.GetSoftwareHistoryAsync("software", "Software", default)).Processes);
        Assert.Equal(GpuGraphicsApi.D3D9, saved.GraphicsApi);
        Assert.Null(saved.NoPresentUntil);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public async Task ProcessStopPersistsANegativeBeforeTheInventoryOrClockChanges()
    {
        using var files = new Files();
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var harness = PresentFrameEtwTestHarness.Create(timeProvider: clock);
        var processes = new PublishedProcesses(Snapshot(Process(files)));
        var registry = new SoftwareRegistry([new("software", "Software", "game", "installed", [], [],
            new(false, "None", "missing_uninstall_entry"), null)]);
        using var owner = new GpuPlacementProcessHistoryObserver(processes, registry, Plans(),
            new JsonGpuPlacementProcessHistoryStore(files), harness.Source,
            NullLogger<GpuPlacementProcessHistoryObserver>.Instance, clock);
        await owner.StartAsync(default);
        await harness.WaitRunningAsync();
        harness.Source.ObserveGraphicsApiProcessLifetime(42, null);
        await EtwTestRunRecorder.UntilAsync(() => File.Exists(files.HistoryPath));
        var saved = Assert.Single((await new JsonGpuPlacementProcessHistoryStore(files)
            .GetSoftwareHistoryAsync("software", "Software", default)).Processes);
        Assert.Equal(clock.GetUtcNow().AddDays(1), saved.NoPresentUntil);
        Assert.Null(saved.GraphicsApi);
        await EtwTestRunRecorder.UntilAsync(() => harness.Sessions.Current.Stops == 1);
        await owner.StopAsync(default);
    }

    [Fact]
    public async Task IncidentalHistoryRetentionCannotEvictANegativeBeforeExpiry()
    {
        using var files = new Files();
        var history = new JsonGpuPlacementProcessHistoryStore(files);
        var expiry = DateTimeOffset.UtcNow.AddDays(1);
        await history.SaveNoPresentAsync("software", "Software", files.Instance(), expiry, default);
        await history.ObserveAsync(new("software", "Software", Enumerable.Range(0, 65)
            .Select(index => new GpuPlacementObservedProcessInput($"helper{index}", Path.Combine(files.Root, $"helper{index}.exe"),
                "x64", 100 + index, ["fixture"])).ToArray()), default);
        var saved = await new JsonGpuPlacementProcessHistoryStore(files).GetSoftwareHistoryAsync("software", "Software", default);
        Assert.Equal(expiry, Assert.Single(saved.Processes.Where(item => item.ProcessKey ==
            JsonGpuPlacementProcessHistoryStore.BuildProcessKey("target", files.Target))).NoPresentUntil);
    }

    [Fact]
    public async Task PresentResultWakesPersistenceWithoutAClockTickOrNewInventory()
    {
        using var files = new Files();
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var harness = PresentFrameEtwTestHarness.Create(timeProvider: clock);
        var processes = new PublishedProcesses(Snapshot(Process(files)));
        var registry = new SoftwareRegistry([new("software", "Software", "game", "installed", [], [],
            new(false, "None", "missing_uninstall_entry"), null)]);
        using var owner = new GpuPlacementProcessHistoryObserver(processes, registry, Plans(),
            new JsonGpuPlacementProcessHistoryStore(files), harness.Source,
            NullLogger<GpuPlacementProcessHistoryObserver>.Instance, clock);
        await owner.StartAsync(default);
        await harness.WaitRunningAsync();
        harness.Source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 42, 42, DateTime.FromFileTimeUtc(1));
        await EtwTestRunRecorder.UntilAsync(() => File.Exists(files.HistoryPath));
        var saved = Assert.Single((await new JsonGpuPlacementProcessHistoryStore(files)
            .GetSoftwareHistoryAsync("software", "Software", default)).Processes);
        Assert.Equal(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12, saved.GraphicsApi);
        Assert.Null(saved.NoPresentUntil);
        Assert.Equal(1, registry.Reads);
        await owner.StopAsync(default);
        Assert.Equal(1, Assert.Single(harness.Sessions.Snapshot()).Stops);
    }

    [Fact]
    public async Task OnlyCurrentGpuUseCanStartAnAttempt()
    {
        using var files = new Files();
        var source = new Presents();
        using var owner = Owner(files, new JsonGpuPlacementProcessHistoryStore(files), source);
        var process = Process(files);
        await owner.ReconcileAsync(Snapshot(process with { Gpus = [] }), Registered, 0, default);
        await owner.ReconcileAsync(Snapshot(process with { Gpus = [process.Gpus[0] with { UsagePercent = 0 }] }), Registered, 0, default);
        await owner.ReconcileAsync(Snapshot(process with { ValidMetricMask = SchedulingProcessMetricMask.RuntimeState }), Registered, 0, default);
        await owner.ReconcileAsync(Snapshot(process), new HashSet<string>(), 0, default);
        Assert.Empty(source.Leases);
        await owner.ReconcileAsync(Snapshot(process), Registered, 0, default);
        Assert.Single(source.Leases);
    }

    [Fact]
    public async Task NegativeImageRecordPersistsAcrossObserverRestartAndExpires()
    {
        using var files = new Files();
        var clock = new PresentFrameEtwManualTimeProvider();
        var source = new Presents();
        var history = new JsonGpuPlacementProcessHistoryStore(files);
        using (var owner = Owner(files, history, source, clock: clock))
        {
            await owner.ReconcileAsync(Snapshot(Process(files)), Registered, clock.GetTimestamp(), default);
            clock.Advance(TimeSpan.FromSeconds(30));
            await owner.ReconcileAsync(Snapshot(Process(files)), Registered, clock.GetTimestamp(), default);
        }
        var recorded = Assert.Single((await new JsonGpuPlacementProcessHistoryStore(files)
            .GetSoftwareHistoryAsync("software", "Software", default)).Processes);
        Assert.Null(recorded.GraphicsApi);
        Assert.Equal(clock.GetUtcNow().AddDays(1), recorded.NoPresentUntil);
        using var restarted = Owner(files, new JsonGpuPlacementProcessHistoryStore(files), source, clock: clock);
        var replacement = Snapshot(Process(files) with { ProcessId = 99, ProcessStartKey = 2 });
        await restarted.ReconcileAsync(replacement, Registered, clock.GetTimestamp(), default);
        Assert.Single(source.Leases);
        clock.Advance(TimeSpan.FromDays(1));
        await restarted.ReconcileAsync(replacement, Registered, clock.GetTimestamp(), default);
        Assert.Equal(2, source.Leases.Count);
    }

    [Fact]
    public async Task PublicationsDriveRoundsAndSessionHoldsOnlyWhileAnAttemptIsLive()
    {
        using var files = new Files();
        var clock = new PresentFrameEtwManualTimeProvider();
        await using var harness = PresentFrameEtwTestHarness.Create(timeProvider: clock);
        // The same registered inventory instance is initially idle; only its
        // published GPU use changes when the first attempt starts.
        var processes = new PublishedProcesses(Snapshot(Process(files) with { Gpus = [] }));
        var registry = new SoftwareRegistry([new("software", "Software", "game", "installed", [], [],
            new(false, "None", "missing_uninstall_entry"), null)]);
        using var owner = new GpuPlacementProcessHistoryObserver(processes, registry, Plans(),
            new JsonGpuPlacementProcessHistoryStore(files), harness.Source,
            NullLogger<GpuPlacementProcessHistoryObserver>.Instance, clock);
        await owner.StartAsync(default);
        await EtwTestRunRecorder.UntilAsync(() => processes.Reads > 0);
        Assert.Equal(0, registry.Reads);
        Assert.Empty(harness.Sessions.Snapshot());
        var unchangedReads = processes.Reads;
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(unchangedReads, processes.Reads);
        Assert.Equal(0, registry.Reads);
        Assert.Equal(0, owner.Rounds);

        processes.Publish(Snapshot(Process(files)));
        await harness.WaitRunningAsync();
        var session = Assert.Single(harness.Sessions.Snapshot());
        var holds = new List<(int Seconds, bool Open)> { (0, session.Stops == 0) };
        // Same facts at a still clock may wake the passive reader, but cause no round/registry read.
        var reads = processes.Reads;
        var rounds = owner.Rounds;
        var armed = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTimerArmed(DateTimeOffset deadline)
        {
            if (Volatile.Read(ref processes.Reads) > reads) armed.TrySetResult(deadline);
        }
        clock.TimerArmed += OnTimerArmed;
        var expectedDeadline = clock.GetUtcNow() + GpuPlacementProcessHistoryObserver.ObservationWindow;
        try
        {
            processes.Publish(processes.Snapshot);
            // ReadLatest precedes deadline rearming. Advance only after the actual
            // timer is armed, or a fake-clock jump can move its relative due time.
            Assert.Equal(expectedDeadline, await armed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { clock.TimerArmed -= OnTimerArmed; }
        Assert.Equal(1, registry.Reads);
        Assert.Equal(rounds, owner.Rounds);
        clock.Advance(TimeSpan.FromSeconds(29));
        holds.Add((29, session.Stops == 0));
        clock.Advance(TimeSpan.FromSeconds(1));
        await EtwTestRunRecorder.UntilAsync(() => session.Stops == 1);
        holds.Add((30, session.Stops == 0));
        processes.Publish(Snapshot(Process(files) with { ProcessId = 99, ProcessStartKey = 2 }));
        await EtwTestRunRecorder.UntilAsync(() => processes.Reads > reads + 1);
        clock.Advance(TimeSpan.FromMinutes(10));
        holds.Add((630, session.Stops == 0));
        Assert.Equal(new[] { (0, true), (29, true), (30, false), (630, false) }, holds);
        Assert.Single(harness.Sessions.Snapshot());
        await owner.StopAsync(default);
        Assert.Equal(0, processes.SamplingSubscriptions);
    }

    [Theory]
    [InlineData(42, true)]
    [InlineData(43, false)]
    [InlineData(55, true)]
    [InlineData(56, false)]
    public void OnlyDxgiPresentStartsIdentifyTheCombinedRoute(int eventId, bool identifies)
        => Assert.Equal(identifies ? GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12 : (GpuGraphicsApi?)null,
            PresentFrameEtwSource.MapPresentGraphicsApi(PresentFrameEtwSource.DxgiProvider, eventId));

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void OnlyD3d9PresentStartIdentifiesD3d9(int eventId, bool identifies)
        => Assert.Equal(identifies ? GpuGraphicsApi.D3D9 : (GpuGraphicsApi?)null,
            PresentFrameEtwSource.MapPresentGraphicsApi(PresentFrameEtwSource.Direct3D9Provider, eventId));

    [Fact]
    public async Task NoPresentOrOtherProvidersDoNotIdentifyAnApi()
    {
        Assert.Null(PresentFrameEtwSource.MapPresentGraphicsApi(PresentFrameEtwSource.GraphicsKernelProvider, 184));
        Assert.Null(PresentFrameEtwSource.MapPresentGraphicsApi(PresentFrameEtwSource.DxgiProvider, 1));
        Assert.Null(PresentFrameEtwSource.MapPresentGraphicsApi(PresentFrameEtwSource.DxgiProvider, 43));
        Assert.Null(PresentFrameEtwSource.MapPresentGraphicsApi(PresentFrameEtwSource.DxgiProvider, 56));
        Assert.Null(PresentFrameEtwSource.MapPresentGraphicsApi(PresentFrameEtwSource.Direct3D9Provider, 2));
        Assert.Null(PresentFrameEtwSource.MapPresentGraphicsApi(Guid.NewGuid(), 42));
        await using var harness = PresentFrameEtwTestHarness.Create();
        var source = harness.Source;
        var lease = source.AcquireGraphicsApiSubscription(Instance());
        await harness.WaitRunningAsync();
        Assert.Null(lease.Read());
        Assert.Single(harness.Sessions.Snapshot());
        await harness.ReleaseAsync(lease);
    }

    [Fact]
    public async Task EventsUseTheSuppliedBirthAndStopOrPidReuseInvalidatesThem()
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        var source = harness.Source;
        var instance = Instance();
        var lease = source.AcquireGraphicsApiSubscription(instance);
        await harness.WaitRunningAsync();
        var birth = DateTime.FromFileTimeUtc((long)instance.ProcessStartKey);
        source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 42, instance.ProcessId, birth.AddTicks(-1));
        source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 42, instance.ProcessId + 1, birth);
        Assert.Null(lease.Read());
        source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 42, instance.ProcessId, birth);
        Assert.Equal(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12, lease.Read());
        source.ObserveGraphicsApiProcessLifetime(instance.ProcessId, (long)instance.ProcessStartKey + 1);
        Assert.Null(lease.Read());
        source.ObserveGraphicsApiPresent(PresentFrameEtwSource.Direct3D9Provider, 1, instance.ProcessId, birth.AddSeconds(1));
        Assert.Null(lease.Read());
        var second = source.AcquireGraphicsApiSubscription(instance);
        await harness.WaitRunningAsync();
        source.ObserveGraphicsApiProcessLifetime(instance.ProcessId, null);
        source.ObserveGraphicsApiPresent(PresentFrameEtwSource.DxgiProvider, 42, instance.ProcessId, birth);
        Assert.Null(second.Read());
        await harness.ReleaseAsync(lease);
        await harness.ReleaseAsync(second);
    }

    [Fact]
    public async Task GraphicsApiHistoryKeepsItsObservationWindowAndPublishesSourceActivity()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), GpuPlacementProcessHistoryObserver.ObservationWindow);
        await using var harness = PresentFrameEtwTestHarness.Create();
        var lease = harness.Source.AcquireGraphicsApiSubscription(Instance());
        await harness.WaitRunningAsync();
        Assert.True(harness.Fixture.Authority.Read<bool>(ResourceManager.App.StateItems.StateItemUids.GraphicsApiObservationInUse).Value);
        await harness.ReleaseAsync(lease);
    }

    [Fact]
    public async Task IdentificationCommitsEtwEvidenceAndReleasesAllInstancesOfThatExecutable()
    {
        using var files = new Files();
        var history = new JsonGpuPlacementProcessHistoryStore(files);
        var source = new Presents();
        using var owner = Owner(files, history, source);
        var now = TimeProvider.System.GetTimestamp();
        var snapshot = Snapshot(Process(files), Process(files) with { ProcessId = 43 });
        await owner.ReconcileAsync(snapshot, Registered, now, default);
        Assert.Equal(2, source.Leases.Count);
        source.Leases[0].Api = GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12;
        await owner.ReconcileAsync(snapshot, Registered, now, default);
        Assert.All(source.Leases, lease => Assert.True(lease.Disposed));
        var saved = Assert.Single((await new JsonGpuPlacementProcessHistoryStore(files)
            .GetSoftwareHistoryAsync("software", "Software", default)).Processes);
        Assert.Equal(source.Leases[0].Api, saved.GraphicsApi);
        Assert.Equal(new[] { "etw-present" }, saved.EvidenceSources);
        await owner.ReconcileAsync(snapshot, Registered, now, default);
        Assert.Equal(2, source.Leases.Count);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("timeout")]
    [InlineData("unavailable")]
    [InlineData("excluded")]
    [InlineData("unregistered")]
    public async Task EndingTheEmptyAttemptPersistsSuppressionAndReleasesWithoutRepeating(string end)
    {
        using var files = new Files();
        var history = new JsonGpuPlacementProcessHistoryStore(files);
        var source = new Presents();
        var plans = Plans();
        using var owner = Owner(files, history, source, plans);
        var now = TimeProvider.System.GetTimestamp();
        var snapshot = Snapshot(Process(files));
        await owner.ReconcileAsync(snapshot, Registered, now, default);
        var lease = Assert.Single(source.Leases);
        if (end == "excluded") plans.Publish(plans.Current with
        {
            GpuPlacement = new(true, new Dictionary<string, ResolvedGpuPlacementPolicy>
                { ["software"] = ResolvedGpuPlacementPolicy.FromSoftware(Policy() with { GpuSchedulingExcluded = true }) }, new Dictionary<string, ResolvedGpuPlacementPolicy>())
        });
        await owner.ReconcileAsync(end == "exit" ? Snapshot() : end == "unavailable" ? null : snapshot,
            end == "unregistered" ? new HashSet<string>() : Registered,
            now + (end == "timeout" ? 31 * TimeProvider.System.TimestampFrequency : 0), default);
        Assert.True(lease.Disposed);
        Assert.True(File.Exists(files.HistoryPath));
        if (end != "exit")
        {
            plans.Publish(Plans().Current);
            await owner.ReconcileAsync(snapshot, Registered, now + 40 * TimeProvider.System.TimestampFrequency, default);
            Assert.Single(source.Leases);
        }
    }

    [Fact]
    public async Task ExcludedUnregisteredAndPreviouslyIdentifiedSoftwareDoNotSubscribe()
    {
        using var files = new Files();
        var history = new JsonGpuPlacementProcessHistoryStore(files);
        var source = new Presents();
        using var owner = Owner(files, history, source, new(Policy() with { GpuSchedulingExcluded = true }));
        await owner.ReconcileAsync(Snapshot(Process(files)), Registered, 0, default);
        Assert.Empty(source.Leases);
        using var other = Owner(files, history, source);
        await other.ReconcileAsync(Snapshot(Process(files)), new HashSet<string>(), 0, default);
        await history.SaveFirstGraphicsApiAsync("software", "Software", files.Instance(), GpuGraphicsApi.D3D9, default);
        await other.ReconcileAsync(Snapshot(Process(files)), Registered, 0, default);
        Assert.Empty(source.Leases);
    }

    [Fact]
    public async Task TimeoutSuppressesNewPidsOfTheImage()
    {
        using var files = new Files();
        var source = new Presents();
        using var owner = Owner(files, new JsonGpuPlacementProcessHistoryStore(files), source);
        await owner.ReconcileAsync(Snapshot(Process(files)), Registered, 0, default);
        await owner.ReconcileAsync(Snapshot(Process(files)), Registered, 31 * TimeProvider.System.TimestampFrequency, default);
        await owner.ReconcileAsync(Snapshot(Process(files)), Registered, 40 * TimeProvider.System.TimestampFrequency, default);
        Assert.Single(source.Leases);
        await owner.ReconcileAsync(Snapshot(Process(files) with { ProcessStartKey = 2 }), Registered,
            41 * TimeProvider.System.TimestampFrequency, default);
        Assert.Single(source.Leases);
        await owner.ReconcileAsync(Snapshot(), Registered, 42 * TimeProvider.System.TimestampFrequency, default);
        Assert.All(source.Leases, lease => Assert.True(lease.Disposed));
    }

    [Fact]
    public async Task MissingRuntimeRowsDoNotEraseAnAttemptWhileItsInventoryInstanceIsStillAlive()
    {
        using var files = new Files();
        var source = new Presents();
        using var owner = Owner(files, new JsonGpuPlacementProcessHistoryStore(files), source);
        var process = Process(files);
        var current = Snapshot(process);
        await owner.ReconcileAsync(current, Registered, 0, default);
        await owner.ReconcileAsync(current, Registered, 31 * TimeProvider.System.TimestampFrequency, default);
        Assert.True(Assert.Single(source.Leases).Disposed);

        // RuntimeState is momentarily absent, but the separate current inventory
        // still contains the exact live PID/birth. The old code treated this as exit.
        var missingRuntime = Snapshot() with
        {
            EnumeratedCount = 1, SkippedCount = 1,
            HasSeparateFoundationPayloads = true, InventoryProcesses = [process]
        };
        Assert.True(missingRuntime.IsInventoryCurrentComplete());
        await owner.ReconcileAsync(missingRuntime, Registered, 35 * TimeProvider.System.TimestampFrequency, default);
        await owner.ReconcileAsync(current, Registered, 40 * TimeProvider.System.TimestampFrequency, default);
        Assert.Single(source.Leases);

        // Complete inventory absence ends the instance, but the image's persisted
        // empty-result record still suppresses another PID until expiry.
        await owner.ReconcileAsync(Snapshot() with { CurrentMetricMask = SchedulingProcessMetricMask.None },
            Registered, 45 * TimeProvider.System.TimestampFrequency, default);
        await owner.ReconcileAsync(current, Registered, 50 * TimeProvider.System.TimestampFrequency, default);
        Assert.Single(source.Leases);
    }

    [Fact]
    public async Task SavedPresentEvidenceChangesStartupFromUnknownUsingTheExistingResolver()
    {
        using var files = new Files();
        var policies = new JsonGpuPlacementPolicyStore(files, NullLogger<JsonGpuPlacementPolicyStore>.Instance);
        var policy = Policy() with { StartupTargetGpu = GpuPlacementTargets.HighPerformanceGpu };
        await policies.SaveSoftwarePolicyAsync(policy, default);
        var history = new JsonGpuPlacementProcessHistoryStore(files);
        var observed = Assert.Single((await history.ObserveAsync(files.Request(), default)).Processes);
        await policies.SaveProcessPolicyAsync(GpuPlacementPolicyDefaults.CreateProcessPolicy("software", observed) with
            { StartupInterceptionEnabled = true }, default);
        var resolver = new GpuStartupPlacementResolver(policies, new PlanProvider(policy),
            new D3d11ProxyShimRuntime(files), history, new EmptyStartupModuleSettingsStore(), ResourceManager.Shared.Packages.PackageRuntimeCatalog.Current,
            new SoftwareRegistry(), NullLogger<GpuStartupPlacementResolver>.Instance);
        Assert.Equal("graphics-api-unidentified", (await resolver.ResolveAsync(new(files.Target), default)).Status);
        var source = new Presents();
        using var owner = Owner(files, history, source);
        await owner.ReconcileAsync(Snapshot(Process(files)), Registered, 0, default);
        Assert.Single(source.Leases).Api = GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12;
        await owner.ReconcileAsync(Snapshot(Process(files)), Registered, 1, default);
        var decision = await resolver.ResolveAsync(new(files.Target), default);
        Assert.Equal(GpuStartupPlacementDecisionKinds.Inject, decision.Decision);
        Assert.Equal(GpuPlacementProviderIds.D3dDeviceCreateShim, Assert.Single(decision.StartupProviders));
    }

    [Fact]
    public async Task HostShutdownReleasesBothThePresentAndProcessObservationLeases()
    {
        using var files = new Files();
        var source = new Presents();
        var processSource = new Processes(Snapshot(Process(files)));
        var record = new SoftwareRecord("software", "Software", "game", "installed", [], [],
            new(false, "None", "missing_uninstall_entry"), null);
        using var owner = new GpuPlacementProcessHistoryObserver(processSource, new SoftwareRegistry([record]),
            Plans(), new JsonGpuPlacementProcessHistoryStore(files), source,
            NullLogger<GpuPlacementProcessHistoryObserver>.Instance);
        await owner.StartAsync(default);
        var lease = await source.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await owner.StopAsync(default);
        Assert.True(lease.Disposed);
        Assert.Equal(0, processSource.SamplingSubscriptions);
        Assert.NotNull(Assert.Single((await new JsonGpuPlacementProcessHistoryStore(files)
            .GetSoftwareHistoryAsync("software", "Software", default)).Processes).NoPresentUntil);
        using var restarted = Owner(files, new JsonGpuPlacementProcessHistoryStore(files), source);
        await restarted.ReconcileAsync(Snapshot(Process(files) with { ProcessId = 99 }), Registered, 0, default);
        Assert.Single(source.Leases);
    }

    private static readonly IReadOnlySet<string> Registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "software" };
    private static GpuPlacementSoftwarePolicy Policy() => GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software");
    private static PlanProvider Plans() => new(Policy());
    private static GpuPlacementProcessInstance Instance() => new(42, (ulong)DateTime.UtcNow.ToFileTimeUtc(), "target", @"C:\target.exe");
    private static SchedulingProcessFact Process(Files files)
        => new(42, 1, "target", files.Target, "software", "Software", "game", "game", 1,
            SchedulingProcessMetricMask.RuntimeState | SchedulingProcessMetricMask.GpuUsage, 0, 0, 1,
            [new(0, 1, SchedulingProcessMetricMask.GpuUsage, 1, 0, 1, 0)]);
    private static SchedulingProcessFactSnapshot Snapshot(params SchedulingProcessFact[] processes)
        => new(SamplingObservationStatus.Current, 1, DateTime.UtcNow.Ticks, (uint)processes.Length,
            (uint)processes.Length, 0, 0, SchedulingProcessMetricMask.RuntimeState | SchedulingProcessMetricMask.GpuUsage,
            SchedulingProcessMetricMask.RuntimeState | SchedulingProcessMetricMask.GpuUsage, processes);
    private static GpuPlacementProcessHistoryObserver Owner(Files files,
        IGpuPlacementProcessHistoryStore history, Presents source, PlanProvider? plans = null, TimeProvider? clock = null)
        => new(new UnusedProcesses(), new SoftwareRegistry(), plans ?? Plans(), history, source,
            NullLogger<GpuPlacementProcessHistoryObserver>.Instance, clock);

    private sealed class HistoryReadHook(IGpuPlacementProcessHistoryStore inner) : IGpuPlacementProcessHistoryStore
    {
        internal Action? AfterRead { get; set; }
        public async Task<GpuPlacementSoftwareProcessHistory> GetSoftwareHistoryAsync(string id, string name, CancellationToken token)
        {
            var result = await inner.GetSoftwareHistoryAsync(id, name, token);
            AfterRead?.Invoke();
            return result;
        }
        public Task SaveNoPresentAsync(string id, string name, GpuPlacementProcessInstance process, DateTimeOffset expiry, CancellationToken token)
            => inner.SaveNoPresentAsync(id, name, process, expiry, token);
        public Task<GpuPlacementSoftwareProcessHistory> ObserveAsync(GpuPlacementProcessObservationRequest request, CancellationToken token)
            => inner.ObserveAsync(request, token);
        public Task<GpuPlacementSoftwareProcessHistory> SaveFirstGraphicsApiAsync(string id, string name, GpuPlacementProcessInstance process,
            GpuGraphicsApi api, CancellationToken token) => inner.SaveFirstGraphicsApiAsync(id, name, process, api, token);
        public Task<GpuPlacementSoftwareProcessHistory> SaveFirstGraphicsApiAsync(string id, string name, GpuPlacementProcessInstance process,
            GpuGraphicsApi api, string evidence, CancellationToken token) => inner.SaveFirstGraphicsApiAsync(id, name, process, api, evidence, token);
    }

    private sealed class Presents : IPresentGraphicsApiObservationSource
    {
        internal List<Subscription> Leases { get; } = [];
        internal TaskCompletionSource<Subscription> Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IPresentGraphicsApiSubscription AcquireGraphicsApiSubscription(GpuPlacementProcessInstance process)
        {
            var lease = new Subscription();
            Leases.Add(lease);
            Acquired.TrySetResult(lease);
            return lease;
        }
    }

    private sealed class Subscription : IPresentGraphicsApiSubscription
    {
        internal GpuGraphicsApi? Api { get; set; }
        internal bool Disposed { get; private set; }
        public GpuGraphicsApi? Read() => Disposed ? null : Api;
        public void Dispose() => Disposed = true;
    }

    private sealed class UnusedProcesses : ISchedulingProcessFactObservationSource
    {
        public IDisposable AcquireSubscription(string id, SchedulingProcessMetricMask mask, TimeSpan interval)
            => throw new NotSupportedException();
        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request)
            => throw new NotSupportedException();
    }

    private sealed class Processes(SchedulingProcessFactSnapshot snapshot) : ISchedulingProcessFactObservationSource
    {
        internal int SamplingSubscriptions { get; private set; }
        public IDisposable AcquireSubscription(string id, SchedulingProcessMetricMask mask, TimeSpan interval)
        {
            SamplingSubscriptions++;
            throw new InvalidOperationException("Observer must consume publications without sampling demand.");
        }
        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request) => snapshot;
    }

    private sealed class PlanProvider(GpuPlacementSoftwarePolicy policy) : IRuntimePlanProvider
    {
        public CompiledRuntimePlan Current { get; private set; } = CompiledRuntimePlan.Default with
        {
            HostManager = CompiledRuntimePlan.Default.HostManager with
            {
                HotPublish = CompiledRuntimePlan.Default.HostManager.HotPublish with
                {
                    PlacementCoordinator = CompiledRuntimePlan.Default.HostManager.HotPublish.PlacementCoordinator with
                    { NoPresentExpirySeconds = 86400 }
                }
            },
            GpuPlacement = new(true, new Dictionary<string, ResolvedGpuPlacementPolicy>
                { [policy.SoftwareId] = ResolvedGpuPlacementPolicy.FromSoftware(policy) },
                new Dictionary<string, ResolvedGpuPlacementPolicy>())
        };
        public RuntimePlanPublicationLease AcquirePublicationLease() => RuntimePlanPublicationLease.CreateUntracked(Current, 1);
        public RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan) { Current = plan; return new(plan, 1, []); }
    }

    private sealed class SoftwareRegistry(IReadOnlyList<SoftwareRecord>? records = null) : ISoftwareRegistryView
    {
        internal int Reads { get; private set; }
        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken token)
        { Reads++; return Task.FromResult<IReadOnlyList<SoftwareRecord>>(records ?? []); }
        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken token) => GetSoftwareAsync(token);
    }

    private sealed class PublishedProcesses(SchedulingProcessFactSnapshot snapshot)
        : ISchedulingProcessFactObservationSource, ISchedulingProcessFactPublicationSource
    {
        public ResourceManager.ReadWrite.Publications.SamplingPublicationSignal Publications { get; } = new();
        internal SchedulingProcessFactSnapshot Snapshot { get; private set; } = snapshot;
        internal int Reads;
        internal int SamplingSubscriptions;
        public IDisposable AcquireSubscription(string id, SchedulingProcessMetricMask mask, TimeSpan interval)
        { Interlocked.Increment(ref SamplingSubscriptions); throw new InvalidOperationException("Passive only."); }
        public SchedulingProcessFactSnapshot ReadLatest(SchedulingProcessFactRequest request)
        { Interlocked.Increment(ref Reads); return Snapshot; }
        internal void Publish(SchedulingProcessFactSnapshot next)
        { Snapshot = next; Publications.Publish([SamplingDatasetIds.ProcessInventory, SamplingDatasetIds.ProcessGpuUsage]); }
    }

    private sealed class Files : IHostEnvironment, IDisposable
    {
        public string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("RM_REVIEW_SCRATCH") ?? Path.GetTempPath(),
            $"present-api-{Guid.NewGuid():N}");
        public string Target => Path.Combine(Root, "target.exe");
        public string HistoryPath => Path.Combine(Root, "UserData", "SoftwareProfiles", "gpu-placement-process-history.local.json");
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PresentApiTests";
        public string ContentRootPath { get => Root; set => throw new NotSupportedException(); }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public Files() { Directory.CreateDirectory(Root); File.WriteAllBytes(Target, []); }
        public GpuPlacementProcessInstance Instance() => new(42, 1, "target", Target);
        public GpuPlacementProcessObservationRequest Request() => new("software", "Software", [new("target", Target, "x64", 42, ["fixture"])]);
        public void Dispose()
        {
            // Only files created by this fixture; leave no recursive cleanup or arbitrary path traversal.
            foreach (var path in new[] { HistoryPath, Target }) if (File.Exists(path)) File.Delete(path);
            // The real startup resolver owns its generated policy files; the registered scratch
            // workspace retains them until official task completion.
        }
    }
}
