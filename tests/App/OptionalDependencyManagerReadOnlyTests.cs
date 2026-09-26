using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Dependencies;

namespace Resource_Manager_APP.Tests;

public sealed class OptionalDependencyManagerReadOnlyTests : IDisposable
{
    /// <summary>只读状态查询不应该解析安装器来源，更不应该联网。</summary>
    private sealed class UnexpectedInstallerSourceResolver : IDependencyInstallerSourceResolver
    {
        public Task<ResourceManager.App.Domain.Dependencies.DependencyVersionOptions> GetVersionOptionsAsync(
            ResourceManager.App.Domain.Dependencies.OptionalDependencyDefinition definition,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("状态查询不应解析安装器来源。");

        public Task<ResourceManager.App.Domain.Dependencies.ResolvedInstallerSource> ResolveAsync(
            ResourceManager.App.Domain.Dependencies.OptionalDependencyDefinition definition,
            string? versionChoice,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("状态查询不应解析安装器来源。");
    }

    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        $"resource-manager-dependency-read-{Guid.NewGuid():N}");

    [Fact]
    public async Task StatusQueriesDoNotCreatePackageDirectories()
    {
        Directory.CreateDirectory(testRoot);
        var before = EnumerateRelativePaths();
        var manager = new OptionalDependencyManager(
            new HttpClient(),
            new TestHostEnvironment(testRoot),
            new UnexpectedFileChangeTracker(),
            new EmptyInstalledSoftwareInventory(),
            new UnexpectedInstallerSourceResolver());

        var statuses = await manager.GetStatusesAsync(CancellationToken.None);
        var status = await manager.GetStatusAsync(
            OptionalDependencyCatalog.Definitions[0].Id,
            CancellationToken.None);

        Assert.Equal(OptionalDependencyCatalog.Definitions.Count, statuses.Count);
        Assert.NotNull(status);
        Assert.False(status.InstallerAvailable);
        Assert.False(status.Installed);
        Assert.Equal(before, EnumerateRelativePaths());
    }

    private string[] EnumerateRelativePaths()
        => Directory.EnumerateFileSystemEntries(
                testRoot,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(testRoot, path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private sealed class EmptyInstalledSoftwareInventory : IInstalledSoftwareInventory
    {
        public Task<IReadOnlyList<InstalledSoftwareEntry>> GetInstalledSoftwareAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<InstalledSoftwareEntry>>([]);
        }
    }

    private sealed class UnexpectedFileChangeTracker : IFileChangeTracker
    {
        public Task<FileInventorySnapshot> CaptureAsync(
            FileChangeTrackingScope scope,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Read-only status queries must not start file tracking.");

        public FileChangeReport Compare(
            FileInventorySnapshot before,
            FileInventorySnapshot after)
            => throw new InvalidOperationException("Read-only status queries must not compare file changes.");
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
