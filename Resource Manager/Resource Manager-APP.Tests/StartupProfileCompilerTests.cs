using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class StartupProfileCompilerTests
{
    [Fact]
    public void Compile_DefaultsToCompleteProduct()
    {
        var compiled = StartupProfileCompiler.Compile(["--no-native-ui"]);

        Assert.Same(StartupCapabilitySet.Full, compiled);
        Assert.True(compiled.Allows(StartupCapability.MutablePersistence));
        Assert.True(compiled.Allows(StartupCapability.OptimizationRuntime));
    }

    [Theory]
    [InlineData("--startup-profile=full")]
    [InlineData("--startup-profile", "full")]
    public void Compile_AcceptsExplicitFullProfile(params string[] args)
    {
        var compiled = StartupProfileCompiler.Compile(args);

        Assert.Same(StartupCapabilitySet.Full, compiled);
        Assert.True(compiled.Allows(StartupCapability.MutablePersistence));
        Assert.True(compiled.Allows(
            StartupCapability.GpuLaunchInterceptionReconciliation));
    }

    [Fact]
    public void Compile_RejectsUnknownAndDuplicateProfiles()
    {
        Assert.Throws<ArgumentException>(() =>
            StartupProfileCompiler.Compile(["--startup-profile=unknown"]));
        Assert.Throws<ArgumentException>(() =>
            StartupProfileCompiler.Compile(
                ["--startup-profile=full", "--startup-profile=normal-read-only"]));
    }

    [Fact]
    public void ProductDefaultsDoNotRequestDisabledStartupCapabilities()
    {
        var settings = AppSettingsDefaults.Create();

        Assert.Equal(AppOptimizationModes.Normal, settings.Performance.OptimizationMode);
        Assert.True(settings.Performance.PreciseGpuPlacementEnabled);
        Assert.False(settings.PublicService.Enabled);
        Assert.False(settings.PublicService.FileIndexEnabled);
        Assert.False(settings.PublicService.DatabaseServiceEnabled);
        Assert.False(settings.PublicService.AiModelCatalogEnabled);
        Assert.False(settings.AiModelService.AutoStartEnabled);
    }
}
