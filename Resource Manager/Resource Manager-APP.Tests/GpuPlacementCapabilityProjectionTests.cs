using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;

namespace Resource_Manager_APP.Tests;

public sealed class GpuPlacementCapabilityProjectionTests
{
    [Fact]
    public void TargetInventory_UsesOnlyCurrentAdaptersAndPreservesMissingSavedTarget()
    {
        var now = DateTimeOffset.UtcNow;
        var observedAt = now.UtcTicks;
        var inventory = new SchedulingGpuInventorySnapshot(
            SamplingObservationStatus.Current,
            7,
            observedAt,
            2,
            0,
            0,
            11,
            [
                Adapter(0, 101, 7, observedAt),
                Adapter(2, 202, 7, observedAt)
            ]);
        var snapshot = new HardwareMetricSnapshot(
            now,
            null!,
            null!,
            null!,
            [],
            inventory,
            new Dictionary<string, MetricValue>());

        var result = GpuPlacementTargetInventoryProjection.Create(snapshot, ["GPU2", "GPU3"]);

        Assert.Equal(GpuPlacementTargetInventoryStates.Current, result.State);
        Assert.Collection(
            result.ExactTargets,
            option => Assert.Equal(("GPU0", true), (option.TargetGpu, option.Available)),
            option => Assert.Equal(("GPU2", true), (option.TargetGpu, option.Available)),
            option => Assert.Equal(("GPU3", false), (option.TargetGpu, option.Available)));
    }

    [Fact]
    public void TargetInventory_WhenObservationIsIncomplete_DisablesEverySavedExactTarget()
    {
        var result = GpuPlacementTargetInventoryProjection.Create(null, ["GPU1", "SystemDefaultGpu"]);

        Assert.Equal(GpuPlacementTargetInventoryStates.Unavailable, result.State);
        var target = Assert.Single(result.ExactTargets);
        Assert.Equal("GPU1", target.TargetGpu);
        Assert.False(target.Available);
    }

    [Fact]
    public void CapabilityReader_ReportsUnknownWithoutExecutablePath()
    {
        var reader = new WindowsGpuPlacementCapabilityReader(Path.GetTempPath());

        var result = reader.Evaluate("process", null, "x64", null);

        Assert.Equal(GpuPlacementCapabilityStates.Unknown, result.Startup.State);
        Assert.Equal(GpuPlacementCapabilityStates.Unknown, result.Runtime.State);
        Assert.Equal("D3D11/D3D12/Vulkan", result.Runtime.GraphicsApi);
    }

