using System.Reflection;
using System.Text.Json;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class OptimizationDisplayTextMigrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FrozenV2FileIsBackedUpExactlyAndRewrittenOnce(bool corruptChecksum, bool withManifest)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rm-display-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "rollback-state.json");
        try
        {
            var assembly = typeof(OptimizationDisplayTextMigrationTests).Assembly;
            var name = Assert.Single(assembly.GetManifestResourceNames(), name => name.EndsWith("rollback-state.c16-v2.json", StringComparison.Ordinal));
            using var input = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            var original = buffer.ToArray();
            if (corruptChecksum) original[^4] = original[^4] == (byte)'0' ? (byte)'1' : (byte)'0';
            await File.WriteAllBytesAsync(path, original);
            byte[]? originalManifest = null;
            if (withManifest)
            {
                var manifest = new HostManagerDurableRootManifest(path, "rollback-state");
                manifest.EnsureInitializing();
                manifest.FinalizeCanonicalTransition(manifest.BeginCanonicalTransition(original));
                originalManifest = await File.ReadAllBytesAsync(path + ".root.manifest");
            }
            using (var store = new JsonHostManagerRollbackStateStore(path, TimeProvider.System, TimeSpan.FromMinutes(1)))
            {
                var state = await store.LoadAsync(default);
                Assert.Equal(3, state.Version);
                Assert.Null(state.Coordinator); // Never parse diagnostic prose to reconstruct facts.
                if (corruptChecksum) Assert.Empty(state.AppliedPlacements);
                else
                {
                    Assert.Equal(7UL, state.NativeHostSessionIncarnation);
                    var placement = Assert.Single(state.AppliedPlacements);
                    Assert.Equal(42, placement.ProcessId);
                    Assert.Equal("133700000000000000", placement.ProcessStartKey);
                    Assert.Equal("example", placement.ProcessName);
                    Assert.Equal("cpu-record", Assert.Single(placement.Records).RecordId);
                }
            }
            var backup = Assert.Single(Directory.GetFiles(directory, "rollback-state.json.display-text.*.backup"));
            Assert.Equal(original, await File.ReadAllBytesAsync(backup));
            if (originalManifest is not null)
            {
                var manifestBackup = Assert.Single(Directory.GetFiles(directory, "rollback-state.json.root.manifest.*.backup"));
                Assert.Equal(originalManifest, await File.ReadAllBytesAsync(manifestBackup));
            }
            var converted = await File.ReadAllBytesAsync(path);
            using var json = JsonDocument.Parse(converted);
            Assert.Equal(corruptChecksum ? 1UL : 4UL, json.RootElement.GetProperty("revision").GetUInt64());
            Assert.False(json.RootElement.GetProperty("state").TryGetProperty("message", out _));
            foreach (var placement in json.RootElement.GetProperty("state").GetProperty("appliedPlacements").EnumerateArray())
                Assert.False(placement.TryGetProperty("displayName", out _));
            using (var reopened = new JsonHostManagerRollbackStateStore(path, TimeProvider.System, TimeSpan.FromMinutes(1)))
                Assert.Equal(3, (await reopened.LoadAsync(default)).Version);
            Assert.Equal(converted, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.GetFiles(directory, "rollback-state.json.display-text.*.backup"));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
    [Fact]
    public async Task TruncatedCurrentV3DoesNotMigrateOrChangeCanonicalAndManifest()
    {
        var root = Directory.CreateTempSubdirectory("rm-truncated-v3-").FullName;
        var path = Path.Combine(root, "rollback-state.json");
        try
        {
            using (var store = new JsonHostManagerRollbackStateStore(path, TimeProvider.System, TimeSpan.FromMinutes(1)))
                await store.SaveAsync(HostManagerRollbackStateDocument.Empty with
                {
                    AppliedPlacements = [new("process:42:123", "example", "cpu",
                        [new("cpu_affinity", "owned-record")], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)]
                }, default);
            var complete = WindowsNativeAtomicFileCommitter.ReadCommittedBytes(path);
            var truncated = complete[..^1];
            File.WriteAllBytes(path, truncated);
            var manifest = WindowsNativeAtomicFileCommitter.ReadCommittedBytes(path + ".root.manifest");
            using var reopened = new JsonHostManagerRollbackStateStore(path, TimeProvider.System, TimeSpan.FromMinutes(1));
            await Assert.ThrowsAnyAsync<JsonException>(() => reopened.LoadAsync(default));
            Assert.Equal(truncated, WindowsNativeAtomicFileCommitter.ReadCommittedBytes(path));
            Assert.Equal(manifest, WindowsNativeAtomicFileCommitter.ReadCommittedBytes(path + ".root.manifest"));
            Assert.Empty(Directory.GetFiles(root, "*.backup"));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedBackupCanRetryAndDifferentEarlierBackupIsPreserved(bool interruptedFinalFromOldWriter)
    {
        var root = Directory.CreateTempSubdirectory("rm-backup-retry-").FullName;
        var path = Path.Combine(root, "rollback-state.json");
        try
        {
            using var source = typeof(OptimizationDisplayTextMigrationTests).Assembly.GetManifestResourceStream(
                Assert.Single(typeof(OptimizationDisplayTextMigrationTests).Assembly.GetManifestResourceNames(),
                    name => name.EndsWith("rollback-state.c16-v2.json", StringComparison.Ordinal)))!;
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var original = buffer.ToArray();
            File.WriteAllBytes(path, original);
            var firstBackup = path + ".display-text.byte-exact." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)).ToLowerInvariant() + ".backup";
            var partial = original[..(original.Length / 2)];
            if (interruptedFinalFromOldWriter) File.WriteAllBytes(firstBackup, partial);
            else
            {
                Assert.Throws<OperationCanceledException>(() => OptimizationDisplayTextBackup.Preserve(path, original, (output, bytes) =>
                {
                    output.Write(bytes.AsSpan(0, bytes.Length / 2));
                    output.Flush();
                    throw new OperationCanceledException("interrupted backup");
                }));
                Assert.False(File.Exists(firstBackup));
                Assert.Empty(Directory.GetFiles(root, "*.tmp"));
            }
            Assert.Equal(original, WindowsNativeAtomicFileCommitter.ReadCommittedBytes(path));
            using (var store = new JsonHostManagerRollbackStateStore(path, TimeProvider.System, TimeSpan.FromMinutes(1)))
                Assert.Single((await store.LoadAsync(default)).AppliedPlacements);
            var completeBackup = OptimizationDisplayTextBackup.Preserve(path, original);
            Assert.Equal(original, WindowsNativeAtomicFileCommitter.ReadCommittedBytes(completeBackup));
            Assert.Equal(completeBackup, OptimizationDisplayTextBackup.Preserve(path, original));
            Assert.Equal(interruptedFinalFromOldWriter ? 2 : 1, Directory.GetFiles(root, "*.backup").Length);
            if (interruptedFinalFromOldWriter)
                Assert.Equal(partial, WindowsNativeAtomicFileCommitter.ReadCommittedBytes(firstBackup));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }

}
