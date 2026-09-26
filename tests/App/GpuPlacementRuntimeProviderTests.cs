using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.GpuPlacement;
using System.Text.Json;
using ResourceManager.App.Domain.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class GpuPlacementRuntimeProviderTests
{
    [Fact]
    public void VulkanLayerPackage_ContainsMatchingExplicitManifestAndLoaderExports()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "GpuPlacementShim");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "ResourceManager.VulkanPlacementLayer.json")));
        var layer = manifest.RootElement.GetProperty("layer");
        Assert.Equal("VK_LAYER_RESOURCE_MANAGER_gpu_placement", layer.GetProperty("name").GetString());
        Assert.Equal("GLOBAL", layer.GetProperty("type").GetString());
        Assert.False(layer.TryGetProperty("enable_environment", out _));
        Assert.Equal(@".\ResourceManager.VulkanPlacementLayer.dll", layer.GetProperty("library_path").GetString());
        var binary = Path.GetFullPath(Path.Combine(directory, layer.GetProperty("library_path").GetString()!));
        foreach (var name in new[] { "vkNegotiateLoaderLayerInterfaceVersion", "vkGetInstanceProcAddr", "vkGetDeviceProcAddr",
            "vk_layerGetPhysicalDeviceProcAddr", "vkCreateInstance", "vkDestroyInstance", "vkEnumeratePhysicalDevices",
            "vkEnumeratePhysicalDeviceGroups", "vkEnumeratePhysicalDeviceGroupsKHR", "vkCreateDevice", "vkDestroyDevice" })
        {
            Assert.True(PortableExecutableExportReader.TryGetExportRva(binary, name, out var rva, out var error), error);
            Assert.True(rva > 0);
        }
        foreach (var notice in new[] { "Vulkan-Headers.NOTICE.md", "Vulkan-Headers.LICENSE.md", "Vulkan-Headers.Apache-2.0.txt", "Vulkan-Headers.MIT.txt" })
        {
            Assert.True(new FileInfo(Path.Combine(AppContext.BaseDirectory, "ThirdPartyNotices", notice)).Length > 0);
        }
    }

    [Fact]
    public void RuntimeProviderBinary_ExportsConfigurationAndStatusEntryPoints()
    {
        var providerPath = Path.Combine(
            AppContext.BaseDirectory,
            "GpuPlacementShim",
            WindowsGpuPlacementInjector.RuntimeProviderFileName);

        Assert.True(File.Exists(providerPath));
        Assert.True(PortableExecutableExportReader.TryGetExportRva(
            providerPath,
            WindowsGpuPlacementInjector.ConfigureExportName,
            out var configureRva,
            out var configureError),
            configureError);
        Assert.True(PortableExecutableExportReader.TryGetExportRva(
            providerPath,
            "ResourceManagerGpuPlacementGetStatus",
            out var statusRva,
            out var statusError),
            statusError);
        Assert.True(configureRva > 0);
        Assert.True(statusRva > 0);
        Assert.NotEqual(configureRva, statusRva);
    }

    [Fact]
    public void StartupBootstrapBinary_ExportsSuspendedProcessImportUpdater()
    {
        var bootstrapPath = Path.Combine(
            AppContext.BaseDirectory,
            "GpuPlacementShim",
            WindowsGpuPlacementInjector.StartupBootstrapFileName);

        Assert.True(File.Exists(bootstrapPath));
        Assert.True(PortableExecutableExportReader.TryGetExportRva(
            bootstrapPath,
            "ResourceManagerGpuPlacementBootstrapUpdate",
            out var exportRva,
            out var exportError),
            exportError);
        Assert.True(exportRva > 0);
    }

    [Theory]
    [InlineData("ResourceManagerGpuPlacementGetD3D11Dependency")]
    [InlineData("ResourceManagerGpuPlacementGetD3D12Dependency")]
    public void PackagedProvider_ContainsBothDirect3DDependencies(string exportName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "GpuPlacementShim", WindowsGpuPlacementInjector.RuntimeProviderFileName);
        Assert.True(PortableExecutableExportReader.TryGetExportRva(path, exportName, out var rva, out var error), error);
        Assert.True(rva > 0);
    }

    [Fact]
    public async Task Injector_RejectsTheCurrentHostWithoutTreatingItAsAnInjectionAttempt()
    {
        var policyPath = Path.Combine(
            Path.GetTempPath(),
            $"resource-manager-gpu-policy-{Guid.NewGuid():N}.txt");
        File.WriteAllText(policyPath, "mode=default\n");
        try
        {
            var injector = new WindowsGpuPlacementInjector(
                NullLogger<WindowsGpuPlacementInjector>.Instance);

            var target = new GpuPlacementProcessInstance(Environment.ProcessId, 0, "self", Environment.ProcessPath!);
            using var firstAttempt = await injector.OpenAndConfigureAsync(target, GpuGraphicsApi.D3D11, policyPath, GpuRemoteCallTestOwner.RejectUnexpected, default);
            using var secondAttempt = await injector.OpenAndConfigureAsync(target, GpuGraphicsApi.D3D11, policyPath, GpuRemoteCallTestOwner.RejectUnexpected, default);
            var first = firstAttempt.Result;
            var second = secondAttempt.Result;

            Assert.False(first.Success);
            Assert.Equal("invalid-target", first.Status);
            Assert.False(second.Success);
            Assert.Equal("invalid-target", second.Status);
        }
        finally
        {
            File.Delete(policyPath);
        }
    }

    [Theory]
    [InlineData(1u, 0u, 0xfffffffeu)]
    [InlineData(0u, 1u, 0xfffffffeu)]
    [InlineData(0u, 0u, 3u)]
    public void InjectorCompatibility_RejectsProtectedOrMitigatedProcesses(
        uint signaturePolicy,
        uint dynamicCodePolicy,
        uint protectionLevel)
    {
        Assert.NotNull(WindowsGpuPlacementInjector.ResolveProcessMitigationBlockReason(
            signaturePolicy,
            dynamicCodePolicy,
            protectionLevel));
    }

    [Fact]
    public void InjectorCompatibility_AllowsUnprotectedProcessWithoutBlockingMitigations()
    {
        Assert.Null(WindowsGpuPlacementInjector.ResolveProcessMitigationBlockReason(
            0,
            0,
            0xfffffffe));
    }

}
