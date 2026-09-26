using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.Preparation;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [OpenGlProductNativeFact]
    public async Task FullFirstUsePublicOpenGlCycleIdentifiesPreparesAndConfiguresThenReusesHistory()
    {
        var root = GpuWindowLedgerTestData.NewRoot("opengl-first-use-native");
        var executable = Path.GetFullPath(Environment.GetEnvironmentVariable("RM_OPENGL_PRODUCT_TARGET")!);
        var worker = Path.GetFullPath(Environment.GetEnvironmentVariable("RM_OPENGL_PRODUCT_WORKER")!);
        var adapter = ulong.Parse(Environment.GetEnvironmentVariable("RM_OPENGL_PRODUCT_ADAPTER")!, CultureInfo.InvariantCulture);
        var startup = Path.Combine(root, "startup-policy.txt");
        await File.WriteAllTextAsync(startup, string.Empty);
        using var target = new Process { StartInfo = new(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        } };
        target.StartInfo.ArgumentList.Add(root);
        target.StartInfo.ArgumentList.Add(adapter.ToString(CultureInfo.InvariantCulture));
        target.StartInfo.Environment[D3d11ProxyShimRuntime.PolicyEnvironmentVariableName] = startup;
        Assert.True(target.Start());
        var identity = new GpuPlacementProcessInstance(target.Id, checked((ulong)target.StartTime.ToFileTimeUtc()),
            Path.GetFileNameWithoutExtension(executable), executable);
        var errors = target.StandardError.ReadToEndAsync();
        var complete = false;
        var forcedCleanup = false;
        try
        {
            var ready = await Read("ready");
            Assert.Equal(identity.ProcessId, ready.GetProperty("pid").GetInt32());
            Assert.Equal(identity.ProcessStartKey, ready.GetProperty("creationFileTime").GetUInt64());
            Assert.Equal(32771U, ready.GetProperty("errorMode").GetUInt32());
            Assert.True(ready.GetProperty("inJob").GetBoolean());
            var source = ready.GetProperty("sourceLuid").GetUInt64();
            var inventory = new WindowsGpuAdapterOrderReader().ReadInventory();
            Assert.Equal(SamplingObservationStatus.Current, inventory.Status);
            Assert.Equal(0U, inventory.SkippedCount);
            Assert.Equal(0U, inventory.OverflowCount);
            var initialAdapter = Assert.Single(inventory.Adapters, item => NativePdhAdapterIdentity.Pack(item.Luid) == source);
            var selected = Assert.Single(inventory.Adapters, item => NativePdhAdapterIdentity.Pack(item.Luid) == adapter);
            Assert.NotEqual(source, adapter);
            const string softwareId = "software:owned-opengl-first-use";
            var policy = ResolvedGpuPlacementPolicy.FromSoftware(GpuPlacementPolicyDefaults.CreateSoftwarePolicy(softwareId, "Owned OpenGL cycle") with
            {
                EnabledMode = GpuPlacementPolicyModes.Auto,
                SchedulingMode = GpuPlacementSchedulingModes.Precise,
                RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise,
                RuntimeHotSwitchEnabled = true,
                AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim],
                TargetGpu = GpuPerformanceScorePresetResolver.IsLikelyIntegratedGpuName(selected.Name)
                    ? GpuPlacementTargets.IntegratedGpu : GpuPlacementTargets.HighPerformanceGpu
            });
            var facts = CreateNativeGpuCycleFacts(identity, softwareId, inventory, initialAdapter.Index);
            var ledgerPath = Path.Combine(root, "recovery.json");
            using var ledger = new JsonHostManagerRollbackStateStore(ledgerPath, TimeProvider.System, TimeSpan.FromMinutes(1));
            await ledger.ReserveNativeHostSessionIncarnationAsync(default);
            CapturingGpuCycleActions actions = null!;
            D3d11ProxyShimRuntime runtime = null!;
            var callback = new WindowsGpuCallbackPreparationRuntime(worker);
            var topology = CreateAutomaticPlacementTopology();
            await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
                warm: true, scoreOnlyEnabled: false, processFactsSnapshot: facts, policyExecutionEnabled: false,
                automaticMemoryCleanupEnabled: false, optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
                cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology),
                automaticPlacementTopology: topology, rollbackStateStoreOverride: ledger, gpuCallbackRuntime: callback,
                processRecoveryRead: pid =>
                {
                    Assert.Equal(identity.ProcessId, pid);
                    return RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(pid,
                        DateTimeOffset.FromFileTime(checked((long)identity.ProcessStartKey))));
                },
                runningGpuActionsFactory: (fixtureRoot, plans) =>
                {
                    runtime = new(new GpuPolicyEnvironment(fixtureRoot));
                    var history = new JsonGpuPlacementProcessHistoryStore(new GpuPolicyEnvironment(fixtureRoot));
                    return actions = new(new WindowsRunningGpuPlacementActionService(
                        NullLogger<WindowsRunningGpuPlacementActionService>.Instance, runtime,
                        new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance), history, plans, callback), ledger, runtime);
                });
            fixture.MetricSampler.SetSnapshot(CreateNativeGpuCycleHardware(inventory, source));
            var identification = new JsonGpuPlacementProcessHistoryStore(new GpuPolicyEnvironment(fixture.Root));
            Assert.Empty((await identification.GetSoftwareHistoryAsync(softwareId, "Owned OpenGL cycle", default)).Processes);
            actions.BeforeObservationWait = async process =>
            {
                Assert.Equal(identity, process);
                Assert.Empty((await identification.GetSoftwareHistoryAsync(softwareId, "Owned OpenGL cycle", default)).Processes);
                Assert.False(GpuActionFacts.HasPlacementEffects((await ledger.LoadAsync(default)).AppliedPlacements));
                var observed = await Command("observe", "observed");
                Assert.Equal(source, observed.GetProperty("actualLuid").GetUInt64());
                Assert.True(observed.GetProperty("sourceRestored").GetBoolean());
                Assert.True(observed.GetProperty("temporaryDeleted").GetBoolean());
            };
            var enabled = fixture.RuntimePlan with
            {
                Version = fixture.RuntimePlan.Version + 1,
                GpuPlacement = new(true, new Dictionary<string, ResolvedGpuPlacementPolicy> { [softwareId] = policy },
                    new Dictionary<string, ResolvedGpuPlacementPolicy>())
            };
            fixture.RuntimePlanProvider.Publish(enabled);
            _ = await fixture.Coordinator.RunOnceAsync(default);
            Assert.Equal(0, actions.ApplyCount);
            _ = await fixture.Coordinator.RunOnceAsync(default);
            Assert.Equal(1, actions.FirstUseCalls);
            Assert.Equal(1, actions.ObservationWaits);
            Assert.Equal(1, actions.ApplyCount);
            Assert.Equal(adapter, actions.Plan!.Request.TargetAdapterKey);
            Assert.Equal(GpuGraphicsApi.OpenGL, actions.Plan.GraphicsApis[identity.ProcessId]);
            Assert.Equal(GpuGraphicsApi.OpenGL, Assert.Single((await identification.GetSoftwareHistoryAsync(
                softwareId, "Owned OpenGL cycle", default)).Processes).GraphicsApi);
            var historyPath = Path.Combine(fixture.Root, "UserData", "SoftwareProfiles", "gpu-placement-process-history.local.json");
            var historyBytes = File.ReadAllBytes(historyPath);
            var applied = Assert.Single((await ledger.LoadAsync(default)).AppliedPlacements);
            var record = Assert.Single(applied.Records, item => item.Kind == HostManagerAppliedRecordKinds.GpuShimPolicy);
            using var preparation = JsonDocument.Parse(record.Metadata!["openGlPreparation"]);
            var prepared = preparation.RootElement;
            Assert.Equal(adapter, prepared.GetProperty("CallbackAdapter").GetUInt64());
            Assert.Equal(0U, prepared.GetProperty("Cleanup").GetProperty("ExitCode").GetUInt32());
            Assert.False(prepared.GetProperty("Cleanup").GetProperty("TerminationRequested").GetBoolean());
            Assert.Equal(0U, prepared.GetProperty("Cleanup").GetProperty("ActiveProcessCount").GetUInt32());
            var summary = JsonSerializer.Deserialize<RunningGpuPlacementActionResult>(record.Metadata["runtimeActionResult"])!;
            Assert.Equal(RunningGpuPlacementActionStatuses.Prepared, summary.Status);
            Assert.False(summary.Applied);
            var processResult = Assert.Single(summary.Processes);
            Assert.True(processResult.Configured);
            Assert.Equal(identity, processResult.Identity);
            Assert.True(processResult.Before!.Success);
            Assert.Equal(processResult.Before.Snapshot, processResult.After!.Snapshot);
            var calls = applied.Records.Where(GpuRemoteCallRecord.IsActionFact).Select(item =>
            {
                Assert.True(GpuRemoteCallRecord.TryRead(item, out var call));
                Assert.Equal(identity, call.Request.Process);
                Assert.True(call.Result!.Completed);
                Assert.False(call.BlocksProcess);
                return call;
            }).ToArray();
            Assert.Equal(6, calls.Length);
            Assert.Equal(new[] { GpuRemoteCallKind.LoadObservationProvider, GpuRemoteCallKind.StartApiObservation, GpuRemoteCallKind.StopApiObservation },
                actions.ObservationCallIds.Select(id => Assert.Single(calls, call => call.Request.CallId == id).Request.Kind));
            Assert.Equal(new[] { GpuRemoteCallKind.ConfigureProvider, GpuRemoteCallKind.ReadDevices, GpuRemoteCallKind.ReadDevices },
                actions.CallIds.Select(id => Assert.Single(calls, call => call.Request.CallId == id).Request.Kind));
            var appliedBytes = File.ReadAllBytes(ledgerPath);
            File.WriteAllBytes(Path.Combine(root, "applied-ledger.json"), appliedBytes);
            File.WriteAllBytes(Path.Combine(root, "software-history.json"), historyBytes);

            await Command("after-install", "after-install");
            await Command("create", "created");
            var rendered = await Command("render", "rendered");
            Assert.Equal(source, rendered.GetProperty("sourceLuid").GetUInt64());
            Assert.Equal(adapter, rendered.GetProperty("targetLuid").GetUInt64());
            _ = await fixture.Coordinator.RunOnceAsync(default);
            Assert.Equal(appliedBytes, File.ReadAllBytes(ledgerPath));
            Assert.Equal(historyBytes, File.ReadAllBytes(historyPath));
            Assert.Equal(1, actions.FirstUseCalls);
            Assert.Equal(1, actions.ApplyCount);
            await Normal(enabled.Version + 1);
            File.Copy(ledgerPath, Path.Combine(root, "restored-ledger.json"));
            Assert.Equal(adapter, (await Command("retained-target", "retained-target")).GetProperty("actualLuid").GetUInt64());
            foreach (var command in new[] { "default-create", "default-layer", "default-attributes" })
            {
                await Command(command, "candidate-created");
                var candidate = await Command("candidate-render", "candidate-rendered");
                Assert.Equal(source, candidate.GetProperty("actualLuid").GetUInt64());
                Assert.True(candidate.GetProperty("originalsPreserved").GetBoolean());
                await Command("candidate-delete", "candidate-deleted");
            }

            // No placement effect remains: this reuse must come from the committed software history.
            fixture.RuntimePlanProvider.Publish(enabled with { Version = enabled.Version + 2 });
            _ = await fixture.Coordinator.RunOnceAsync(default);
            Assert.Equal(1, actions.FirstUseCalls);
            Assert.Equal(1, actions.ObservationWaits);
            Assert.Equal(2, actions.ApplyCount);
            Assert.Equal(historyBytes, File.ReadAllBytes(historyPath));
            File.Copy(ledgerPath, Path.Combine(root, "known-history-applied-ledger.json"));
            await Normal(enabled.Version + 3);
            File.Copy(ledgerPath, Path.Combine(root, "known-history-restored-ledger.json"));
            Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
            Assert.Equal(0, fixture.GraphicsPreferenceStore.TotalCalls);
            Assert.Equal(string.Empty, await File.ReadAllTextAsync(startup));
            var done = await Command("stop-first-use", "complete");
            await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, target.ExitCode);
            Assert.Empty(await errors);
            Assert.True(done.GetProperty("foregroundUnchanged").GetBoolean());
            await File.WriteAllTextAsync(Path.Combine(root, "product-result.json"), JsonSerializer.Serialize(new
            {
                passed = true, identity, sourceAdapter = source, targetAdapter = adapter, preparation = prepared.Clone(), summary,
                calls, native = done, targetExitCode = target.ExitCode, worker, originalPublicRunOnce = true,
                actualObservation = true, firstUsePolicySeeded = false, firstUseCalls = actions.FirstUseCalls,
                observationWaits = actions.ObservationWaits, applyCalls = actions.ApplyCount,
                knownHistoryAfterRestore = true, loadFixture = true, thirdPartyMigrationVerified = false, deployed = false
            }));
            output.WriteLine("openGlFirstUseRoot=" + root);
            complete = true;

            async Task Normal(long version)
            {
                fixture.RuntimePlanProvider.Publish(enabled with
                {
                    Version = version,
                    OptimizationMode = CompiledOptimizationModePlan.Compile(AppOptimizationModes.Normal)
                });
                _ = await fixture.Coordinator.RunOnceAsync(default);
                var state = await ledger.LoadAsync(default);
                Assert.False(GpuActionFacts.HasPlacementEffects(state.AppliedPlacements));
                Assert.False(GpuActionFacts.HasUnsettledActions(state.AppliedPlacements));
                Assert.Null(runtime.ReadPolicy(applied.TargetId));
            }
        }
        finally
        {
            if (!complete && !target.HasExited)
            {
                forcedCleanup = true;
                target.Kill();
                await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            await File.WriteAllTextAsync(Path.Combine(root, "target.stderr.log"), await errors);
            await File.WriteAllTextAsync(Path.Combine(root, "target-cleanup.json"), JsonSerializer.Serialize(new
            {
                identity, complete, forcedCleanup, exited = target.HasExited,
                exitCode = target.HasExited ? target.ExitCode : (int?)null
            }));
        }

        async Task<JsonElement> Command(string command, string stage)
        {
            await target.StandardInput.WriteLineAsync(command);
            await target.StandardInput.FlushAsync();
            return await Read(stage);
        }
        async Task<JsonElement> Read(string stage)
        {
            var line = await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(35));
            Assert.NotNull(line);
            await File.AppendAllTextAsync(Path.Combine(root, "target.stdout.log"), line + "\n");
            using var document = JsonDocument.Parse(line);
            Assert.Equal(stage, document.RootElement.GetProperty("stage").GetString());
            return document.RootElement.Clone();
        }
    }
}
