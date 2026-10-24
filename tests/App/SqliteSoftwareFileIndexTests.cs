using Microsoft.Data.Sqlite;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Indexing;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Indexing;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Persistence;

namespace Resource_Manager_APP.Tests;

public sealed class SqliteSoftwareFileIndexTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"rm-file-index-{Guid.NewGuid():N}");
    private readonly List<IDisposable> nativeWorkspaces = [];

    [Theory]
    [InlineData("a")]
    [InlineData("match")]
    public async Task SearchContinuesBeyondRejectedCandidatePages(string query)
    {
        var index = CreateIndex();
        var root = Directory.CreateDirectory(Path.Combine(testRoot, "filtered")).FullName;
        for (var i = 0; i < 300; i++) File.WriteAllText(Path.Combine(root, $"{query}{i:D3}.txt"), "x");
        var visible = query + "zzzzzz.txt";
        File.WriteAllText(Path.Combine(root, visible), "ok");
        await index.GetOrRefreshAsync(new("filter", "Filter", [new(root, "Program")]), TimeSpan.Zero, true, default);
        var calls = 0;
        var results = await index.SearchAsync(query, 1, default, (_, relative) =>
        {
            calls++;
            return relative == visible;
        });
        Assert.Equal(visible, Assert.Single(results).FileName);
        Assert.True(calls > 256);
    }

    [Fact]
    public async Task RefreshAndSearch_TracksAddedChangedAndDeletedFiles()
    {
        var index = CreateIndex();
        var softwareRoot = Directory.CreateDirectory(Path.Combine(testRoot, "sample-app")).FullName;
        var nestedRoot = Directory.CreateDirectory(Path.Combine(softwareRoot, "cache")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(softwareRoot, "app.exe"), new byte[4]);
        var telemetryPath = Path.Combine(nestedRoot, "telemetry-cache.bin");
        await File.WriteAllBytesAsync(telemetryPath, new byte[17]);
        var request = new SoftwareFileIndexRequest(
            "sample-app",
            "Sample App",
            [new SoftwareFileIndexRoot(softwareRoot, "Program")]);

        var initial = await index.GetOrRefreshAsync(request, TimeSpan.FromHours(1), true, CancellationToken.None);

        Assert.Equal(21, initial.TotalBytes);
        Assert.Equal(2, initial.FileCount);
        var search = await index.SearchAsync("metry-ca", 20, CancellationToken.None);
        Assert.Equal("telemetry-cache.bin", Assert.Single(search).FileName);

        await File.WriteAllBytesAsync(Path.Combine(softwareRoot, "app.exe"), new byte[10]);
        File.Delete(telemetryPath);
        await File.WriteAllBytesAsync(Path.Combine(nestedRoot, "settings.json"), new byte[7]);

        var cached = await index.GetOrRefreshAsync(request, TimeSpan.FromDays(1), false, CancellationToken.None);
        Assert.Equal(21, cached.TotalBytes);

        var refreshed = await index.GetOrRefreshAsync(request, TimeSpan.FromDays(1), true, CancellationToken.None);
        Assert.Equal(17, refreshed.TotalBytes);
        Assert.Equal(2, refreshed.FileCount);
        Assert.Empty(await index.SearchAsync("metry-ca", 20, CancellationToken.None));
        Assert.Equal("settings.json", Assert.Single(await index.SearchAsync("setting", 20, CancellationToken.None)).FileName);

        var statistics = await index.GetStatisticsAsync(CancellationToken.None);
        Assert.Equal(1, statistics.SoftwareCount);
        Assert.Equal(1, statistics.RootCount);
        Assert.Equal(2, statistics.FileCount);
        Assert.Equal(17, statistics.TotalBytes);
        Assert.NotNull(statistics.LatestIndexedAt);
    }

    [Fact]
    public async Task Refresh_WithSameRootDifferentCase_DoesNotCreateDuplicateRoot()
    {
        var index = CreateIndex();
        var softwareRoot = Directory.CreateDirectory(Path.Combine(testRoot, "CaseRoot")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(softwareRoot, "data.bin"), new byte[3]);

        await index.GetOrRefreshAsync(
            new SoftwareFileIndexRequest("case-app", "Case App", [new SoftwareFileIndexRoot(softwareRoot, "Program")]),
            TimeSpan.Zero,
            true,
            CancellationToken.None);
        var snapshot = await index.GetOrRefreshAsync(
            new SoftwareFileIndexRequest("case-app", "Case App", [new SoftwareFileIndexRoot(softwareRoot.ToUpperInvariant(), "Program")]),
            TimeSpan.Zero,
            true,
            CancellationToken.None);

        Assert.Single(snapshot.Roots);
        Assert.Equal(3, snapshot.TotalBytes);
    }

    [Fact]
    public async Task SearchAsync_UsesUnicodeFileNamePathAndSoftwareNameIndexes()
    {
        var index = CreateIndex();
        var softwareRoot = Directory.CreateDirectory(Path.Combine(testRoot, "search-app")).FullName;
        var diagnosticRoot = Directory.CreateDirectory(Path.Combine(softwareRoot, "diagnostic-telemetry")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(softwareRoot, "性能监控数据.json"), new byte[3]);
        await File.WriteAllBytesAsync(Path.Combine(diagnosticRoot, "settings.json"), new byte[4]);
        var request = new SoftwareFileIndexRequest(
            "search-app",
            "Search Product",
            [new SoftwareFileIndexRoot(softwareRoot, "Program")]);
        await index.GetOrRefreshAsync(request, TimeSpan.Zero, true, CancellationToken.None);

        Assert.Contains(
            await index.SearchAsync("监控数", 10, CancellationToken.None),
            static result => result.FileName == "性能监控数据.json");
        Assert.Contains(
            await index.SearchAsync("diagnostic", 10, CancellationToken.None),
            static result => result.FileName == "settings.json");
        Assert.Equal(2, (await index.SearchAsync("Product", 10, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Refresh_AcrossStagingBatchBoundary_MergesAndDeletesAtomically()
    {
        var index = CreateIndex();
        var softwareRoot = Directory.CreateDirectory(Path.Combine(testRoot, "batch-app")).FullName;
        for (var fileIndex = 0; fileIndex < 97; fileIndex++)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(softwareRoot, $"file-{fileIndex:D3}.bin"),
                new byte[fileIndex + 1]);
        }
        var request = new SoftwareFileIndexRequest(
            "batch-app",
            "Batch App",
            [new SoftwareFileIndexRoot(softwareRoot, "Program")]);

        var initial = await index.GetOrRefreshAsync(request, TimeSpan.Zero, true, CancellationToken.None);

        Assert.Equal(97, initial.FileCount);
        Assert.Equal(Enumerable.Range(1, 97).Sum(), initial.TotalBytes);

        File.Delete(Path.Combine(softwareRoot, "file-000.bin"));
        await File.WriteAllBytesAsync(Path.Combine(softwareRoot, "file-096.bin"), new byte[200]);
        var refreshed = await index.GetOrRefreshAsync(request, TimeSpan.Zero, true, CancellationToken.None);

        Assert.Equal(96, refreshed.FileCount);
        Assert.Equal(Enumerable.Range(2, 95).Sum() + 200, refreshed.TotalBytes);
    }

    [Fact]
    public async Task Refresh_WhenRootIsTemporarilyUnavailable_PreservesLastGoodSnapshot()
    {
        var index = CreateIndex();
        var softwareRoot = Directory.CreateDirectory(Path.Combine(testRoot, "offline-app")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(softwareRoot, "data.bin"), new byte[19]);
        var request = new SoftwareFileIndexRequest(
            "offline-app",
            "Offline App",
            [new SoftwareFileIndexRoot(softwareRoot, "Program")]);
        var initial = await index.GetOrRefreshAsync(request, TimeSpan.Zero, true, CancellationToken.None);
        Directory.Move(softwareRoot, $"{softwareRoot}-offline");

        var retained = await index.GetOrRefreshAsync(request, TimeSpan.Zero, true, CancellationToken.None);

        Assert.Equal(initial.TotalBytes, retained.TotalBytes);
        Assert.Equal(initial.FileCount, retained.FileCount);
        Assert.Single(retained.Roots);
    }

    [Fact]
    public void NormalizePath_PreservesDriveRootSeparator()
    {
        var driveRoot = Path.GetPathRoot(testRoot)!;

        var normalized = SqliteSoftwareFileIndex.NormalizePath(driveRoot);

        Assert.Equal(driveRoot, normalized, ignoreCase: true);
        Assert.True(Path.EndsInDirectorySeparator(normalized));
    }

    public void Dispose()
    {
        foreach (var workspace in nativeWorkspaces)
        {
            workspace.Dispose();
        }
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(testRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task CallerSnapshotExcludesUnreadableChildFilesAndDoesNotChangeServiceTotals()
    {
        var index = CreateIndex();
        var root = Directory.CreateDirectory(Path.Combine(testRoot, "visible-app")).FullName;
        var privateDirectory = Directory.CreateDirectory(Path.Combine(root, "private")).FullName;
        var deniedFile = Path.Combine(root, "denied.bin");
        var goneFile = Path.Combine(root, "gone.bin");
        await File.WriteAllBytesAsync(Path.Combine(root, "public.bin"), new byte[7]);
        await File.WriteAllBytesAsync(deniedFile, new byte[13]);
        await File.WriteAllBytesAsync(Path.Combine(privateDirectory, "secret.bin"), new byte[29]);
        await File.WriteAllBytesAsync(goneFile, new byte[11]);
        await index.GetOrRefreshAsync(new("visible-app", "Visible App", [new(root, "Program")]),
            TimeSpan.Zero, true, default);
        File.Delete(goneFile);

        using var caller = WindowsIdentity.GetCurrent();
        var fileSecurity = new FileInfo(deniedFile).GetAccessControl();
        var fileDeny = new FileSystemAccessRule(caller.User!, FileSystemRights.ReadData, AccessControlType.Deny);
        var directorySecurity = new DirectoryInfo(privateDirectory).GetAccessControl();
        var directoryDeny = new FileSystemAccessRule(caller.User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        fileSecurity.AddAccessRule(fileDeny);
        directorySecurity.AddAccessRule(directoryDeny);
        new FileInfo(deniedFile).SetAccessControl(fileSecurity);
        new DirectoryInfo(privateDirectory).SetAccessControl(directorySecurity);
        try
        {
            var snapshot = (await index.GetSnapshotAsync("visible-app", default,
                WindowsFileIndexCallerAccess.CreatePathFilter(caller.AccessToken)))!;
            Assert.Equal(7, snapshot.TotalBytes);
            Assert.Equal(1, snapshot.FileCount);
            Assert.Equal(7, Assert.Single(snapshot.Roots).TotalBytes);
            var serviceSnapshot = (await index.GetSnapshotAsync("visible-app", default))!;
            Assert.Equal(60, serviceSnapshot.TotalBytes);
            Assert.Equal(4, serviceSnapshot.FileCount);
            Assert.Null(await index.GetSnapshotAsync("visible-app", default, (_, _) => false));
        }
        finally
        {
            fileSecurity.RemoveAccessRuleSpecific(fileDeny);
            directorySecurity.RemoveAccessRuleSpecific(directoryDeny);
            new FileInfo(deniedFile).SetAccessControl(fileSecurity);
            new DirectoryInfo(privateDirectory).SetAccessControl(directorySecurity);
        }
        // A later caller receives a fresh ACL view, not another request's cached denial.
        var after = (await index.GetSnapshotAsync("visible-app", default,
            WindowsFileIndexCallerAccess.CreatePathFilter(caller.AccessToken)))!;
        Assert.Equal(49, after.TotalBytes);
        Assert.Equal(3, after.FileCount);
    }

    private SqliteSoftwareFileIndex CreateIndex()
    {
        var appRoot = Directory.CreateDirectory(Path.Combine(testRoot, "Resource Manager-APP")).FullName;
        var environment = new TestHostEnvironment(appRoot);
        var workspace = NativeFileQueryTestWorkspaceFactory.Create();
        nativeWorkspaces.Add(workspace);
        return new SqliteSoftwareFileIndex(
            new ResourceManagerDatabase(environment),
            workspace,
            NullLogger<SqliteSoftwareFileIndex>.Instance);
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

internal static class NativeFileQueryTestWorkspaceFactory
{
    internal static NativeFileQueryWorkspace Create(
        Func<ulong, uint, ResourceManager.ReadWrite.Native.INativeFileQueryScratch>? createScratch = null)
    {
        const long residentBudget = 12L * 1024 * 1024;
        var capacity = new CompiledHostManagerFileQueryCapacityPlan(
            MaximumQueryUtf8ByteCount: 4096,
            MaximumQueryRuneCount: 1024,
            MaximumPlanUtf8ByteCount: 131072,
            MaximumSourcePlanCount: 3,
            MaximumCandidateCountPerSource: 1000,
            MaximumSubmittedCandidateCount: 3000,
            MaximumUniqueCandidateCount: 3000,
            MaximumCandidateSubmitBatchCount: 128,
            MaximumCandidateSubmitUtf8ByteCount: 1024 * 1024,
            CandidateTextArenaByteCount: 8 * 1024 * 1024,
            MaximumFileNameUtf8ByteCount: 1024,
            MaximumResultCount: 200,
            EntryIndexCapacity: 4096,
            OrdinalIndexCapacity: 4096);
        var plan = new CompiledHostManagerFileQueryPlan(
            new CompiledHostManagerFileQueryBuildPlan(
                NativeFileQueryAbi.Version,
                "file_query",
                "ResourceManager.NativeCore.dll",
                new string('A', 64),
                MaximumQuerySessionCount: 1,
                MaximumTotalResidentByteBudget: residentBudget,
                CapacityLimits: capacity),
            new CompiledHostManagerFileQueryRecreatePlan(1, capacity),
            new CompiledHostManagerFileQueryHotPublishPlan(
                ConfigurationGeneration: 1,
                ShortQueryRuneThreshold: 3,
                UnicodeTokenizerVersion: NativeFileQueryAbi.UnicodeTokenizerVersion,
                UnicodeRemoveDiacriticsMode: NativeFileQueryAbi.UnicodeRemoveDiacriticsMode,
                TrigramTokenizerContractVersion:
                    NativeFileQueryAbi.TrigramTokenizerContractVersion,
                CandidateLimitMultiplier: 8,
                CandidateLimitFloor: 128,
                CandidateLimitCeiling: 1000,
                FileNamePriority: 1,
                RelativePathPriority: 2,
                SoftwareNamePriority: 3,
                TextMatchingVersion: NativeFileQueryAbi.TextMatchingVersion,
                PerSessionResidentByteBudget: residentBudget,
                TotalResidentByteBudget: residentBudget),
            ConfigurationGeneration: 1,
            ConfigurationSha256: new string('B', 64));
        return new NativeFileQueryWorkspace(plan, createScratch ?? ResourceManager.ReadWrite.Native.NativeFileQueryScratch.CreateOrdinary);
    }
}
