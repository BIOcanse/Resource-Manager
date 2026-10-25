using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Management;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Dependencies;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Operations;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace Resource_Manager_APP.Tests;

public sealed partial class ControlHelperPayloadTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task MissingPayloadIsVisibleAndVerificationRepairsAndRebindsCcdOnce(bool active, bool fail, bool external)
    {
        const string id = "amd-smu-pawnio-provider";
        var definition = OptionalDependencyCatalog.Find(id)!;
        var root = Path.Combine(Path.GetTempPath(), "rm-payload-status-" + Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", id)).FullName;
        var module = Path.Combine(directory, "AMDFamily17.bin");
        var runtimeDirectory = external ? Directory.CreateDirectory(Path.Combine(root, "PawnIO")).FullName : directory;
        try
        {
            // Recorded old installation: runtime probe and original module, second module absent.
            File.WriteAllText(Path.Combine(runtimeDirectory, "PawnIOLib.dll"), "recorded runtime, never loaded");
            File.WriteAllText(Path.Combine(directory, "RyzenSMU.bin"), "original module");
            using var handler = new ArchiveHandler(BuildArchive(definition.PayloadSource!.FileNames)) { Fail = fail };
            using var client = new HttpClient(handler);
            var environment = new TestEnvironment(root);
            using var bridge = new HardwareBridgeClient(environment);
            DependencyReleaseRecordStore.Write(DependencyReleaseRecordStore.InstalledPath(directory),
                new(definition.PayloadSource.VerifiedTag, definition.PayloadSource.VerifiedAssetName,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(BuildArchive(definition.PayloadSource.FileNames)))));
            var dependencies = new OptionalDependencyManager(client, environment, new FileSystemChangeTracker(), new RecordedRuntimeInventory(runtimeDirectory),
                new DependencyInstallerSourceResolver(client, NullLogger<DependencyInstallerSourceResolver>.Instance), bridge);
            var revisions = new ManagementInventoryRevisionOwner();
            var provider = new ReadyPawnIoProbe();
            var manager = new ComponentManager(dependencies, new Sampler(active), provider,
                new DependencyPayloadAcquisition(client, environment, bridge), revisions);
            var reads = HostReadWriteDependencies.Create(client, provider, root, bridge, revisions);
            var opens = 0;
            var resolves = 0;
            var target = new CpuThreadCpuidData(new("AuthenticAMD", 0x19, 0x61, 1, "recorded AMD", [], []),
                0xA60F12, null, null, null, null, null, []);
            var topology = new CpuOsTopologyInformation([], [])
                { Packages = [new("package-opaque", [0])], ThreadCpuid = [new(0, target)] };
            using var ccd = new AmdSmuCcdBinding(() => { resolves++; return AmdSmuHardwareBinding.LocateModulePath(root, "AMDFamily17.bin"); },
                () => revisions.Current.ComponentRevision,
                (cpu, path) => new(cpu.Family, cpu.Model, 1, () => { opens++; return new RecordedSmnSession(); }));

            var dependency = (await dependencies.GetStatusAsync(id, default))!;
            Assert.True(dependency.Installed);
            Assert.Equal(external ? "WindowsInstalledSoftware:PawnIO" : "ManagedDependencies", dependency.DetectionSource);
            Assert.False(dependency.PayloadFilesPresent);
            Assert.Equal("payload_missing", dependency.State);
            var before = (await manager.GetStatusAsync(id, default))!;
            Assert.Equal(ComponentStatusStates.PayloadMissing, before.State);
            Assert.Equal(BackendMessageCodes.Dependency.PayloadMissing, before.Message.Code);
            Assert.True(before.Installed);
            Assert.True(before.CanVerify);
            Assert.False(before.CanInstall);
            Assert.Equal(active, before.ProviderActive);
            Assert.Equal("dependency-module-missing", reads.Query("amd.smn").Reason);
            Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, ccd.Discover(topology, 16).Status);
            // Status reads neither fetch modules nor change revision.
            _ = await manager.GetStatusesAsync(default);
            _ = await manager.GetStatusesAsync("S-1-5-21-recorded", default);
            Assert.Equal(0, handler.Requests);
            Assert.Equal("0", revisions.Current.ComponentRevision);

            var result = await manager.VerifyAsync(id, default);
            if (fail)
            {
                Assert.Equal("runtimeFilesUnavailable", result.State);
                Assert.Equal(ComponentStatusStates.PayloadMissing, result.Status!.State);
                Assert.Equal("0", revisions.Current.ComponentRevision);
                Assert.Equal("dependency-module-missing", reads.Query("amd.smn").Reason);
                Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, ccd.Discover(topology, 16).Status);
                Assert.Equal(1, resolves);
                Assert.Equal(0, opens);
            }
            else
            {
                Assert.True(File.Exists(module));
                Assert.True((await dependencies.GetStatusAsync(id, default))!.PayloadFilesPresent);
                Assert.Equal("installed", (await dependencies.GetStatusAsync(id, default))!.State);
                Assert.Equal(active ? ComponentStatusStates.Active : ComponentStatusStates.Installed, result.Status!.State);
                Assert.Equal("1", revisions.Current.ComponentRevision);
                Assert.Equal(ResourceManager.ReadWrite.Dependencies.DependencyAvailability.Ready, reads.Query("amd.smn").Availability);
                Assert.Equal(51, ccd.Read(Assert.Single(ccd.Discover(topology, 16).Identities)));
                _ = ccd.Read(Assert.Single(ccd.Discover(topology, 16).Identities));
                Assert.Equal(2, resolves);
                Assert.Equal(1, opens);
                _ = await manager.GetStatusesAsync(default);
                _ = await manager.GetStatusesAsync("S-1-5-21-recorded", default);
                _ = await manager.GetStatusAsync(id, default);
                Assert.Equal("1", revisions.Current.ComponentRevision);
            }
            Assert.Equal("original module", File.ReadAllText(Path.Combine(directory, "RyzenSMU.bin")));
            Assert.Equal(1, handler.Requests);
            Assert.EndsWith("/namazso/PawnIO.Modules/releases/download/0.2.11/release_0_2_11.zip", handler.LastUri!.AbsolutePath);

        }
        finally
        {
            if (external)
            {
                File.Delete(Path.Combine(runtimeDirectory, "PawnIOLib.dll"));
                Directory.Delete(runtimeDirectory);
            }
            DeleteOwnedPackage(root, id, ["PawnIOLib.dll", .. definition.PayloadSource!.FileNames], definition.InstallerFileName);
        }
    }

    private sealed class RecordedRuntimeInventory(string directory) : IInstalledSoftwareInventory
    {
        public Task<IReadOnlyList<InstalledSoftwareEntry>> GetInstalledSoftwareAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstalledSoftwareEntry>>(
                [new("recorded:pawnio", "PawnIO", "2.2.0", "namazso", directory, [directory], null, "recorded registry")]);
    }

    private sealed class ReadyPawnIoProbe : IProviderRuntimeProbe
    {
        public ComponentProviderRuntimeProbeResult Probe(string id)
            => id == "amd-smu-pawnio-provider"
                ? new(true, true, ComponentProviderStates.Installed, "recorded installed runtime", null)
                : new(false, false, ComponentProviderStates.Missing, "recorded absent runtime", null);
    }

    private sealed class RecordedSmnSession : IAmdSmnSession
    {
        public uint ReadRegister(uint address) => address == 0x59B08 ? 0xB20U : 0;
        public void Dispose() { }
    }
}
