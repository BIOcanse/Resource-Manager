using System.Runtime.InteropServices;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;

namespace Resource_Manager_APP.Tests;

public sealed class HostReadWriteDependencyTests
{
    [Fact]
    public void DependencyConstructionIsIdleAndSourceQueriesUseTheirExactProvider()
    {
        using var client = new HttpClient();
        using var modules = new ModuleRoot();
        var providers = new Providers();
        var nativeQueries = new List<string>();
        var dependencies = HostReadWriteDependencies.Create(client, providers, modules.Root, source =>
        { nativeQueries.Add(source); return new(source, DependencyAvailability.Missing, "recorded-missing"); });
        Assert.Empty(nativeQueries);
        Assert.Empty(providers.Queries);
        Assert.Equal(DependencyAvailability.Missing, dependencies.Query("nvidia.nvml").Availability);
        Assert.Equal(DependencyAvailability.Missing, dependencies.Query("intel.igcl").Availability);
        Assert.Equal(new[] { "nvidia.nvml", "intel.igcl" }, nativeQueries);
        Assert.Empty(providers.Queries);
        var pairs = new Dictionary<string, string>
        {
            ["nvidia.nvapi"] = "nvidia-nvapi-provider", ["amd.adlx"] = "amd-adlx-provider",
            ["amd.smu"] = "amd-smu-pawnio-provider", ["intel.msr"] = "intel-msr-pawnio-provider",
            ["hardware-monitor.wmi"] = "librehardwaremonitor-provider", ["notebook-oem.fan"] = "notebook-oem-fan-provider"
        };
        foreach (var (source, component) in pairs)
        {
            Assert.Equal(DependencyAvailability.Ready, dependencies.Query(source).Availability);
            Assert.Equal(component, providers.Queries[^1]);
        }
    }

    [Theory]
    [InlineData("Missing", false, false, DependencyAvailability.Missing)]
    [InlineData("Installed", true, false, DependencyAvailability.Unknown)]
    [InlineData("RuntimeAvailable", true, true, DependencyAvailability.Ready)]
    public void UnverifiedFilesDoNotBecomeReadyRuntimeDependencies(string state, bool runtime, bool primary,
        DependencyAvailability expected)
    {
        using var client = new HttpClient();
        using var modules = new ModuleRoot();
        var providers = new Providers { Result = new(runtime, primary, state, "not-exposed-prose", null) };
        var dependencies = HostReadWriteDependencies.Create(client, providers, modules.Root);
        var status = dependencies.Query("amd.smu");
        Assert.Equal(expected, status.Availability);
        Assert.DoesNotContain("not-exposed-prose", status.Reason ?? "");
    }

    [Fact]
    public async Task SelectedModuleInstallationDoesNotInstallTheDriverOrChooseASource()
    {
        using var modules = new ModuleRoot();
        File.Delete(modules.AmdModule);
        using var client = new HttpClient();
        var providers = new Providers();
        var launches = 0;
        var installer = new DependencyPackageInstaller((start, wait, token) =>
        { launches++; return ValueTask.CompletedTask; });
        var dependencies = HostReadWriteDependencies.Create(client, providers, modules.Root, installer: installer);
        Assert.Equal("dependency-module-missing", dependencies.Query("amd.smu").Reason);
        var package = Path.Combine(modules.Root, "selected.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(package, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var module = new StreamWriter(archive.CreateEntry("release/RyzenSMU.bin").Open());
            module.Write("recorded module, never loaded");
        }
        try
        {
            Assert.Equal("filesExtracted", (await dependencies.InstallAsync(new("amd.smu", package), default)).Status);
            Assert.True(File.Exists(modules.AmdModule));
            Assert.Equal(0, launches);
            Assert.Equal(DependencyAvailability.Ready, dependencies.Query("amd.smu").Availability);
            var unavailable = await dependencies.InstallAsync(new("nvidia.nvml", "not-opened"), default);
            Assert.Equal("dependency-installer-not-bound", unavailable.Reason);
        }
        finally { File.Delete(package); }
    }

    [Fact]
    public async Task SelectedPawnIoExecutableUsesTheOriginalElevationAndComponentDirectory()
    {
        using var modules = new ModuleRoot();
        using var client = new HttpClient();
        System.Diagnostics.ProcessStartInfo? recorded = null;
        var dependencies = HostReadWriteDependencies.Create(client, new Providers(), modules.Root,
            installer: new((start, wait, token) => { recorded = start; Assert.False(wait); return ValueTask.CompletedTask; }));
        var package = Path.Combine(modules.Root, "chosen.exe");
        File.WriteAllText(package, "recorded installer, never executed");
        try
        {
            Assert.Equal("installerStarted", (await dependencies.InstallAsync(new("intel.msr", package), default)).Status);
            Assert.Equal(package, recorded!.FileName);
            Assert.Equal("runas", recorded.Verb);
            Assert.Equal(Path.GetDirectoryName(modules.IntelModule), recorded.WorkingDirectory);
            Assert.Empty(recorded.Arguments);
        }
        finally { File.Delete(package); }
    }

    private sealed class ModuleRoot : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "rm-read-write-modules-" + Guid.NewGuid().ToString("N"));
        internal string AmdModule => Path.Combine(Root, "Dependencies", "amd-smu-pawnio-provider", "RyzenSMU.bin");
        internal string IntelModule => Path.Combine(Root, "Dependencies", "intel-msr-pawnio-provider", "IntelMSR.bin");
        internal ModuleRoot()
        {
            foreach (var path in new[] { AmdModule, IntelModule })
            { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "recorded, never loaded"); }
        }
        public void Dispose()
        {
            foreach (var path in new[] { AmdModule, IntelModule })
            { File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
            Directory.Delete(Path.Combine(Root, "Dependencies")); Directory.Delete(Root);
        }
    }

    [Fact]
    public void NativeLibraryQueryChecksActualExportsWithoutOpeningAHardwareSession()
    {
        var ready = ReadWriteNativeDependencyProbe.QueryLibrary("recorded", "kernel32.dll",
            ["GetCurrentProcessId"], DllImportSearchPath.System32);
        Assert.Equal(DependencyAvailability.Ready, ready.Availability);
        var incomplete = ReadWriteNativeDependencyProbe.QueryLibrary("recorded", "kernel32.dll",
            ["ResourceManager_Missing_Test_Export"], DllImportSearchPath.System32);
        Assert.Equal(DependencyAvailability.Unknown, incomplete.Availability);
        Assert.Equal("dependency-exports-missing", incomplete.Reason);
        var missing = ReadWriteNativeDependencyProbe.QueryLibrary("recorded", "ResourceManager_Missing_Test_Library.dll",
            ["unused"], DllImportSearchPath.System32);
        Assert.Equal(DependencyAvailability.Missing, missing.Availability);
    }

    private sealed class Providers : IProviderRuntimeProbe
    {
        internal List<string> Queries { get; } = [];
        internal ComponentProviderRuntimeProbeResult Result { get; set; } = new(true, true, "RuntimeAvailable", "recorded", null);
        public ComponentProviderRuntimeProbeResult Probe(string component) { Queries.Add(component); return Result; }
    }
}
