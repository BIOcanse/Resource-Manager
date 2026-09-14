using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.Preparation;

namespace Resource_Manager_APP.Tests;

public sealed class OpenGlRuntimeAdmissionTests
{
    [Theory]
    [InlineData("allowed", true)]
    [InlineData("global-off", false)]
    [InlineData("disabled", false)]
    [InlineData("preview", false)]
    [InlineData("ordinary", false)]
    [InlineData("hot-switch-off", false)]
    [InlineData("empty-permission", false)]
    [InlineData("process-disabled", false)]
    [InlineData("missing-worker", false)]
    [InlineData("missing-history", false)]
    [InlineData("mixed-api", false)]
    public async Task SavedOpenGlUsesOnePermissionAndOriginalPlanWithoutSamplingOrPublishing(string boundary, bool expected)
    {
        using var files = new GpuGraphicsApiIdentificationTests.Fixture();
        var writer = new JsonGpuPlacementProcessHistoryStore(files);
        if (boundary != "missing-history")
        {
            await writer.SaveFirstGraphicsApiAsync("software", "Software", files.Instance(), GpuGraphicsApi.OpenGL, default);
            if (boundary == "mixed-api")
            {
                // Malformed persisted input must not become a supported route on load.
                var json = await File.ReadAllTextAsync(files.HistoryPath);
                Assert.Contains("\"OpenGL\"", json, StringComparison.Ordinal);
                await File.WriteAllTextAsync(files.HistoryPath, json.Replace("\"OpenGL\"", "\"D3D11, OpenGL\"", StringComparison.Ordinal));
            }
        }
        var history = new RunningGpuPlacementIdentityTests.HistoryReader(new JsonGpuPlacementProcessHistoryStore(files));
        var software = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software") with
        {
            EnabledMode = boundary == "disabled" ? GpuPlacementPolicyModes.Disabled
                : boundary == "preview" ? GpuPlacementPolicyModes.Preview : GpuPlacementPolicyModes.Auto,
            SchedulingMode = GpuPlacementSchedulingModes.Precise,
            RuntimeSchedulingMode = boundary == "ordinary" ? GpuPlacementRuntimeSchedulingModes.Ordinary
                : GpuPlacementRuntimeSchedulingModes.Precise,
            RuntimeHotSwitchEnabled = boundary != "hot-switch-off",
            AllowedProviders = boundary == "empty-permission" ? [] : [GpuPlacementProviderIds.D3dDeviceCreateShim]
        };
        var plans = new GpuGraphicsApiIdentificationTests.Plans(software);
        if (boundary == "global-off")
            plans.Publish(plans.Current with { GpuPlacement = plans.Current.GpuPlacement with { GlobalPreciseProviderEnabled = false } });
        if (boundary == "process-disabled")
        {
            var key = ResourceManager.App.Domain.RuntimeSpecialization.CompiledBaseScorePlan.CreateProcessPolicyKey(
                "software", JsonGpuPlacementProcessHistoryStore.BuildProcessKey("target", files.Target));
            plans.Publish(plans.Current with
            {
                GpuPlacement = plans.Current.GpuPlacement with
                {
                    ProcessPoliciesBySoftwareAndProcessKey = new Dictionary<string, ResolvedGpuPlacementPolicy>
                    { [key] = ResolvedGpuPlacementPolicy.FromSoftware(software) with { RuntimeHotSwitchEnabled = false } }
                }
            });
        }
        var worker = Path.Combine(files.Root, WindowsGpuCallbackPreparationRuntime.FileName);
        if (boundary != "missing-worker") File.WriteAllBytes(worker, []);
        var runtime = new D3d11ProxyShimRuntime(files);
        Assert.True(runtime.RuntimeProviderAvailable);
        var service = new WindowsRunningGpuPlacementActionService(NullLogger<WindowsRunningGpuPlacementActionService>.Instance,
            runtime, new(NullLogger<WindowsGpuPlacementInjector>.Instance), history, plans, new(worker));
        var before = File.Exists(files.HistoryPath) ? File.ReadAllBytes(files.HistoryPath) : null;
        var result = await service.PrepareAsync(new("owned-plan", "software", "Software",
            [new(42, 123, "target", files.Target)], "fixture", 456, GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover), default);
        Assert.Equal(expected, result.Plan is not null);
        if (result.Plan is { } plan)
        {
            Assert.Equal(GpuGraphicsApi.OpenGL, Assert.Single(plan.GraphicsApis).Value);
            Assert.Equal(D3d11ProxyShimRuntime.CreateExactPolicyValue(456), plan.PolicyValue);
        }
        Assert.Null(runtime.ReadPolicy("owned-plan"));
        Assert.Equal(before, File.Exists(files.HistoryPath) ? File.ReadAllBytes(files.HistoryPath) : null);
    }
}
