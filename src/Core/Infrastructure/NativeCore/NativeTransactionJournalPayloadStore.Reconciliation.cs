using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal readonly record struct NativeTransactionJournalPayloadLiveReference(
    NativeTransactionJournalPayloadReference Reference,
    NativeTransactionJournalPayloadProvenance Provenance)
{
    public bool IsValid => Reference.IsValid && Provenance.IsValid;
}

internal readonly record struct NativeTransactionJournalPayloadReconciliationResult(
    int LivePayloadCount,
    long LivePayloadBytes,
    int DeletedPayloadCount);

internal sealed partial class NativeTransactionJournalPayloadStore
{
    public async Task<NativeTransactionJournalPayloadReconciliationResult> ReconcileAsync(
        ReadOnlyMemory<NativeTransactionJournalPayloadLiveReference> liveReferences,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var live = BuildLiveSet(liveReferences.Span);
            var liveIdentities = live
                .Select(static item => (item.Reference.Slot, item.Reference.Generation))
                .ToHashSet();
            var allocationSequence = await ReadSequenceAsync(
                allowMissingForEmptyStore: true,
                cancellationToken);
            QuarantineNonCanonicalPayloadFiles();
            var canonicalPaths = Directory
                .EnumerateFiles(directoryPath, "payload-*.bin", SearchOption.TopDirectoryOnly)
                .Where(path => !string.Equals(
                    Path.GetFileName(path),
                    SequenceFileName,
                    StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
            var livePayloadCount = 0;
            long livePayloadBytes = 0;
            var deleted = 0;
            foreach (var path in canonicalPaths)
            {
                CanonicalPayloadFile file;
                try
                {
                    file = await ReadCanonicalFileAsync(
                        path,
                        cancellationToken);
                }
                catch (InvalidDataException)
                {
                    QuarantinePayloadFile(path);
                    continue;
                }
                catch (EndOfStreamException)
                {
                    QuarantinePayloadFile(path);
                    continue;
                }
                catch (FileNotFoundException)
                {
                    // A missing payload is handled by recovery for the record that references it.
                    continue;
                }

                var item = file.LiveReference;
                if (ToSequence(item.Reference.Slot, item.Reference.Generation) > allocationSequence)
                {
                    throw new InvalidDataException(
                        $"The canonical payload identity is newer than the durable allocation sequence: " +
                        $"{Path.GetFileName(file.Path)}.");
                }
                if (live.Contains(item))
                {
                    livePayloadCount = checked(livePayloadCount + 1);
                    livePayloadBytes = checked(
                        livePayloadBytes + checked((long)item.Reference.Length));
                }
                else if (liveIdentities.Contains((item.Reference.Slot, item.Reference.Generation)))
                {
                    QuarantinePayloadFile(file.Path);
                }
                else if (WindowsNativeAtomicFileCommitter.DeleteExact(file.Path) ==
                         WindowsNativeFileDeleteResult.Deleted)
                {
                    deleted = checked(deleted + 1);
                }
            }

            EnsureUsageWithinBudget(livePayloadCount, livePayloadBytes);
            this.livePayloadCount = livePayloadCount;
            this.livePayloadBytes = livePayloadBytes;
            return new NativeTransactionJournalPayloadReconciliationResult(
                livePayloadCount,
                livePayloadBytes,
                deleted);
        }
        finally
        {
            gate.Release();
        }
    }

    private static HashSet<NativeTransactionJournalPayloadLiveReference> BuildLiveSet(
        ReadOnlySpan<NativeTransactionJournalPayloadLiveReference> liveReferences)
    {
        var live = new HashSet<NativeTransactionJournalPayloadLiveReference>(liveReferences.Length);
        foreach (var item in liveReferences)
        {
            if (!item.IsValid)
            {
                throw new ArgumentException(
                    "The rollback payload live-set contains an invalid reference.",
                    nameof(liveReferences));
            }
            // Each record verifies its own provenance when reading. A conflicting claim
            // must not hide an exact valid owner or stop unrelated payload allocation.
            live.Add(item);
        }
        return live;
    }

    private async Task<CanonicalPayloadFile> ReadCanonicalFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        if (!TryParseCanonicalPayloadFileName(fileName, out var generation, out var slot))
        {
            throw new InvalidDataException(
                $"The payload store contains a non-canonical payload file name: {fileName}.");
        }

        await using var stream = OpenRead(path);
        if (stream.Length <= PayloadHeaderSize)
        {
            throw new InvalidDataException(
                $"The payload store contains a truncated canonical payload file: {fileName}.");
        }
        var header = new byte[PayloadHeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var reference = new NativeTransactionJournalPayloadReference(
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20, 4)),
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(40, 8)));
        if (!reference.IsValid)
        {
            throw new InvalidDataException(
                $"The canonical payload reference is invalid: {fileName}.");
        }
        if (reference.Slot != slot ||
            reference.Generation != generation ||
            stream.Length != checked((long)PayloadHeaderSize + checked((long)reference.Length)))
        {
            throw new InvalidDataException(
                $"The canonical payload identity or length is invalid: {fileName}.");
        }

        Span<byte> bindingHash = stackalloc byte[32];
        SHA256.HashData(
            header.AsSpan(BindingOffset, NativeTransactionJournalPayloadBinding.EncodedSize),
            bindingHash);
        var provenance = new NativeTransactionJournalPayloadProvenance(
            BinaryPrimitives.ReadUInt64LittleEndian(bindingHash[..8]),
            BinaryPrimitives.ReadUInt64LittleEndian(bindingHash.Slice(8, 8)));
        if (!provenance.IsValid)
        {
            throw new InvalidDataException(
                $"The canonical payload provenance is invalid: {fileName}.");
        }
        ValidatePayloadHeader(header, reference, provenance);

        var payloadHash = await SHA256.HashDataAsync(stream, cancellationToken);
        if (BinaryPrimitives.ReadUInt64LittleEndian(payloadHash.AsSpan(0, 8)) !=
                reference.DigestLow ||
            BinaryPrimitives.ReadUInt64LittleEndian(payloadHash.AsSpan(8, 8)) !=
                reference.DigestHigh)
        {
            throw new InvalidDataException(
                $"The canonical rollback payload checksum is invalid: {fileName}.");
        }

        return new CanonicalPayloadFile(
            path,
            new NativeTransactionJournalPayloadLiveReference(reference, provenance));
    }

    private sealed record CanonicalPayloadFile(
        string Path,
        NativeTransactionJournalPayloadLiveReference LiveReference);
}
