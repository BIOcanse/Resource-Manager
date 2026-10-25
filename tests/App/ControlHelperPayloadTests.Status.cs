using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Management;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Dependencies;
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

    [Fact]
    public async Task ValidManagedReceiptWithMissingBridgeExecutableKeepsSamePinVerifyRepair()
    {
        const string id = "hardware-bridge";
        var definition = OptionalDependencyCatalog.Find(id)!;
        var source = definition.PayloadSource!;
        var root = Path.Combine(Path.GetTempPath(), "rm-receipt-missing-probe-" + Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", id)).FullName;
        var archive = BuildArchive(source.FileNames);
        try
        {
            DependencyReleaseRecordStore.Write(DependencyReleaseRecordStore.InstalledPath(directory),
                new(source.VerifiedTag, source.VerifiedAssetName,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive))));
            using var handler = new ArchiveHandler(archive);
            using var client = new HttpClient(handler);
            var environment = new TestEnvironment(root);
            using var bridge = new HardwareBridgeClient(environment);
            var dependencies = new OptionalDependencyManager(client, environment, new FileSystemChangeTracker(), new EmptyInventory(),
                new DependencyInstallerSourceResolver(client, NullLogger<DependencyInstallerSourceResolver>.Instance), bridge);
            var revisions = new ManagementInventoryRevisionOwner();
            var manager = new ComponentManager(dependencies, new Sampler(), new ProviderProbe(),
                new DependencyPayloadAcquisition(client, environment, bridge), revisions);

            var dependency = (await dependencies.GetStatusAsync(id, default))!;
            Assert.True(dependency.Installed);
            Assert.Equal("ManagedDependencies", dependency.DetectionSource);
            Assert.Equal("payload_missing", dependency.State);
            Assert.False(dependency.UpdateAvailable);
            var before = (await manager.GetStatusAsync(id, default))!;
            Assert.Equal(ComponentStatusStates.PayloadMissing, before.State);
            Assert.True(before.Installed);
            Assert.True(before.CanVerify);
            Assert.False(before.CanInstall);
            Assert.False(File.Exists(Path.Combine(directory, source.FileNames[0])));
            Assert.Equal(0, handler.Requests);
            Assert.Equal("0", revisions.Current.ComponentRevision);

            var result = await manager.VerifyAsync(id, default);

            Assert.Equal(ComponentStatusStates.Installed, result.Status!.State);
            Assert.All(source.FileNames, name => Assert.True(File.Exists(Path.Combine(directory, name)), name));
            Assert.Equal(1, handler.Requests);
            Assert.EndsWith($"/{source.VerifiedTag}/{source.VerifiedAssetName}", handler.LastUri!.AbsolutePath);
            Assert.Equal(source.VerifiedTag, DependencyReleaseRecordStore.Read(DependencyReleaseRecordStore.InstalledPath(directory))!.Tag);
            Assert.Equal("1", revisions.Current.ComponentRevision);
        }
        finally { DeleteOwnedPackage(root, id, source.FileNames.ToArray(), definition.InstallerFileName); }
    }

    [Fact]
    public async Task InvalidManagedReceiptWithMissingBridgeExecutableRequiresPinnedUpdate()
    {
        const string id = "hardware-bridge";
        var definition = OptionalDependencyCatalog.Find(id)!;
        var source = definition.PayloadSource!;
        var root = Path.Combine(Path.GetTempPath(), "rm-invalid-receipt-missing-probe-" + Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", id)).FullName;
        try
        {
            File.WriteAllText(DependencyReleaseRecordStore.InstalledPath(directory), "{}");
            var archive = BuildArchive(source.FileNames);
            using var handler = new ArchiveHandler(archive);
            using var client = new HttpClient(handler);
            var environment = new TestEnvironment(root);
            using var bridge = new HardwareBridgeClient(environment);
            var dependencies = new OptionalDependencyManager(client, environment, new FileSystemChangeTracker(), new EmptyInventory(),
                new DependencyInstallerSourceResolver(client, NullLogger<DependencyInstallerSourceResolver>.Instance), bridge);
            var revisions = new ManagementInventoryRevisionOwner();
            var manager = new ComponentManager(dependencies, new Sampler(), new ProviderProbe(),
                new DependencyPayloadAcquisition(client, environment, bridge), revisions);

            var dependency = (await dependencies.GetStatusAsync(id, default))!;
            var before = (await manager.GetStatusAsync(id, default))!;
            Assert.True(dependency.Installed);
            Assert.True(dependency.UpdateAvailable);
            Assert.Equal("payload_missing", dependency.State);
            Assert.Equal("ManagedDependencies", dependency.DetectionSource);
            Assert.Equal(ComponentStatusStates.PayloadMissing, before.State);
            Assert.True(before.CanInstall);
            Assert.False(before.CanVerify);
            Assert.Equal(0, handler.Requests);

            var result = await manager.InstallAsync(id, true, versionChoice: null, cancellationToken: default);

            Assert.Equal(ComponentStatusStates.Installed, result.Status!.State);
            Assert.All(source.FileNames, name => Assert.True(File.Exists(Path.Combine(directory, name)), name));
            Assert.Equal(1, handler.Requests);
            Assert.EndsWith($"/{source.VerifiedTag}/{source.VerifiedAssetName}", handler.LastUri!.AbsolutePath);
        }
        finally { DeleteOwnedPackage(root, id, source.FileNames.ToArray(), definition.InstallerFileName); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedManagedReplacementIsDiscoveredAndOnlyExplicitInstallUsesPinnedUpdate(bool backupHasReceipt)
    {
        const string id = "hardware-bridge";
        var definition = OptionalDependencyCatalog.Find(id)!;
        var source = definition.PayloadSource!;
        var root = Path.Combine(Path.GetTempPath(), "rm-interrupted-replacement-" + Guid.NewGuid().ToString("N"));
        var live = Path.Combine(root, "Dependencies", id);
        var backup = live + ".previous-" + Guid.NewGuid().ToString("N");
        var malformedBackup = live + ".previous-not-a-guid";
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(malformedBackup);
        File.WriteAllText(Path.Combine(malformedBackup, "fixture-marker.txt"), "must not be treated as an owned backup");
        var archive = BuildArchive(source.FileNames);
        try
        {
            if (backupHasReceipt)
            {
                DependencyReleaseRecordStore.Write(DependencyReleaseRecordStore.InstalledPath(backup),
                    new(source.VerifiedTag, source.VerifiedAssetName,
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive))));
            }
            using var handler = new ArchiveHandler(archive);
            using var client = new HttpClient(handler);
            var environment = new TestEnvironment(root);
            using var bridge = new HardwareBridgeClient(environment);
            var dependencies = new OptionalDependencyManager(client, environment, new FileSystemChangeTracker(), new EmptyInventory(),
                new DependencyInstallerSourceResolver(client, NullLogger<DependencyInstallerSourceResolver>.Instance), bridge);
            var revisions = new ManagementInventoryRevisionOwner();
            var manager = new ComponentManager(dependencies, new Sampler(), new ProviderProbe(),
                new DependencyPayloadAcquisition(client, environment, bridge), revisions);

            var dependency = (await dependencies.GetStatusAsync(id, default))!;
            var before = (await manager.GetStatusAsync(id, default))!;
            Assert.False(dependency.Installed);
            Assert.True(dependency.UpdateAvailable);
            Assert.Equal("ManagedDependenciesInterruptedReplacement", dependency.DetectionSource);
            Assert.Equal(ComponentStatusStates.UpdateAvailable, before.State);
            Assert.False(before.Installed);
            Assert.True(before.CanInstall);
            Assert.False(before.CanVerify);
            Assert.False(Directory.Exists(live));
            Assert.True(Directory.Exists(backup));
            Assert.True(File.Exists(Path.Combine(malformedBackup, "fixture-marker.txt")));
            Assert.Equal("0", revisions.Current.ComponentRevision);
            Assert.Equal(0, handler.Requests);

            var result = await manager.InstallAsync(id, true, versionChoice: null, cancellationToken: default);

            Assert.Equal(ComponentStatusStates.Installed, result.Status!.State);
            Assert.True(Directory.Exists(live));
            Assert.False(Directory.Exists(backup));
            Assert.True(File.Exists(Path.Combine(malformedBackup, "fixture-marker.txt")));
            Assert.All(source.FileNames, name => Assert.True(File.Exists(Path.Combine(live, name)), name));
            Assert.Equal(1, handler.Requests);
            Assert.EndsWith($"/{source.VerifiedTag}/{source.VerifiedAssetName}", handler.LastUri!.AbsolutePath);
            Assert.Equal("1", revisions.Current.ComponentRevision);
        }
        finally
        {
            DependencyInstallPaths.DeleteOwnedTree(backup);
            DependencyInstallPaths.DeleteOwnedTree(malformedBackup);
            DeleteOwnedPackage(root, id, source.FileNames.ToArray(), definition.InstallerFileName);
        }
    }

    [Fact]
    public async Task PendingOwnedInstallerKeepsOnlyItsComponentReservedAndCommitsOneRevisionOnExit()
    {
        const string managedId = "amd-smu-pawnio-provider";
        const string unrelatedId = "hardware-sensors-pawnio-provider";
        var root = Path.Combine(Path.GetTempPath(), "rm-component-owned-installer-" + Guid.NewGuid().ToString("N"));
        var managed = OptionalDependencyCatalog.Find(managedId)!;
        var unrelatedDefinition = OptionalDependencyCatalog.Find(unrelatedId)!;
        var dependencies = new PendingOwnedInstallerManager(managed, unrelatedDefinition, root);
        var revisions = new ManagementInventoryRevisionOwner();
        var payload = new RecordingPayloadAcquisition(root, dependencies.ManagedRelease);
        var manager = new ComponentManager(dependencies, new Sampler(), new ProviderProbe(),
            payload, revisions);
        using var cancellation = new CancellationTokenSource();
        var first = manager.InstallAsync(managedId, acknowledgeExternalTerms: true,
            versionChoice: DependencyVersionChoices.Verified, cancellationToken: cancellation.Token);
        try
        {
            await dependencies.ManagedInstallerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            Assert.False(first.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InstallAsync(
                managedId, acknowledgeExternalTerms: true, versionChoice: DependencyVersionChoices.Verified,
                cancellationToken: default));
            Assert.Equal("0", revisions.Current.ComponentRevision);

            var unrelated = await manager.InstallAsync(unrelatedId, acknowledgeExternalTerms: true,
                versionChoice: DependencyVersionChoices.Verified, cancellationToken: default)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ComponentStatusStates.Installed, unrelated.Status!.State);
            Assert.False(first.IsCompleted);
            Assert.Equal("1", revisions.Current.ComponentRevision);

            dependencies.CompleteManagedInstaller(0);
            var completed = await first.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("installerCompleted", completed.State);
            Assert.Equal(ComponentStatusStates.Installed, completed.Status!.State);
            Assert.Equal("2", revisions.Current.ComponentRevision);
            Assert.True(payload.InstallToken.HasValue);
            Assert.False(payload.InstallToken.Value.CanBeCanceled);
            Assert.Equal(dependencies.ManagedRelease,
                DependencyReleaseRecordStore.Read(DependencyReleaseRecordStore.InstalledPath(dependencies.ManagedDirectory)));
            Assert.True(File.Exists(Path.Combine(dependencies.ManagedDirectory, managed.PayloadSource!.FileNames[0])));
        }
        finally
        {
            dependencies.CompleteManagedInstaller(0);
            try { await first.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { }
            DependencyInstallPaths.DeleteOwnedTree(root);
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

    private sealed class RecordingPayloadAcquisition(string root, DependencyReleaseRecord release) : IDependencyPayloadAcquisition
    {
        internal CancellationToken? InstallToken { get; private set; }

        public Task<DependencyPayloadResult> EnsureAsync(OptionalDependencyDefinition definition, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DependencyPayloadResult> InstallAsync(OptionalDependencyDefinition definition, CancellationToken cancellationToken)
        {
            InstallToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            var payloadDirectory = Path.Combine(root, "Dependencies", definition.Id);
            Directory.CreateDirectory(payloadDirectory);
            foreach (var name in definition.PayloadSource!.FileNames)
                File.WriteAllText(Path.Combine(payloadDirectory, name), "fixture module");
            DependencyReleaseRecordStore.Write(DependencyReleaseRecordStore.InstalledPath(payloadDirectory), release);
            return Task.FromResult(new DependencyPayloadResult(true, "fixture payload published", Changed: true));
        }
    }

    private sealed class PendingOwnedInstallerManager(
        OptionalDependencyDefinition managedDriver,
        OptionalDependencyDefinition unrelatedModule,
        string root) : IOptionalDependencyManager
    {
        private readonly DependencyReleaseRecord managedRelease = CreateRelease(managedDriver);
        private readonly Dictionary<string, OptionalDependencyStatus> statuses = new(StringComparer.OrdinalIgnoreCase)
        {
            [managedDriver.Id] = CreateStatus(managedDriver, installed: false, payloadPresent: true, root: root),
            [unrelatedModule.Id] = CreateStatus(unrelatedModule, installed: false, payloadPresent: false, root: root)
        };
        private readonly TaskCompletionSource<int> managedInstallerExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ManagedInstallerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string ManagedDirectory => Path.Combine(root, "Dependencies", managedDriver.Id);
        internal DependencyReleaseRecord ManagedRelease => managedRelease;

        internal void CompleteManagedInstaller(int exitCode) => managedInstallerExit.TrySetResult(exitCode);

        public Task<IReadOnlyList<OptionalDependencyStatus>> GetStatusesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<OptionalDependencyStatus>>(statuses.Values.ToArray());
        }

        public Task<OptionalDependencyStatus?> GetStatusAsync(string id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(statuses.TryGetValue(id, out var status) ? status : null);
        }

        public Task<DependencyVersionOptions> GetVersionOptionsAsync(string id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<OptionalDependencyDownloadResult> DownloadAsync(
            string id,
            bool acknowledgeExternalTerms,
            string? versionChoice,
            CancellationToken cancellationToken,
            IProgress<DependencyDownloadProgress>? progress = null)
            => Task.FromResult(new OptionalDependencyDownloadResult(id, "downloaded", "fixture-installer", 1,
                BackendMessage.Create(BackendMessageDomains.Dependency, BackendMessageCodes.Dependency.InstallerCached)));

        public async Task<OptionalDependencyLaunchResult> LaunchInstallerAsync(
            string id,
            bool acknowledgeExternalTerms,
            string? versionChoice,
            CancellationToken cancellationToken,
            SoftwareOperationCaller? caller = null)
        {
            if (id.Equals(managedDriver.Id, StringComparison.OrdinalIgnoreCase))
            {
                ManagedInstallerStarted.TrySetResult();
                var exitCode = await managedInstallerExit.Task.ConfigureAwait(false);
                if (exitCode != 0) throw new InvalidOperationException($"The fixture installer exited with {exitCode}.");
                statuses[id] = CreateStatus(managedDriver, installed: true, payloadPresent: true, root: root);
            }
            else
            {
                statuses[id] = CreateStatus(unrelatedModule, installed: true, payloadPresent: true, root: root);
            }

            return new OptionalDependencyLaunchResult(id, "installerCompleted", "fixture-installer",
                BackendMessage.Create(BackendMessageDomains.Dependency, BackendMessageCodes.Dependency.ComponentFilesUnverified));
        }

        private static OptionalDependencyStatus CreateStatus(
            OptionalDependencyDefinition definition,
            bool installed,
            bool payloadPresent,
            string root)
        {
            var directory = Path.Combine(root, "Dependencies", definition.Id);
            return new OptionalDependencyStatus(
                definition,
                installed ? "installed" : "readyToInstall",
                Path.Combine(root, "Dependencies"),
                Path.Combine(root, "Misc", definition.Id),
                directory,
                Path.Combine(root, "Misc", definition.Id, definition.InstallerFileName),
                InstallerAvailable: true,
                Installed: installed,
                CanDownload: !installed,
                CanLaunchInstaller: !installed,
                EffectiveInstallDirectory: directory,
                DetectedInstallDirectory: installed ? directory : null,
                DetectionSource: installed ? "ManagedDependencies" : "None",
                Message: BackendMessage.Create(BackendMessageDomains.Dependency,
                    installed ? BackendMessageCodes.Dependency.InstalledInManagedRoot : BackendMessageCodes.Dependency.InstallerCached),
                InstallerSourceKind: definition.InstallerSourceKind,
                PayloadFilesPresent: payloadPresent,
                UpdateAvailable: false);
        }

        private static DependencyReleaseRecord CreateRelease(OptionalDependencyDefinition definition)
        {
            var source = definition.PayloadSource!;
            var archive = BuildArchive(source.FileNames);
            return new DependencyReleaseRecord(source.VerifiedTag, source.VerifiedAssetName,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive)));
        }
    }
}
