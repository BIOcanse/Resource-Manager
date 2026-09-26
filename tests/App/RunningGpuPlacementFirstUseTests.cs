using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class RunningGpuPlacementFirstUseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InjectorRejectedInstanceCannotBecomeMovementOrObservationCandidate(bool knownApi)
    {
        var f = new Fixture();
        if (knownApi)
            await f.History.SaveFirstGraphicsApiAsync("software", "Software", f.Process, GpuGraphicsApi.D3D11, default);
        var cacheField = typeof(WindowsGpuPlacementInjector).GetField("failuresByProcessId",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var cache = cacheField.GetValue(f.Injector)!;
        var failureType = cache.GetType().GetGenericArguments()[1];
        var failure = new RuntimeGpuProviderInjectionResult(42, false, false, "binary-signature-policy",
            "fixture", null, null, 0, null);
        var cached = Activator.CreateInstance(failureType, new object[] { 1UL, failure });
        cache.GetType().GetProperty("Item")!.SetValue(cache, cached, [42]);

        var blocked = await f.Actions.PrepareAsync(f.Request, default);
        Assert.Null(blocked.Plan);
        Assert.Empty(blocked.ApiObservationProcesses);
        Assert.Equal(failure, f.Injector.GetKnownProcessFailure(f.Process));

        var other = f.Process with { ProcessId = 43 };
        var mixed = await f.Actions.PrepareAsync(f.Request with { Processes = [f.Process, other] }, default);
        if (knownApi) Assert.Equal(other, Assert.Single(mixed.Plan!.Request.Processes));
        else Assert.Equal(other, Assert.Single(mixed.ApiObservationProcesses));

        var nextBirth = f.Process with { ProcessStartKey = 2 };
        Assert.Null(f.Injector.GetKnownProcessFailure(nextBirth));
        var available = await f.Actions.PrepareAsync(f.Request with { Processes = [nextBirth] }, default);
        if (knownApi) Assert.NotNull(available.Plan);
        else Assert.Equal(nextBirth, Assert.Single(available.ApiObservationProcesses));
        Assert.Null(f.Runtime.ReadPolicy("target"));
    }

    [Fact]
    public async Task PurePreparationReportsUnknownInstancesWithoutPublishingOrExposingAnObservationField()
    {
        var f = new Fixture();
        var prepared = await f.Actions.PrepareAsync(f.Request, default);
        Assert.Null(prepared.Plan);
        Assert.Equal(f.Process, Assert.Single(prepared.ApiObservationProcesses));
        Assert.Empty((await f.History.GetSoftwareHistoryAsync("software", "Software", default)).Processes);
        Assert.Null(f.Runtime.ReadPolicy("target"));
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(prepared));
        Assert.Equal(new[] { "Message", "Plan" }, json.RootElement.EnumerateObject().Select(item => item.Name).Order());
    }

    [Theory]
    [InlineData(GpuGraphicsApi.D3D11)]
    [InlineData(GpuGraphicsApi.OpenGL)]
    [InlineData(GpuGraphicsApi.D3D9)]
    public async Task KnownApiIsNotReportedAsUnknownEvenWhenItsMovementPathIsUnavailable(GpuGraphicsApi api)
    {
        var f = new Fixture();
        await f.History.SaveFirstGraphicsApiAsync("software", "Software", f.Process, api, default);
        var prepared = await f.Actions.PrepareAsync(f.Request, default);
        Assert.Empty(prepared.ApiObservationProcesses);
        if (api == GpuGraphicsApi.D3D11) Assert.NotNull(prepared.Plan);
        else Assert.Null(prepared.Plan);
        Assert.Null(f.Runtime.ReadPolicy("target"));
    }

    [Fact]
    public async Task FirstOpenGlObservationIsSavedWithoutMovementOrRepeatedIdentification()
    {
        var f = new Fixture();
        var observations = 0;
        var first = await f.Actions.PrepareWithFirstApiObservationAsync(f.Request, (_, _) =>
        {
            observations++;
            return Task.FromResult<GpuGraphicsApi?>(GpuGraphicsApi.OpenGL);
        }, default);
        Assert.Null(first.Plan);
        Assert.Equal(1, observations);
        Assert.Equal(GpuGraphicsApi.OpenGL, Assert.Single((await f.History.GetSoftwareHistoryAsync(
            "software", "Software", default)).Processes).GraphicsApi);
        var later = await f.Actions.PrepareWithFirstApiObservationAsync(f.Request,
            (_, _) => throw new InvalidOperationException("A known unsupported API must not be observed again."), default);
        Assert.Null(later.Plan);
        Assert.Empty(later.ApiObservationProcesses);
        Assert.Null(f.Runtime.ReadPolicy("target"));
    }

    [Fact]
    public async Task PurePreparationKeepsKnownAndUnknownProcessesSeparate()
    {
        var f = new Fixture();
        await f.History.SaveFirstGraphicsApiAsync("software", "Software", f.Process, GpuGraphicsApi.D3D11, default);
        var unknown = f.Process with { ProcessId = 43, ProcessName = "other", ExecutablePath = Path.Combine(f.Environment.ContentRootPath, "other.exe") };
        var prepared = await f.Actions.PrepareAsync(f.Request with { Processes = [f.Process, unknown] }, default);
        Assert.Equal(unknown, Assert.Single(prepared.ApiObservationProcesses));
        Assert.Equal(f.Process, Assert.Single(prepared.Plan!.Request.Processes));
    }

    [Theory]
    [InlineData("global")]
    [InlineData("hot-switch")]
    public async Task PurePreparationDoesNotRequestObservationWhenMovementIsDisabled(string kind)
    {
        var f = new Fixture();
        var plan = f.Plans.Current.GpuPlacement;
        if (kind == "global") plan = plan with { GlobalPreciseProviderEnabled = false };
        else plan = plan with { SoftwarePoliciesBySoftwareId = new Dictionary<string, ResolvedGpuPlacementPolicy>
        { ["software"] = ResolvedGpuPlacementPolicy.FromSoftware(f.Software with { RuntimeHotSwitchEnabled = false }) } };
        f.Plans.Publish(f.Plans.Current with { GpuPlacement = plan });
        var prepared = await f.Actions.PrepareAsync(f.Request, default);
        Assert.Null(prepared.Plan);
        Assert.Empty(prepared.ApiObservationProcesses);
    }

    [Theory]
    [InlineData(GpuGraphicsApi.D3D11)]
    [InlineData(GpuGraphicsApi.D3D12)]
    [InlineData(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12)]
    [InlineData(GpuGraphicsApi.Vulkan)]
    public async Task FirstObservationCommitsThenPreparesAndLaterInstancesReuseTheRecord(GpuGraphicsApi api)
    {
        var f = new Fixture();
        var calls = 0;
        var request = f.Request with { Processes = [f.Process, f.Process, f.Process with { ProcessId = 43 }] };
        var prepared = await f.Actions.PrepareWithFirstApiObservationAsync(request, async (process, token) =>
        {
            calls++;
            Assert.Equal(f.Process, process);
            Assert.Null((await f.Actions.PrepareAsync(f.Request, token)).Plan);
            Assert.Empty((await f.History.GetSoftwareHistoryAsync("software", "Software", token)).Processes);
            Assert.Null(f.Runtime.ReadPolicy("target"));
            return api;
        }, default);
        Assert.Equal(1, calls);
        Assert.NotNull(prepared.Plan);
        Assert.Equal(new[] { 42, 43 }, prepared.Plan.GraphicsApis.Keys.Order());
        Assert.All(prepared.Plan.GraphicsApis.Values, value => Assert.Equal(api, value));
        Assert.Equal(api, Assert.Single((await new JsonGpuPlacementProcessHistoryStore(f.Environment)
            .GetSoftwareHistoryAsync("software", "Software", default)).Processes).GraphicsApi);
        var later = f.Request with { Processes = [f.Process with { ProcessId = 44, ProcessStartKey = 2 }] };
        Assert.Equal(api, (await f.Actions.PrepareWithFirstApiObservationAsync(later,
            (_, _) => throw new InvalidOperationException("A saved API must not be observed again."), default)).Plan!.GraphicsApis[44]);
        Assert.Null(f.Runtime.ReadPolicy("target"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData((GpuGraphicsApi)0)]
    [InlineData(GpuGraphicsApi.OpenGL | GpuGraphicsApi.Vulkan)]
    [InlineData((GpuGraphicsApi)32)]
    public async Task EmptyOrAmbiguousObservationDoesNotPublishARecordOrInferARoute(GpuGraphicsApi? api)
    {
        var f = new Fixture();
        var result = await f.Actions.PrepareWithFirstApiObservationAsync(f.Request, (_, _) => Task.FromResult(api), default);
        Assert.Null(result.Plan);
        Assert.Empty((await f.History.GetSoftwareHistoryAsync("software", "Software", default)).Processes);
        Assert.Null(f.Runtime.ReadPolicy("target"));
    }

    [Theory]
    [InlineData("global")]
    [InlineData("software")]
    [InlineData("process")]
    public async Task CurrentPermissionIsCheckedBeforeAndAfterObservation(string kind)
    {
        var f = new Fixture();
        var original = f.Plans.Current;
        void Revoke()
        {
            var plan = original.GpuPlacement;
            var denied = ResolvedGpuPlacementPolicy.FromSoftware(f.Software with { AllowedProviders = [] });
            if (kind == "global") plan = plan with { GlobalPreciseProviderEnabled = false };
            else if (kind == "software") plan = plan with { SoftwarePoliciesBySoftwareId = new Dictionary<string, ResolvedGpuPlacementPolicy> { ["software"] = denied } };
            else plan = plan with { ProcessPoliciesBySoftwareAndProcessKey = new Dictionary<string, ResolvedGpuPlacementPolicy>
            {
                [CompiledBaseScorePlan.CreateProcessPolicyKey("software",
                    JsonGpuPlacementProcessHistoryStore.BuildProcessKey(f.Process.ProcessName, f.Process.ExecutablePath))] = denied
            } };
            f.Plans.Publish(original with { GpuPlacement = plan });
        }
        Revoke();
        Assert.Null((await f.Actions.PrepareWithFirstApiObservationAsync(f.Request,
            (_, _) => throw new InvalidOperationException("Denied observation."), default)).Plan);
        f.Plans.Publish(original);
        var calls = 0;
        Assert.Null((await f.Actions.PrepareWithFirstApiObservationAsync(f.Request, (_, _) =>
        {
            calls++;
            Revoke();
            return Task.FromResult<GpuGraphicsApi?>(GpuGraphicsApi.D3D11);
        }, default)).Plan);
        Assert.Equal(1, calls);
        Assert.Empty((await f.History.GetSoftwareHistoryAsync("software", "Software", default)).Processes);
    }

    [Fact]
    public async Task DisablingHotSwitchDoesNotDisableIdentificationButStopsMovementPreparation()
    {
        var f = new Fixture();
        var policy = ResolvedGpuPlacementPolicy.FromSoftware(f.Software with { RuntimeHotSwitchEnabled = false });
        f.Plans.Publish(f.Plans.Current with { GpuPlacement = f.Plans.Current.GpuPlacement with
        { SoftwarePoliciesBySoftwareId = new Dictionary<string, ResolvedGpuPlacementPolicy> { ["software"] = policy } } });
        var calls = 0;
        var result = await f.Actions.PrepareWithFirstApiObservationAsync(f.Request, (_, _) =>
        { calls++; return Task.FromResult<GpuGraphicsApi?>(GpuGraphicsApi.D3D11); }, default);
        Assert.Equal(1, calls);
        Assert.Null(result.Plan);
        Assert.Equal(GpuGraphicsApi.D3D11, Assert.Single((await f.History.GetSoftwareHistoryAsync("software", "Software", default)).Processes).GraphicsApi);
    }

    [Fact]
    public async Task ConcurrentFirstCommitWinsInsteadOfTheReturnedObservationProposal()
    {
        var f = new Fixture();
        var result = await f.Actions.PrepareWithFirstApiObservationAsync(f.Request, async (process, token) =>
        {
            await f.History.SaveFirstGraphicsApiAsync("software", "Software", process, GpuGraphicsApi.Vulkan, token);
            return GpuGraphicsApi.D3D11;
        }, default);
        Assert.Equal(GpuGraphicsApi.Vulkan, result.Plan!.GraphicsApis[42]);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("birth")]
    [InlineData("path")]
    [InlineData("root")]
    [InlineData("conflict")]
    public async Task InvalidIdentityNeverReachesObservation(string kind)
    {
        var f = new Fixture();
        var process = kind switch
        {
            "pid" => f.Process with { ProcessId = 4 },
            "birth" => f.Process with { ProcessStartKey = 0 },
            "path" => f.Process with { ExecutablePath = "relative.exe" },
            "root" => f.Process with { ExecutablePath = Path.GetPathRoot(f.Process.ExecutablePath)! },
            _ => f.Process with { ProcessStartKey = 2 }
        };
        var request = f.Request with { Processes = kind == "conflict" ? [f.Process, process] : [process] };
        Assert.Null((await f.Actions.PrepareWithFirstApiObservationAsync(request,
            (_, _) => throw new InvalidOperationException("Invalid identity."), default)).Plan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeOrDuringObservationNeverCommits(bool during)
    {
        var f = new Fixture();
        using var cancel = new CancellationTokenSource();
        if (!during) cancel.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Actions.PrepareWithFirstApiObservationAsync(f.Request,
            (_, _) => { calls++; cancel.Cancel(); return Task.FromResult<GpuGraphicsApi?>(GpuGraphicsApi.D3D11); }, cancel.Token));
        Assert.Equal(during ? 1 : 0, calls);
        Assert.Empty((await f.History.GetSoftwareHistoryAsync("software", "Software", default)).Processes);
    }

    [Fact]
    public async Task ObservationFailureStopsTheCompositionWithoutARecordOrRetry()
    {
        var f = new Fixture();
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => f.Actions.PrepareWithFirstApiObservationAsync(f.Request,
            (_, _) => { calls++; throw new IOException("observation fixture"); }, default));
        Assert.Equal(1, calls);
        Assert.Empty((await f.History.GetSoftwareHistoryAsync("software", "Software", default)).Processes);
    }

    [Fact]
    public async Task SaveFailureDoesNotUseAnUncommittedApiForPreparation()
    {
        var f = new Fixture();
        var failing = new HistoryProbe(f.History);
        var actions = f.CreateActions(failing);
        await Assert.ThrowsAsync<IOException>(() => actions.PrepareWithFirstApiObservationAsync(f.Request,
            (_, _) => Task.FromResult<GpuGraphicsApi?>(GpuGraphicsApi.D3D11), default));
        Assert.Equal(1, failing.Reads);
        Assert.Empty((await f.History.GetSoftwareHistoryAsync("software", "Software", default)).Processes);
        Assert.Null(f.Runtime.ReadPolicy("target"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationOrCancellationDuringHistoryReadNeverStartsObservation(bool cancelDuringRead)
    {
        var f = new Fixture();
        using var cancel = new CancellationTokenSource();
        var history = new HistoryProbe(f.History, () =>
        {
            if (cancelDuringRead) cancel.Cancel();
            else f.Plans.Publish(f.Plans.Current with { GpuPlacement = f.Plans.Current.GpuPlacement with { GlobalPreciseProviderEnabled = false } });
        }, false);
        var actions = f.CreateActions(history);
        Task<RunningGpuPlacementPreparation> Run() => actions.PrepareWithFirstApiObservationAsync(f.Request,
            (_, _) => throw new InvalidOperationException("Admission changed while reading."), cancel.Token);
        if (cancelDuringRead) await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
        else Assert.Null((await Run()).Plan);
        Assert.Empty((await f.History.GetSoftwareHistoryAsync("software", "Software", default)).Processes);
    }

    [Theory]
    [InlineData("global", false)]
    [InlineData("global", true)]
    [InlineData("software", false)]
    [InlineData("software", true)]
    [InlineData("process", false)]
    [InlineData("process", true)]
    [InlineData("hot-switch", false)]
    [InlineData("hot-switch", true)]
    public async Task MovementRevokedDuringFinalPrepareReadCannotReturnAMovementPlan(string kind, bool alreadyKnown)
    {
        var f = new Fixture();
        if (alreadyKnown)
            await f.History.SaveFirstGraphicsApiAsync("software", "Software", f.Process, GpuGraphicsApi.D3D11, default);
        var reads = 0;
        var calls = 0;
        var history = new HistoryProbe(f.History, () =>
        {
            if (++reads != 2) return;
            var plan = f.Plans.Current.GpuPlacement;
            var denied = ResolvedGpuPlacementPolicy.FromSoftware(kind == "hot-switch"
                ? f.Software with { RuntimeHotSwitchEnabled = false } : f.Software with { AllowedProviders = [] });
            if (kind == "global") plan = plan with { GlobalPreciseProviderEnabled = false };
            else if (kind == "process") plan = plan with { ProcessPoliciesBySoftwareAndProcessKey = new Dictionary<string, ResolvedGpuPlacementPolicy>
            {
                [CompiledBaseScorePlan.CreateProcessPolicyKey("software",
                    JsonGpuPlacementProcessHistoryStore.BuildProcessKey(f.Process.ProcessName, f.Process.ExecutablePath))] = denied
            } };
            else plan = plan with { SoftwarePoliciesBySoftwareId = new Dictionary<string, ResolvedGpuPlacementPolicy> { ["software"] = denied } };
            f.Plans.Publish(f.Plans.Current with { GpuPlacement = plan });
        }, false);
        var result = await f.CreateActions(history).PrepareWithFirstApiObservationAsync(f.Request,
            (_, _) => { calls++; return Task.FromResult<GpuGraphicsApi?>(GpuGraphicsApi.D3D11); }, default);
        Assert.Equal(2, reads);
        Assert.Equal(alreadyKnown ? 0 : 1, calls);
        Assert.Null(result.Plan);
        Assert.Equal(GpuGraphicsApi.D3D11, Assert.Single((await f.History.GetSoftwareHistoryAsync("software", "Software", default)).Processes).GraphicsApi);
        Assert.Null(f.Runtime.ReadPolicy("target"));
    }

    [Fact]
    public async Task ProductionExecutionReusesKnownApiWithoutCallingEitherDelegate()
    {
        var f = new Fixture();
        await f.History.SaveFirstGraphicsApiAsync("software", "Software", f.Process, GpuGraphicsApi.D3D11, default);
        var prepared = await f.Actions.PrepareWithFirstApiObservationAsync(f.Request,
            new RunningGpuApiObservationExecution(37, GpuRemoteCallTestOwner.RejectUnexpected,
                (_, _, _) => throw new InvalidOperationException("Unexpected observation wait"), default), default);
        Assert.Equal(GpuGraphicsApi.D3D11, prepared.Plan!.GraphicsApis[42]);
        Assert.Null(f.Runtime.ReadPolicy("target"));
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("execute")]
    [InlineData("wait")]
    [InlineData("cancel")]
    public async Task ProductionExecutionRejectsInvalidInputsBeforeReadingOrExecuting(string boundary)
    {
        var f = new Fixture();
        var history = new HistoryProbe(f.History, () => throw new InvalidOperationException("Unexpected history read"));
        var execution = new RunningGpuApiObservationExecution(boundary == "duration" ? 0 : 37,
            boundary == "execute" ? null! : GpuRemoteCallTestOwner.RejectUnexpected,
            boundary == "wait" ? null! : (_, _, _) => throw new InvalidOperationException("Unexpected wait"), default);
        using var cancellation = new CancellationTokenSource();
        if (boundary == "cancel") cancellation.Cancel();
        Task<RunningGpuPlacementPreparation> Run() => f.CreateActions(history)
            .PrepareWithFirstApiObservationAsync(f.Request, execution, cancellation.Token);
        if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
        else if (boundary == "duration") await Assert.ThrowsAsync<ArgumentOutOfRangeException>(Run);
        else await Assert.ThrowsAsync<ArgumentNullException>(Run);
        Assert.Equal(0, history.Reads);
    }

    [Fact]
    public async Task ProductionExecutionWithNoNativeResultDoesNotCreateHistoryOrPolicy()
    {
        var f = new Fixture();
        var request = f.Request with { Processes = [f.Process with { ProcessId = int.MaxValue }] };
        var result = await f.Actions.PrepareWithFirstApiObservationAsync(request,
            new RunningGpuApiObservationExecution(37, GpuRemoteCallTestOwner.RejectUnexpected,
                (_, _, _) => throw new InvalidOperationException("Unexpected wait"), default), default);
        Assert.Null(result.Plan);
        Assert.Empty((await f.History.GetSoftwareHistoryAsync("software", "Software", default)).Processes);
        Assert.Null(f.Runtime.ReadPolicy("target"));
    }

    private sealed class HistoryProbe(IGpuPlacementProcessHistoryStore inner, Action? afterRead = null, bool failSave = true)
        : IGpuPlacementProcessHistoryStore
    {
        internal int Reads;
        public async Task<GpuPlacementSoftwareProcessHistory> GetSoftwareHistoryAsync(string id, string name, CancellationToken token)
        {
            Reads++;
            var history = await inner.GetSoftwareHistoryAsync(id, name, token);
            afterRead?.Invoke();
            return history;
        }
        public Task<GpuPlacementSoftwareProcessHistory> ObserveAsync(GpuPlacementProcessObservationRequest request, CancellationToken token)
            => throw new InvalidOperationException("Metadata observation is not part of this action.");
        public Task<GpuPlacementSoftwareProcessHistory> SaveFirstGraphicsApiAsync(string id, string name,
            GpuPlacementProcessInstance process, GpuGraphicsApi api, CancellationToken token)
            => failSave ? throw new IOException("save fixture") : inner.SaveFirstGraphicsApiAsync(id, name, process, api, token);
    }

    private sealed class Fixture
    {
        internal readonly HostingEnvironment Environment = new() { ContentRootPath = GpuWindowLedgerTestData.NewRoot("first-api-composition") };
        internal readonly GpuPlacementSoftwarePolicy Software = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software") with
        {
            EnabledMode = GpuPlacementPolicyModes.Auto, SchedulingMode = GpuPlacementSchedulingModes.Precise,
            RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise, RuntimeHotSwitchEnabled = true,
            AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim]
        };
        internal readonly JsonGpuPlacementProcessHistoryStore History;
        internal readonly D3d11ProxyShimRuntime Runtime;
        internal readonly GpuGraphicsApiIdentificationTests.Plans Plans;
        internal readonly WindowsRunningGpuPlacementActionService Actions;
        internal readonly WindowsGpuPlacementInjector Injector = new(NullLogger<WindowsGpuPlacementInjector>.Instance);
        internal GpuPlacementProcessInstance Process => new(42, 1, "target", Path.Combine(Environment.ContentRootPath, "target.exe"));
        internal RunningGpuPlacementActionRequest Request => new("target", "software", "Software", [Process], "background", 123,
            GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover);
        internal Fixture()
        {
            History = new(Environment);
            Runtime = new(Environment);
            Plans = new(Software);
            Actions = CreateActions(History);
        }
        internal WindowsRunningGpuPlacementActionService CreateActions(IGpuPlacementProcessHistoryStore history)
            => new(NullLogger<WindowsRunningGpuPlacementActionService>.Instance, Runtime,
                Injector, history, Plans, new());
    }
}
