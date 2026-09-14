using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed partial class GpuPlacementInjectorNativeTests
{
    [GpuApiNativeTheory]
    [InlineData("load")]
    [InlineData("configure")]
    [InlineData("before-read")]
    [InlineData("after-read")]
    public async Task RemoteCallStopEndsTheCurrentServicePlanEvenWithAnActiveCallerToken(string stage)
    {
        using var files = new D3d11ProxyShimRuntimeTests.PolicyFiles();
        await using var first = await OwnedProbe.StartAsync("d3d11", output);
        await using var second = await OwnedProbe.StartAsync("d3d11", output);
        var request = new RunningGpuPlacementActionRequest("remote-batch", "fixture", "Fixture",
            [first.Identity, second.Identity], "background", first.AdapterLuid, GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover);
        var plan = new RunningGpuPlacementActionPlan(request, D3d11ProxyShimRuntime.CreateExactPolicyValue(first.AdapterLuid),
            request.Processes.ToDictionary(process => process.ProcessId, _ => GpuGraphicsApi.D3D11));
        var policy = files.Runtime.CapturePolicyRecord(request.TargetId, plan.PolicyValue)!;
        Assert.True(files.Runtime.TryApplyPolicyRecord(policy));
        var service = new WindowsRunningGpuPlacementActionService(NullLogger<WindowsRunningGpuPlacementActionService>.Instance,
            files.Runtime, new(NullLogger<WindowsGpuPlacementInjector>.Instance), null!, null!, new());
        var calls = new List<(int Pid, GpuRemoteCallKind Kind)>();
        var stopped = false;
        var firstReads = 0;
        var windows = new RunningGpuPlacementExecution(16,
            _ => throw new InvalidOperationException("Hidden target has no eligible window."), ExecuteAsync, GpuRemoteCallTestOwner.RejectPreparation);
        var result = await service.TryApplyAsync(plan, windows, CancellationToken.None);
        Assert.True(stopped);
        Assert.Equal(RunningGpuPlacementActionStatuses.Unresolved, result.Status);
        Assert.Equal(stage is "load" or "configure" ? 1 : 2, result.Processes.Count);
        Assert.Equal(stage == "load" ? 1 : stage == "configure" ? 2 : stage == "before-read" ? 5 : 7, calls.Count);
        Assert.Equal(first.Identity.ProcessId, calls[^1].Pid);
        Assert.Equal(stage is "load" or "configure" ? 0 : stage == "before-read" ? 0 : 1,
            calls.Count(call => call.Pid == second.Identity.ProcessId && call.Kind == GpuRemoteCallKind.ReadDevices));
        await first.StopAsync();
        await second.StopAsync();
        Assert.True(files.Runtime.RestorePolicyRecord(policy).CanRemoveReceipt);
        output.WriteLine($"remotePlanStopped={stage}; callerTokenActive=true; stoppedCallNotStarted=true; actualPriorCallsCompleted=true; calls={calls.Count}");

        async Task<GpuRemoteCallSnapshot> ExecuteAsync(GpuRemoteCallExecution call, CancellationToken token)
        {
            Assert.False(stopped);
            calls.Add((call.Request.Process.ProcessId, call.Request.Kind));
            if (call.Request.Process.ProcessId == first.Identity.ProcessId && call.Request.Kind == GpuRemoteCallKind.ReadDevices)
                firstReads++;
            var stopHere = call.Request.Process.ProcessId == first.Identity.ProcessId && stage switch
            {
                "load" => call.Request.Kind == GpuRemoteCallKind.LoadProvider,
                "configure" => call.Request.Kind == GpuRemoteCallKind.ConfigureProvider,
                "before-read" => firstReads == 1,
                _ => firstReads == 2
            };
            if (!stopHere) return await GpuRemoteCallTestOwner.ExecuteCompletedAsync(call, token);
            stopped = true;
            var snapshot = call.Snapshot;
            call.Dispose();
            return snapshot;
        }
    }

    [GpuApiNativeTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteCallCancellationOrPendingOwnershipDoesNotBecomeALifetimeCompatibilityFailure(bool started)
    {
        using var files = new D3d11ProxyShimRuntimeTests.PolicyFiles();
        await using var child = await OwnedProbe.StartAsync("d3d11", output);
        var injector = new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance);
        var policy = files.Runtime.PrepareExact("remote-cache", child.AdapterLuid);
        Assert.True(policy.PolicyPrepared, policy.ErrorMessage);
        GpuRemoteCallExecution? retained = null;
        try
        {
            using var owner = await injector.OpenAndConfigureAsync(child.Identity, GpuGraphicsApi.D3D11, policy.PolicyPath!, (call, _) =>
            {
                Assert.Null(retained);
                retained = call;
                if (started) call.Start();
                return Task.FromResult(call.Snapshot);
            }, default);
            Assert.False(owner.Result.Success);
            Assert.True(owner.CallsStopped);
            Assert.NotNull(retained);
            if (started)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await retained.WaitAsync(deadline.Token);
                Assert.True(retained.Snapshot.Completed);
            }
            retained.Dispose();
            using var next = await injector.OpenAndConfigureAsync(child.Identity, GpuGraphicsApi.D3D11, policy.PolicyPath!,
                GpuRemoteCallTestOwner.ExecuteCompletedAsync, default);
            Assert.True(next.Result.Success, next.Result.Message);
            Assert.NotEqual("retry-suppressed-until-process-restart", next.Result.Status);
            await child.StopAsync();
            output.WriteLine($"remoteFailureCacheSeparated=true; originallyStarted={started}; laterExplicitConfiguration=true; automaticRetry=false");
        }
        finally { retained?.Dispose(); }
    }
}
