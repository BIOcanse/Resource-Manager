using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Management;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.Dependencies;
using ResourceManager.App.Infrastructure.Operations;

namespace Resource_Manager_APP.Tests;

public sealed partial class ControlHelperPayloadTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("v0.2.0")]
    [InlineData("current")]
    public async Task InstalledBridgeUpdatesToPinUnlessItsReleaseRecordIsAlreadyCurrent(string? recordedTag)
    {
        const string id = "hardware-bridge";
        var definition = OptionalDependencyCatalog.Find(id)!;
        var source = definition.PayloadSource!;
        if (recordedTag == "current") recordedTag = source.VerifiedTag;
        var root = Path.Combine(Path.GetTempPath(), "rm-pinned-update-" + Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", id)).FullName;
        var files = source.FileNames.ToArray();
        try
        {
            foreach (var file in files) File.WriteAllText(Path.Combine(directory, file), "old release");
            var bytes = BuildArchive(files);
            if (recordedTag is not null)
                DependencyReleaseRecordStore.Write(DependencyReleaseRecordStore.InstalledPath(directory),
                    new(recordedTag, source.VerifiedAssetName, new string('A', 64)));
            using var handler = new ArchiveHandler(bytes);
            using var client = new HttpClient(handler);
            var environment = new TestEnvironment(root);
            using var bridge = new HardwareBridgeClient(environment);
            var dependencies = new OptionalDependencyManager(client, environment, new FileSystemChangeTracker(), new EmptyInventory(),
                new DependencyInstallerSourceResolver(client, NullLogger<DependencyInstallerSourceResolver>.Instance), bridge);
            var revisions = new ManagementInventoryRevisionOwner();
            var manager = new ComponentManager(dependencies, new Sampler(), new ProviderProbe(),
                new DependencyPayloadAcquisition(client, environment, bridge), revisions);
            var needsUpdate = recordedTag != source.VerifiedTag;
            var before = (await manager.GetStatusAsync(id, default))!;
            Assert.True(before.Installed);
            Assert.Equal(needsUpdate ? ComponentStatusStates.UpdateAvailable : ComponentStatusStates.Installed, before.State);
            Assert.Equal(needsUpdate, before.CanInstall);
            Assert.Equal(needsUpdate ? BackendMessageCodes.Dependency.UpdateAvailable : BackendMessageCodes.Dependency.InstalledInManagedRoot,
                before.Message.Code);
            _ = await manager.GetStatusesAsync("recorded-user", default);
            Assert.Equal(0, handler.Requests);
            var result = await manager.InstallAsync(id, true, "verified", default);
            Assert.Equal(ComponentStatusStates.Installed, result.Status!.State);
            var record = DependencyReleaseRecordStore.Read(DependencyReleaseRecordStore.InstalledPath(directory))!;
            Assert.Equal(source.VerifiedTag, record.Tag);
            Assert.Equal(source.VerifiedAssetName, record.AssetName);
            if (needsUpdate)
            {
                Assert.All(files, file => Assert.Equal("owned fake payload", File.ReadAllText(Path.Combine(directory, file))));
                Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), record.AssetSha256);
                Assert.EndsWith($"/{source.VerifiedTag}/{source.VerifiedAssetName}", handler.LastUri!.AbsolutePath);
            }
            Assert.Equal(needsUpdate ? 1 : 0, handler.Requests);
            Assert.Equal(needsUpdate ? "1" : "0", revisions.Current.ComponentRevision);
            _ = await manager.GetStatusesAsync("recorded-user", default);
            _ = await manager.GetStatusAsync(id, default);
            Assert.Equal(needsUpdate ? "1" : "0", revisions.Current.ComponentRevision);
        }
        finally { DeleteOwnedPackage(root, id, files, definition.InstallerFileName); }
    }

    [Fact]
    public async Task InstalledPawnIoModulesUseTheirOwnPinnedReleaseWithoutLaunchingTheDriverInstaller()
    {
        const string id = "amd-smu-pawnio-provider";
        var definition = OptionalDependencyCatalog.Find(id)!;
        var source = definition.PayloadSource!;
        var root = Path.Combine(Path.GetTempPath(), "rm-module-update-" + Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", id)).FullName;
        var files = source.FileNames.Concat(["PawnIOLib.dll"]).ToArray();
        try
        {
            foreach (var file in files) File.WriteAllText(Path.Combine(directory, file), "old module/runtime");
            using var handler = new ArchiveHandler(BuildArchive(source.FileNames));
            using var client = new HttpClient(handler);
            var environment = new TestEnvironment(root);
            using var bridge = new HardwareBridgeClient(environment);
            var dependencies = new OptionalDependencyManager(client, environment, new FileSystemChangeTracker(), new EmptyInventory(),
                new DependencyInstallerSourceResolver(client, NullLogger<DependencyInstallerSourceResolver>.Instance), bridge);
            var manager = new ComponentManager(dependencies, new Sampler(), new ProviderProbe(),
                new DependencyPayloadAcquisition(client, environment, bridge));
            Assert.Equal(ComponentStatusStates.UpdateAvailable, (await manager.GetStatusAsync(id, default))!.State);
            Assert.Equal(ComponentStatusStates.UpdateAvailable, (await manager.VerifyAsync(id, default)).State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InstallAsync(id, false, "verified", default));
            Assert.Equal(0, handler.Requests);
            Assert.False(File.Exists(DependencyReleaseRecordStore.InstalledPath(directory)));
            Assert.Equal(ComponentStatusStates.Installed, (await manager.InstallAsync(id, true, "verified", default)).Status!.State);
            var record = DependencyReleaseRecordStore.Read(DependencyReleaseRecordStore.InstalledPath(directory))!;
            Assert.Equal(source.VerifiedTag, record.Tag);
            Assert.NotEqual(definition.ReleaseSource!.VerifiedTag, record.Tag);
            Assert.Equal("old module/runtime", File.ReadAllText(Path.Combine(directory, "PawnIOLib.dll")));
            Assert.All(source.FileNames, file => Assert.Equal("owned fake payload", File.ReadAllText(Path.Combine(directory, file))));
            Assert.Equal(1, handler.Requests);
            Assert.False(Directory.Exists(Path.Combine(root, "Misc")));
        }
        finally { DeleteOwnedPackage(root, id, files, definition.InstallerFileName); }
    }

    [Fact]
    public async Task ExternalRuntimeIsReusedWithoutAnUpdateRecordOrAnyDownload()
    {
        const string id = "amd-smu-pawnio-provider";
        var definition = OptionalDependencyCatalog.Find(id)!;
        var root = Path.Combine(Path.GetTempPath(), "rm-external-release-" + Guid.NewGuid().ToString("N"));
        var external = Directory.CreateDirectory(Path.Combine(root, "external")).FullName;
        var executable = Path.Combine(external, "PawnIOLib.dll");
        var managed = Directory.CreateDirectory(Path.Combine(root, "Dependencies", id)).FullName;
        try
        {
            File.WriteAllText(executable, "external owned bytes");
            foreach (var file in definition.PayloadSource!.FileNames) File.WriteAllText(Path.Combine(managed, file), "owned module");
            using var handler = new ArchiveHandler([]) { Fail = true };
            using var client = new HttpClient(handler);
            var environment = new TestEnvironment(root);
            using var bridge = new HardwareBridgeClient(environment);
            var dependencies = new OptionalDependencyManager(client, environment, new FileSystemChangeTracker(), new RecordedRuntimeInventory(external),
                new DependencyInstallerSourceResolver(client, NullLogger<DependencyInstallerSourceResolver>.Instance), bridge);
            var manager = new ComponentManager(dependencies, new Sampler(), new ProviderProbe(), new UnexpectedPayload());
            var before = (await manager.GetStatusAsync(id, default))!;
            Assert.Equal(ComponentStatusStates.Installed, before.State);
            Assert.Equal(BackendMessageCodes.Dependency.ReusingExternalInstall, before.Message.Code);
            Assert.False(before.CanInstall);
            Assert.Equal(BackendMessageCodes.Dependency.AlreadyInstalledReuse, (await manager.InstallAsync(id, true, "verified", default)).Message.Code);
            Assert.Equal("external owned bytes", File.ReadAllText(executable));
            Assert.False(File.Exists(DependencyReleaseRecordStore.InstalledPath(external)));
            Assert.Equal(0, handler.Requests);
        }
        finally
        {
            File.Delete(executable);
            Directory.Delete(external);
            DeleteOwnedPackage(root, id, definition.PayloadSource!.FileNames.ToArray(), definition.InstallerFileName);
        }
    }

    [Fact]
    public async Task CachedArchiveMustMatchItsDownloadedShaBeforeItCanBeInstalled()
    {
        const string id = "hardware-bridge";
        var definition = OptionalDependencyCatalog.Find(id)!;
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rm-release-digest-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using var client = new HttpClient(new ArchiveHandler(BuildArchive(definition.PayloadSource!.FileNames)));
            var environment = new TestEnvironment(root);
            using var bridge = new HardwareBridgeClient(environment);
            var dependencies = new OptionalDependencyManager(client, environment, new FileSystemChangeTracker(), new EmptyInventory(),
                new DependencyInstallerSourceResolver(client, NullLogger<DependencyInstallerSourceResolver>.Instance), bridge);
            var download = await dependencies.DownloadAsync(id, true, "verified", default);
            File.AppendAllText(download.FilePath, "tampered");
            await Assert.ThrowsAsync<InvalidDataException>(() => dependencies.LaunchInstallerAsync(id, true, "verified", default));
            Assert.False(Directory.Exists(Path.Combine(root, "Dependencies", id)));
        }
        finally { DeleteOwnedPackage(root, id, definition.PayloadSource!.FileNames.ToArray(), definition.InstallerFileName); }
    }

    [Fact]
    public async Task InstallerCannotCertifyOldFilesThatAreMissingFromTheSelectedArchive()
    {
        const string id = "hardware-bridge";
        var definition = OptionalDependencyCatalog.Find(id)!;
        var root = Path.Combine(Path.GetTempPath(), "rm-release-incomplete-" + Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", id)).FullName;
        try
        {
            foreach (var name in definition.PayloadSource!.FileNames) File.WriteAllText(Path.Combine(directory, name), "old release");
            using var client = new HttpClient(new ArchiveHandler(BuildArchive(["ResourceManager.HardwareBridge.exe"])));
            var environment = new TestEnvironment(root);
            using var bridge = new HardwareBridgeClient(environment);
            var dependencies = new OptionalDependencyManager(client, environment, new FileSystemChangeTracker(), new EmptyInventory(),
                new DependencyInstallerSourceResolver(client, NullLogger<DependencyInstallerSourceResolver>.Instance), bridge);
            await dependencies.DownloadAsync(id, true, "verified", default);
            await Assert.ThrowsAsync<InvalidDataException>(() => dependencies.LaunchInstallerAsync(id, true, "verified", default));
            Assert.All(definition.PayloadSource.FileNames, file => Assert.Equal("old release", File.ReadAllText(Path.Combine(directory, file))));
            Assert.False(File.Exists(DependencyReleaseRecordStore.InstalledPath(directory)));
        }
        finally { DeleteOwnedPackage(root, id, definition.PayloadSource!.FileNames.ToArray(), definition.InstallerFileName); }
    }

    [Fact]
    public async Task UpdatingHeldBridgeDrainsOnlyItsOwnedSessionAndNextEntryStartsFresh()
    {
        const string id = "hardware-bridge";
        var definition = OptionalDependencyCatalog.Find(id)!;
        var root = Path.Combine(Path.GetTempPath(), "rm-owned-release-" + Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateDirectory(Path.Combine(root, "Dependencies", id)).FullName;
        var executable = Path.Combine(directory, "ResourceManager.HardwareBridge.exe");
        var observers = new List<Process>();
        var starts = 0;
        foreach (var file in definition.PayloadSource!.FileNames) File.WriteAllText(Path.Combine(directory, file), "old release");
        Process? Start(ProcessStartInfo start)
        {
            Assert.Equal(executable, start.FileName);
            var info = new ProcessStartInfo(HardwareBridgeSessionTests.Fixture)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.Environment["RM_BRIDGE_FIXTURE_HOLD_FILE"] = executable;
            var running = Process.Start(info)!;
            var observer = Process.GetProcessById(running.Id);
            _ = observer.SafeHandle;
            observers.Add(observer);
            starts++;
            return running;
        }
        var environment = new TestEnvironment(root);
        using var bridge = new HardwareBridgeClient(environment, Start);
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = bridge.Owner.RunAsync(stop.Token, () => ready.SetResult());
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(await bridge.SendAsync("describe", null, default));
            Assert.Throws<IOException>(() => File.WriteAllText(executable, "must be held"));
            using var client = new HttpClient(new ArchiveHandler(BuildArchive(definition.PayloadSource.FileNames)));
            var dependencies = new OptionalDependencyManager(client, environment, new FileSystemChangeTracker(), new EmptyInventory(),
                new DependencyInstallerSourceResolver(client, NullLogger<DependencyInstallerSourceResolver>.Instance), bridge);
            var manager = new ComponentManager(dependencies, new Sampler(), new ProviderProbe(),
                new DependencyPayloadAcquisition(client, environment, bridge));
            Assert.Equal(ComponentStatusStates.Installed, (await manager.InstallAsync(id, true, "verified", default)).Status!.State);
            Assert.True(observers[0].HasExited);
            Assert.Equal("owned fake payload", File.ReadAllText(executable));
            Assert.Equal(1, starts);
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await bridge.SendAsync("describe", null, default));
            Assert.Equal(1, starts);
            ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            run = bridge.Owner.RunAsync(stop.Token, () => ready.SetResult());
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(await bridge.SendAsync("describe", null, default));
            Assert.Equal(2, starts);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            bridge.Dispose();
            foreach (var observer in observers)
            {
                await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                observer.Dispose();
            }
            DeleteOwnedPackage(root, id, definition.PayloadSource.FileNames.ToArray(), definition.InstallerFileName);
        }
    }

}
