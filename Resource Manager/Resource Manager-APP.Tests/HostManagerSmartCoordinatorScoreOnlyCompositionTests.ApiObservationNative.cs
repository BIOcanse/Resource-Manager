using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.Optimization;
using static Resource_Manager_APP.Tests.GpuWindowLedgerTestData;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [GpuApiNativeTheory]
    [InlineData("d3d9", GpuGraphicsApi.D3D9, false)]
    [InlineData("d3d11", GpuGraphicsApi.D3D11, false)]
    [InlineData("d3d12", GpuGraphicsApi.D3D12, false)]
    [InlineData("d3d9", GpuGraphicsApi.D3D9, true)]
    [InlineData("d3d11", GpuGraphicsApi.D3D11, true)]
    [InlineData("d3d12", GpuGraphicsApi.D3D12, true)]
    public async Task NativeApiObservationUsesOriginalLedgerThenCommitsActualApiAndConfiguresSameProvider(
        string api, GpuGraphicsApi expected, bool productionCandidates)
    {
        var root = NewRoot("native-api-bridge-" + api);
        var path = Path.Combine(root, "recovery.json");
        var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = root };
        var runtime = new D3d11ProxyShimRuntime(environment);
        var history = new JsonGpuPlacementProcessHistoryStore(environment);
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await using var fixture = await CreateGpuActionFixture(new RecordingRunningGpuActions(), store);
        await using var child = await GpuPlacementInjectorNativeTests.OwnedProbe.StartAsync(api, output);
        var process = AutomaticGpuProcess() with
        {
            ProcessId = child.Identity.ProcessId, ProcessStartKey = child.Identity.ProcessStartKey,
            ProcessName = child.Identity.ProcessName, ExecutablePath = child.Identity.ExecutablePath
        };
        var software = GpuPlacementPolicyDefaults.CreateSoftwarePolicy(process.SoftwareId, process.DisplayName) with
        {
            EnabledMode = GpuPlacementPolicyModes.Auto, SchedulingMode = GpuPlacementSchedulingModes.Precise,
            RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise, RuntimeHotSwitchEnabled = true,
            AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim]
        };
        var injector = new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance);
        var actions = new WindowsRunningGpuPlacementActionService(NullLogger<WindowsRunningGpuPlacementActionService>.Instance,
            runtime, injector, history, new GpuGraphicsApiIdentificationTests.Plans(software), new());
        var request = new RunningGpuPlacementActionRequest(process.TargetId, process.SoftwareId, process.DisplayName,
            [child.Identity], "background", child.AdapterLuid, GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover);
        Assert.Null((await actions.PrepareAsync(request, default)).Plan);
        Assert.False(child.HasProvider());
        Assert.Null(runtime.ReadPolicy(process.TargetId));
        var state = await store.LoadAsync(default);
        var calls = new List<GpuRemoteCallRequest>();
        async Task<GpuRemoteCallSnapshot> Observe(GpuRemoteCallExecution call, CancellationToken token)
        {
            calls.Add(call.Request);
            (state, var result) = await InvokeObservationOwner(fixture.Coordinator, state, process, call, token);
            output.WriteLine(JsonSerializer.Serialize(new { nativeObservationCall = true, api, request = call.Request, result }));
            Assert.True(result.Completed, result.Status);
            Assert.NotNull(result.ThreadId);
            Assert.NotNull(result.ThreadCreationFileTimeUtc);
            return result;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var observationExecutions = 0;
        var observationWaits = 0;
        var productionExecution = new RunningGpuApiObservationExecution(
            fixture.RuntimePlanProvider.Current.HostManager.HotPublish.PlacementCoordinator.ApiObservationWindowMilliseconds,
            Observe, async (identity, window, token) =>
            {
                observationExecutions++;
                observationWaits++;
                Assert.Equal(child.Identity, identity);
                Assert.True(child.HasProvider());
                Assert.Null(runtime.ReadPolicy(process.TargetId));
                Assert.Empty((await history.GetSoftwareHistoryAsync(process.SoftwareId, process.DisplayName, token)).Processes);
                Assert.Equal(child.AdapterLuid, await child.CreateDeviceAsync());
                await Task.Delay(window, token);
            }, cleanupDeadline.Token);
        var prepared = productionCandidates
            ? await actions.PrepareWithFirstApiObservationAsync(request, productionExecution, deadline.Token)
            : await actions.PrepareWithFirstApiObservationAsync(request, async (identity, token) =>
        {
            observationExecutions++;
            Assert.Equal(child.Identity, identity);
            using var observer = await injector.OpenForApiObservationAsync(identity, expected, Observe, token);
            Assert.True(observer.Result.Success, observer.Result.Status + ": " + observer.Result.Message);
            Assert.True(child.HasProvider());
            Assert.Null(observer.Result.PolicyPath);
            var duration = fixture.RuntimePlanProvider.Current.HostManager.HotPublish.PlacementCoordinator.ApiObservationWindowMilliseconds;
            var stopped = (await observer.ObserveApiOnceAsync(duration, async (window, waitToken) =>
            {
                observationWaits++;
                Assert.Equal(TimeSpan.FromMilliseconds(duration), window);
                Assert.Equal(new GpuApiObservationSnapshot(0, true), (await observer.ReadApiObservationAsync(Assert.Single(observer.ApiObservations), waitToken)).Snapshot);
                Assert.Equal(child.AdapterLuid, await child.CreateDeviceAsync());
                Assert.Equal(new GpuApiObservationSnapshot(expected, true), (await observer.ReadApiObservationAsync(Assert.Single(observer.ApiObservations), waitToken)).Snapshot);
                await Task.Delay(window, waitToken);
            }, token, cleanupDeadline.Token)).Snapshot;
            Assert.Equal(new GpuApiObservationSnapshot(expected, false), stopped);
            Assert.Equal(child.AdapterLuid, await child.CreateDeviceAsync());
            Assert.Equal(stopped, (await observer.ReadApiObservationAsync(Assert.Single(observer.ApiObservations), token)).Snapshot);
            Assert.Equal(1U, (await observer.StartApiObservationAsync(Assert.Single(observer.ApiObservations), duration, token)).ExitCode);
            Assert.Equal(new GpuApiObservationSnapshot(0, true), (await observer.ReadApiObservationAsync(Assert.Single(observer.ApiObservations), token)).Snapshot);
            Assert.Equal(new GpuApiObservationSnapshot(0, false), (await observer.StopApiObservationAsync(Assert.Single(observer.ApiObservations), token)).Snapshot);
            Assert.Empty((await history.GetSoftwareHistoryAsync(process.SoftwareId, process.DisplayName, token)).Processes);
            return stopped!.Value.Apis;
        }, deadline.Token);
        Assert.Equal(1, observationExecutions);
        Assert.Equal(1, observationWaits);
        var expectedObservationCalls = productionCandidates ? 3 : 9;
        Assert.Equal(GpuRemoteCallKind.LoadObservationProvider, calls[0].Kind);
        Assert.Equal(expectedObservationCalls, calls.Count);
        Assert.False(GpuActionFacts.HasPlacementEffects(state.AppliedPlacements));
        Assert.False(GpuActionFacts.HasUnsettledActions(state.AppliedPlacements));
        Assert.Null(runtime.ReadPolicy(process.TargetId));
        var reopened = new JsonGpuPlacementProcessHistoryStore(environment);
        Assert.Equal(expected, Assert.Single((await reopened.GetSoftwareHistoryAsync(process.SoftwareId, process.DisplayName, default)).Processes).GraphicsApi);
        Assert.NotNull(prepared.Plan);
        Assert.Equal(expected, prepared.Plan.GraphicsApis[child.Identity.ProcessId]);
        Assert.NotNull((await actions.PrepareWithFirstApiObservationAsync(request,
            (_, _) => throw new InvalidOperationException("A committed API must bypass observation."), deadline.Token)).Plan);
        Assert.NotNull((await actions.PrepareWithFirstApiObservationAsync(request, productionExecution with
        {
            ExecuteRemoteCallAsync = GpuRemoteCallTestOwner.RejectUnexpected,
            WaitAsync = (_, _, _) => throw new InvalidOperationException("A committed API must bypass the production observer.")
        }, deadline.Token)).Plan);
        Assert.Equal(expectedObservationCalls, calls.Count);
        Assert.Null(runtime.ReadPolicy(process.TargetId));

        // Explicit test orchestration of the original policy owner; not the automatic scheduler loop.
        var policy = runtime.CapturePolicyRecord(process.TargetId, prepared.Plan.PolicyValue)!;
        var metadata = new Dictionary<string, string>(policy.Metadata!)
        {
            ["processId"] = child.Identity.ProcessId.ToString(CultureInfo.InvariantCulture),
            ["processStartKey"] = child.Identity.ProcessStartKey.ToString(CultureInfo.InvariantCulture)
        };
        policy = policy with { Metadata = metadata };
        var placement = Assert.Single(state.AppliedPlacements);
        state = state with { AppliedPlacements = [placement with { Records = [.. placement.Records, policy] }] };
        await store.SaveAsync(state, default);
        Assert.True(runtime.TryApplyPolicyRecord(policy));
        async Task<GpuRemoteCallSnapshot> Configure(GpuRemoteCallExecution call, CancellationToken token)
        {
            Assert.NotEqual(GpuRemoteCallKind.LoadProvider, call.Request.Kind);
            (state, var result) = await InvokeRemoteOwner(fixture.Coordinator, state, call, token);
            output.WriteLine(JsonSerializer.Serialize(new { nativeConfigurationCall = true, api, request = call.Request, result }));
            Assert.True(result.Completed, result.Status);
            return result;
        }
        using var configured = await injector.OpenAndConfigureAsync(child.Identity, expected,
            runtime.GetPolicyPath(process.TargetId), Configure, deadline.Token);
        Assert.True(configured.Result.Success, configured.Result.Status + ": " + configured.Result.Message);
        Assert.True(configured.Result.AlreadyLoaded);
        Assert.Equal(child.AdapterLuid, await child.CreateDeviceAsync());
        Assert.NotNull((await configured.ReadDeviceObservationsAsync(deadline.Token)).Snapshot);
        Assert.True(runtime.RestorePolicyRecord(policy).CanRemoveReceipt);
        Assert.Null(runtime.ReadPolicy(process.TargetId));
        Assert.False(GpuActionFacts.HasUnsettledActions(state.AppliedPlacements));
        await child.StopAsync();
        output.WriteLine(JsonSerializer.Serialize(new { actualApiCommitted = true, api, expected,
            originalLedger = path, historyRoot = root, sameProviderConfigured = true, observationCalls = calls.Count,
            firstUseServiceComposed = true, observationExecutions, observationWaits, onceActionComposed = true, productionCandidates,
            automaticCycleConnected = false, otherAdapterMigration = false }));
    }
}
