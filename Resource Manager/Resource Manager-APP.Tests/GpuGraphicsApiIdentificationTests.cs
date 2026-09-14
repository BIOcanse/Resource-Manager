using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class GpuGraphicsApiIdentificationTests
{
    [Theory]
    [InlineData("d3d11.dll", GpuGraphicsApi.D3D11, GpuPlacementProviderIds.D3dDeviceCreateShim)]
    [InlineData("D3D12.DLL", GpuGraphicsApi.D3D12, GpuPlacementProviderIds.D3dDeviceCreateShim)]
    [InlineData("d3d11.dll,d3d12.dll", GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12, GpuPlacementProviderIds.D3dDeviceCreateShim)]
    [InlineData("vulkan-1.dll", GpuGraphicsApi.Vulkan, GpuPlacementProviderIds.VulkanExplicitLayer)]
    [InlineData("opengl32.dll", GpuGraphicsApi.OpenGL, null)]
    [InlineData("d3d9.dll", GpuGraphicsApi.D3D9, null)]
    public void ModuleCandidatesMapToTheExistingRoute(string modules, GpuGraphicsApi expected, string? provider)
    {
        var api = new WindowsGpuGraphicsApiDetector((_, _) => modules.Split(',')).Detect(42, "target.exe");
        Assert.Equal(expected, api);
        Assert.Equal(provider, GpuGraphicsApiRoutes.StartupProvider(api));
        Assert.Equal(expected == GpuGraphicsApi.Vulkan ? GpuPlacementProviderIds.VulkanExplicitLayer
            : GpuPlacementProviderIds.D3dDeviceCreateShim, GpuGraphicsApiRoutes.RuntimeProvider(api));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("d3d12core.dll")]
    [InlineData("d3d12.dll,vulkan-1.dll")]
    [InlineData("vulkan-1.dll,opengl32.dll")]
    [InlineData("ResourceManager.GpuPlacementShim.dll,d3d11.dll,d3d12.dll")]
    [InlineData("ResourceManager.VulkanPlacementLayer.dll,vulkan-1.dll")]
    public void MissingAmbiguousAndShimOwnedImportsAreNotSuccessfulDetection(string? modules)
        => Assert.Null(new WindowsGpuGraphicsApiDetector((_, _) => modules?.Split(',')).Detect(42, "target.exe"));

    [Fact]
    public void InvalidPidDoesNotReadModules()
        => Assert.Null(new WindowsGpuGraphicsApiDetector((_, _) => throw new Exception("Unexpected read")).Detect(0, "target.exe"));

    [Theory]
    [InlineData(GpuGraphicsApi.D3D11)]
    [InlineData(GpuGraphicsApi.D3D9)]
    [InlineData(GpuGraphicsApi.D3D12)]
    [InlineData(GpuGraphicsApi.Vulkan)]
    [InlineData(GpuGraphicsApi.OpenGL)]
    public async Task FirstIdentificationSurvivesReloadWithoutAnotherDetection(GpuGraphicsApi api)
    {
        using var files = new Fixture();
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        Assert.Empty((await store.GetSoftwareHistoryAsync("software", "Software", default)).Processes);
        Assert.Null(Assert.Single((await store.ObserveAsync(files.Request(), default)).Processes).GraphicsApi);
        var first = await store.SaveFirstGraphicsApiAsync("software", "Software", files.Instance(), api, default);
        Assert.Equal(api, Assert.Single(first.Processes).GraphicsApi);
        var bytes = File.ReadAllBytes(files.HistoryPath);
        var reloaded = new JsonGpuPlacementProcessHistoryStore(files);
        var after = await reloaded.ObserveAsync(files.Request(43), default);
        Assert.Equal(api, Assert.Single(after.Processes).GraphicsApi);
        Assert.Equal(43, Assert.Single(after.Processes).LastProcessId);
        Assert.Equal(bytes, File.ReadAllBytes(files.HistoryPath));
        var winner = await reloaded.SaveFirstGraphicsApiAsync("software", "Software", files.Instance(43), GpuGraphicsApi.Vulkan, default);
        Assert.Equal(api, Assert.Single(winner.Processes).GraphicsApi);
        Assert.Equal(bytes, File.ReadAllBytes(files.HistoryPath));
    }

    [Fact]
    public async Task ConcurrentSubmissionsReturnTheFirstCommittedResult()
    {
        using var files = new Fixture();
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        var observations = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            store.SaveFirstGraphicsApiAsync("software", "Software", files.Instance(42 + i),
                i % 2 == 0 ? GpuGraphicsApi.Vulkan : GpuGraphicsApi.D3D11, default)));
        var saved = Assert.Single((await new JsonGpuPlacementProcessHistoryStore(files)
            .GetSoftwareHistoryAsync("software", "Software", default)).Processes).GraphicsApi;
        Assert.NotNull(saved);
        Assert.All(observations, result => Assert.Equal(saved, Assert.Single(result.Processes).GraphicsApi));
    }

    [Fact]
    public async Task NoResultIsNotPersistedAsAnApiAndALaterObservationCanIdentify()
    {
        using var files = new Fixture();
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        Assert.Null(Assert.Single((await store.ObserveAsync(files.Request(), default)).Processes).GraphicsApi);
        Assert.Equal(GpuGraphicsApi.D3D12, Assert.Single((await store.SaveFirstGraphicsApiAsync(
            "software", "Software", files.Instance(), GpuGraphicsApi.D3D12, default)).Processes).GraphicsApi);
        Assert.Equal(GpuGraphicsApi.D3D12, Assert.Single((await new JsonGpuPlacementProcessHistoryStore(files)
            .GetSoftwareHistoryAsync("software", "Software", default)).Processes).GraphicsApi);
    }

    [Fact]
    public async Task SoftwareAndExecutablePathsDoNotShareAnIdentification()
    {
        using var files = new Fixture();
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        await store.SaveFirstGraphicsApiAsync("software", "Software", files.Instance(), GpuGraphicsApi.D3D11, default);
        var other = await store.SaveFirstGraphicsApiAsync("other", "Other", files.Instance(), GpuGraphicsApi.Vulkan, default);
        Assert.Equal(GpuGraphicsApi.Vulkan, Assert.Single(other.Processes).GraphicsApi);
        var anotherPath = files.Request() with
        {
            Processes = [files.Input() with { ExecutablePath = Path.Combine(files.Root, "other.exe") }]
        };
        var history = await store.ObserveAsync(anotherPath, default);
        Assert.Equal(2, history.Processes.Count);
        Assert.Equal(GpuGraphicsApi.D3D11, history.Processes.Single(p => p.ExecutablePath == files.Target).GraphicsApi);
        Assert.Null(history.Processes.Single(p => p.ExecutablePath != files.Target).GraphicsApi);
    }

    [Fact]
    public async Task MetadataGroupsInstancesWithoutIdentifyingThenAcceptsAnExplicitResult()
    {
        using var files = new Fixture();
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        var request = files.Request() with { Processes = [files.Input(42), files.Input(42), files.Input(43), files.Input(44)] };
        var process = Assert.Single((await store.ObserveAsync(request, default)).Processes);
        Assert.Null(process.GraphicsApi);
        Assert.Equal(1, process.ObservationCount);
        await store.SaveFirstGraphicsApiAsync("software", "Software", files.Instance(43), GpuGraphicsApi.D3D12, default);
        var reloaded = new JsonGpuPlacementProcessHistoryStore(files);
        Assert.Equal(GpuGraphicsApi.D3D12, Assert.Single((await reloaded.ObserveAsync(request, default)).Processes).GraphicsApi);
    }

    [Theory]
    [InlineData(GpuGraphicsApi.D3D9)]
    [InlineData(GpuGraphicsApi.D3D11)]
    [InlineData(GpuGraphicsApi.D3D12)]
    [InlineData(GpuGraphicsApi.OpenGL)]
    public async Task RuntimeSelectionKeepsAllCurrentInstancesOfTheSameExecutable(GpuGraphicsApi api)
    {
        using var files = new Fixture();
        var inputs = new[] { files.Input(42), files.Input(43) };
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        await store.SaveFirstGraphicsApiAsync("software", "Software", files.Instance(), api, default);
        var history = await store.ObserveAsync(files.Request() with { Processes = inputs }, default);
        Assert.Single(history.Processes);
        var selected = WindowsRunningGpuPlacementActionService.SelectRuntimeGraphicsApis(inputs, history);
        Assert.Equal(new[] { 42, 43 }, selected.Keys.Order());
        Assert.All(selected.Values, value => Assert.Equal(api, value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(GpuGraphicsApi.Vulkan)]
    [InlineData(GpuGraphicsApi.OpenGL)]
    public async Task RuntimeSelectionDoesNotRouteAReusedPidUsingAnotherExecutable(GpuGraphicsApi? currentApi)
    {
        using var files = new Fixture();
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        await store.SaveFirstGraphicsApiAsync("software", "Software", files.Instance(), GpuGraphicsApi.D3D11, default);
        var current = files.Input(42) with { ExecutablePath = Path.Combine(files.Root, "different.exe") };
        if (currentApi is { } api)
            await store.SaveFirstGraphicsApiAsync("software", "Software",
                files.Instance() with { ExecutablePath = current.ExecutablePath! }, api, default);
        var history = await store.ObserveAsync(files.Request() with { Processes = [current] }, default);
        Assert.Equal(2, history.Processes.Count);
        Assert.All(history.Processes, process => Assert.Equal(42, process.LastProcessId));
        var selected = WindowsRunningGpuPlacementActionService.SelectRuntimeGraphicsApis([current], history);
        if (currentApi is GpuGraphicsApi.Vulkan or GpuGraphicsApi.OpenGL)
            Assert.Equal(currentApi, Assert.Single(selected).Value);
        else
            Assert.Empty(selected);
    }

    [Fact]
    public async Task KnownApisAreNotEvictedByTheRecentUnknownProcessLimit()
    {
        using var files = new Fixture();
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        var request = files.Request() with
        {
            Processes = Enumerable.Range(0, 70).Select(i => files.Input() with
                { ExecutablePath = Path.Combine(files.Root, $"target{i}.exe") }).ToArray()
        };
        foreach (var input in request.Processes)
            await store.SaveFirstGraphicsApiAsync("software", "Software",
                files.Instance() with { ExecutablePath = input.ExecutablePath! }, GpuGraphicsApi.D3D12, default);
        var next = new JsonGpuPlacementProcessHistoryStore(files);
        Assert.Equal(70, (await next.ObserveAsync(request, default)).Processes.Count);
    }

    [Fact]
    public async Task CorruptSoftwareInformationIsNotReplacedByAnEmptyHistory()
    {
        using var files = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(files.HistoryPath)!);
        await File.WriteAllTextAsync(files.HistoryPath, "{broken");
        var store = new JsonGpuPlacementProcessHistoryStore(files);
        await Assert.ThrowsAsync<JsonException>(() => store.ObserveAsync(files.Request(), default));
        await Assert.ThrowsAsync<JsonException>(() => store.SaveFirstGraphicsApiAsync(
            "software", "Software", files.Instance(), GpuGraphicsApi.D3D11, default));
        Assert.Equal("{broken", await File.ReadAllTextAsync(files.HistoryPath));
    }

    [Theory]
    [InlineData(GpuGraphicsApi.D3D11, GpuPlacementProviderIds.D3dDeviceCreateShim)]
    [InlineData(GpuGraphicsApi.D3D12, GpuPlacementProviderIds.D3dDeviceCreateShim)]
    [InlineData(GpuGraphicsApi.Vulkan, GpuPlacementProviderIds.VulkanExplicitLayer)]
    [InlineData(GpuGraphicsApi.D3D9, null)]
    public async Task ActualStartupResolverUsesTheSavedApiAndOnePermission(GpuGraphicsApi api, string? expectedProvider)
    {
        using var files = new Fixture();
        var history = new JsonGpuPlacementProcessHistoryStore(files);
        var process = Assert.Single((await history.SaveFirstGraphicsApiAsync(
            "software", "Software", files.Instance(), api, default)).Processes);
        var policies = new JsonGpuPlacementPolicyStore(files);
        var software = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software") with
        {
            StartupTargetGpu = GpuPlacementTargets.HighPerformanceGpu,
            AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim]
        };
        await policies.SaveSoftwarePolicyAsync(software, default);
        await policies.SaveProcessPolicyAsync(GpuPlacementPolicyDefaults.CreateProcessPolicy("software", process) with
            { StartupInterceptionEnabled = true }, default);
        var plan = new Plans(software);
        var resolver = new GpuStartupPlacementResolver(policies, plan, new D3d11ProxyShimRuntime(files),
            new JsonGpuPlacementProcessHistoryStore(files));
        var result = await resolver.ResolveAsync(new(files.Target), default);
        if (expectedProvider is null)
        {
            Assert.Equal("graphics-api-unsupported", result.Status);
            Assert.NotEqual(GpuStartupPlacementDecisionKinds.Inject, result.Decision);
            Assert.Empty(result.StartupProviders);
            Assert.Null(result.PolicyPath);
            return;
        }
        Assert.Equal(GpuStartupPlacementDecisionKinds.Inject, result.Decision);
        Assert.Equal(expectedProvider, Assert.Single(result.StartupProviders));
        Assert.True(File.Exists(result.PolicyPath));
        var noRecord = new Fixture();
        try
        {
            var withoutHistory = new GpuStartupPlacementResolver(policies, plan, new D3d11ProxyShimRuntime(files),
                new JsonGpuPlacementProcessHistoryStore(noRecord));
            var unknown = await withoutHistory.ResolveAsync(new(files.Target), default);
            Assert.Equal("graphics-api-unidentified", unknown.Status);
            Assert.Empty(unknown.StartupProviders);
        }
        finally { noRecord.Dispose(); }
    }

    internal sealed class Plans(GpuPlacementSoftwarePolicy software) : IRuntimePlanProvider
    {
        public CompiledRuntimePlan Current { get; private set; } = CompiledRuntimePlan.Default with
        {
            GpuPlacement = new(true, new Dictionary<string, ResolvedGpuPlacementPolicy>
                { [software.SoftwareId] = ResolvedGpuPlacementPolicy.FromSoftware(software) },
                new Dictionary<string, ResolvedGpuPlacementPolicy>())
        };
        public RuntimePlanPublicationLease AcquirePublicationLease() => RuntimePlanPublicationLease.CreateUntracked(Current, 1);
        public RuntimePlanPublicationResult Publish(CompiledRuntimePlan plan) { Current = plan; return new(plan, 1, []); }
    }

    internal sealed class Fixture : IHostEnvironment, IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"gpu-api-info-{Guid.NewGuid():N}");
        public string Target => Path.Combine(Root, "target.exe");
        public string HistoryPath => Path.Combine(Root, "UserData", "SoftwareProfiles", "gpu-placement-process-history.local.json");
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "GpuApiTests";
        public string ContentRootPath { get => Root; set => throw new NotSupportedException(); }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public Fixture() { Directory.CreateDirectory(Root); File.WriteAllBytes(Target, []); }
        public GpuPlacementObservedProcessInput Input(int pid = 42) => new("target", Target, "x64", pid, ["fixture"]);
        public GpuPlacementProcessInstance Instance(int pid = 42) => new(pid, 1, "target", Target);
        public GpuPlacementProcessObservationRequest Request(int pid = 42) => new("software", "Software", [Input(pid)]);
        public void Dispose()
        {
            var pending = new Queue<string>();
            var directories = new List<string>();
            pending.Enqueue(Root);
            while (pending.TryDequeue(out var directory))
            {
                Assert.True(directories.Count < 40);
                Assert.False(File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint));
                directories.Add(directory);
                foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
                foreach (var child in Directory.EnumerateDirectories(directory)) pending.Enqueue(child);
            }
            for (var i = directories.Count - 1; i >= 0; i--) Directory.Delete(directories[i]);
        }
    }
}
