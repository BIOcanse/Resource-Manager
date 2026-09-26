using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed partial class GpuPlacementInjectorNativeTests
{
    [VulkanNativeTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task VulkanUsesTheOriginalRemoteOwnerAndFileRestoration(bool existingLayer, bool existingPolicy)
    {
        using var files = new D3d11ProxyShimRuntimeTests.PolicyFiles();
        var executable = Environment.GetEnvironmentVariable("RM_GPU_VULKAN_NATIVE_PROBE")!;
        var layerPath = Path.Combine(AppContext.BaseDirectory, "GpuPlacementShim", GpuStartupProviderArtifacts.VulkanLayerFileName);
        await using var child = await OwnedProbe.StartAsync("vulkan", output, existingLayer ? layerPath : null, executable);
        Assert.NotEqual(0UL, child.AdapterLuid);
        Assert.NotEqual(0UL, child.AlternateAdapterLuid);
        Assert.NotEqual(child.AdapterLuid, child.AlternateAdapterLuid);
        Assert.False(child.HasProvider());
        const string target = "owned-vulkan-target";
        if (existingPolicy) Assert.True(files.Runtime.PrepareExact(target, child.AdapterLuid).PolicyPrepared);
        var original = files.Runtime.ReadPolicy(target);
        var record = files.Runtime.CapturePolicyRecord(target, D3d11ProxyShimRuntime.CreateExactPolicyValue(child.AlternateAdapterLuid))!;
        Assert.NotNull(record);
        Assert.True(files.Runtime.TryApplyPolicyRecord(record));
        var calls = new List<(GpuRemoteCallRequest Request, GpuRemoteCallSnapshot Result)>();
        async Task<GpuRemoteCallSnapshot> Execute(GpuRemoteCallExecution call, CancellationToken token)
        {
            var request = call.Request;
            Assert.Equal(child.Identity, request.Process);
            var result = await GpuRemoteCallTestOwner.ExecuteCompletedAsync(call, token);
            Assert.True(result.Completed);
            Assert.NotNull(result.ThreadId);
            Assert.NotNull(result.ThreadCreationFileTimeUtc);
            calls.Add((request, result));
            output.WriteLine(JsonSerializer.Serialize(new { remoteRequest = request, remoteResult = result }));
            return result;
        }
        var injector = new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance);
        using var owner = await injector.OpenAndConfigureAsync(child.Identity, GpuGraphicsApi.Vulkan,
            files.Runtime.GetPolicyPath(target), Execute, default);
        Assert.True(owner.Result.Success, owner.Result.Message);
        Assert.Equal(existingLayer, owner.Result.AlreadyLoaded);
        Assert.Equal(existingLayer ? layerPath : injector.RuntimeProviderPath, owner.Result.ProviderPath);
        Assert.Equal(!existingLayer, child.HasProvider());
        Assert.Equal(19U, owner.Result.ProviderStatus & 19U);
        var initial = await owner.ReadDeviceObservationsAsync(default);
        Assert.True(initial.Success, initial.Status);
        var initialSnapshot = Assert.IsType<GpuDeviceObservationSnapshot>(initial.Snapshot);
        Assert.Equal(existingLayer ? 1UL : 0UL, initialSnapshot.Vulkan.ReturnedDeviceCount);
        var creations = 0UL;
        async Task CreateAndVerify(ulong expected)
        {
            Assert.Equal(expected, await child.CreateDeviceAsync());
            var read = await owner.ReadDeviceObservationsAsync(default);
            Assert.True(read.Success, read.Status);
            var snapshot = Assert.IsType<GpuDeviceObservationSnapshot>(read.Snapshot);
            Assert.Equal(initialSnapshot.Vulkan.ReturnedDeviceCount + ++creations, snapshot.Vulkan.ReturnedDeviceCount);
            Assert.Equal(GpuDeviceAdapterIdentityKind.Adapter, snapshot.Vulkan.Identity);
            Assert.Equal(expected, snapshot.Vulkan.AdapterLuid);
            Assert.Equal(initialSnapshot.D3D9, snapshot.D3D9);
            Assert.Equal(initialSnapshot.OpenGL, snapshot.OpenGL);
            Assert.Equal(initialSnapshot.D3D11, snapshot.D3D11);
            Assert.Equal(initialSnapshot.D3D12, snapshot.D3D12);
        }
        await CreateAndVerify(child.AlternateAdapterLuid);
        var second = files.Runtime.CapturePolicyRecord(target, D3d11ProxyShimRuntime.CreateExactPolicyValue(child.AdapterLuid))!;
        Assert.True(files.Runtime.TryApplyPolicyRecord(second));
        await CreateAndVerify(child.AdapterLuid);
        Assert.True(files.Runtime.RestorePolicyRecord(second).CanRemoveReceipt);
        await CreateAndVerify(child.AlternateAdapterLuid);
        Assert.True(files.Runtime.RestorePolicyRecord(record).CanRemoveReceipt);
        Assert.Equal(original, files.Runtime.ReadPolicy(target));
        await CreateAndVerify(child.AdapterLuid);
        Assert.Single(calls.Where(call => call.Request.Kind == GpuRemoteCallKind.ConfigureProvider));
        Assert.Equal(existingLayer ? 0 : 1, calls.Count(call => call.Request.Kind == GpuRemoteCallKind.LoadProvider));
        Assert.Equal(5, calls.Count(call => call.Request.Kind == GpuRemoteCallKind.ReadDevices));
        Assert.Equal(calls.Count, calls.Select(call => call.Request.CallId).Distinct().Count());
        await child.StopAsync();
        output.WriteLine($"vulkanOriginalRemoteOwner=true; existingLayer={existingLayer}; existingPolicy={existingPolicy}; originalFileRestored=true; configureOnce=true; deviceCreations=4; oldDeviceReadbacks=4; coordinatorTested=false");
    }
}

internal sealed class VulkanNativeTheoryAttribute : TheoryAttribute
{
    public VulkanNativeTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RM_GPU_VULKAN_NATIVE_PROBE")))
            Skip = "Requires the explicitly supplied self-owned Vulkan command probe.";
    }
}
