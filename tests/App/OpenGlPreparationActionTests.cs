using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class OpenGlPreparationActionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationIsExplicitAndCannotInjectAfterFailureOrBeforePolicyPublication(bool publish)
    {
        using var files = new D3d11ProxyShimRuntimeTests.PolicyFiles();
        var process = new GpuPlacementProcessInstance(int.MaxValue, 123, "owned", Path.Combine(files.Root, "owned.exe"));
        const ulong adapter = 0x8000000100000002;
        var request = new RunningGpuPlacementActionRequest("gl-target", "software", "Software", [process],
            "fixture", adapter, GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover);
        var plan = new RunningGpuPlacementActionPlan(request, D3d11ProxyShimRuntime.CreateExactPolicyValue(adapter),
            new Dictionary<int, GpuGraphicsApi> { [process.ProcessId] = GpuGraphicsApi.OpenGL });
        var policy = files.Runtime.CapturePolicyRecord(request.TargetId, plan.PolicyValue)!;
        if (publish) Assert.True(files.Runtime.TryApplyPolicyRecord(policy));
        var calls = 0;
        var execution = new RunningGpuPlacementExecution(1,
            _ => throw new InvalidOperationException("Preparation failure cannot request a window."),
            GpuRemoteCallTestOwner.RejectUnexpected, (actual, actualAdapter, token) =>
            {
                calls++;
                Assert.Equal(process, actual);
                Assert.Equal(adapter, actualAdapter);
                return Task.FromResult<PreparedOpenGlCallbacks?>(null);
            });
        var service = new WindowsRunningGpuPlacementActionService(NullLogger<WindowsRunningGpuPlacementActionService>.Instance,
            files.Runtime, null!, null!, null!, new());
        var result = await service.TryApplyAsync(plan, execution, default);
        Assert.Equal(publish ? 1 : 0, calls);
        Assert.Equal(publish ? RunningGpuPlacementActionStatuses.Unresolved : RunningGpuPlacementActionStatuses.Skipped, result.Status);
        Assert.False(result.Applied);
        Assert.False(result.Triggered);
        if (publish)
        {
            var failure = Assert.Single(result.Processes);
            Assert.Equal(process, failure.Identity);
            Assert.False(failure.Configured);
            Assert.Equal("opengl-preparation-not-completed", failure.ConfigurationStatus);
            Assert.Null(failure.ConfigurationError);
        }
        Assert.True(files.Runtime.RestorePolicyRecord(policy).CanRemoveReceipt);
        Assert.Null(files.Runtime.ReadPolicy(request.TargetId));
    }
}
