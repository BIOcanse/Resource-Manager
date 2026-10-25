using System.Runtime.InteropServices;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Management;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.Dependencies;
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
        using var bridge = CreateBridge(modules);
        var providers = new Providers();
        var nativeQueries = new List<string>();
        var dependencies = HostReadWriteDependencies.Create(client, providers, modules.Root, bridge,
            new ManagementInventoryRevisionOwner(), source =>
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
            ["hardware-monitor.core"] = "librehardwaremonitor-provider", ["notebook-oem.fan"] = "notebook-oem-fan-provider"
        };
        foreach (var (source, component) in pairs)
        {
            Assert.Equal(DependencyAvailability.Ready, dependencies.Query(source).Availability);
            Assert.Equal(component, providers.Queries[^1]);
        }
        Assert.Equal("dependency-module-missing", dependencies.Query("amd.smn").Reason);
        File.WriteAllText(modules.AmdSmnModule, "recorded SMN module, never loaded");
        Assert.Equal(DependencyAvailability.Ready, dependencies.Query("amd.smn").Availability);
        File.Delete(modules.AmdModule);
        Assert.Equal(DependencyAvailability.Ready, dependencies.Query("amd.smn").Availability);
        Assert.Equal("dependency-module-missing", dependencies.Query("amd.smu").Reason);
    }

    [Theory]
    [InlineData(ComponentProviderStates.Missing, false, false, DependencyAvailability.Missing)]
    [InlineData(ComponentProviderStates.Installed, true, false, DependencyAvailability.Unknown)]
    [InlineData(ComponentProviderStates.RuntimeAvailable, true, true, DependencyAvailability.Ready)]
    public void UnverifiedFilesDoNotBecomeReadyRuntimeDependencies(string state, bool runtime, bool primary,
        DependencyAvailability expected)
    {
        using var client = new HttpClient();
        using var modules = new ModuleRoot();
        using var bridge = CreateBridge(modules);
        var providers = new Providers { Result = new(runtime, primary, state, "not-exposed-prose", null) };
        var dependencies = HostReadWriteDependencies.Create(client, providers, modules.Root, bridge,
            new ManagementInventoryRevisionOwner());
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
        using var bridge = CreateBridge(modules);
        var providers = new Providers();
        var revisions = new ManagementInventoryRevisionOwner();
        var launches = 0;
        var installer = new DependencyPackageInstaller((start, wait, token) =>
        { launches++; return ValueTask.CompletedTask; });
        var dependencies = HostReadWriteDependencies.Create(client, providers, modules.Root, bridge, revisions,
            installer: installer);
        Assert.Equal("dependency-module-missing", dependencies.Query("amd.smu").Reason);
        DependencyReleaseRecordStore.Write(modules.AmdReleaseRecord,
            new("0.2.10", "previous-modules.zip", new string('A', 64)));
        var package = Path.Combine(modules.Root, "selected.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(package, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "RyzenSMU.bin", "AMDFamily17.bin" })
            { using var module = new StreamWriter(archive.CreateEntry(name).Open()); module.Write("recorded module, never loaded"); }
            using var ignored = new StreamWriter(archive.CreateEntry("extras/ignored.txt").Open());
            ignored.Write("not a declared module");
        }
        try
        {
            Assert.Equal("filesExtracted", (await dependencies.InstallAsync(new("amd.smu", package), default)).Status);
            Assert.True(File.Exists(modules.AmdModule));
            Assert.True(File.Exists(modules.AmdSmnModule));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(modules.AmdModule)!, "extras", "ignored.txt")));
            Assert.False(File.Exists(modules.AmdReleaseRecord));
            Assert.Equal(0, launches);
            Assert.Equal("1", revisions.Current.ComponentRevision);
            Assert.Equal(DependencyAvailability.Ready, dependencies.Query("amd.smu").Availability);
            var unavailable = await dependencies.InstallAsync(new("nvidia.nvml", "not-opened"), default);
            Assert.Equal("dependency-installer-not-bound", unavailable.Reason);
        }
        finally { File.Delete(package); }
    }

    [Theory]
    [InlineData("nested")]
    [InlineData("incomplete")]
    public async Task InvalidSelectedModuleArchivePreservesFilesAndReceipt(string archiveShape)
    {
        using var modules = new ModuleRoot();
        using var client = new HttpClient();
        using var bridge = CreateBridge(modules);
        var revisions = new ManagementInventoryRevisionOwner();
        var dependencies = HostReadWriteDependencies.Create(client, new Providers(), modules.Root, bridge, revisions);
        var componentDirectory = Path.GetDirectoryName(modules.AmdModule)!;
        var previousModule = "previous module bytes";
        File.WriteAllText(modules.AmdModule, previousModule);
        DependencyReleaseRecordStore.Write(modules.AmdReleaseRecord,
            new("0.2.10", "previous-modules.zip", new string('B', 64)));
        var previousRecord = File.ReadAllBytes(modules.AmdReleaseRecord);
        var package = Path.Combine(modules.Root, archiveShape + ".zip");
        using (var archive = System.IO.Compression.ZipFile.Open(package, System.IO.Compression.ZipArchiveMode.Create))
        {
            if (archiveShape == "nested")
            {
                using (var smu = new StreamWriter(archive.CreateEntry("release/RyzenSMU.bin").Open()))
                    smu.Write("wrong root");
                using (var smn = new StreamWriter(archive.CreateEntry("release/AMDFamily17.bin").Open()))
                    smn.Write("wrong root");
            }
            else
            {
                using var smu = new StreamWriter(archive.CreateEntry("RyzenSMU.bin").Open());
                smu.Write("one declared file only");
            }
        }

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await dependencies.InstallAsync(new("amd.smu", package), default));
            Assert.Equal(previousModule, File.ReadAllText(modules.AmdModule));
            Assert.Equal(previousRecord, File.ReadAllBytes(modules.AmdReleaseRecord));
            Assert.Equal("0", revisions.Current.ComponentRevision);
            Assert.True(Directory.Exists(componentDirectory));
        }
        finally { File.Delete(package); }
    }

    [Fact]
    public async Task CachedInstallerMustMatchItsDownloadSidecarBeforeLaunch()
    {
        using var modules = new ModuleRoot();
        using var client = new HttpClient();
        using var bridge = CreateBridge(modules);
        var launches = 0;
        var installer = new DependencyPackageInstaller((start, wait, token) =>
        { launches++; return ValueTask.CompletedTask; });
        var dependencies = HostReadWriteDependencies.Create(client, new Providers(), modules.Root, bridge,
            new ManagementInventoryRevisionOwner(), installer: installer);
        Directory.CreateDirectory(Path.GetDirectoryName(modules.CachedInstaller)!);
        File.WriteAllText(modules.CachedInstaller, "modified selected installer");
        DependencyReleaseRecordStore.Write(DependencyReleaseRecordStore.DownloadPath(modules.CachedInstaller),
            new("2.2.0", "PawnIO_setup.exe", new string('C', 64)));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await dependencies.InstallAsync(new("amd.smu", modules.CachedInstaller), default));
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task SelectedPawnIoExecutableUsesTheOriginalElevationAndComponentDirectory()
    {
        using var modules = new ModuleRoot();
        using var client = new HttpClient();
        using var bridge = CreateBridge(modules);
        System.Diagnostics.ProcessStartInfo? recorded = null;
        var dependencies = HostReadWriteDependencies.Create(client, new Providers(), modules.Root, bridge,
            new ManagementInventoryRevisionOwner(),
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
        internal string AmdSmnModule => Path.Combine(Root, "Dependencies", "amd-smu-pawnio-provider", "AMDFamily17.bin");
        internal string AmdReleaseRecord => DependencyReleaseRecordStore.InstalledPath(Path.GetDirectoryName(AmdModule)!);
        internal string IntelModule => Path.Combine(Root, "Dependencies", "intel-msr-pawnio-provider", "IntelMSR.bin");
        internal string CachedInstaller => Path.Combine(Root, "Misc", "DependencyInstallers", "amd-smu-pawnio-provider", "PawnIO_setup.exe");
        internal ModuleRoot()
        {
            foreach (var path in new[] { AmdModule, IntelModule })
            { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "recorded, never loaded"); }
        }
        public void Dispose()
        {
            File.Delete(AmdSmnModule);
            File.Delete(AmdReleaseRecord);
            foreach (var path in new[] { AmdModule, IntelModule })
            { File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
            if (Directory.Exists(Path.GetDirectoryName(CachedInstaller)!))
            {
                File.Delete(DependencyReleaseRecordStore.DownloadPath(CachedInstaller));
                File.Delete(CachedInstaller);
                Directory.Delete(Path.GetDirectoryName(CachedInstaller)!);
            }
            if (Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(CachedInstaller)!)!))
                Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(CachedInstaller)!)!);
            if (Directory.Exists(Path.Combine(Root, "Misc"))) Directory.Delete(Path.Combine(Root, "Misc"));
            Directory.Delete(Path.Combine(Root, "Dependencies")); Directory.Delete(Root);
        }
    }

    private static HardwareBridgeClient CreateBridge(ModuleRoot modules)
        => new(new TestEnvironment(modules.Root), static _ => null);

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Resource Manager tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
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
        internal ComponentProviderRuntimeProbeResult Result { get; set; } = new(true, true, ComponentProviderStates.RuntimeAvailable, "recorded", null);
        public ComponentProviderRuntimeProbeResult Probe(string component) { Queries.Add(component); return Result; }
    }
}
