using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.Shared.Packages;

namespace Resource_Manager_APP.Tests;

public sealed class PerformanceOverlaySettingsTests
{
    [Fact]
    public async Task GenericSelectionPersistsWithoutRenderingPolicy()
    {
        using var files = new StartupModuleTestCatalog();
        var store = new JsonStartupModuleSettingsStore(new BackendStateItems().Authority, files, files.Catalog);
        Assert.Empty((await store.GetAsync(default)).Software);
        await store.SaveAsync(new("software", ["fixture-module"]), default);
        var saved = Assert.Single((await new JsonStartupModuleSettingsStore(new BackendStateItems().Authority, files, files.Catalog).GetAsync(default)).Software);
        Assert.Equal(["fixture-module"], saved.ModuleIds);
        var path = Path.Combine(files.Root, "UserData", "SoftwareProfiles", "startup-modules.local.json");
        var bytes = await File.ReadAllTextAsync(path);
        foreach (var presentation in new[] { "enabled", "mode", "metrics", "fontSizePx", "overlay" })
            Assert.DoesNotContain(presentation, bytes, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new("software", ["not-admitted"]), default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new("software", ["fixture-module", "fixture-module"]), default));
    }

    [Fact]
    public void ExactManifestImagesAreProtectedRegardlessOfBasename()
    {
        using var files = new StartupModuleTestCatalog();
        var image = Path.Combine(files.Root, "Bin", "Tools", "renamed-client.exe");
        File.WriteAllBytes(image, [4, 5, 6]);
        files.WriteRelease("Bin/Tools/renamed-client.exe");
        var catalog = PackageRuntimeCatalog.Load(files.Root);
        Assert.True(catalog.IsInfrastructureImage(image));
        Assert.True(catalog.MayBeInfrastructureImageName("RENAMED-CLIENT"));
        Assert.False(catalog.MayBeInfrastructureImageName("unlisted-client"));
        Assert.False(catalog.IsInfrastructureImage(Path.Combine(files.Root, "elsewhere", "renamed-client.exe")));
        Assert.Single(catalog.StartupModules);
        File.AppendAllText(files.Image, "corrupt");
        Assert.Throws<InvalidDataException>(() => PackageRuntimeCatalog.Load(files.Root));
    }

    [Fact]
    public void CapabilityAndIfeoEligibilityConsumeTheSameExactImageAdmission()
    {
        using var files = new StartupModuleTestCatalog();
        var image = Path.Combine(files.Root, "Bin", "Tools", "renamed.exe");
        var outside = Path.Combine(Directory.CreateDirectory(Path.Combine(files.Root, "Outside")).FullName, "renamed.exe");
        File.Copy(Environment.ProcessPath!, image);
        File.Copy(Environment.ProcessPath!, outside);
        var broker = Path.Combine(files.Root, "broker.exe");
        File.WriteAllBytes(broker, []);
        files.WriteRelease("Bin/Tools/renamed.exe");
        var catalog = PackageRuntimeCatalog.Load(files.Root);
        var reader = new WindowsGpuPlacementCapabilityReader(files.Root, catalog);
        Assert.Equal("self-process-blocked", reader.Evaluate("inside", image, "x64", null).Startup.ReasonCode);
        Assert.NotEqual("self-process-blocked", reader.Evaluate("outside", outside, "x64", null).Startup.ReasonCode);
        var registry = new WindowsIfeoGpuLaunchInterceptionRegistry(broker, new NeverReadRegistry(), catalog);
        Assert.False(registry.EvaluateEligibility(image, broker).Eligible);
        Assert.False(registry.EvaluateEligibility(broker, broker).Eligible);
        Assert.True(registry.EvaluateEligibility(outside, broker).Eligible);
    }

    private sealed class NeverReadRegistry : IIfeoOwnedRuleStore
    {
        public IReadOnlyList<string> EnumerateImageNames(Microsoft.Win32.RegistryView view) => throw new InvalidOperationException();
        public IfeoImageSnapshot? ReadImage(Microsoft.Win32.RegistryView view, string imageName) => throw new InvalidOperationException();
        public int DeleteOwnedRules(Microsoft.Win32.RegistryView view, string imageName, IReadOnlyList<IfeoOwnedRuleCandidate> candidates, bool settleParentOwnership)
            => throw new InvalidOperationException();
    }

    [Fact]
    public void MissingManifestProtectsOnlyActualHostAndFinalImageManifestIsSupported()
    {
        using var files = new StartupModuleTestCatalog();
        File.Move(Path.Combine(files.Root, "release-manifest.json"), Path.Combine(files.Root, "resource-manager-final-image.manifest.json"));
        Assert.Single(PackageRuntimeCatalog.Load(files.Root).StartupModules);
        File.Delete(Path.Combine(files.Root, "resource-manager-final-image.manifest.json"));
        var catalog = PackageRuntimeCatalog.Load(files.Root, files.Image);
        Assert.Empty(catalog.StartupModules);
        Assert.True(catalog.IsInfrastructureImage(files.Image));
    }
}
