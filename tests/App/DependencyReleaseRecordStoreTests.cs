using System.IO.Compression;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Dependencies;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class DependencyReleaseRecordStoreTests
{
    [Fact]
    public async Task ReceiptReplacementCoexistsWithCommittedReaders()
    {
        var path = Path.Combine(Path.GetTempPath(), "rm-component-receipt-" + Guid.NewGuid().ToString("N") + ".json");
        var oldRecord = new DependencyReleaseRecord("old", "old.zip", new string('A', 64));
        var newRecord = new DependencyReleaseRecord("new", "new.zip", new string('B', 64));
        try
        {
            DependencyReleaseRecordStore.Write(path, oldRecord);
            using var oldReader = WindowsNativeAtomicFileCommitter.OpenCommittedRead(path);
            await Task.Run(() => DependencyReleaseRecordStore.Write(path, newRecord));
            Assert.Equal(oldRecord, JsonSerializer.Deserialize(oldReader, DependencyReleaseJsonContext.Default.DependencyReleaseRecord));
            Assert.Equal(newRecord, DependencyReleaseRecordStore.Read(path));
            await Task.WhenAll(
                Task.Run(() => { for (var index = 0; index < 30; index++) Assert.NotNull(DependencyReleaseRecordStore.Read(path)); }),
                Task.Run(() => { for (var index = 0; index < 30; index++) DependencyReleaseRecordStore.Write(path, index % 2 == 0 ? oldRecord : newRecord); }));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReplacementExcludesOldReceiptRegardlessOfWindowsFileNameCasing()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-component-receipt-case-" + Guid.NewGuid().ToString("N"));
        var live = Path.Combine(root, "component");
        Directory.CreateDirectory(live);
        try
        {
            File.WriteAllText(Path.Combine(live, DependencyReleaseRecordStore.FileName.ToUpperInvariant()), "old receipt");
            File.WriteAllText(Path.Combine(live, "payload.bin"), "old");
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            { using var writer = new StreamWriter(archive.CreateEntry("payload.bin").Open()); writer.Write("new"); }
            buffer.Position = 0;
            using var input = new ZipArchive(buffer, ZipArchiveMode.Read);
            using (var transaction = new DependencyPayloadTransaction(live, replaceRelease: true))
            {
                transaction.Prepare(input, ["payload.bin"], [], false, null, default);
                transaction.Publish(default);
            }
            Assert.Null(DependencyReleaseRecordStore.Read(DependencyReleaseRecordStore.InstalledPath(live)));
            Assert.Equal("new", File.ReadAllText(Path.Combine(live, "payload.bin")));
        }
        finally { DependencyInstallPaths.DeleteOwnedTree(root); }
    }
}
