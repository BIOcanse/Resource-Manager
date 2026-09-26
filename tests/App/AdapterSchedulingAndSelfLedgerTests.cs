using System.Text.Json;
using ResourceManager.Adapter;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;
using ResourceManager.App.Infrastructure.Adaptation;

namespace Resource_Manager_APP.Tests;

public sealed class AdapterSchedulingAndSelfLedgerTests
{
    [Fact]
    public void SchedulingCapabilities_KeepCpuAndGpuAsIndependentTypedDimensions()
    {
        var capabilities = new AdapterSoftwareSchedulingCapabilities(
            new AdapterCpuSchedulingCapabilities(
                [AdapterCpuSchedulingGrade.Optimize, AdapterCpuSchedulingGrade.Normal, AdapterCpuSchedulingGrade.Optimize]),
            null).Normalize();

        Assert.True(capabilities.HasAnyDimension);
        Assert.Equal(
            [AdapterCpuSchedulingGrade.Optimize, AdapterCpuSchedulingGrade.Normal],
            capabilities.Cpu!.SupportedGrades);
        Assert.Null(capabilities.Gpu);
    }

    [Fact]
    public void SchedulingGrades_SerializeAsSemanticTokens()
    {
        var envelope = new AdapterSoftwareSchedulingEnvelope(
            "semantic-wire",
            DateTimeOffset.UnixEpoch,
            "target",
            "Target",
            "software",
            AdapterCpuSchedulingGrade.Extreme,
            AdapterGpuSchedulingGrade.Optimize,
            100,
            20,
            "test");

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"cpuGrade\":\"extreme\"", json, StringComparison.Ordinal);
        Assert.Contains("\"gpuGrade\":\"optimize\"", json, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AdapterCpuSchedulingGrade>("\"turbo\""));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AdapterGpuSchedulingGrade>("2"));
    }

    [Fact]
    public void ResourceManagerSelfPolicy_AcceptsCoarseCommandWithoutFineConstraints()
    {
        var schedulingControl = CreateSchedulingControl();
        var envelope = new AdapterSoftwareSchedulingEnvelope(
            "coarse-command",
            DateTimeOffset.Now,
            "resource-manager:self",
            "Resource Manager",
            "resource-manager:self",
            AdapterCpuSchedulingGrade.Optimize,
            AdapterGpuSchedulingGrade.Normal,
            42,
            84,
            "test");

        var result = schedulingControl.ApplyScheduling(envelope);

        Assert.True(result.Accepted);
        Assert.Equal(AdapterCpuSchedulingGrade.Optimize, result.AppliedCpuGrade);
        Assert.Equal(AdapterGpuSchedulingGrade.Normal, result.AppliedGpuGrade);
        Assert.True(result.CpuChanged);
        Assert.False(result.GpuChanged);
        Assert.Contains("optimize", result.Message, StringComparison.Ordinal);
        Assert.Contains("normal", result.Message, StringComparison.Ordinal);
        var snapshot = schedulingControl.GetSchedulingSnapshot();
        Assert.Equal(ResourceManagerSelfCpuGrade.Optimize, snapshot.CpuGrade);
        Assert.Equal(ResourceManagerSelfGpuGrade.Normal, snapshot.GpuGrade);
        Assert.Single(snapshot.Sources);
    }

    [Fact]
    public void ResourceManagerSelfPolicy_AppliesOnlyCpuGradeToInternalCpuConsumers()
    {
        var zone = new FakeSelfComputeZone("test-zone");
        var schedulingControl = CreateSchedulingControl([zone]);

        var gpuOptimize = new AdapterSoftwareSchedulingEnvelope(
            "gpu-optimize",
            DateTimeOffset.Now,
            "process:1",
            "Resource Manager Backend",
            "resource-manager:self",
            AdapterCpuSchedulingGrade.Normal,
            AdapterGpuSchedulingGrade.Optimize,
            80,
            20,
            "test");
        var cpuOptimize = new AdapterSoftwareSchedulingEnvelope(
            "coarse-command-optimize",
            DateTimeOffset.Now,
            "process:1",
            "Resource Manager Backend",
            "resource-manager:self",
            AdapterCpuSchedulingGrade.Optimize,
            AdapterGpuSchedulingGrade.Optimize,
            20,
            20,
            "test");
        var normal = cpuOptimize with
        {
            PolicyId = "coarse-command-normal",
            CpuGrade = AdapterCpuSchedulingGrade.Normal,
            GpuGrade = AdapterGpuSchedulingGrade.Normal
        };

        var gpuResult = schedulingControl.ApplyScheduling(gpuOptimize);
        Assert.True(gpuResult.Accepted);
        Assert.False(gpuResult.CpuChanged);
        Assert.True(gpuResult.GpuChanged);
        Assert.Equal(ResourceManagerComputeZoneMode.Normal, zone.CurrentMode);

        var optimizeResult = schedulingControl.ApplyScheduling(cpuOptimize);
        var optimizeSnapshot = schedulingControl.GetSchedulingSnapshot();
        Assert.True(optimizeResult.Accepted);
        Assert.Equal(ResourceManagerSelfCpuGrade.Optimize, optimizeSnapshot.CpuGrade);
        Assert.Equal(ResourceManagerSelfGpuGrade.Optimize, optimizeSnapshot.GpuGrade);
        Assert.Equal(ResourceManagerComputeZoneMode.LowPower, zone.CurrentMode);

        var normalResult = schedulingControl.ApplyScheduling(normal);
        var normalSnapshot = schedulingControl.GetSchedulingSnapshot();
        Assert.True(normalResult.Accepted);
        Assert.Equal(ResourceManagerSelfCpuGrade.Normal, normalSnapshot.CpuGrade);
        Assert.Equal(ResourceManagerSelfGpuGrade.Normal, normalSnapshot.GpuGrade);
        Assert.Empty(normalSnapshot.Sources);
        Assert.Equal(ResourceManagerComputeZoneMode.Normal, zone.CurrentMode);
    }

    [Fact]
    public void ResourceManagerSelfPolicy_RestoresCpuWithoutOverwritingGpu()
    {
        var schedulingControl = CreateSchedulingControl();
        var initial = new AdapterSoftwareSchedulingEnvelope(
            "both-optimize",
            DateTimeOffset.Now,
            "resource-manager:self",
            "Resource Manager",
            "resource-manager:self",
            AdapterCpuSchedulingGrade.Optimize,
            AdapterGpuSchedulingGrade.Optimize,
            20,
            20,
            "test");
        var cpuRestore = initial with
        {
            PolicyId = "cpu-restore",
            CpuGrade = AdapterCpuSchedulingGrade.Normal,
            GpuGrade = null
        };

        Assert.True(schedulingControl.ApplyScheduling(initial).Accepted);
        var result = schedulingControl.ApplyScheduling(cpuRestore);
        var snapshot = schedulingControl.GetSchedulingSnapshot();

        Assert.True(result.Accepted);
        Assert.Equal(AdapterCpuSchedulingGrade.Normal, result.AppliedCpuGrade);
        Assert.Null(result.AppliedGpuGrade);
        Assert.True(result.CpuChanged);
        Assert.False(result.GpuChanged);
        Assert.Equal(ResourceManagerSelfCpuGrade.Normal, snapshot.CpuGrade);
        Assert.Equal(ResourceManagerSelfGpuGrade.Optimize, snapshot.GpuGrade);
    }

    [Fact]
    public void ResourceManagerSelfPolicy_RejectsUnsupportedSoftwareLevelModes()
    {
        var schedulingControl = CreateSchedulingControl();
        var envelope = new AdapterSoftwareSchedulingEnvelope(
            "coarse-command-unsupported",
            DateTimeOffset.Now,
            "resource-manager:self",
            "Resource Manager",
            "resource-manager:self",
            AdapterCpuSchedulingGrade.Freeze,
            AdapterGpuSchedulingGrade.Extreme,
            10,
            140,
            "test");

        var result = schedulingControl.ApplyScheduling(envelope);

        Assert.False(result.Accepted);
        Assert.Contains("只支持 CPU/GPU normal 和 optimize", result.Message, StringComparison.Ordinal);
        Assert.Contains("freeze", result.Message, StringComparison.Ordinal);
        Assert.Contains("extreme", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpAdapterPolicyDispatcher_RejectsSoftwareLevelModeWithoutSdkCapability()
    {
        var dispatcher = new HttpAdapterPolicyDispatcher(
            new HttpClient(new ThrowingHttpMessageHandler()),
            new StaticAdapterSoftwareRegistry([
                CreateAdapterRegistration("adapter:test", null)
            ]),
            CreateSchedulingControl());
        var envelope = new AdapterSoftwareSchedulingEnvelope(
            "software-level-without-capability",
            DateTimeOffset.Now,
            "process:1",
            "Test Adapter",
            "adapter:test",
            AdapterCpuSchedulingGrade.Optimize,
            AdapterGpuSchedulingGrade.Optimize,
            20,
            20,
            "test");

        var result = await dispatcher.ApplySoftwareSchedulingAsync("adapter:test", envelope, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Contains("未声明 SDK 软件级调度器能力", result.Message, StringComparison.Ordinal);
    }

    private sealed class StaticAdapterSoftwareRegistry(IReadOnlyList<AdapterSoftwareRegistration> registrations) : IAdapterSoftwareRegistry
    {
        public Task<IReadOnlyList<AdapterSoftwareRegistration>> GetAllAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(registrations);
        }

        public Task<AdapterRegistrationResult> RegisterAsync(
            AdapterSoftwareRegistrationRequest request,
            AdapterResourceMarkerProbeResult markerProbe,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> RemoveAsync(string id, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("HTTP should not be called when scheduling capability is missing.");
        }
    }

    private static AdapterSoftwareRegistration CreateAdapterRegistration(
        string id,
        AdapterSoftwareSchedulingCapabilities? schedulingCapabilities)
    {
        return new AdapterSoftwareRegistration(
            id,
            AdapterRegistrationSchemaVersions.Current,
            $"{id}.adapter",
            $"{id}.app",
            "Test Adapter",
            null,
            [@"D:\Apps\TestAdapter"],
            [],
            [],
            new AdapterResourceMarkerEndpoint(AdapterResourceMarkerTransports.LoopbackHttp, "http://127.0.0.1:9322/ledger"),
            new AdapterResourceMarkerProbeResult(AdapterResourceMarkerStates.Online, DateTimeOffset.Now, 200, "ok"),
            "registered",
            DateTimeOffset.Now,
            DateTimeOffset.Now,
            schedulingCapabilities);
    }

    private sealed class FakeSelfComputeZone : IResourceManagerSelfComputeZone
    {
        public FakeSelfComputeZone(string displayName)
        {
            DisplayName = displayName;
            ZoneKey = AdapterResourceKey.FromString($"test:{displayName}");
        }

        public ulong ZoneKey { get; }

        public string DisplayName { get; }

        public ResourceManagerComputeZoneMode CurrentMode { get; private set; } = ResourceManagerComputeZoneMode.Normal;

        public void ApplyMode(ResourceManagerComputeZoneMode mode)
        {
            CurrentMode = mode;
        }
    }

    private static ResourceManagerSelfSchedulingControl CreateSchedulingControl(
        IEnumerable<IResourceManagerSelfComputeZone>? standaloneComputeZones = null)
        => new(standaloneComputeZones: standaloneComputeZones);

}