    [Theory]
    [InlineData("d3d11.dll", "D3D11")]
    [InlineData("d3d12.dll", "D3D12")]
    [InlineData("D3D12.DLL,d3d11.dll,d3d12.dll", "D3D11/D3D12")]
    public void CapabilityReader_RequiresProviderFilesAndReportsOnlySupportedObservedApis(
        string modules,
        string expectedApi)
    {
        var executablePath = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(executablePath));
        using var files = new ProviderFiles();
        var api = new WindowsGpuGraphicsApiDetector((_, _) => modules.Split(',')).Detect(42, executablePath!);
        var reader = new WindowsGpuPlacementCapabilityReader(files.Root);
        var missing = reader.Evaluate("process", executablePath, "x64", api);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, missing.Startup.State);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, missing.Runtime.State);
        Assert.Equal(expectedApi, missing.Runtime.GraphicsApi);

        File.WriteAllBytes(files.Runtime, []);
        var runtimeOnly = reader.Evaluate("process", executablePath, "x64", api);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, runtimeOnly.Startup.State);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, runtimeOnly.Runtime.State);

        files.CreateAll();
        var available = reader.Evaluate("process", executablePath, "x64", api);
        Assert.Equal(GpuPlacementCapabilityStates.Supported, available.Startup.State);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, available.Runtime.State);
        var vulkanStartup = modules.Contains("vulkan-1.dll", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(vulkanStartup ? "Vulkan" : expectedApi, available.Startup.GraphicsApi);
        Assert.Equal(expectedApi, available.Runtime.GraphicsApi);
        Assert.Equal("x64", available.Runtime.Architecture);
        Assert.Contains("无注入运行期换卡路径", available.Runtime.Reason, StringComparison.Ordinal);
        if (expectedApi.Contains("D3D12", StringComparison.Ordinal))
        {
            var reasons = vulkanStartup ? Array.Empty<string>() : new[] { available.Startup.Reason };
            foreach (var reason in reasons)
            {
                Assert.Contains("D3D12CreateDevice", reason, StringComparison.Ordinal);
                Assert.Contains("ID3D12DeviceFactory::CreateDevice", reason, StringComparison.Ordinal);
                Assert.Contains("D3D12GetInterface", reason, StringComparison.Ordinal);
                Assert.Contains("不保证预先持有的工厂", reason, StringComparison.Ordinal);
            }
        }
        else
        {
            Assert.DoesNotContain("D3D12", available.Runtime.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("DeviceFactory", available.Startup.Reason, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("opengl32.dll", "OpenGL")]
    public void CapabilityReader_OpenGlIsIdentifiedButUnsupported(string modules, string expectedApi)
    {
        using var files = new ProviderFiles();
        files.CreateAll();
        var reader = new WindowsGpuPlacementCapabilityReader(files.Root);
        var api = new WindowsGpuGraphicsApiDetector((_, _) => modules.Split(',')).Detect(42, Environment.ProcessPath!);

        var result = reader.Evaluate("process", Environment.ProcessPath, "x64", api);

        Assert.Equal(modules.Contains("vulkan-1.dll", StringComparison.OrdinalIgnoreCase)
            ? GpuPlacementCapabilityStates.Supported : GpuPlacementCapabilityStates.Unsupported, result.Startup.State);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, result.Runtime.State);
        Assert.Equal(expectedApi, result.Runtime.GraphicsApi);
        Assert.Contains("无注入运行期换卡路径", result.Runtime.Reason, StringComparison.Ordinal);
        Assert.True(GpuGraphicsApiRoutes.IsIdentified(api));
    }

    [Fact]
    public void D3D9RemainsIdentifiableButHasNoEnabledPlacementRoute()
    {
        using var files = new ProviderFiles();
        var reader = new WindowsGpuPlacementCapabilityReader(files.Root);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported,
            reader.Evaluate("process", Environment.ProcessPath, "x64", GpuGraphicsApi.D3D9).Runtime.State);
        files.CreateAll();
        var result = reader.Evaluate("process", Environment.ProcessPath, "x64", GpuGraphicsApi.D3D9);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, result.Startup.State);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, result.Runtime.State);
        Assert.Equal("D3D9", result.Runtime.GraphicsApi);
        Assert.True(GpuGraphicsApiRoutes.IsIdentified(GpuGraphicsApi.D3D9));
        Assert.Null(GpuGraphicsApiRoutes.RuntimeProvider(GpuGraphicsApi.D3D9));
        Assert.Null(GpuGraphicsApiRoutes.StartupProvider(GpuGraphicsApi.D3D9));
    }

    [Theory]
    [InlineData(GpuGraphicsApi.D3D9, 15U)]
    [InlineData(GpuGraphicsApi.D3D11, 7U)]
    [InlineData(GpuGraphicsApi.D3D12, 7U)]
    [InlineData(GpuGraphicsApi.Vulkan, 19U)]
    [InlineData(GpuGraphicsApi.OpenGL, 35U)]
    [InlineData(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12, 7U)]
    public void RuntimeReadinessIsSpecificToTheSavedApi(GpuGraphicsApi api, uint expected)
        => Assert.Equal(expected, WindowsGpuPlacementInjector.RequiredProviderStatus(api));

    [Theory]
    [InlineData(GpuGraphicsApi.OpenGL | GpuGraphicsApi.D3D11)]
    [InlineData(GpuGraphicsApi.D3D9 | GpuGraphicsApi.D3D11)]
    public void UnsupportedOrAmbiguousRuntimeApisCannotReuseAnotherApisReadiness(GpuGraphicsApi api)
    {
        Assert.Null(GpuGraphicsApiRoutes.RuntimeProvider(api));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsGpuPlacementInjector.RequiredProviderStatus(api));
    }

    [Fact]
    public void OpenGlHasNoMovementRouteEvenWithAllHistoricalArtifacts()
    {
        Assert.Equal(35U, WindowsGpuPlacementInjector.RequiredProviderStatus(GpuGraphicsApi.OpenGL));
        Assert.Null(GpuGraphicsApiRoutes.RuntimeProvider(GpuGraphicsApi.OpenGL));
        Assert.Null(GpuGraphicsApiRoutes.StartupProvider(GpuGraphicsApi.OpenGL));
        using var files = new ProviderFiles();
        files.CreateAll();
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported,
            new WindowsGpuPlacementCapabilityReader(files.Root)
                .Evaluate("process", Environment.ProcessPath, "x64", GpuGraphicsApi.OpenGL).Runtime.State);
        File.WriteAllBytes(files.Preparation, []);
        var reader = new WindowsGpuPlacementCapabilityReader(files.Root);
        var available = reader.Evaluate("process", Environment.ProcessPath, "x64", GpuGraphicsApi.OpenGL);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, available.Runtime.State);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, available.Startup.State);
        Assert.Equal(WindowsExternalGpuPlacementRuntime.ProviderId, available.Runtime.ProviderId);
        Assert.Contains("无注入运行期换卡路径", available.Runtime.Reason, StringComparison.Ordinal);
        File.Delete(files.Runtime);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported,
            reader.Evaluate("process", Environment.ProcessPath, "x64", GpuGraphicsApi.OpenGL).Runtime.State);
        File.WriteAllBytes(files.Runtime, []);
        File.Delete(files.Preparation);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported,
            reader.Evaluate("process", Environment.ProcessPath, "x64", GpuGraphicsApi.D3D11).Runtime.State);
    }

    [Fact]
    public void VulkanRuntimeUsesItsOwnReadinessAndExistingPolicyIdentifier()
    {
        Assert.Equal(19U, WindowsGpuPlacementInjector.RequiredProviderStatus(GpuGraphicsApi.Vulkan));
        Assert.Equal(GpuPlacementProviderIds.VulkanExplicitLayer, GpuGraphicsApiRoutes.RuntimeProvider(GpuGraphicsApi.Vulkan));
        using var files = new ProviderFiles();
        files.CreateAll();
        var capability = new WindowsGpuPlacementCapabilityReader(files.Root)
            .Evaluate("process", Environment.ProcessPath, "x64", GpuGraphicsApi.Vulkan).Runtime;
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, capability.State);
        Assert.Equal(WindowsExternalGpuPlacementRuntime.QtVulkanProviderId, capability.ProviderId);
        Assert.Contains("无注入运行期换卡路径", capability.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("vulkan-1.dll")]
    public void CapabilityReader_VulkanStartupRequiresItsOwnCompleteArtifacts(string modules)
    {
        using var files = new ProviderFiles();
        var reads = 0;
        var detector = new WindowsGpuGraphicsApiDetector((_, _) =>
        {
            reads++;
            return modules.Split(',');
        });
        var api = detector.Detect(42, Environment.ProcessPath!);
        var reader = new WindowsGpuPlacementCapabilityReader(files.Root);
        files.CreateAll();
        File.Delete(files.VulkanManifest);
        var missingManifest = reader.Evaluate("process", Environment.ProcessPath, "x64", api);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, missingManifest.Startup.State);
        File.WriteAllBytes(files.VulkanManifest, []);
        File.Delete(files.VulkanLayer);
        var missingLayer = reader.Evaluate("process", Environment.ProcessPath, "x64", api);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, missingLayer.Startup.State);
        File.WriteAllBytes(files.VulkanLayer, []);
        File.Delete(files.Runtime);
        var available = reader.Evaluate("process", Environment.ProcessPath, "x64", api);
        Assert.Equal(GpuPlacementCapabilityStates.Supported, available.Startup.State);
        Assert.Equal(GpuPlacementProviderIds.VulkanExplicitLayer, available.Startup.ProviderId);
        Assert.Equal("Vulkan", available.Startup.GraphicsApi);
        Assert.Contains("普通权限", available.Startup.Reason, StringComparison.Ordinal);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, available.Runtime.State);
        Assert.Equal("Vulkan", available.Runtime.GraphicsApi);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kernel32.dll")]
    [InlineData("d3d12core.dll")]
    public void CapabilityReader_MissingModuleObservationIsUnknownEvenWithProviderFiles(string? modules)
    {
        using var files = new ProviderFiles();
        files.CreateAll();
        var api = new WindowsGpuGraphicsApiDetector((_, _) => modules?.Split(',')).Detect(42, Environment.ProcessPath!);
        var reader = new WindowsGpuPlacementCapabilityReader(files.Root);

        var result = reader.Evaluate("process", Environment.ProcessPath, "x64", api);

        Assert.Equal(GpuPlacementCapabilityStates.Unknown, result.Startup.State);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, result.Runtime.State);
    }

    [Fact]
    public void CapabilityReader_NoProcessDoesNotInventAnObservedApi()
    {
        using var files = new ProviderFiles();
        files.CreateAll();
        var reader = new WindowsGpuPlacementCapabilityReader(files.Root);

        var result = reader.Evaluate("process", Environment.ProcessPath, "x64", null);

        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, result.Runtime.State);
    }

    [Fact]
    public void RuntimeControllersRequireExactRendererConfirmationBeforeReportingSupport()
    {
        using var files = new ProviderFiles();
        files.CreateAll();
        File.WriteAllBytes(files.ChromiumController, []);
        File.WriteAllBytes(files.RendererController, []);
        var reader = new WindowsGpuPlacementCapabilityReader(files.Root);
        var executable = Environment.ProcessPath!;

        var d3d11 = reader.Evaluate("process", executable, "x64", GpuGraphicsApi.D3D11).Runtime;
        var d3d12 = reader.Evaluate("process", executable, "x64", GpuGraphicsApi.D3D12).Runtime;
        var vulkan = reader.Evaluate("process", executable, "x64", GpuGraphicsApi.Vulkan).Runtime;
        var mixed = reader.Evaluate("process", executable, "x64",
            GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12).Runtime;

        Assert.Equal(GpuPlacementCapabilityStates.Unknown, d3d11.State);
        Assert.Equal(WindowsExternalGpuPlacementRuntime.ProviderId, d3d11.ProviderId);
        Assert.Equal(GpuPlacementCapabilityStates.Unknown, d3d12.State);
        Assert.Equal(WindowsExternalGpuPlacementRuntime.QtProviderId, d3d12.ProviderId);
        Assert.Equal(GpuPlacementCapabilityStates.Unknown, vulkan.State);
        Assert.Equal(WindowsExternalGpuPlacementRuntime.QtVulkanProviderId, vulkan.ProviderId);
        Assert.Equal(GpuPlacementCapabilityStates.Unsupported, mixed.State);
    }

    private sealed class ProviderFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"rm-gpu-capability-{Guid.NewGuid():N}");
        public string Runtime => Path.Combine(Root, "GpuPlacementShim", WindowsGpuPlacementInjector.RuntimeProviderFileName);
        public string ChromiumController => Path.Combine(Root, "GpuPlacementShim", WindowsExternalGpuPlacementRuntime.FileName);
        public string RendererController => Path.Combine(Root, "GpuPlacementShim", WindowsExternalGpuPlacementRuntime.RendererFileName);
        public string Preparation => Path.Combine(Root, "GpuPlacementShim", "ResourceManager.GpuPlacementPreparation.exe");
        private string Bootstrap => Path.Combine(Root, "GpuPlacementShim", WindowsGpuPlacementInjector.StartupBootstrapFileName);
        private string Broker => Path.Combine(Root, WindowsIfeoGpuLaunchInterceptionRegistry.BrokerFileName);
        public string VulkanLayer => Path.Combine(Root, "GpuPlacementShim", GpuStartupProviderArtifacts.VulkanLayerFileName);
        public string VulkanManifest => Path.Combine(Root, "GpuPlacementShim", GpuStartupProviderArtifacts.VulkanManifestFileName);

        public ProviderFiles() => Directory.CreateDirectory(Path.Combine(Root, "GpuPlacementShim"));

        public void CreateAll()
        {
            File.WriteAllBytes(Runtime, []);
            File.WriteAllBytes(Bootstrap, []);
            File.WriteAllBytes(Broker, []);
            File.WriteAllBytes(VulkanLayer, []);
            File.WriteAllBytes(VulkanManifest, []);
        }

        public void Dispose()
        {
            File.Delete(Runtime);
            File.Delete(ChromiumController);
            File.Delete(RendererController);
            File.Delete(Preparation);
            File.Delete(Bootstrap);
            File.Delete(Broker);
            File.Delete(VulkanLayer);
            File.Delete(VulkanManifest);
            Directory.Delete(Path.Combine(Root, "GpuPlacementShim"));
            Directory.Delete(Root);
        }
    }

    private static SchedulingGpuAdapterObservation Adapter(
        int index,
        ulong key,
        ulong generation,
        long observedAt) => new(
            index,
            key,
            SchedulingGpuCapabilityMask.Usage | SchedulingGpuCapabilityMask.DedicatedMemory,
            SchedulingGpuMetricMask.Usage
                | SchedulingGpuMetricMask.UsedDedicatedMemory
                | SchedulingGpuMetricMask.TotalDedicatedMemory,
            SamplingObservationStatus.Current,
            SamplingObservationStatus.Current,
            10,
            1,
            2,
            generation,
            observedAt);
}
