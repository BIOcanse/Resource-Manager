using System.Text;
using System.Text.Json;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerRollbackStateAuthorityTests
{
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task OwnerLeaseRejectsConcurrentAuthorityAndCanBeReacquired()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        var first = CreateStore(path);
        var second = CreateStore(path);
        try
        {
            _ = await first.LoadAsync(CancellationToken.None);

            await Assert.ThrowsAsync<HostManagerRollbackStateOwnerLeaseException>(
                () => second.LoadAsync(CancellationToken.None));

            first.Dispose();
            var reserved = await second.ReserveNativeHostSessionIncarnationAsync(
                CancellationToken.None);

            Assert.Equal<ulong>(1, reserved.NativeHostSessionIncarnation);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReservationIsMonotonicAcrossAuthorityReopen()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        try
        {
            using (var first = CreateStore(path))
            {
                var reserved = await first.ReserveNativeHostSessionIncarnationAsync(
                    CancellationToken.None);
                Assert.Equal<ulong>(1, reserved.NativeHostSessionIncarnation);
            }

            var firstEnvelope = HostManagerRollbackStateEnvelopeCodec.Decode(
                await File.ReadAllBytesAsync(path));
            Assert.Equal<ulong>(1, firstEnvelope.Revision);
            Assert.Equal<ulong>(1, firstEnvelope.State.NativeHostSessionIncarnation);

            using (var second = CreateStore(path))
            {
                var reserved = await second.ReserveNativeHostSessionIncarnationAsync(
                    CancellationToken.None);
                Assert.Equal<ulong>(2, reserved.NativeHostSessionIncarnation);
            }

            var secondEnvelope = HostManagerRollbackStateEnvelopeCodec.Decode(
                await File.ReadAllBytesAsync(path));
            Assert.Equal<ulong>(2, secondEnvelope.Revision);
            Assert.Equal<ulong>(2, secondEnvelope.State.NativeHostSessionIncarnation);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GenericSaveCannotAdvanceOrRollBackIncarnation()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        try
        {
            using var store = CreateStore(path);
            var reserved = await store.ReserveNativeHostSessionIncarnationAsync(
                CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(
                reserved with { NativeHostSessionIncarnation = 0 },
                CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(
                reserved with { NativeHostSessionIncarnation = 2 },
                CancellationToken.None));

            Assert.Equal<ulong>(
                1,
                (await store.LoadAsync(CancellationToken.None)).NativeHostSessionIncarnation);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedFirstCreateDoesNotPublishOrConsumeIncarnation()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        try
        {
            using (var failing = new JsonHostManagerRollbackStateStore(
                       path,
                       TimeProvider.System,
                       CheckpointInterval,
                       new FailFirstCommitter(),
                       WindowsHostManagerDurableRootManifestCommitter.Instance))
            {
                var exception = await Assert.ThrowsAsync<HostManagerRollbackStateCommitException>(
                    () => failing.ReserveNativeHostSessionIncarnationAsync(
                        CancellationToken.None));
                Assert.Equal(
                    HostManagerRollbackStateCommitOutcome.NotCommitted,
                    exception.Outcome);
                Assert.False(File.Exists(path));
            }

            using var reopened = CreateStore(path);
            var reserved = await reopened.ReserveNativeHostSessionIncarnationAsync(
                CancellationToken.None);
            Assert.Equal<ulong>(1, reserved.NativeHostSessionIncarnation);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AmbiguousFirstCreateIsRecoveredWithoutReissuingIncarnation()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        try
        {
            using var store = new JsonHostManagerRollbackStateStore(
                path,
                TimeProvider.System,
                CheckpointInterval,
                new CommitThenReportAmbiguousCommitter(),
                WindowsHostManagerDurableRootManifestCommitter.Instance);

            var exception = await Assert.ThrowsAsync<HostManagerRollbackStateCommitException>(
                () => store.ReserveNativeHostSessionIncarnationAsync(
                    CancellationToken.None));
            Assert.Equal(
                HostManagerRollbackStateCommitOutcome.CommitAmbiguous,
                exception.Outcome);

            var recovered = await store.LoadAsync(CancellationToken.None);
            Assert.Equal<ulong>(1, recovered.NativeHostSessionIncarnation);

            var next = await store.ReserveNativeHostSessionIncarnationAsync(
                CancellationToken.None);
            Assert.Equal<ulong>(2, next.NativeHostSessionIncarnation);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ChecksumMismatchIsRejectedBeforeAuthorityPublication()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        try
        {
            var image = HostManagerRollbackStateEnvelopeCodec.Encode(
                HostManagerRollbackStateDocument.Empty with
                {
                    NativeHostSessionIncarnation = 7
                },
                revision: 9);
            await File.WriteAllBytesAsync(path, CorruptChecksum(image));

            using var store = CreateStore(path);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => store.LoadAsync(CancellationToken.None));

            Assert.Contains("checksum", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PayloadLengthMismatchIsRejectedBeforeAuthorityPublication()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        try
        {
            var image = HostManagerRollbackStateEnvelopeCodec.Encode(
                HostManagerRollbackStateDocument.Empty,
                revision: 1);
            await File.WriteAllBytesAsync(path, CorruptPayloadLength(image));

            using var store = CreateStore(path);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => store.LoadAsync(CancellationToken.None));

            Assert.Contains("payload length", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnvelopeWithoutDurableRootManifestFailsClosed()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        try
        {
            await File.WriteAllBytesAsync(
                path,
                HostManagerRollbackStateEnvelopeCodec.Encode(
                    HostManagerRollbackStateDocument.Empty with
                    {
                        NativeHostSessionIncarnation = 7
                    },
                    revision: 9));

            using var store = CreateStore(path);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => store.LoadAsync(CancellationToken.None));

            Assert.Contains("not initialized", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedCanonicalIsRejectedBeforeJsonParsing()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        try
        {
            await using (var stream = new FileStream(
                             path,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                stream.SetLength(
                    HostManagerRollbackStateEnvelopeCodec.MaximumCanonicalBytes + 1L);
            }

            using var store = CreateStore(path);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => store.LoadAsync(CancellationToken.None));

            Assert.Contains("canonical size", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ValidExternalCanonicalMutationFailsCasValidation()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        try
        {
            using var store = CreateStore(path);
            var reserved = await store.ReserveNativeHostSessionIncarnationAsync(
                CancellationToken.None);
            var externalImage = HostManagerRollbackStateEnvelopeCodec.Encode(
                reserved with { Message = "external mutation" },
                revision: 1);
            await File.WriteAllBytesAsync(path, externalImage);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => store.ReserveNativeHostSessionIncarnationAsync(
                    CancellationToken.None));

            Assert.Contains("outside its active owner", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RolledBackValidEnvelopeIsRejectedByDurableRootManifest()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        try
        {
            using (var first = CreateStore(path))
            {
                _ = await first.ReserveNativeHostSessionIncarnationAsync(
                    CancellationToken.None);
            }
            var firstImage = await File.ReadAllBytesAsync(path);

            using (var second = CreateStore(path))
            {
                _ = await second.ReserveNativeHostSessionIncarnationAsync(
                    CancellationToken.None);
            }
            await File.WriteAllBytesAsync(path, firstImage);

            using var reopened = CreateStore(path);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => reopened.LoadAsync(CancellationToken.None));

            Assert.Contains("manifest does not match", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializedAuthorityMissingCanonicalFailsClosed()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        try
        {
            using (var first = CreateStore(path))
            {
                _ = await first.ReserveNativeHostSessionIncarnationAsync(
                    CancellationToken.None);
            }
            File.Delete(path);

            using var reopened = CreateStore(path);
            var exception = await Assert.ThrowsAsync<IOException>(
                () => reopened.LoadAsync(CancellationToken.None));

            Assert.Contains("lost its canonical file", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedDurableRootManifestIsRejectedBeforeAllocation()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "rollback-state.json");
        var manifestPath = $"{path}.root.manifest";
        try
        {
            using (var first = CreateStore(path))
            {
                _ = await first.ReserveNativeHostSessionIncarnationAsync(
                    CancellationToken.None);
            }
            await using (var stream = new FileStream(
                             manifestPath,
                             FileMode.Append,
                             FileAccess.Write,
                             FileShare.None))
            {
                await stream.WriteAsync(new byte[] { 0xFF });
            }

            using var reopened = CreateStore(path);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => reopened.LoadAsync(CancellationToken.None));

            Assert.Contains("manifest has an invalid size", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProductionPathMigratesPackageStateOnceAndKeepsStableAuthority()
    {
        var root = CreateTempDirectory();
        var packageRoot = Path.Combine(root, "package");
        var contentRoot = Path.Combine(packageRoot, "Resource Manager-APP");
        var stableRoot = Path.Combine(root, "stable");
        Directory.CreateDirectory(contentRoot);
        try
        {
            var paths = JsonHostManagerRollbackStateStore.ResolveProductionPaths(
                contentRoot,
                stableRoot);
            var packageLegacyPath = Path.Combine(
                packageRoot,
                "Config",
                "smart-optimization-state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(packageLegacyPath)!);
            await WriteLegacyVersion2Async(packageLegacyPath, incarnation: 7);

            Assert.StartsWith(
                Path.GetFullPath(stableRoot),
                paths.CanonicalPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                Path.Combine(
                    Path.GetFullPath(stableRoot),
                    "UserData",
                    "HostManager",
                    "RollbackState",
                    "rollback-state.owner.lock"),
                paths.OwnerLeasePath,
                ignoreCase: true);
            Assert.Equal(
                Path.Combine(
                    Path.GetFullPath(stableRoot),
                    "UserData",
                    "HostManager",
                    "RollbackState",
                    "rollback-state.root.manifest"),
                paths.RootManifestPath,
                ignoreCase: true);
            Assert.Contains(
                paths.LegacyPaths,
                path => path.Equals(
                    Path.GetFullPath(packageLegacyPath),
                    StringComparison.OrdinalIgnoreCase));

            using (var migrating = new JsonHostManagerRollbackStateStore(
                       paths,
                       TimeProvider.System,
                       CheckpointInterval))
            {
                var migrated = await migrating.LoadAsync(CancellationToken.None);
                Assert.Equal<ulong>(7, migrated.NativeHostSessionIncarnation);
            }

            await WriteLegacyVersion2Async(packageLegacyPath, incarnation: 1);
            using var reopened = new JsonHostManagerRollbackStateStore(
                paths,
                TimeProvider.System,
                CheckpointInterval);
            var reserved = await reopened.ReserveNativeHostSessionIncarnationAsync(
                CancellationToken.None);

            Assert.Equal<ulong>(8, reserved.NativeHostSessionIncarnation);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProductionGenerationLossFailsClosedBeforeDirectoryRecreationOrLegacyFallback()
    {
        var root = CreateTempDirectory();
        var packageRoot = Path.Combine(root, "package");
        var contentRoot = Path.Combine(packageRoot, "Resource Manager-APP");
        var stableRoot = Path.Combine(root, "stable");
        Directory.CreateDirectory(contentRoot);
        try
        {
            var paths = JsonHostManagerRollbackStateStore.ResolveProductionPaths(
                contentRoot,
                stableRoot);
            using (var initialized = new JsonHostManagerRollbackStateStore(
                       paths,
                       TimeProvider.System,
                       CheckpointInterval))
            {
                var reserved = await initialized.ReserveNativeHostSessionIncarnationAsync(
                    CancellationToken.None);
                Assert.Equal<ulong>(1, reserved.NativeHostSessionIncarnation);
            }

            var generationDirectory = Path.GetDirectoryName(paths.CanonicalPath)!;
            Directory.Delete(generationDirectory, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.LegacyPaths[0])!);
            await WriteLegacyVersion2Async(paths.LegacyPaths[0], incarnation: 0);

            Assert.True(File.Exists(paths.RootManifestPath));
            Assert.True(File.Exists(paths.OwnerLeasePath));
            Assert.False(Directory.Exists(generationDirectory));

            using var reopened = new JsonHostManagerRollbackStateStore(
                paths,
                TimeProvider.System,
                CheckpointInterval);
            var exception = await Assert.ThrowsAsync<IOException>(
                () => reopened.LoadAsync(CancellationToken.None));

            Assert.Contains("lost its canonical file", exception.Message);
            Assert.False(Directory.Exists(generationDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonHostManagerRollbackStateStore CreateStore(string path)
        => new(path, TimeProvider.System, CheckpointInterval);

    private static byte[] CorruptChecksum(byte[] image)
    {
        var json = Encoding.UTF8.GetString(image);
        const string marker = "\"checksumSha256\":\"";
        var checksumOffset = json.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(checksumOffset >= 0);
        checksumOffset += marker.Length;
        var characters = json.ToCharArray();
        characters[checksumOffset] = characters[checksumOffset] == '0' ? '1' : '0';
        return Encoding.UTF8.GetBytes(characters);
    }

    private static byte[] CorruptPayloadLength(byte[] image)
    {
        var json = Encoding.UTF8.GetString(image);
        const string marker = "\"payloadLength\":";
        var valueOffset = json.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(valueOffset >= 0);
        valueOffset += marker.Length;
        var valueEnd = json.IndexOf(',', valueOffset);
        Assert.True(valueEnd > valueOffset);
        var payloadLength = int.Parse(json.AsSpan(valueOffset, valueEnd - valueOffset));
        return Encoding.UTF8.GetBytes(
            string.Concat(
                json.AsSpan(0, valueOffset),
                (payloadLength + 1).ToString(),
                json.AsSpan(valueEnd)));
    }

    private static Task WriteLegacyVersion2Async(string path, ulong incarnation)
    {
        var state = HostManagerRollbackStateDocument.Empty with
        {
            NativeHostSessionIncarnation = incarnation,
            Message = "legacy"
        };
        return File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                state,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"resource-manager-rollback-authority-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FailFirstCommitter : IHostManagerRollbackStateFileCommitter
    {
        private bool failNext = true;

        public void Commit(
            string temporaryPath,
            string canonicalPath,
            ReadOnlySpan<byte> expectedImage,
            bool replaceExisting)
        {
            if (failNext)
            {
                failNext = false;
                throw new HostManagerRollbackStateCommitException(
                    HostManagerRollbackStateCommitOutcome.NotCommitted,
                    new IOException("simulated first-create interruption"));
            }

            WindowsHostManagerRollbackStateFileCommitter.Instance.Commit(
                temporaryPath,
                canonicalPath,
                expectedImage,
                replaceExisting);
        }
    }

    private sealed class CommitThenReportAmbiguousCommitter
        : IHostManagerRollbackStateFileCommitter
    {
        private bool reportAmbiguous = true;

        public void Commit(
            string temporaryPath,
            string canonicalPath,
            ReadOnlySpan<byte> expectedImage,
            bool replaceExisting)
        {
            WindowsHostManagerRollbackStateFileCommitter.Instance.Commit(
                temporaryPath,
                canonicalPath,
                expectedImage,
                replaceExisting);
            if (!reportAmbiguous)
            {
                return;
            }

            reportAmbiguous = false;
            throw new HostManagerRollbackStateCommitException(
                HostManagerRollbackStateCommitOutcome.CommitAmbiguous,
                new IOException("simulated post-commit verification loss"));
        }
    }
}
