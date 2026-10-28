using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.Dependencies;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed partial class ControlHelperPayloadTests
{
    [Theory]
    [InlineData("hardware-bridge")]
    public async Task PayloadAcquisitionExtractsCompleteReleaseIncludingLegacyRuntime(string id)
    {
        var definition = Assert.Single(OptionalDependencyCatalog.Definitions, row => row.Id == id);
        Assert.Single(ComponentCatalog.Definitions, row => row.Id == id);
        Assert.NotNull(definition.ReleaseSource);
        var source = Assert.IsType<ResourceManager.App.Domain.Dependencies.DependencyPayloadSource>(definition.PayloadSource);
        Assert.True(source.IncludeArchiveContents);
        var files = source.FileNames.Concat(["LegacyRuntime.dll", "runtimes/win-x64/native/transport.dll"]);
        var payload = BuildArchive(files);
        var root = Path.Combine(Path.GetTempPath(), "rm-helper-payload-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new ArchiveHandler(payload);
            using var client = new HttpClient(handler);
            var environment = new TestEnvironment(root);
            using var bridge = new HardwareBridgeClient(environment);
            var acquisition = new DependencyPayloadAcquisition(client, environment, bridge);
            var result = await acquisition.InstallAsync(definition, CancellationToken.None);
            Assert.True(result.Satisfied, result.Message);
            foreach (var name in files)
                Assert.True(File.Exists(Path.Combine(root, "Dependencies", id, name)), name);
            await acquisition.InstallAsync(definition, CancellationToken.None);
            Assert.Equal(1, handler.Requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("hardware-bridge")]
    public async Task ComponentInstallationDownloadsAndExtractsTheCompleteArchiveWithoutVerification(string id)
    {
        var definition = OptionalDependencyCatalog.Find(id)!;
        var files = definition.PayloadSource!.FileNames.Concat(["LegacyRuntime.dll", "runtimes/win-x64/native/transport.dll"]).ToArray();
        var root = Path.Combine(Path.GetTempPath(), "rm-helper-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new ArchiveHandler(BuildArchive(files));
            using var client = new HttpClient(handler);
            var environment = new TestEnvironment(root);
            using var installBridge = new HardwareBridgeClient(environment);
            var dependency = new OptionalDependencyManager(client, environment, new FileSystemChangeTracker(), new EmptyInventory(),
                new DependencyInstallerSourceResolver(client, NullLogger<DependencyInstallerSourceResolver>.Instance), installBridge);
            var manager = new ComponentManager(dependency, new Sampler(), new ProviderProbe(), new UnexpectedPayload());
            Assert.True((await manager.GetStatusAsync(id, default))!.CanInstall);
            var result = await manager.InstallAsync(id, true, null, default);
            Assert.Equal("filesExtracted", result.State);
            Assert.True(result.Status!.Installed);
            foreach (var file in files) Assert.True(File.Exists(Path.Combine(root, "Dependencies", id, file)), file);
            using var bridge = new HardwareBridgeClient(environment);
            Assert.True(bridge.IsInstalled);
            await manager.InstallAsync(id, true, null, default);
            Assert.Equal(1, handler.Requests);
            Assert.EndsWith(definition.ReleaseSource!.VerifiedAssetName, handler.LastUri!.AbsolutePath);
        }
        finally { DeleteOwnedPackage(root, id, files, definition.InstallerFileName); }
    }

    [Theory]
    [InlineData("amd-smu-pawnio-provider", false)]
    [InlineData("intel-msr-pawnio-provider", false)]
    [InlineData("amd-smu-pawnio-provider", true)]
    [InlineData("intel-msr-pawnio-provider", true)]
    public async Task ComponentVerificationAcquiresDeclaredModulesAndReportsDownloadFailure(string id, bool fail)
    {
        var definition = OptionalDependencyCatalog.Find(id)!;
        var root = Path.Combine(Path.GetTempPath(), "rm-module-verify-" + Guid.NewGuid().ToString("N"));
        var files = definition.PayloadSource!.FileNames.ToArray();
        try
        {
            using var handler = new ArchiveHandler(BuildArchive(files)) { Fail = fail };
            using var client = new HttpClient(handler);
            var environment = new TestEnvironment(root);
            using var bridge = new HardwareBridgeClient(environment);
            var moduleDirectory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", id)).FullName;
            DependencyReleaseRecordStore.Write(DependencyReleaseRecordStore.InstalledPath(moduleDirectory),
                new(definition.PayloadSource!.VerifiedTag, definition.PayloadSource.VerifiedAssetName,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(BuildArchive(files)))));
            var dependency = new OptionalDependencyManager(client, environment, new FileSystemChangeTracker(), new EmptyInventory(),
                new DependencyInstallerSourceResolver(client, NullLogger<DependencyInstallerSourceResolver>.Instance), bridge);
            var manager = new ComponentManager(dependency, new Sampler(), new ProviderProbe(), new DependencyPayloadAcquisition(client, environment, bridge));
            var result = await manager.VerifyAsync(id, default);
            Assert.NotNull(result.Status);
            Assert.Equal(1, handler.Requests);
            foreach (var file in files) Assert.Equal(!fail, File.Exists(Path.Combine(root, "Dependencies", id, file)));
            Assert.Equal(fail, result.State == "runtimeFilesUnavailable");
            if (fail) Assert.Equal(BackendMessageCodes.Dependency.RuntimeFilesUnavailable, result.Message.Code);
            else
            {
                Assert.Equal(BackendMessageCodes.Dependency.ComponentFilesUnverified, result.Message.Code);
                await manager.VerifyAsync(id, default);
                Assert.Equal(1, handler.Requests);
            }
        }
        finally { DeleteOwnedPackage(root, id, files, definition.InstallerFileName); }
    }

    [Theory]
    [InlineData("amd-smu-pawnio-provider")]
    [InlineData("intel-msr-pawnio-provider")]
    public async Task ModuleFilesAloneDoNotCountAsAnInstalledPawnIoRuntime(string id)
    {
        var definition = OptionalDependencyCatalog.Find(id)!;
        var root = Path.Combine(Path.GetTempPath(), "rm-pawnio-detection-" + Guid.NewGuid().ToString("N"));
        var files = definition.PayloadSource!.FileNames.Concat(["PawnIOLib.dll"]).ToArray();
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", id)).FullName;
            foreach (var module in definition.PayloadSource.FileNames) File.WriteAllText(Path.Combine(directory, module), "module marker");
            using var client = new HttpClient(new ArchiveHandler([]));
            var environment = new TestEnvironment(root);
            using var bridge = new HardwareBridgeClient(environment);
            var manager = new OptionalDependencyManager(client, environment, new FileSystemChangeTracker(), new EmptyInventory(),
                new DependencyInstallerSourceResolver(client, NullLogger<DependencyInstallerSourceResolver>.Instance), bridge);
            // A real system installation may be reused, but this module-only folder must not own that receipt.
            Assert.NotEqual("ManagedDependencies", (await manager.GetStatusAsync(id, default))!.DetectionSource);
            File.WriteAllText(Path.Combine(directory, "PawnIOLib.dll"), "runtime marker, never loaded");
            var status = await manager.GetStatusAsync(id, default);
            Assert.True(status!.Installed);
            Assert.Equal("ManagedDependencies", status.DetectionSource);
            Assert.Equal(directory, status.EffectiveInstallDirectory);
        }
        finally { DeleteOwnedPackage(root, id, files, definition.InstallerFileName); }
    }

    private static void DeleteOwnedPackage(string root, string id, string[] files, string installer)
    {
        foreach (var receipt in new[] { DependencyReleaseRecordStore.InstalledPath(Path.Combine(root, "Dependencies", id)),
                     DependencyReleaseRecordStore.DownloadPath(Path.Combine(root, "Misc", "DependencyInstallers", id, installer)) })
            if (File.Exists(receipt)) File.Delete(receipt);
        foreach (var file in files)
        {
            var path = Path.Combine(root, "Dependencies", id, file);
            if (File.Exists(path)) File.Delete(path);
        }
        var cached = Path.Combine(root, "Misc", "DependencyInstallers", id, installer);
        if (File.Exists(cached)) File.Delete(cached);
        var nestedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var parent = Path.GetDirectoryName(file);
            while (!string.IsNullOrEmpty(parent))
            {
                nestedDirectories.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }
        foreach (var relative in nestedDirectories.OrderByDescending(path => path.Length))
        {
            var directory = Path.Combine(root, "Dependencies", id, relative);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
        foreach (var path in new[] { $"Dependencies/{id}/runtimes/win-x64/native", $"Dependencies/{id}/runtimes/win-x64",
                     $"Dependencies/{id}/runtimes", $"Dependencies/{id}", "Dependencies", $"Misc/DependencyInstallers/{id}", "Misc/DependencyInstallers", "Misc", "" })
            if (Directory.Exists(Path.Combine(root, path))) Directory.Delete(Path.Combine(root, path));
    }

    private sealed class EmptyInventory : IInstalledSoftwareInventory
    {
        public Task<IReadOnlyList<InstalledSoftwareEntry>> GetInstalledSoftwareAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstalledSoftwareEntry>>([]);
    }
    private sealed class ProviderProbe : IProviderRuntimeProbe
    {
        public ComponentProviderRuntimeProbeResult Probe(string id)
            => new(false, false, ComponentProviderStates.Missing, "fake unavailable", null);
    }
    private sealed class Sampler(bool active = false) : IMetricSampler
    {
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(MetricSampleRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(MetricSampleRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new HardwareMetricSnapshot(DateTimeOffset.UtcNow,
                new CpuMetrics("CPU", 0, false, CpuMetricObservationStatus.NotRequested, 0, 0, 0, 0, 0, "",
                    new CpuSensorMetrics(new("AMD SMU / PawnIO", "", null), active ? 42 : null, null, null, null)),
                new(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsUsageAvailable: false), new(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsSelectable: false), [], null!, new Dictionary<string, MetricReadingValue>()));
    }
    private sealed class UnexpectedPayload : IDependencyPayloadAcquisition
    {
        public Task<DependencyPayloadResult> InstallAsync(ResourceManager.App.Domain.Dependencies.OptionalDependencyDefinition definition, CancellationToken token)
            => throw new InvalidOperationException("This fixture does not invoke payload installation.");
        public Task<DependencyPayloadResult> EnsureAsync(ResourceManager.App.Domain.Dependencies.OptionalDependencyDefinition definition, CancellationToken token)
            => throw new InvalidOperationException("Installation must use its complete archive; this fixture does not invoke verification.");
    }

    [Fact]
    public async Task ReleaseArchiveCannotWriteOutsideComponentDirectory()
    {
        var definition = Assert.Single(OptionalDependencyCatalog.Definitions, row => row.Id == "hardware-bridge");
        var root = Path.Combine(Path.GetTempPath(), "rm-helper-payload-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new HttpClient(new ArchiveHandler(BuildArchive(["../outside.txt"])));
            var environment = new TestEnvironment(root);
            using var bridge = new HardwareBridgeClient(environment);
            var result = await new DependencyPayloadAcquisition(client, environment, bridge)
                .InstallAsync(definition, CancellationToken.None);
            Assert.False(result.Satisfied);
            Assert.False(File.Exists(Path.Combine(root, "Dependencies", "outside.txt")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static byte[] BuildArchive(IEnumerable<string> files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in files)
            {
                var entry = archive.CreateEntry(name);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("owned fake payload");
            }
        return buffer.ToArray();
    }

    private sealed class ArchiveHandler(byte[] bytes) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        internal bool Fail { get; init; }
        internal Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
