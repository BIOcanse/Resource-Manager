using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Security;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    private sealed class ExternalRendererReplayFactAttribute : FactAttribute
    {
        public ExternalRendererReplayFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RM_EXTERNAL_RENDERER_REPLAY")))
                Skip = "Requires retained real renderer snapshots; never starts graphics or actions.";
        }
    }

    [ExternalRendererReplayFact]
    public async Task RetainedRendererSnapshotsReachScoringWithoutAnyGpuAction()
    {
        var root = Environment.GetEnvironmentVariable("RM_EXTERNAL_RENDERER_REPLAY")!;
        var hardware = JsonSerializer.Deserialize<HardwareMetricSnapshot>(File.ReadAllText(Path.Combine(root, "hardware-before.json")))!;
        var facts = JsonSerializer.Deserialize<SchedulingProcessFactSnapshot>(File.ReadAllText(Path.Combine(root, "snapshot-before.json")))!;
        var rows = JsonSerializer.Deserialize<SchedulingProcessFact[]>(File.ReadLines(Path.Combine(root, "coordinator-facts.jsonl")).First())!;
        facts = facts with { Processes = rows, InventoryProcesses = rows, AttributionProcesses = rows,
            EnumeratedCount = (uint)rows.Length, EmittedCount = (uint)rows.Length, ExcludedCount = 0, SkippedCount = 0 };
        var logs = new ReplayLogger();
        using var self = new ResourceManager.App.Infrastructure.Adaptation.ResourceManagerSelfLocalResourceManager(
            new ResourceManagerSelfTypedResourceActionTests.RecordingPolicyWriter(), new ResourceManager.App.Infrastructure.DiskUsage.DiskUsageTreeStore());
        var topology = CreateAutomaticPlacementTopology();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            processFactsOverride: new RetainedFacts(facts), metricSamplerOverride: new RetainedHardware(hardware),
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology),
            automaticPlacementTopology: topology, coordinatorLogger: logs, selfLocalResourceManager: self);
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.True(logs.Errors.Count == 0, string.Join(Environment.NewLine, logs.Errors));
        Assert.NotNull(fixture.Coordinator.SchedulingAuthority.Compute?.Gpu);
        var gpuScores = fixture.Coordinator.SchedulingAuthority.Compute!.Gpu!.Scores;
        var row = Assert.Single(rows);
        var policy = ResolvedGpuPlacementPolicy.FromSoftware(GpuPlacementPolicyDefaults.CreateSoftwarePolicy(row.SoftwareId, "Owned renderer") with
        {
            EnabledMode = GpuPlacementPolicyModes.Auto, TargetGpu = GpuPlacementTargets.AutoIdleGpu,
            RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise, RuntimeHotSwitchEnabled = true,
            AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim]
        });
        var process = new HostManagerAutomaticPlacementProcess("retained", row.SoftwareId, "Owned renderer", row.ProcessId,
            row.ProcessStartKey, row.ProcessName, row.ExecutablePath, true, policy, null,
            gpuScores.Where(s => s.ProcessId == row.ProcessId).ToDictionary(s => s.AdapterKey, s => s.Score),
            row.Gpus.ToDictionary(g => g.AdapterKey, g => g.UsagePercent))
        { ObservedDedicatedMemoryBytes = row.Gpus.Where(g => g.ResidentMemoryBytes.HasValue)
            .ToDictionary(g => g.AdapterKey, g => g.ResidentMemoryBytes!.Value) };
        var plan = HostManagerAutomaticPlacementPlanner.Plan(topology, hardware, CompiledHardwareScorePlan.Default,
            [process], new(16, 16, 16, 16, 4, 2, 16, 16), true, new(95, 80));
        var movement = Assert.Single(plan.Gpu);
        Assert.Equal(62525UL, movement.ObservedAdapterKey);
        Assert.Equal(69842UL, movement.TargetAdapterKey);
    }

    private sealed class RetainedHardware(HardwareMetricSnapshot snapshot) : IMetricSnapshotObservationSource
    {
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => snapshot;
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan interval) => new RetainedLease();
        private sealed class RetainedLease : IDisposable { public void Dispose() { } }
    }

    private sealed class RetainedFacts(SchedulingProcessFactSnapshot snapshot) : ISchedulingProcessFactObservationSource
    {
        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request) => snapshot;
        public IDisposable AcquireSubscription(string id, SchedulingProcessMetricMask mask, TimeSpan interval) => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }

    private sealed class ReplayLogger : ILogger<HostManagerSmartCoordinator>
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { if (error is not null) Errors.Add(error.ToString()); }
    }

    [Fact]
    public async Task InspectOwnedSamplingHostWithoutGpuAction()
    {
        var root = Environment.GetEnvironmentVariable("RM_SAMPLING_DIAGNOSTIC_ROOT");
        if (string.IsNullOrEmpty(root)) return;
        Assert.False(Directory.Exists(root));
        Directory.CreateDirectory(root);
        using var host = Host.CreateDefaultBuilder().UseContentRoot(root)
            .ConfigureServices(services => services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly)).Build();
        await host.StartAsync();
        try
        {
            var source = host.Services.GetRequiredService<ISchedulingProcessFactObservationSource>();
            await host.Services.GetRequiredService<IMetricSampler>().GetSnapshotAsync(MetricSampleRequest.CatalogProbe, default);
            using var lease = source.AcquireSubscription("sampling-diagnostic", SchedulingProcessMetricMask.CpuUsage
                | SchedulingProcessMetricMask.RuntimeState | SchedulingProcessMetricMask.GpuUsage | SchedulingProcessMetricMask.GpuDedicatedMemory, TimeSpan.FromSeconds(1));
            await Task.Delay(10000);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var state = source.GetType().GetProperty("schedulingSnapshotState", flags)!.GetValue(source)!;
            var values = new Dictionary<string, object?>();
            foreach (var name in new[] { "inventory", "attribution", "datasets" })
            {
                var value = state.GetType().GetField(name, flags)!.GetValue(state);
                var entries = value is System.Collections.IDictionary map ? map.Values.Cast<object>().ToArray() : value is null ? [] : new[] { value };
                values[name] = entries.Select(entry => entry.GetType().GetProperties().Where(p => p.Name is not ("Processes" or "SoftwareBaseScores"))
                    .ToDictionary(p => p.Name, p => p.GetValue(entry))).ToArray();
            }
            File.WriteAllText(Path.Combine(root, "sampling-state.json"), JsonSerializer.Serialize(values));
        }
        finally { await host.StopAsync(); }
    }

    [ExternalRendererFact]
    public async Task OwnedRendererExternalRouteUsesAutomaticCoordinatorAndLedger()
    {
        WindowsNativeLaunchErrorPolicy.InitializeForCurrentProcess();
        var root = Environment.GetEnvironmentVariable("RM_EXTERNAL_RENDERER_ROOT")!;
        var automaticOverflow = Environment.GetEnvironmentVariable("RM_EXTERNAL_RENDERER_AUTOMATIC") == "1";
        var qt = Environment.GetEnvironmentVariable("RM_EXTERNAL_RENDERER_ENGINE") is "qtquick-d3d12" or "qtquick-vulkan";
        Assert.False(Directory.Exists(root));
        Directory.CreateDirectory(root);
        using var identityJson = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("RM_EXTERNAL_RENDERER_IDENTITY")!));
        var input = identityJson.RootElement;
        var identity = new GpuPlacementProcessInstance(input.GetProperty("gpuPid").GetInt32(),
            ulong.Parse(input.GetProperty("gpuFileTime").GetString()!),
            Path.GetFileNameWithoutExtension(input.GetProperty("rootImage").GetString()!), input.GetProperty("rootImage").GetString()!);
        using var host = Host.CreateDefaultBuilder().UseContentRoot(root)
            .ConfigureLogging(log => log.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services => services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly)).Build();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await host.StartAsync(bound.Token);
        var controlled = host.Services.GetRequiredService<IControlledSoftwareRegistry>();
        ControlledSoftwareRegistration? registration = null;
        if (qt)
        {
            registration = controlled.Register(new("owned-qt-renderer", ["Owned Qt renderer"], [],
                [new(identity.ProcessName, identity.ProcessId, identity.ExecutablePath)], []));
            File.WriteAllText(Path.Combine(root, "owned-registration.json"), JsonSerializer.Serialize(registration));
        }
        var hardware = host.Services.GetRequiredService<IMetricSnapshotObservationSource>();
        var processes = host.Services.GetRequiredService<ISchedulingProcessFactObservationSource>();
        var external = host.Services.GetRequiredService<WindowsExternalGpuPlacementRuntime>();
        await host.Services.GetRequiredService<IMetricSampler>().GetSnapshotAsync(MetricSampleRequest.CatalogProbe, bound.Token);
        var request = MetricSampleRequest.ForIdsAndAllGpuCoreMetrics([SamplingDatasetIds.SystemCpuUsage,
            SamplingDatasetIds.SystemMemoryUsage, SamplingDatasetIds.SystemVirtualMemoryUsage]);
        const SchedulingProcessMetricMask mask = SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.RuntimeState
            | SchedulingProcessMetricMask.GpuUsage | SchedulingProcessMetricMask.GpuDedicatedMemory;
        var hardwareLease = hardware.AcquireSubscription("owned-external-coordinator", request, TimeSpan.FromSeconds(5));
        var processLease = processes.AcquireSubscription("owned-external-coordinator", mask, TimeSpan.FromSeconds(5));
        CapturingExternalActions? actions = null;
        string? failure = null;
        try
        {
            HardwareMetricSnapshot? latest = null;
            SchedulingProcessFactSnapshot? facts = null;
            var end = Environment.TickCount64 + 25000;
            do
            {
                latest = hardware.ReadLatest(request);
                if (latest is not null) facts = processes.ReadLatest(new(mask, null, latest.GpuInventory));
                var observed = facts?.Processes.SingleOrDefault(p => p.ProcessId == identity.ProcessId && p.ProcessStartKey == identity.ProcessStartKey
                    && p.ExecutablePath == identity.ExecutablePath && p.Gpus.Any(g => g.ResidentMemoryBytes is > 0));
                if (observed is not null)
                {
                    if (!automaticOverflow) break;
                    var adapter = latest!.GpuInventory.Adapters.Single(a => a.AdapterKey ==
                        observed.Gpus.OrderByDescending(g => g.ResidentMemoryBytes).First().AdapterKey);
                    if (adapter.UsageStatus == SamplingObservationStatus.Current && adapter.UsagePercent >= 95
                        || adapter.CapacityStatus == SamplingObservationStatus.Current && adapter.TotalDedicatedMemoryBytes > 0
                            && adapter.UsedDedicatedMemoryBytes * 100d / adapter.TotalDedicatedMemoryBytes >= 80) break;
                }
                await Task.Delay(250, bound.Token);
            } while (Environment.TickCount64 < end);
            File.WriteAllText(Path.Combine(root, "sampling-before.json"), JsonSerializer.Serialize(
                host.Services.GetRequiredService<IResourceBreakdownObservationSource>().ReadLatest(
                    new([], new Dictionary<string, string>(), ProcessSampleDetailLevel.SmartSchedulingLite, mask))));
            File.WriteAllText(Path.Combine(root, "hardware-before.json"), JsonSerializer.Serialize(latest));
            File.WriteAllText(Path.Combine(root, "snapshot-before.json"), JsonSerializer.Serialize(facts));
            Assert.NotNull(latest);
            Assert.NotNull(facts);
            var ownedFact = Assert.Single(facts.Processes, p => p.ProcessId == identity.ProcessId && p.ProcessStartKey == identity.ProcessStartKey);
            Assert.Equal(identity.ExecutablePath, ownedFact.ExecutablePath);
            File.WriteAllText(Path.Combine(root, "hardware-before.json"), JsonSerializer.Serialize(latest));
            File.WriteAllText(Path.Combine(root, "snapshot-before.json"), JsonSerializer.Serialize(facts));
            if (automaticOverflow)
            {
                var source = latest.GpuInventory.Adapters.Single(adapter => adapter.AdapterKey ==
                    ownedFact.Gpus.OrderByDescending(gpu => gpu.ResidentMemoryBytes).First().AdapterKey);
                Assert.True(source.UsageStatus == SamplingObservationStatus.Current && source.UsagePercent >= 95
                    || source.CapacityStatus == SamplingObservationStatus.Current && source.TotalDedicatedMemoryBytes > 0
                        && source.UsedDedicatedMemoryBytes * 100d / source.TotalDedicatedMemoryBytes >= 80,
                    "Actual source pressure is below the unchanged overflow thresholds; no move requested.");
            }
            var topology = CreateAutomaticPlacementTopology();
            using var self = new ResourceManager.App.Infrastructure.Adaptation.ResourceManagerSelfLocalResourceManager(
                new ResourceManagerSelfTypedResourceActionTests.RecordingPolicyWriter(), new ResourceManager.App.Infrastructure.DiskUsage.DiskUsageTreeStore());
            using var ledger = new JsonHostManagerRollbackStateStore(Path.Combine(root, "automatic-ledger.json"), TimeProvider.System, TimeSpan.FromMinutes(1));
            await ledger.ReserveNativeHostSessionIncarnationAsync(default);
            var scoped = new OwnedRendererFacts(processes, identity, root);
            await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
                policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
                optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
                metricSamplerOverride: hardware, processFactsOverride: scoped,
                cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology,
                rollbackStateStoreOverride: ledger,
                coordinatorLogger: host.Services.GetRequiredService<ILogger<HostManagerSmartCoordinator>>(),
                selfLocalResourceManager: self,
                processRecoveryRead: pid =>
                {
                    Assert.Equal(identity.ProcessId, pid);
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(pid, process.StartTime));
                    }
                    catch (ArgumentException) { return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.NotFoundOrExited(0, "Owned child exited."); }
                },
                runningGpuActionsFactory: (path, plans) => actions = new(
                    new ExternalOnlyRunningGpuPlacementActionService(plans, external), identity, ledger, root));
            var policy = GpuPlacementPolicyDefaults.CreateSoftwarePolicy(ownedFact.SoftwareId, "Owned renderer") with
            {
                EnabledMode = GpuPlacementPolicyModes.Auto, SchedulingMode = GpuPlacementSchedulingModes.Precise,
                RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise, RuntimeHotSwitchEnabled = true,
                AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim],
                TargetGpu = automaticOverflow ? GpuPlacementTargets.AutoIdleGpu : GpuPlacementTargets.IntegratedGpu
            };
            var enabled = fixture.RuntimePlan with { Version = fixture.RuntimePlan.Version + 1,
                GpuPlacement = new(true, new Dictionary<string, ResolvedGpuPlacementPolicy>
                    { [ownedFact.SoftwareId] = ResolvedGpuPlacementPolicy.FromSoftware(policy) }, new Dictionary<string, ResolvedGpuPlacementPolicy>()) };
            fixture.RuntimePlanProvider.Publish(enabled);
            var preparation = await actions!.PrepareAsync(new("owned-renderer-preflight", ownedFact.SoftwareId,
                "Owned renderer", [identity], "automatic-placement", latest.GpuInventory.Adapters.First().AdapterKey,
                policy.PreferredRuntimeSwitchMethod), bound.Token);
            File.WriteAllText(Path.Combine(root, "preparation-before-cycle.json"), JsonSerializer.Serialize(preparation));
            Assert.NotNull(preparation.Plan?.External);
            Assert.Equal(input.GetProperty("rootPid").GetInt32(), preparation.Plan.External.Root.ProcessId);
            Assert.Equal(ulong.Parse(input.GetProperty("rootFileTime").GetString()!), preparation.Plan.External.Root.ProcessStartKey);
            _ = await fixture.RunRealtimeCycleAsync();
            File.WriteAllText(Path.Combine(root, "authority.json"), JsonSerializer.Serialize(fixture.Coordinator.SchedulingAuthority));
            Assert.Equal(1, actions!.ApplyCount);
            Assert.Equal(RunningGpuPlacementActionStatuses.RecreateRequested, actions.Result!.Status);
            var confirmation = Assert.Single(actions.Result.Records).Metadata;
            var beforeResident = JsonSerializer.Deserialize<ExternalResidentSample>(confirmation["beforeResident"])!;
            var afterResident = JsonSerializer.Deserialize<ExternalResidentSample>(confirmation["afterResident"])!;
            var transfer = ExternalGpuResidentObservation.ClassifyTransfer(beforeResident, afterResident,
                ulong.Parse(confirmation["targetAdapterKey"], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(transfer == ExternalResidentTransfer.Full ? "confirmed" : "partial", confirmation["residentConfirmation"]);
            Assert.True(afterResident.ObservedAtUtcTicks > long.Parse(confirmation["confirmationStartedAtUtcTicks"],
                System.Globalization.CultureInfo.InvariantCulture));
            var applied = await ledger.LoadAsync(default);
            File.WriteAllText(Path.Combine(root, "automatic-ledger-applied.json"), JsonSerializer.Serialize(applied));
            var record = Assert.Single(applied.AppliedPlacements.SelectMany(p => p.Records),
                ExternalGpuRuntimePlacementRecord.IsRecord);
            Assert.Equal(actions.Result.Status, JsonSerializer.Deserialize<RunningGpuPlacementActionResult>(record.Metadata!["runtimeActionResult"])!.Status);
            Assert.Equal("true", record.Metadata[ExternalGpuRuntimePlacementRecord.SettledKey]);
            Assert.False(external.HasUnreleasedControl);
            fixture.RuntimePlanProvider.Publish(enabled with { Version = enabled.Version + 1,
                OptimizationMode = CompiledOptimizationModePlan.Compile(AppOptimizationModes.Normal) });
            _ = await fixture.RunRealtimeCycleAsync();
            Assert.Empty((await ledger.LoadAsync(default)).AppliedPlacements);
            Assert.Equal(1, actions.ApplyCount);
            Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
            Assert.Equal(0, fixture.GraphicsPreferenceStore.TotalCalls);
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            while (external.HasUnreleasedControl) await Task.Delay(1000);
            processLease.Dispose(); hardwareLease.Dispose();
            if (registration is not null) Assert.True(controlled.Remove(registration.Id));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await host.StopAsync(stop.Token);
            File.WriteAllText(Path.Combine(root, "probe-result.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null, failure, result = actions?.Result, pid = Environment.ProcessId,
                birth = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToFileTimeUtc().ToString(),
                automaticCoordinator = true, pressureTriggered = automaticOverflow && failure is null,
                qualification = automaticOverflow
                    ? "Real published snapshots, automatic performance overflow, owned renderer scope; no synthetic pressure or explicit destination."
                    : "Real published snapshots and automatic coordinator; explicit owned IntegratedGpu policy, not a live overflow claim."
            }));
        }
        Assert.True(failure is null, failure);
    }

    private sealed class OwnedRendererFacts(ISchedulingProcessFactObservationSource source, GpuPlacementProcessInstance identity, string root)
        : ISchedulingProcessFactObservationSource
    {
        public SchedulingProcessFactSnapshot? ReadLatest(SchedulingProcessFactRequest request)
        {
            var snapshot = source.ReadLatest(request);
            if (snapshot is null) return null;
            var rows = snapshot.Processes.Where(p => p.ProcessId == identity.ProcessId && p.ProcessStartKey == identity.ProcessStartKey).ToArray();
            File.AppendAllText(Path.Combine(root, "coordinator-facts.jsonl"), JsonSerializer.Serialize(rows) + Environment.NewLine);
            return snapshot with { Processes = rows, EnumeratedCount = (uint)rows.Length, EmittedCount = (uint)rows.Length,
                ExcludedCount = 0, SkippedCount = 0, InventoryProcesses = rows, AttributionProcesses = rows };
        }
        public IDisposable AcquireSubscription(string id, SchedulingProcessMetricMask mask, TimeSpan interval)
            => source.AcquireSubscription(id, mask, interval);
    }

    private sealed class CapturingExternalActions(IRunningGpuPlacementActionService inner, GpuPlacementProcessInstance identity,
        IHostManagerRollbackStateStore ledger, string root) : IRunningGpuPlacementActionService
    {
        public int ApplyCount;
        public RunningGpuPlacementActionResult? Result;
        public bool HasUnreleasedExternalControl => inner.HasUnreleasedExternalControl;
        public async Task<RunningGpuPlacementPreparation> PrepareAsync(RunningGpuPlacementActionRequest request, CancellationToken token)
        {
            Assert.Equal(identity, Assert.Single(request.Processes));
            var prepared = await inner.PrepareAsync(request, token);
            File.AppendAllText(Path.Combine(root, "preparations.jsonl"), JsonSerializer.Serialize(new { request, prepared }) + Environment.NewLine);
            return prepared;
        }
        public async Task<RunningGpuPlacementActionResult> TryApplyAsync(RunningGpuPlacementActionPlan plan, RunningGpuPlacementExecution execution, CancellationToken token)
        {
            Assert.Equal(identity, Assert.Single(plan.Request.Processes));
            Assert.Equal(1, ++ApplyCount);
            var state = await ledger.LoadAsync(default);
            Assert.Contains(state.AppliedPlacements.SelectMany(p => p.Records), r => r.Metadata?.GetValueOrDefault("runtimeActionResult")?.Contains(RunningGpuPlacementActionStatuses.Unresolved) == true);
            File.WriteAllText(Path.Combine(root, "automatic-ledger-before-controller.json"), JsonSerializer.Serialize(state));
            return Result = await inner.TryApplyAsync(plan, execution with
            {
                ExecuteAsync = _ => throw new InvalidOperationException("Unexpected window action."),
                ExecuteRemoteCallAsync = (_, _) => throw new InvalidOperationException("No injected execution."),
                PrepareOpenGlAsync = (_, _, _) => throw new InvalidOperationException("No OpenGL.")
            }, token);
        }
        public Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(RunningGpuPlacementActionRequest request, RunningGpuApiObservationExecution execution, CancellationToken token)
            => throw new InvalidOperationException("No injected observation.");
        public Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(RunningGpuPlacementActionRequest request, Func<GpuPlacementProcessInstance, CancellationToken, Task<GpuGraphicsApi?>> observe, CancellationToken token)
            => throw new InvalidOperationException("No injected observation.");
    }
}

internal sealed class ExternalRendererFactAttribute : FactAttribute
{
    public ExternalRendererFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RM_EXTERNAL_RENDERER_IDENTITY")))
            Skip = "Requires an explicitly owned renderer session and fresh evidence root.";
    }
}
