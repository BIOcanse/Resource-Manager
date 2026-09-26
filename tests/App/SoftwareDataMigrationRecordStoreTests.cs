using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.Migration;
using ResourceManager.App.Infrastructure.Migration;

namespace Resource_Manager_APP.Tests;

public sealed class SoftwareDataMigrationRecordStoreTests
{
    [Fact]
    public async Task ExistingRecordsSurviveAppendAndUnreadableFileCannotBeOverwritten()
    {
        using var root = new OwnedRoot();
        var manager = new SoftwareDataMigrationManager(root.Environment);
        Assert.Empty(await manager.GetRecordsAsync(CancellationToken.None));

        await manager.AppendRecordsAsync([Record("one")], CancellationToken.None);
        await manager.AppendRecordsAsync([Record("two")], CancellationToken.None);
        Assert.Equal(["one", "two"], (await manager.GetRecordsAsync(CancellationToken.None))
            .Select(static record => record.Id));

        File.WriteAllText(root.RecordPath, "{invalid");
        await Assert.ThrowsAsync<JsonException>(() => manager.GetRecordsAsync(CancellationToken.None));
        await Assert.ThrowsAsync<JsonException>(() => manager.AppendRecordsAsync([Record("three")], CancellationToken.None));
        Assert.Equal("{invalid", File.ReadAllText(root.RecordPath));
    }

    [Fact]
    public async Task NullRecordsDocumentFailsClosed()
    {
        using var root = new OwnedRoot();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(root.RecordPath)!);
        File.WriteAllText(root.RecordPath, "null");
        var manager = new SoftwareDataMigrationManager(root.Environment);

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.GetRecordsAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.AppendRecordsAsync([Record("one")], CancellationToken.None));
        Assert.Equal("null", File.ReadAllText(root.RecordPath));
    }

    private static SoftwareDataMigrationRecord Record(string id)
        => new(id, "fixture", "fixture", "fixture", @"C:\source", @"C:\destination",
            @"C:\backup", "Migrated", DateTimeOffset.UnixEpoch, null);

    private sealed class OwnedRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"rm-migration-records-{Guid.NewGuid():N}");
        public string RecordPath => System.IO.Path.Combine(Path, "Config", "migration-records.json");
        public IHostEnvironment Environment { get; }

        public OwnedRoot()
        {
            Directory.CreateDirectory(Path);
            Environment = new TestEnvironment(Path);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            Assert.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), Path,
                StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("rm-migration-records-", System.IO.Path.GetFileName(Path),
                StringComparison.Ordinal);
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories),
                entry => File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint));
            Directory.Delete(Path, recursive: true);
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
