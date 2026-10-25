using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Dependencies;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.Shared.Runtime;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed partial class OptionalDependencyUserSessionTests : IDisposable
{
    private const string UserSid = "S-1-5-21-1-2-3-1001";
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"rm-user-installer-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("shared-webview2-runtime")]
    [InlineData("amd-smu-pawnio-provider")]
    public async Task VisibleInstallerUsesCallerDesktopAndPreservesElevation(string id)
    {
        var definition = OptionalDependencyCatalog.Find(id)!;
        var directory = Directory.CreateDirectory(Path.Combine(testRoot, "Misc", "DependencyInstallers", id)).FullName;
        var installer = Path.Combine(directory, definition.InstallerFileName);
        await File.WriteAllBytesAsync(installer, []);
        DependencyReleaseRecordStore.Write(DependencyReleaseRecordStore.DownloadPath(installer),
            new(definition.ReleaseSource?.VerifiedTag ?? "unversioned", definition.InstallerFileName, DependencyReleaseRecordStore.Hash(installer)));
        var broker = new RecordedBroker();
        using var bridge = new HardwareBridgeClient(new HostEnvironment(testRoot));
        var manager = new OptionalDependencyManager(new HttpClient(), new HostEnvironment(testRoot),
            new NoTracking(), new EmptyInventory(), new NoSourceResolution(), bridge, broker);

        // Stop at the recorded dispatch boundary, before any installer or readiness polling.
        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.LaunchInstallerAsync(
            id, true, null, default, new SoftwareOperationCaller(UserSid, 7)));
        var request = Assert.Single(broker.Requests);
        Assert.Equal(installer, request.FileName);
        Assert.Equal("runas", request.Verb);
        if (id == "shared-webview2-runtime") Assert.Equal("/silent /install", request.Arguments);
    }

    public void Dispose()
    {
        DependencyInstallPaths.DeleteOwnedTree(testRoot);
    }

    private sealed class RecordedBroker : IInteractiveUserSessionBroker
    {
        internal List<UserShellExecuteRequest> Requests { get; } = [];
        public IReadOnlyList<uint> GetActiveSessionIds() => [7u];
        public string? GetUserSid(uint sessionId) => UserSid;
        public uint LaunchUserShell(uint sessionId, string expectedUserSid, string executablePath, string arguments)
        {
            Assert.Equal(7u, sessionId);
            Assert.Equal(UserSid, expectedUserSid);
            Requests.Add(UserShellExecuteRequest.Decode(arguments[(UserShellExecuteRequest.Argument.Length + 1)..]));
            throw new OperationCanceledException("Recorded launch boundary reached.");
        }
        public Task<int> LaunchUserShellAndWaitAsync(uint sessionId, string expectedUserSid, string executablePath, string arguments, CancellationToken token)
        {
            _ = LaunchUserShell(sessionId, expectedUserSid, executablePath, arguments);
            throw new InvalidOperationException("The recorded boundary must throw.");
        }
    }

    private sealed class EmptyInventory : IInstalledSoftwareInventory
    {
        public Task<IReadOnlyList<InstalledSoftwareEntry>> GetInstalledSoftwareAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<InstalledSoftwareEntry>>([]);
        }
    }
    private sealed class NoTracking : IFileChangeTracker
    {
        public Task<FileInventorySnapshot> CaptureAsync(FileChangeTrackingScope scope, CancellationToken token) => throw new NotSupportedException();
        public FileChangeReport Compare(FileInventorySnapshot before, FileInventorySnapshot after) => throw new NotSupportedException();
    }
    private sealed class NoSourceResolution : IDependencyInstallerSourceResolver
    {
        public Task<DependencyVersionOptions> GetVersionOptionsAsync(OptionalDependencyDefinition definition, CancellationToken token) => throw new NotSupportedException();
        public Task<ResolvedInstallerSource> ResolveAsync(OptionalDependencyDefinition definition, string? versionChoice, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class HostEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
