using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ResourceManager.App.Infrastructure.NativeCore;
using Xunit;

namespace Resource_Manager_APP.Tests;

public sealed class NativeTransactionJournalPayloadStoreTests
{
    [Fact]
    public async Task JournalInstanceMayUseEitherNonzeroHalf()
    {
        var root = CreateTempDirectory();
        try
        {
            using var store = CreateStore(root);
            var binding = CreateBinding(actionId: 71) with
            {
                JournalInstanceLow = 0,
                JournalInstanceHigh = 0x71
            };
            var payload = new byte[] { 7, 1 };

            var reference = await store.PersistAsync(binding, payload, CancellationToken.None);

            Assert.Equal(payload, await store.ReadAsync(reference, binding, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.PersistAsync(
                    binding with { JournalInstanceHigh = 0 },
                    payload,
                    CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestartPreservesMonotonicIdentityAndToleratesOrphanPayload()
    {
        var root = CreateTempDirectory();
        try
        {
            var orphanBinding = CreateBinding(actionId: 1);
            NativeTransactionJournalPayloadReference orphan;
            using (var first = CreateStore(root))
            {
                orphan = await first.PersistAsync(
                    orphanBinding,
                    new byte[] { 1, 2, 3 },
                    CancellationToken.None);
            }

            var nextBinding = CreateBinding(actionId: 2);
            NativeTransactionJournalPayloadReference next;
            using (var restarted = CreateStore(root))
            {
                next = await restarted.PersistAsync(
                    nextBinding,
                    new byte[] { 4, 5, 6 },
                    CancellationToken.None);
                Assert.Equal(
                    new byte[] { 1, 2, 3 },
                    await restarted.ReadAsync(orphan, orphanBinding, CancellationToken.None));
            }

            Assert.True(ToSequence(next) > ToSequence(orphan));
            Assert.NotEqual(orphan, next);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExactReferenceControlsReadAndDelete()
    {
        var root = CreateTempDirectory();
        try
        {
            using var store = CreateStore(root);
            var binding = CreateBinding(
                actionId: 11,
                atomicGroupId: 501,
                groupMemberIndex: 0,
                groupMemberCount: 2);
            var payload = new byte[] { 10, 20, 30, 40 };
            var reference = await store.PersistAsync(binding, payload, CancellationToken.None);

            Assert.Equal(payload, await store.ReadAsync(reference, binding, CancellationToken.None));

            var wrongActionBinding = binding with
            {
                ActionIdentity = CreateIdentity(actionId: 12)
            };
            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.ReadAsync(reference, wrongActionBinding, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.DeleteAsync(reference, wrongActionBinding, CancellationToken.None));

            var wrongJournalBinding = binding with { JournalInstanceHigh = binding.JournalInstanceHigh + 1 };
            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.ReadAsync(reference, wrongJournalBinding, CancellationToken.None));

            var wrongGroupBinding = binding with { GroupMemberIndex = 1 };
            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.ReadAsync(reference, wrongGroupBinding, CancellationToken.None));

            var wrongDigest = reference with { DigestLow = reference.DigestLow ^ 1 };
            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.ReadAsync(wrongDigest, binding, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.DeleteAsync(wrongDigest, binding, CancellationToken.None));

            Assert.True(await store.DeleteAsync(reference, binding, CancellationToken.None));
            Assert.False(await store.DeleteAsync(reference, binding, CancellationToken.None));
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => store.ReadAsync(reference, binding, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DirectDelete_DoesNotInferSuccessWhenWindowsRejectsDelete()
    {
        var root = CreateTempDirectory();
        try
        {
            using var store = CreateStore(root);
            var binding = CreateBinding(actionId: 12);
            var first = await store.PersistAsync(
                binding,
                new byte[] { 1, 2 },
                CancellationToken.None);
            var second = await store.PersistAsync(
                binding with { ActionIdentity = CreateIdentity(actionId: 13) },
                new byte[] { 3, 4 },
                CancellationToken.None);
            var firstPath = Path.Combine(
                root,
                $"payload-{first.Generation:D10}-{first.Slot:D10}.bin");
            var secondPath = Path.Combine(
                root,
                $"payload-{second.Generation:D10}-{second.Slot:D10}.bin");
            await using var firstLock = new FileStream(
                firstPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            await using var secondLock = new FileStream(
                secondPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            await Assert.ThrowsAnyAsync<IOException>(() =>
                store.DeleteAsync(first, binding, CancellationToken.None));
            await Assert.ThrowsAnyAsync<IOException>(() =>
                store.DeleteAsync(
                    second,
                    NativeTransactionJournalPayloadProvenance.Create(
                        binding with { ActionIdentity = CreateIdentity(actionId: 13) }),
                    CancellationToken.None));

            Assert.Equal(
                new byte[] { 1, 2 },
                await store.ReadAsync(first, binding, CancellationToken.None));
            Assert.Equal(
                new byte[] { 3, 4 },
                await store.ReadAsync(
                    second,
                    binding with { ActionIdentity = CreateIdentity(actionId: 13) },
                    CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExactWindowsDelete_DistinguishesDeletedFromNotFound()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "delete-me.bin");
            File.WriteAllBytes(path, new byte[] { 1 });

            Assert.Equal(
                WindowsNativeFileDeleteResult.Deleted,
                WindowsNativeAtomicFileCommitter.DeleteExact(path));
            Assert.Equal(
                WindowsNativeFileDeleteResult.NotFound,
                WindowsNativeAtomicFileCommitter.DeleteExact(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReconciliationKeepsExactLivePayloadsAndDeletesOnlyOrphans()
    {
        var root = CreateTempDirectory();
        try
        {
            using var store = CreateStore(root);
            var liveBinding = CreateBinding(actionId: 13);
            var live = await store.PersistAsync(
                liveBinding,
                new byte[] { 1, 3, 5 },
                CancellationToken.None);
            var orphanBinding = CreateBinding(actionId: 14);
            var orphan = await store.PersistAsync(
                orphanBinding,
                new byte[] { 2, 4, 6, 8 },
                CancellationToken.None);

            var result = await store.ReconcileAsync(
                new[]
                {
                    new NativeTransactionJournalPayloadLiveReference(
                        live,
                        NativeTransactionJournalPayloadProvenance.Create(liveBinding))
                },
                CancellationToken.None);

            Assert.Equal(1, result.LivePayloadCount);
            Assert.Equal(3, result.LivePayloadBytes);
            Assert.Equal(1, result.DeletedPayloadCount);
            Assert.Equal(
                new byte[] { 1, 3, 5 },
                await store.ReadAsync(live, liveBinding, CancellationToken.None));
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => store.ReadAsync(orphan, orphanBinding, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconciliation_DoesNotInferDeletionWhenWindowsRejectsDelete()
    {
        var root = CreateTempDirectory();
        try
        {
            using var store = CreateStore(root);
            var binding = CreateBinding(actionId: 15);
            var reference = await store.PersistAsync(
                binding,
                new byte[] { 1, 5, 9 },
                CancellationToken.None);
            var path = Path.Combine(
                root,
                $"payload-{reference.Generation:D10}-{reference.Slot:D10}.bin");
            await using var lockStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            await Assert.ThrowsAnyAsync<IOException>(
                () => store.ReconcileAsync(
                    ReadOnlyMemory<NativeTransactionJournalPayloadLiveReference>.Empty,
                    CancellationToken.None));

            Assert.Equal(new byte[] { 1, 5, 9 }, await store.ReadAsync(
                reference,
                binding,
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReconciliationValidatesTheWholeLiveSetBeforeDeletingAnything()
    {
        var root = CreateTempDirectory();
        try
        {
            using var store = CreateStore(root);
            var binding = CreateBinding(actionId: 15);
            var existing = await store.PersistAsync(
                binding,
                new byte[] { 1, 5 },
                CancellationToken.None);
            var missing = existing with
            {
                Slot = checked(existing.Slot + 1)
            };

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ReconcileAsync(
                    new[]
                    {
                        new NativeTransactionJournalPayloadLiveReference(
                            missing,
                            NativeTransactionJournalPayloadProvenance.Create(binding))
                    },
                    CancellationToken.None));

            Assert.Equal(
                new byte[] { 1, 5 },
                await store.ReadAsync(existing, binding, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptOrphanPreventsPartialCleanup()
    {
        var root = CreateTempDirectory();
        try
        {
            NativeTransactionJournalPayloadReference first;
            NativeTransactionJournalPayloadReference second;
            using (var store = CreateStore(root))
            {
                first = await store.PersistAsync(
                    CreateBinding(actionId: 16),
                    new byte[] { 1, 6 },
                    CancellationToken.None);
                second = await store.PersistAsync(
                    CreateBinding(actionId: 17),
                    new byte[] { 1, 7 },
                    CancellationToken.None);
            }

            var corruptPath = Path.Combine(
                root,
                $"payload-{second.Generation:D10}-{second.Slot:D10}.bin");
            var bytes = await File.ReadAllBytesAsync(corruptPath);
            bytes[^1] ^= 0x40;
            await File.WriteAllBytesAsync(corruptPath, bytes);

            using var reopened = CreateStore(root);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => reopened.ReconcileAsync(
                    ReadOnlyMemory<NativeTransactionJournalPayloadLiveReference>.Empty,
                    CancellationToken.None));

            Assert.True(File.Exists(Path.Combine(
                root,
                $"payload-{first.Generation:D10}-{first.Slot:D10}.bin")));
            Assert.True(File.Exists(corruptPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PayloadAndSequenceCorruptionFailClosed()
    {
        var root = CreateTempDirectory();
        try
        {
            var binding = CreateBinding(actionId: 21);
            NativeTransactionJournalPayloadReference reference;
            using (var store = CreateStore(root))
            {
                reference = await store.PersistAsync(
                    binding,
                    new byte[] { 7, 8, 9 },
                    CancellationToken.None);
            }

            var payloadPath = Directory.GetFiles(root, "payload-??????????-??????????.bin").Single();
            var bytes = await File.ReadAllBytesAsync(payloadPath);
            bytes[^1] ^= 0x5A;
            await File.WriteAllBytesAsync(payloadPath, bytes);

            using (var corruptedPayload = CreateStore(root))
            {
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => corruptedPayload.ReadAsync(reference, binding, CancellationToken.None));
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => corruptedPayload.DeleteAsync(reference, binding, CancellationToken.None));
            }

            var sequencePath = Path.Combine(root, "payload-sequence.bin");
            bytes = await File.ReadAllBytesAsync(sequencePath);
            bytes[16] ^= 0x01;
            await File.WriteAllBytesAsync(sequencePath, bytes);

            using var corruptedSequence = CreateStore(root);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => corruptedSequence.PersistAsync(
                    CreateBinding(actionId: 22),
                    new byte[] { 11 },
                    CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => corruptedSequence.ReadAsync(reference, binding, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BindingHeaderCorruptionFailsClosed()
    {
        var root = CreateTempDirectory();
        try
        {
            var binding = CreateBinding(actionId: 31);
            NativeTransactionJournalPayloadReference reference;
            using (var store = CreateStore(root))
            {
                reference = await store.PersistAsync(
                    binding,
                    new byte[] { 1, 3, 5, 7 },
                    CancellationToken.None);
            }

            var payloadPath = Directory.GetFiles(root, "payload-??????????-??????????.bin").Single();
            var bytes = await File.ReadAllBytesAsync(payloadPath);
            bytes[88] ^= 0x01;
            await File.WriteAllBytesAsync(payloadPath, bytes);

            using var reopened = CreateStore(root);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => reopened.ReadAsync(reference, binding, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => reopened.DeleteAsync(reference, binding, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentOperationsAreSerializedWithoutIdentityReuse()
    {
        var root = CreateTempDirectory();
        try
        {
            using var store = CreateStore(root);
            var operations = Enumerable.Range(1, 32)
                .Select(value => (Value: value, Binding: CreateBinding((ulong)value)))
                .ToArray();
            var writes = operations
                .Select(operation => store.PersistAsync(
                    operation.Binding,
                    BitConverter.GetBytes(operation.Value),
                    CancellationToken.None))
                .ToArray();
            var references = await Task.WhenAll(writes);

            Assert.Equal(references.Length, references.Distinct().Count());
            var sequences = references.Select(ToSequence).Order().ToArray();
            Assert.Equal(
                Enumerable.Range(1, references.Length).Select(value => (ulong)value),
                sequences);

            var reads = references
                .Zip(
                    operations,
                    (reference, operation) => store.ReadAsync(
                        reference,
                        operation.Binding,
                        CancellationToken.None));
            var payloads = await Task.WhenAll(reads);
            Assert.Equal(
                Enumerable.Range(1, 32),
                payloads.Select(payload => BitConverter.ToInt32(payload)).Order());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingSequenceBesideCommittedPayloadFailsClosed()
    {
        var root = CreateTempDirectory();
        try
        {
            using (var store = CreateStore(root))
            {
                _ = await store.PersistAsync(
                    CreateBinding(actionId: 41),
                    new byte[] { 1 },
                    CancellationToken.None);
            }

            File.Delete(Path.Combine(root, "payload-sequence.bin"));
            using var reopened = CreateStore(root);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => reopened.PersistAsync(
                    CreateBinding(actionId: 42),
                    new byte[] { 2 },
                    CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeIsIdempotentAndRejectsFurtherOperations()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = CreateStore(root);
            var binding = CreateBinding(actionId: 51);
            var reference = await store.PersistAsync(
                binding,
                new byte[] { 2, 4, 6, 8 },
                CancellationToken.None);

            store.Dispose();
            store.Dispose();

            await Assert.ThrowsAnyAsync<ObjectDisposedException>(
                () => store.PersistAsync(
                    CreateBinding(actionId: 52),
                    new byte[] { 10 },
                    CancellationToken.None));
            await Assert.ThrowsAnyAsync<ObjectDisposedException>(
                () => store.ReadAsync(reference, binding, CancellationToken.None));
            await Assert.ThrowsAnyAsync<ObjectDisposedException>(
                () => store.DeleteAsync(reference, binding, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StoreOwnershipRemainsExclusive()
    {
        var root = CreateTempDirectory();
        try
        {
            using var owner = CreateStore(root);
            Assert.Throws<IOException>(() => CreateStore(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NewPayloadCommitNeverReplacesAnExistingCanonicalIdentity()
    {
        var root = CreateTempDirectory();
        try
        {
            using var store = CreateStore(root);
            _ = await store.PersistAsync(
                CreateBinding(actionId: 61),
                new byte[] { 1 },
                CancellationToken.None);

            var collidingPath = Path.Combine(
                root,
                "payload-0000000001-0000000002.bin");
            var sentinel = new byte[] { 9, 8, 7, 6 };
            await File.WriteAllBytesAsync(collidingPath, sentinel);

            await Assert.ThrowsAnyAsync<IOException>(
                () => store.PersistAsync(
                    CreateBinding(actionId: 62),
                    new byte[] { 2 },
                    CancellationToken.None));

            Assert.Equal(sentinel, await File.ReadAllBytesAsync(collidingPath));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PayloadBudgetsMustBeExplicitAndPositive()
    {
        var root = CreateTempDirectory();
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new NativeTransactionJournalPayloadStore(root, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new NativeTransactionJournalPayloadStore(root, 1, 0));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CountAndByteBudgetsAreReleasedOnlyAfterDurableDelete()
    {
        var root = CreateTempDirectory();
        try
        {
            using var store = new NativeTransactionJournalPayloadStore(
                root,
                maximumPayloadCount: 2,
                maximumPayloadBytes: 3);
            var firstBinding = CreateBinding(actionId: 81);
            var first = await store.PersistAsync(
                firstBinding,
                new byte[] { 1, 2 },
                CancellationToken.None);

            await Assert.ThrowsAsync<NativeTransactionJournalPayloadCapacityException>(() =>
                store.PersistAsync(
                    CreateBinding(actionId: 82),
                    new byte[] { 3, 4 },
                    CancellationToken.None));

            Assert.True(await store.DeleteAsync(first, firstBinding, CancellationToken.None));
            _ = await store.PersistAsync(
                CreateBinding(actionId: 83),
                new byte[] { 5, 6 },
                CancellationToken.None);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CountBudgetRejectsASecondLivePayload()
    {
        var root = CreateTempDirectory();
        try
        {
            using var store = new NativeTransactionJournalPayloadStore(
                root,
                maximumPayloadCount: 1,
                maximumPayloadBytes: 1024);
            _ = await store.PersistAsync(
                CreateBinding(actionId: 86),
                new byte[] { 1 },
                CancellationToken.None);

            await Assert.ThrowsAsync<NativeTransactionJournalPayloadCapacityException>(() =>
                store.PersistAsync(
                    CreateBinding(actionId: 87),
                    new byte[] { 2 },
                    CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestartCanReconcileOrphansThatExceedTheNewBudget()
    {
        var root = CreateTempDirectory();
        try
        {
            using (var first = new NativeTransactionJournalPayloadStore(
                       root,
                       maximumPayloadCount: 2,
                       maximumPayloadBytes: 4))
            {
                _ = await first.PersistAsync(
                    CreateBinding(actionId: 91),
                    new byte[] { 1, 2, 3 },
                    CancellationToken.None);
            }

            using var reopened = new NativeTransactionJournalPayloadStore(
                root,
                maximumPayloadCount: 2,
                maximumPayloadBytes: 2);
            var result = await reopened.ReconcileAsync(
                ReadOnlyMemory<NativeTransactionJournalPayloadLiveReference>.Empty,
                CancellationToken.None);
            Assert.Equal(0, result.LivePayloadCount);
            Assert.Equal(1, result.DeletedPayloadCount);
            _ = await reopened.PersistAsync(
                CreateBinding(actionId: 92),
                new byte[] { 4, 5 },
                CancellationToken.None);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ulong ToSequence(NativeTransactionJournalPayloadReference reference)
        => checked((ulong)(reference.Generation - 1) * uint.MaxValue + reference.Slot);

    private static NativeTransactionJournalPayloadStore CreateStore(string root)
        => new(root, maximumPayloadCount: 64, maximumPayloadBytes: 1024 * 1024);

    private static NativeTransactionJournalPayloadBinding CreateBinding(
        ulong actionId,
        ulong atomicGroupId = 0,
        uint groupMemberIndex = 0,
        uint groupMemberCount = 0)
        => new(
            JournalInstanceLow: 0xA11CE,
            JournalInstanceHigh: 0xB01D,
            ActionIdentity: CreateIdentity(actionId),
            Scope: NativeTransactionJournalScope.Process,
            Disposition: NativeTransactionJournalDisposition.Apply,
            Domain: NativeTransactionJournalDomain.Process,
            GradeValidMask: NativeTransactionJournalGradeValidity.Process,
            ProcessFromGrade: (int)NativeTransactionJournalProcessGrade.Normal,
            ProcessToGrade: (int)NativeTransactionJournalProcessGrade.Level1,
            CpuFromGrade: 0,
            CpuToGrade: 0,
            GpuFromGrade: 0,
            GpuToGrade: 0,
            StableSystemStatus: 0,
            StableSystemError: 0,
            MaximumRecoveryAttempts: 3,
            RecoveryDeadlineUtcMilliseconds: 100_000,
            AtomicGroupId: atomicGroupId,
            GroupMemberIndex: groupMemberIndex,
            GroupMemberCount: groupMemberCount);

    private static NativeTransactionJournalIdentity CreateIdentity(ulong actionId)
        => new()
        {
            ConfigurationGeneration = 1,
            PlanEpoch = 2,
            ActionId = actionId,
            HostSessionIncarnation = 3,
            TargetId = 4,
            SoftwareId = 5,
            ProcessStartKey = 6,
            ProcessId = 7,
            Reserved = 0
        };

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"resource-manager-native-payload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
