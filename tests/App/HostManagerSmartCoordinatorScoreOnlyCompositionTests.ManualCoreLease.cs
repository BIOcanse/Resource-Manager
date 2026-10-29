using System.Net.Http.Json;
using System.Reflection;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.Software;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManualRegistrationAndPolicyPutBindUnrelatedImageAndMoveOtherSoftwareOffCore(bool cpuMetricsAvailable)
    {
        const int pid = 53042, otherPid = 53043;
        const ulong birth = 133900000000000042;
        const string root = @"X:\Builds\Browser\out\Release_GN_x64";
        var topology = CreateAutomaticPlacementTopology();
        var facts = CreateCompleteProcessFacts((pid, birth, "not-attributed-yet", 80d, 20d, 5d),
            (otherPid, birth + 1, "background", 20d, 20d, 5d));
        if (!cpuMetricsAvailable) facts = facts with
        {
            CurrentMetricMask = facts.CurrentMetricMask & ~SchedulingProcessMetricMask.CpuUsage,
            DatasetObservations = facts.DatasetObservations.Where(pair => pair.Key != SchedulingProcessMetricMask.CpuUsage)
                .ToDictionary(pair => pair.Key, pair => pair.Value),
            Processes = facts.Processes.Select(process => process with
                { ValidMetricMask = process.ValidMetricMask & ~SchedulingProcessMetricMask.CpuUsage }).ToArray()
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: facts, policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement,
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology), automaticPlacementTopology: topology);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = fixture.Root });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        // Untouched routes in these two production groups must not construct their owners.
        foreach (var type in typeof(ISoftwareRegistryView).Assembly.GetTypes().Where(type => type.IsInterface
            && type.Namespace?.StartsWith("ResourceManager.App.Application", StringComparison.Ordinal) == true))
            builder.Services.AddSingleton(type, _ => throw new InvalidOperationException("Unexpected route dependency: " + type.Name));
        builder.Services.AddSingleton<GpuLaunchInterceptionReconciler>(_ => throw new InvalidOperationException("Unexpected interception"));
        var manual = new JsonManualSoftwareRegistry(builder.Environment);
        var view = SoftwareRegistryViewTests.CreateRuntimeReminderView(registry: manual);
        using var provider = RuntimeProcessAttributionCatalogProviderTests.CreateProvider(view);
        var policies = new JsonGpuPlacementPolicyStore(builder.Environment, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonGpuPlacementPolicyStore>.Instance);
        builder.Services.AddSingleton<IManualSoftwareRegistry>(manual);
        builder.Services.AddSingleton<IRuntimeProcessAttributionCatalogProvider>(provider);
        builder.Services.AddSingleton<IGpuPlacementPolicyStore>(policies);
        builder.Services.AddSingleton<IRuntimeSpecializationCoordinator>(new ManualCorePolicyPublisher(policies, fixture.RuntimePlanProvider));
        await using var app = builder.Build();
        foreach (var method in new[] { "MapSoftwareEndpoints", "MapGpuPlacementEndpoints" })
            typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [app]);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)) };
            using (var beforeRegistration = await provider.AcquireCatalogAsync(CancellationToken.None)) { }
            using var registered = await client.PostAsJsonAsync("/api/software/manual", new ManualSoftwareRequest("Unbound frame rate evidence", SoftwareKinds.HighPerformance, [root], null));
            registered.EnsureSuccessStatusCode();
            var registration = (await registered.Content.ReadFromJsonAsync<ManualSoftwareRecord>())!;
            using var saved = await client.PutAsJsonAsync($"/api/gpu-placement/software/{registration.Id}/policy",
                GpuPlacementPolicyDefaults.CreateSoftwarePolicy(registration.Id, registration.Name) with
                { CpuManualLockedPositionIds = ["core:0"], CpuManualExclusivePositionIds = ["core:0"] });
            saved.EnsureSuccessStatusCode();
            var capture = await RuntimeProcessAttributionCapture.CaptureAsync(provider,
                [new RuntimeProcessIdentity(pid, null, "RmWebHost", root + @"\bin\RmWebHost.exe", false, StartKey: checked((long)birth))],
                CompiledBaseScorePlan.Empty, CancellationToken.None);
            var attributed = capture.Batch.SoftwareByProcessId[pid];
            Assert.Equal(registration.Id, attributed.Id);
            fixture.ProcessFacts.SetSnapshot(facts with { Processes = [facts.Processes[0] with
                { SoftwareId = attributed.Id, SoftwareName = attributed.Name, SoftwareKind = attributed.Kind,
                    ProcessName = "RmWebHost", ExecutablePath = root + @"\bin\RmWebHost.exe" }, facts.Processes[1]] });
            var allowed = RecordCpuLeaseBindings(fixture);
            for (var cycle = 0; cycle < 8 && (allowed[pid].Count != 1 || allowed[otherPid].Contains(1U)); cycle++)
            { if (cycle != 0 && cpuMetricsAvailable) fixture.PublishNextHostedCpuObservation(); await fixture.RunRealtimeCycleAsync(); }
            Assert.Equal([1U], allowed[pid]);
            Assert.DoesNotContain(1U, allowed[otherPid]);
            Assert.Contains(fixture.StateStore.Current.AppliedPlacements, receipt => receipt.SoftwareId == registration.Id
                && receipt.Records.Any(record => record.Metadata?.GetValueOrDefault("source") == "manual-physical-core-lock"));
            Assert.Contains(fixture.StateStore.Current.AppliedPlacements, receipt => receipt.SoftwareId == "background"
                && receipt.Records.Any(record => record.Metadata?.GetValueOrDefault("source") == "cpu-exclusive-avoidance"));
        }
        finally { await app.StopAsync(); }
    }

    private static Dictionary<int, IReadOnlyList<uint>> RecordCpuLeaseBindings(ScoreOnlyCoordinatorFixture fixture)
    {
        var allowed = fixture.ProcessFacts.Snapshot.Processes.ToDictionary(process => process.ProcessId, _ => (IReadOnlyList<uint>)new uint[] { 1, 2, 3, 4 });
        fixture.ProcessPolicyWriter.PlacementReadHandler = (pid, _) =>
        {
            var process = fixture.ProcessFacts.Snapshot.Processes.FirstOrDefault(process => process.ProcessId == pid);
            if (process is null) return RecoveryReadResult<ProcessPlacementRecoverySnapshot>.NotFoundOrExited(87, "fixture process exited");
            return RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(new(pid,
                DateTimeOffset.FromFileTime(checked((long)process.ProcessStartKey)), process.ProcessName, process.ExecutablePath, 15, allowed[pid], null, null));
        };
        fixture.ProcessPolicyWriter.BatchHandler = requests => requests.Select(request =>
        {
            allowed[request.ProcessId] = request.CpuSetIds!.ToArray();
            return new ProcessResourcePolicyBatchWriteResult(request.ProcessId,
                [new ProcessResourcePolicyBatchFieldResult(ProcessResourcePolicyBatchFields.CpuSets, true, "applied")]);
        }).ToArray();
        fixture.ProcessPolicyWriter.DefaultCpuSetWriteHandler = (pid, ids) => { allowed[pid] = ids.ToArray(); return new(true, "restored"); };
        return allowed;
    }

    private sealed class ManualCorePolicyPublisher(IGpuPlacementPolicyStore policies,
        ResourceManager.App.Infrastructure.RuntimeSpecialization.RuntimePlanProvider provider) : IRuntimeSpecializationCoordinator
    {
        public async Task<RuntimePlanPublicationResult> RebuildAsync(string reason, CancellationToken cancellationToken)
        {
            var document = await policies.GetAsync(cancellationToken);
            return provider.Publish(provider.Current with { Version = provider.Current.Version + 1,
                GpuPlacement = ResourceManager.App.Infrastructure.RuntimeSpecialization.RuntimePlanCompiler.CompileGpuPlacementPlan(
                    false, document, provider.Current.CpuPlacementTopology!) });
        }
        public Task<CpuBaselineRatioUpdateResult> ApplyCpuBaselineRatioAsync(double? overrideRatio, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AppSettingsUpdateResult> ApplyAppSettingsAsync(AppSettings settings, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AppSettingsUpdateResult> ApplyAppSettingsPatchAsync(AppSettingsPatchRequest patch, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
