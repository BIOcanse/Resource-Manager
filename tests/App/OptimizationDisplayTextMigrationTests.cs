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
}

