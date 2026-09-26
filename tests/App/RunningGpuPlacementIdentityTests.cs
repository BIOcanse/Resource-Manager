using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class RunningGpuPlacementIdentityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RuntimeActionReadsTheSoftwareRecordWithoutIdentifyingOrUsingHistoricalPid(bool recorded, bool duplicate)
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var software = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("actual-software", "Software") with
        {
            EnabledMode = GpuPlacementPolicyModes.Auto,
            SchedulingMode = GpuPlacementSchedulingModes.Precise,
            RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise,
            RuntimeHotSwitchEnabled = true,
            AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim]
        };
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        if (recorded)
            await store.SaveFirstGraphicsApiAsync(software.SoftwareId, "Software", files.Instance(), GpuGraphicsApi.Vulkan, default);
        var before = recorded ? File.ReadAllBytes(files.HistoryPath) : null;
        var readOnly = new HistoryReader(new JsonGpuPlacementProcessHistoryStore(files));
        var runtime = new D3d11ProxyShimRuntime(files);
        var service = new WindowsRunningGpuPlacementActionService(NullLogger<WindowsRunningGpuPlacementActionService>.Instance,
            runtime, new(NullLogger<WindowsGpuPlacementInjector>.Instance), readOnly, new GpuGraphicsApiIdentificationTests.Plans(software), new());
        var process = new GpuPlacementProcessInstance(int.MaxValue, 123UL, "target", files.Target);
        var result = await service.PrepareAsync(new("per-process-target", software.SoftwareId, "Software",
            duplicate ? [process, process] : [process], "background", 0x8000000100000002UL,
            GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover), default);

        Assert.Equal(software.SoftwareId, readOnly.ReadSoftwareId);
        if (recorded)
        {
            Assert.NotNull(result.Plan);
            Assert.Equal(process, Assert.Single(result.Plan.Request.Processes));
            Assert.Equal(GpuGraphicsApi.Vulkan, result.Plan.GraphicsApis[process.ProcessId]);
        }
        else
        {
            Assert.Null(result.Plan);
            Assert.Contains("图形 API", result.Message);
        }
        Assert.Null(runtime.ReadPolicy("per-process-target"));
        Assert.Equal(before, File.Exists(files.HistoryPath) ? File.ReadAllBytes(files.HistoryPath) : null);
    }

    [Fact]
    public async Task ConflictingProcessInstancesAreRejectedBeforeAnyConsumerOrEffect()
    {
        var service = new WindowsRunningGpuPlacementActionService(NullLogger<WindowsRunningGpuPlacementActionService>.Instance,
            null!, null!, null!, null!, new());
        var first = new GpuPlacementProcessInstance(int.MaxValue, 123UL, "target", @"C:\owned\target.exe");
        var result = await service.PrepareAsync(new("target", "software", "Software",
            [first, first with { ProcessStartKey = 124 }], "background", 1,
            GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover), default);
        Assert.Null(result.Plan);
        Assert.Contains("冲突", result.Message);
    }

    internal sealed class HistoryReader(IGpuPlacementProcessHistoryStore store) : IGpuPlacementProcessHistoryStore
    {
        public string? ReadSoftwareId { get; private set; }
        public Task<GpuPlacementSoftwareProcessHistory> GetSoftwareHistoryAsync(string softwareId, string softwareName, CancellationToken token)
        {
            ReadSoftwareId = softwareId;
            return store.GetSoftwareHistoryAsync(softwareId, softwareName, token);
        }
        public Task<GpuPlacementSoftwareProcessHistory> ObserveAsync(GpuPlacementProcessObservationRequest request, CancellationToken token)
            => throw new InvalidOperationException("An action must not identify or update software information.");
        public Task<GpuPlacementSoftwareProcessHistory> SaveFirstGraphicsApiAsync(string softwareId, string softwareName,
            GpuPlacementProcessInstance process, GpuGraphicsApi graphicsApi, CancellationToken token)
            => throw new InvalidOperationException("Preparation and selection only read committed software information.");
    }
}
