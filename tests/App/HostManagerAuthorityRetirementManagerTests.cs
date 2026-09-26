using System.Security.Cryptography;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerAuthorityRetirementManagerTests
{
    [Fact]
    public void ProductionPathsKeepAllAuthorityDataInsideTheStableRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "resource-manager-authority-retirement-paths",
            Guid.NewGuid().ToString("N"));

        var paths = HostManagerAuthorityRetirementManager.CompileProductionPaths(root);

        Assert.Equal(Path.GetFullPath(root), paths.StorageBoundaryRoot);
        Assert.Equal(
            Path.Combine(root, "UserData"),
            paths.RuntimeDataRoot);
        Assert.Equal(
            Path.Combine(
                root,
                "UserData",
                "HostManager",
                "AuthorityRetirement",
                "generation-00000001"),
            paths.RetirementDirectory);
    }

    [Fact]
    public void TombstoneCodecRoundTripsExactIdentity()
    {
        var record = CreateRecord(
            HostManagerAuthorityKind.TransactionJournal,
            "authorities/A/journal.bin",
            "authorities/A/payloads");

        var image = record.Encode();
        var decoded = HostManagerAuthorityRetirementRecord.Decode(image);

        Assert.Equal(record.TicketId, decoded.TicketId);
        Assert.Equal(record.Kind, decoded.Kind);
        Assert.Equal(record.CanonicalRelativePath, decoded.CanonicalRelativePath);
        Assert.Equal(record.PayloadRelativeDirectory, decoded.PayloadRelativeDirectory);
        Assert.Equal(record.RootIncarnation, decoded.RootIncarnation);
        Assert.Equal(record.CanonicalLength, decoded.CanonicalLength);
        Assert.Equal(record.ManifestLength, decoded.ManifestLength);
        Assert.Equal(record.CanonicalSha256, decoded.CanonicalSha256);
        Assert.Equal(record.ManifestSha256, decoded.ManifestSha256);
        Assert.Equal(image, decoded.Encode());
    }

    [Fact]
    public void TombstoneCodecRejectsChecksumAndPathEscapes()
    {
        var record = CreateRecord(
            HostManagerAuthorityKind.AppliedOwnership,
            "authorities/A/ledger.bin");
        var image = record.Encode();
        image[64] ^= 0x5a;

        Assert.Throws<InvalidDataException>(() =>
            HostManagerAuthorityRetirementRecord.Decode(image));
        Assert.Throws<InvalidDataException>(() => (record with
        {
            CanonicalRelativePath = "authorities/../ledger.bin"
        }).Encode());
    }

    [Fact]
    public async Task DisposeFailureKeepsIntentAndPerformsNoNamespaceDelete()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var authority = CreateAppliedOwnershipAuthority(root, "A", [1, 2, 3, 4]);
            var storage = new FaultInjectingStorage();
            using var manager = new HostManagerAuthorityRetirementManager(root, storage);
            var resource = new FaultInjectingResource(disposeFailures: 1);
            var ticket = await manager.PrepareAppliedOwnershipAsync(
                authority.CanonicalPath,
                CancellationToken.None);
            manager.Attach(ticket, resource);

            await manager.RetryAsync();

            Assert.Equal(1, manager.PendingCount(HostManagerAuthorityKind.AppliedOwnership));
            Assert.NotNull(manager.LastFailure);
            Assert.Empty(storage.DeletedPaths);
            manager.RequireAvailable(Path.Combine(root, "authorities", "B", "ledger.bin"));
            Assert.Throws<InvalidOperationException>(() =>
                manager.RequireAvailable(authority.CanonicalPath));

            await manager.RetryAsync();

            Assert.Equal(0, manager.PendingCount(HostManagerAuthorityKind.AppliedOwnership));
            Assert.Null(manager.LastFailure);
            Assert.Equal(2, resource.DisposeCalls);
            Assert.False(File.Exists(authority.CanonicalPath));
            Assert.False(File.Exists(authority.ManifestPath));
            Assert.False(File.Exists(authority.OwnerLockPath));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task DeleteFailureAndRestartReserveAUntilExactRetirementCompletes()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var firstImage = new byte[] { 9, 8, 7, 6 };
            var authority = CreateAppliedOwnershipAuthority(root, "A", firstImage);
            var storage = new FaultInjectingStorage(
                failDeletePathOnce: authority.CanonicalPath);
            using (var manager = new HostManagerAuthorityRetirementManager(root, storage))
            {
                var ticket = await manager.PrepareAppliedOwnershipAsync(
                    authority.CanonicalPath,
                    CancellationToken.None);
                manager.Attach(ticket, new FaultInjectingResource(0));
                await manager.RetryAsync();

                Assert.Equal(1, manager.PendingCount(HostManagerAuthorityKind.AppliedOwnership));
                Assert.True(File.Exists(authority.CanonicalPath));
                Assert.Throws<InvalidOperationException>(() =>
                    manager.RequireAvailable(authority.CanonicalPath));
            }

            using (var restarted = new HostManagerAuthorityRetirementManager(root, storage))
            {
                Assert.Throws<InvalidOperationException>(() =>
                    restarted.RequireAvailable(authority.CanonicalPath));
                await restarted.RetryAsync();
                Assert.Equal(0, restarted.PendingCount(
                    HostManagerAuthorityKind.AppliedOwnership));
                restarted.RequireAvailable(authority.CanonicalPath);
            }

            var secondImage = new byte[] { 1, 3, 5, 7, 9 };
            var second = CreateAppliedOwnershipAuthority(root, "A", secondImage);
            var secondManifest = File.ReadAllBytes(second.ManifestPath);
            using (var final = new HostManagerAuthorityRetirementManager(root, storage))
            {
                await final.RetryAsync();
            }

            Assert.Equal(secondImage, File.ReadAllBytes(second.CanonicalPath));
            Assert.Equal(secondManifest, File.ReadAllBytes(second.ManifestPath));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task CanonicalDigestMismatchRetainsEveryArtifactAndTombstone()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var authority = CreateAppliedOwnershipAuthority(root, "A", [4, 3, 2, 1]);
            using (var manager = new HostManagerAuthorityRetirementManager(
                       root,
                       WindowsHostManagerAuthorityRetirementStorage.Instance))
            {
                _ = await manager.PrepareAppliedOwnershipAsync(
                    authority.CanonicalPath,
                    CancellationToken.None);
            }
            var manifestBefore = File.ReadAllBytes(authority.ManifestPath);
            File.WriteAllBytes(authority.CanonicalPath, [4, 3, 2, 0]);

            using var restarted = new HostManagerAuthorityRetirementManager(
                root,
                WindowsHostManagerAuthorityRetirementStorage.Instance);
            await restarted.RetryAsync();

            Assert.Equal(1, restarted.PendingCount(
                HostManagerAuthorityKind.AppliedOwnership));
            Assert.IsType<InvalidDataException>(restarted.LastFailure);
            Assert.True(File.Exists(authority.CanonicalPath));
            Assert.Equal(manifestBefore, File.ReadAllBytes(authority.ManifestPath));
            Assert.True(File.Exists(authority.OwnerLockPath));
            Assert.Single(Directory.EnumerateFiles(
                restarted.RetirementDirectory,
                "retirement-*.bin"));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task RootIncarnationMismatchWithSameCanonicalDigestDeletesNothing()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var image = new byte[] { 7, 7, 7, 7 };
            var authority = CreateAppliedOwnershipAuthority(root, "A", image);
            using (var manager = new HostManagerAuthorityRetirementManager(
                       root,
                       WindowsHostManagerAuthorityRetirementStorage.Instance))
            {
                _ = await manager.PrepareAppliedOwnershipAsync(
                    authority.CanonicalPath,
                    CancellationToken.None);
            }

            File.Delete(authority.ManifestPath);
            new HostManagerDurableRootManifest(
                    authority.CanonicalPath,
                    "applied-ownership")
                .ValidateOrAdoptCanonical(image);
            var replacementManifest = File.ReadAllBytes(authority.ManifestPath);

            using var restarted = new HostManagerAuthorityRetirementManager(
                root,
                WindowsHostManagerAuthorityRetirementStorage.Instance);
            await restarted.RetryAsync();

            Assert.Equal(1, restarted.PendingCount(
                HostManagerAuthorityKind.AppliedOwnership));
            Assert.Equal(image, File.ReadAllBytes(authority.CanonicalPath));
            Assert.Equal(replacementManifest, File.ReadAllBytes(authority.ManifestPath));
            Assert.True(File.Exists(authority.OwnerLockPath));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task JournalRetirementDeletesItsCompleteKnownClosureAndKeepsSibling()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var authority = CreateTransactionJournalAuthority(root, "A", [2, 4, 6, 8]);
            var sibling = Path.Combine(root, "authorities", "sibling.keep");
            Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
            File.WriteAllText(sibling, "keep");
            using var manager = new HostManagerAuthorityRetirementManager(
                root,
                WindowsHostManagerAuthorityRetirementStorage.Instance);
            var ticket = await manager.PrepareTransactionJournalAsync(
                authority.CanonicalPath,
                authority.PayloadDirectory!,
                CancellationToken.None);
            manager.Attach(ticket, new FaultInjectingResource(0));

            await manager.RetryAsync();

            Assert.Equal(0, manager.PendingCount(
                HostManagerAuthorityKind.TransactionJournal));
            Assert.False(File.Exists(authority.CanonicalPath));
            Assert.False(File.Exists(authority.ManifestPath));
            Assert.False(File.Exists(authority.OwnerLockPath));
            Assert.False(Directory.Exists(authority.PayloadDirectory));
            Assert.Equal("keep", File.ReadAllText(sibling));
            Assert.Empty(Directory.EnumerateFiles(
                manager.RetirementDirectory,
                "retirement-*.bin"));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task UnknownPayloadMemberFailsClosedBeforeAnyPayloadDelete()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var authority = CreateTransactionJournalAuthority(root, "A", [6, 6, 6, 6]);
            var unknown = Path.Combine(authority.PayloadDirectory!, "unrecognized.bin");
            File.WriteAllText(unknown, "do-not-delete");
            var sequence = Path.Combine(authority.PayloadDirectory!, "payload-sequence.bin");
            var storage = new FaultInjectingStorage();
            using var manager = new HostManagerAuthorityRetirementManager(root, storage);
            var ticket = await manager.PrepareTransactionJournalAsync(
                authority.CanonicalPath,
                authority.PayloadDirectory!,
                CancellationToken.None);
            manager.Attach(ticket, new FaultInjectingResource(0));

            await manager.RetryAsync();

            Assert.Equal(1, manager.PendingCount(
                HostManagerAuthorityKind.TransactionJournal));
            Assert.IsType<InvalidDataException>(manager.LastFailure);
            Assert.Equal("do-not-delete", File.ReadAllText(unknown));
            Assert.True(File.Exists(sequence));
            Assert.DoesNotContain(sequence, storage.DeletedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.True(File.Exists(authority.CanonicalPath));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task CommitAfterEffectFailureCreatesReservationUntilDurableCancel()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var authority = CreateAppliedOwnershipAuthority(root, "A", [5, 4, 3, 2, 1]);
            var storage = new FaultInjectingStorage(throwAfterCommitNew: true);
            using var manager = new HostManagerAuthorityRetirementManager(root, storage);

            var exception = await Assert.ThrowsAsync<HostManagerAuthorityRetirementPrepareException>(
                () => manager.PrepareAppliedOwnershipAsync(
                    authority.CanonicalPath,
                    CancellationToken.None));

            Assert.Equal(
                HostManagerAuthorityRetirementCommitOutcome.CommitAmbiguous,
                exception.Outcome);
            var ticket = new HostManagerAuthorityRetirementTicket(
                exception.TicketId,
                HostManagerAuthorityKind.AppliedOwnership);
            Assert.Throws<InvalidOperationException>(() =>
                manager.RequireAvailable(authority.CanonicalPath));

            await manager.CancelAsync(ticket, CancellationToken.None);

            manager.RequireAvailable(authority.CanonicalPath);
            Assert.True(File.Exists(authority.CanonicalPath));
            Assert.Empty(Directory.EnumerateFiles(
                manager.RetirementDirectory,
                "retirement-*.bin"));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task TombstoneDeleteFailureKeepsPathReservedAfterAuthorityIsGone()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var authority = CreateAppliedOwnershipAuthority(root, "A", [8, 6, 4, 2]);
            var storage = new FaultInjectingStorage();
            using (var manager = new HostManagerAuthorityRetirementManager(root, storage))
            {
                var ticket = await manager.PrepareAppliedOwnershipAsync(
                    authority.CanonicalPath,
                    CancellationToken.None);
                manager.Attach(ticket, new FaultInjectingResource(0));
                var tombstone = Assert.Single(Directory.EnumerateFiles(
                    manager.RetirementDirectory,
                    "retirement-*.bin"));
                storage.FailNextDelete(tombstone);

                await manager.RetryAsync();

                Assert.False(File.Exists(authority.CanonicalPath));
                Assert.True(File.Exists(tombstone));
                Assert.Equal(1, manager.PendingCount(
                    HostManagerAuthorityKind.AppliedOwnership));
                Assert.Throws<InvalidOperationException>(() =>
                    manager.RequireAvailable(authority.CanonicalPath));
            }

            using var restarted = new HostManagerAuthorityRetirementManager(root, storage);
            Assert.Throws<InvalidOperationException>(() =>
                restarted.RequireAvailable(authority.CanonicalPath));
            await restarted.RetryAsync();
            restarted.RequireAvailable(authority.CanonicalPath);
            Assert.Equal(0, restarted.PendingCount(
                HostManagerAuthorityKind.AppliedOwnership));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task HeldOwnerLeasePreventsEveryNamespaceDelete()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var authority = CreateAppliedOwnershipAuthority(root, "A", [1, 1, 2, 3, 5]);
            var storage = new FaultInjectingStorage();
            using var manager = new HostManagerAuthorityRetirementManager(root, storage);
            _ = await manager.PrepareAppliedOwnershipAsync(
                authority.CanonicalPath,
                CancellationToken.None);
            using (var competingOwner = new FileStream(
                       authority.OwnerLockPath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                await manager.RetryAsync();
                Assert.NotNull(manager.LastFailure);
                Assert.Empty(storage.DeletedPaths);
                Assert.True(File.Exists(authority.CanonicalPath));
                Assert.True(File.Exists(authority.ManifestPath));
            }

            await manager.RetryAsync();
            Assert.Equal(0, manager.PendingCount(
                HostManagerAuthorityKind.AppliedOwnership));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task IndependentTombstoneCanCompleteWhenAnotherDeleteFails()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var first = CreateAppliedOwnershipAuthority(root, "A", [1, 2, 3]);
            var second = CreateAppliedOwnershipAuthority(root, "C", [4, 5, 6]);
            var storage = new FaultInjectingStorage(failDeletePathOnce: first.CanonicalPath);
            using var manager = new HostManagerAuthorityRetirementManager(root, storage);
            var firstTicket = await manager.PrepareAppliedOwnershipAsync(
                first.CanonicalPath,
                CancellationToken.None);
            var secondTicket = await manager.PrepareAppliedOwnershipAsync(
                second.CanonicalPath,
                CancellationToken.None);
            manager.Attach(firstTicket, new FaultInjectingResource(0));
            manager.Attach(secondTicket, new FaultInjectingResource(0));

            await manager.RetryAsync();

            Assert.Equal(1, manager.PendingCount(
                HostManagerAuthorityKind.AppliedOwnership));
            Assert.True(File.Exists(first.CanonicalPath));
            Assert.False(File.Exists(second.CanonicalPath));
            Assert.Throws<InvalidOperationException>(() =>
                manager.RequireAvailable(first.CanonicalPath));
            manager.RequireAvailable(second.CanonicalPath);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void RestartDeletesOnlyWellFormedOrphanTombstoneTemps()
    {
        var root = CreateTemporaryRoot();
        try
        {
            string retirementRoot;
            using (var manager = new HostManagerAuthorityRetirementManager(
                       root,
                       WindowsHostManagerAuthorityRetirementStorage.Instance))
            {
                retirementRoot = manager.RetirementDirectory;
            }
            var orphan = Path.Combine(
                retirementRoot,
                $".retirement-{Guid.NewGuid():N}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(orphan, [1, 2, 3]);

            using var restarted = new HostManagerAuthorityRetirementManager(
                root,
                WindowsHostManagerAuthorityRetirementStorage.Instance);

            Assert.False(File.Exists(orphan));
            Assert.Equal(0, restarted.PendingCount(
                HostManagerAuthorityKind.AppliedOwnership));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void StartupRejectsMoreThanBoundedTombstoneCapacity()
    {
        var root = CreateTemporaryRoot();
        try
        {
            string retirementRoot;
            using (var manager = new HostManagerAuthorityRetirementManager(
                       root,
                       WindowsHostManagerAuthorityRetirementStorage.Instance))
            {
                retirementRoot = manager.RetirementDirectory;
            }
            for (var index = 0; index < 65; index++)
            {
                var record = CreateRecord(
                    HostManagerAuthorityKind.AppliedOwnership,
                    $"authorities/{index:D2}/ledger.bin");
                File.WriteAllBytes(
                    Path.Combine(
                        retirementRoot,
                        $"retirement-{record.TicketId:N}.bin"),
                    record.Encode());
            }

            Assert.Throws<InvalidDataException>(() =>
                new HostManagerAuthorityRetirementManager(
                    root,
                    WindowsHostManagerAuthorityRetirementStorage.Instance));
            Assert.Equal(65, Directory.EnumerateFiles(
                retirementRoot,
                "retirement-*.bin").Count());
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void StartupRejectsUnboundedOrphanTempsWithoutDeletingEvidence()
    {
        var root = CreateTemporaryRoot();
        try
        {
            string retirementRoot;
            using (var manager = new HostManagerAuthorityRetirementManager(
                       root,
                       WindowsHostManagerAuthorityRetirementStorage.Instance))
            {
                retirementRoot = manager.RetirementDirectory;
            }
            for (var index = 0; index < 65; index++)
            {
                File.WriteAllBytes(
                    Path.Combine(
                        retirementRoot,
                        $".retirement-{Guid.NewGuid():N}.{Guid.NewGuid():N}.tmp"),
                    [checked((byte)index)]);
            }

            Assert.Throws<InvalidDataException>(() =>
                new HostManagerAuthorityRetirementManager(
                    root,
                    WindowsHostManagerAuthorityRetirementStorage.Instance));
            Assert.Equal(65, Directory.EnumerateFiles(
                retirementRoot,
                ".retirement-*.tmp").Count());

            File.Delete(Assert.Single(Directory.EnumerateFiles(
                retirementRoot,
                ".retirement-*.tmp").Take(1)));
            using var restarted = new HostManagerAuthorityRetirementManager(
                root,
                WindowsHostManagerAuthorityRetirementStorage.Instance);
            Assert.Empty(Directory.EnumerateFiles(
                retirementRoot,
                ".retirement-*.tmp"));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void StartupRejectsCorruptAndOversizedTombstones()
    {
        var root = CreateTemporaryRoot();
        try
        {
            string retirementRoot;
            using (var manager = new HostManagerAuthorityRetirementManager(
                       root,
                       WindowsHostManagerAuthorityRetirementStorage.Instance))
            {
                retirementRoot = manager.RetirementDirectory;
            }
            var ticket = Guid.NewGuid();
            var corruptPath = Path.Combine(
                retirementRoot,
                $"retirement-{ticket:N}.bin");
            File.WriteAllBytes(
                corruptPath,
                new byte[HostManagerAuthorityRetirementRecord.MaximumImageLength + 1]);

            Assert.Throws<InvalidDataException>(() =>
                new HostManagerAuthorityRetirementManager(
                    root,
                    WindowsHostManagerAuthorityRetirementStorage.Instance));
            Assert.True(File.Exists(corruptPath));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static HostManagerAuthorityRetirementRecord CreateRecord(
        HostManagerAuthorityKind kind,
        string canonicalPath,
        string? payloadPath = null)
        => new(
            Guid.NewGuid(),
            kind,
            canonicalPath,
            payloadPath,
            Guid.NewGuid(),
            128,
            SHA256.HashData([1, 2, 3]),
            144,
            SHA256.HashData([4, 5, 6]));

    private static AuthorityFixture CreateAppliedOwnershipAuthority(
        string root,
        string name,
        byte[] image)
        => CreateAuthority(root, name, image, "applied-ownership", payload: false);

    private static AuthorityFixture CreateTransactionJournalAuthority(
        string root,
        string name,
        byte[] image)
        => CreateAuthority(root, name, image, "transaction-journal", payload: true);

    private static AuthorityFixture CreateAuthority(
        string root,
        string name,
        byte[] image,
        string storeKind,
        bool payload)
    {
        var authorityRoot = Path.Combine(root, "authorities", name);
        Directory.CreateDirectory(authorityRoot);
        var canonicalPath = Path.Combine(
            authorityRoot,
            payload ? "journal.bin" : "ledger.bin");
        var manifest = new HostManagerDurableRootManifest(canonicalPath, storeKind);
        manifest.EnsureInitializing();
        var transition = manifest.BeginCanonicalTransition(image);
        File.WriteAllBytes(canonicalPath, image);
        manifest.FinalizeCanonicalTransition(transition);
        var ownerLock = $"{canonicalPath}.owner.lock";
        File.WriteAllBytes(ownerLock, []);

        string? payloadDirectory = null;
        if (payload)
        {
            payloadDirectory = Path.Combine(authorityRoot, "payloads");
            Directory.CreateDirectory(payloadDirectory);
            File.WriteAllBytes(Path.Combine(payloadDirectory, "payload-store.lock"), []);
            File.WriteAllBytes(Path.Combine(payloadDirectory, "payload-sequence.bin"), [1, 0, 0, 0]);
            File.WriteAllBytes(
                Path.Combine(payloadDirectory, "payload-0000000001-0000000001.bin"),
                [1, 2, 3]);
            File.WriteAllBytes(
                Path.Combine(
                    payloadDirectory,
                    $"payload-0000000001-0000000002.bin.{Guid.NewGuid():N}.tmp"),
                [4, 5, 6]);
        }

        var canonicalTemp = Path.Combine(
            authorityRoot,
            $".{Path.GetFileName(canonicalPath)}.{Guid.NewGuid():N}.tmp");
        var manifestTemp = Path.Combine(
            authorityRoot,
            $".{Path.GetFileName(canonicalPath)}.root.manifest.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(canonicalTemp, [8]);
        File.WriteAllBytes(manifestTemp, [9]);
        return new AuthorityFixture(
            canonicalPath,
            $"{canonicalPath}.root.manifest",
            ownerLock,
            payloadDirectory);
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"resource-manager-authority-retirement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record AuthorityFixture(
        string CanonicalPath,
        string ManifestPath,
        string OwnerLockPath,
        string? PayloadDirectory);

    private sealed class FaultInjectingResource(int disposeFailures) : IAsyncDisposable
    {
        private int remainingDisposeFailures = disposeFailures;

        internal int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (remainingDisposeFailures > 0)
            {
                remainingDisposeFailures--;
                throw new IOException("Injected resource dispose failure.");
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FaultInjectingStorage(
        string? failDeletePathOnce = null,
        bool throwAfterCommitNew = false)
        : IHostManagerAuthorityRetirementStorage
    {
        private string? pendingDeleteFailurePath = failDeletePathOnce;
        private bool commitFailurePending = throwAfterCommitNew;

        internal List<string> DeletedPaths { get; } = [];

        internal void FailNextDelete(string path)
            => pendingDeleteFailurePath = path;

        public void CommitNew(string temporaryPath, string destinationPath)
        {
            WindowsNativeAtomicFileCommitter.CommitNew(temporaryPath, destinationPath);
            if (commitFailurePending)
            {
                commitFailurePending = false;
                throw new IOException("Injected failure after tombstone namespace commit.");
            }
        }

        public WindowsNativeFileDeleteResult DeleteFile(string path)
        {
            if (pendingDeleteFailurePath is not null
                && string.Equals(
                    path,
                    pendingDeleteFailurePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                pendingDeleteFailurePath = null;
                throw new IOException("Injected authority file delete failure.");
            }
            var result = WindowsNativeAtomicFileCommitter.DeleteExact(path);
            if (result == WindowsNativeFileDeleteResult.Deleted)
            {
                DeletedPaths.Add(path);
            }
            return result;
        }

        public void DeleteEmptyDirectory(string path)
            => WindowsHostManagerAuthorityRetirementStorage.Instance
                .DeleteEmptyDirectory(path);

        public FileStream AcquireDeleteLease(string path)
            => WindowsHostManagerAuthorityRetirementStorage.Instance
                .AcquireDeleteLease(path);
    }
}
