using System.ComponentModel;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;
using static ResourceManager.App.Infrastructure.GpuPlacement.WindowsGpuPlacementInjector;

namespace Resource_Manager_APP.Tests;

public sealed class GpuPlacementInjectorModuleTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "rm-module-selection");

    [Fact]
    public void OpenGlUsesOnlyTheCommonProviderAndItsPreparedCallbackExport()
    {
        var queries = new List<string>();
        var binding = SelectProvider(GpuGraphicsApi.OpenGL, Root, name => { queries.Add(name); return null; });
        Assert.Equal([RuntimeProviderFileName], queries);
        Assert.Equal(ConfigureOpenGlExportName, binding.ConfigureExport);
        Assert.Equal(35U, binding.RequiredStatus);
    }

    [Fact]
    public void ExistingVulkanLayerSelectsOnlyItsOwnExports()
    {
        var queries = new List<string>();
        var layer = new RemoteModule(new IntPtr(123), Path.Combine(Root, "GpuPlacementShim", GpuStartupProviderArtifacts.VulkanLayerFileName));
        var binding = SelectProvider(GpuGraphicsApi.Vulkan, Root, name =>
        {
            queries.Add(name);
            Assert.Equal(GpuStartupProviderArtifacts.VulkanLayerFileName, name);
            return layer;
        });
        Assert.Equal([GpuStartupProviderArtifacts.VulkanLayerFileName], queries);
        Assert.Equal(layer, binding.LoadedModule);
        Assert.Equal(layer.Path, binding.Path);
        Assert.Equal(ConfigureExportName, binding.ConfigureExport);
        Assert.Equal(19U, binding.RequiredStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsentLayerSelectsTheOriginalProviderWithExplicitVulkanExport(bool commonLoaded)
    {
        var queries = new List<string>();
        RemoteModule? common = commonLoaded ? new(new IntPtr(456), Path.Combine(Root, "GpuPlacementShim", RuntimeProviderFileName)) : null;
        var binding = SelectProvider(GpuGraphicsApi.Vulkan, Root, name =>
        {
            queries.Add(name);
            return name == RuntimeProviderFileName ? common : null;
        });
        Assert.Equal([GpuStartupProviderArtifacts.VulkanLayerFileName, RuntimeProviderFileName], queries);
        Assert.Equal(common, binding.LoadedModule);
        Assert.Equal(Path.Combine(Root, "GpuPlacementShim", RuntimeProviderFileName), binding.Path);
        Assert.Equal(ConfigureVulkanExportName, binding.ConfigureExport);
        Assert.Equal(19U, binding.RequiredStatus);
    }

    [Theory]
    [InlineData(GpuGraphicsApi.D3D9, 15U)]
    [InlineData(GpuGraphicsApi.D3D11, 7U)]
    [InlineData(GpuGraphicsApi.D3D12, 7U)]
    [InlineData(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12, 7U)]
    public void Direct3DDoesNotInspectOrSelectVulkan(GpuGraphicsApi api, uint mask)
    {
        var queries = new List<string>();
        var binding = SelectProvider(api, Root, name => { queries.Add(name); return null; });
        Assert.Equal([RuntimeProviderFileName], queries);
        Assert.Equal(ConfigureExportName, binding.ConfigureExport);
        Assert.Equal(mask, binding.RequiredStatus);
    }

    [Fact]
    public void SnapshotFailureIsNotLayerAbsenceAndDoesNotQueryAnotherRoute()
    {
        var queries = new List<string>();
        var error = Assert.Throws<Win32Exception>(() => SelectProvider(GpuGraphicsApi.Vulkan, Root, name =>
        {
            queries.Add(name);
            throw new Win32Exception(5);
        }));
        Assert.Equal(5, error.NativeErrorCode);
        Assert.Equal([GpuStartupProviderArtifacts.VulkanLayerFileName], queries);
    }

    [Fact]
    public void DifferentPathLayerIsRetainedForTheOriginalPathRejectionNotReplaced()
    {
        var actual = new RemoteModule(new IntPtr(789), Path.Combine(Root, "another", GpuStartupProviderArtifacts.VulkanLayerFileName));
        var binding = SelectProvider(GpuGraphicsApi.Vulkan, Root, _ => actual);
        Assert.Equal(actual, binding.LoadedModule);
        Assert.NotEqual(actual.Path, binding.Path);
        Assert.EndsWith(GpuStartupProviderArtifacts.VulkanLayerFileName, binding.Path);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(24)]
    [InlineData(299)]
    public void OnlyNormalEnumerationEndMeansAbsent(int error)
        => Assert.Equal(error, Assert.Throws<Win32Exception>(() => EndOfModuleEnumeration(error)).NativeErrorCode);

    [Fact]
    public void NormalEnumerationEndIsAbsent() => Assert.Null(EndOfModuleEnumeration(18));

    [Fact]
    public void ActualOwnProcessSnapshotDistinguishesKnownAndAbsentModules()
    {
        var module = FindRemoteModule(Environment.ProcessId, "kernel32.dll");
        Assert.NotNull(module);
        Assert.NotEqual(IntPtr.Zero, module.Value.BaseAddress);
        Assert.Equal("kernel32.dll", Path.GetFileName(module.Value.Path), ignoreCase: true);
        Assert.Null(FindRemoteModule(Environment.ProcessId, "ResourceManager.NonexistentSnapshotProbe.dll"));
    }

    [Fact]
    public void ActualInvalidProcessSnapshotIsAnErrorNotAbsence()
        => Assert.Throws<Win32Exception>(() => FindRemoteModule(-1, "kernel32.dll"));
}
