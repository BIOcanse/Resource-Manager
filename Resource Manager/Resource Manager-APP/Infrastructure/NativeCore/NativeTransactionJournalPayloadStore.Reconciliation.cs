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
            var allocationSequence = await ReadSequenceAsync(
                allowMissingForEmptyStore: true,
                cancellationToken);
            var canonicalPaths = Directory
                .EnumerateFiles(directoryPath, "payload-*.bin", SearchOption.TopDirectoryOnly)
                .Where(path => !string.Equals(
                    Path.GetFileName(path),
                    SequenceFileName,
                    StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
            var files = new List<CanonicalPayloadFile>(canonicalPaths.Length);
            foreach (var path in canonicalPaths)
            {
                files.Add(await ReadCanonicalFileAsync(
                    path,
                    allocationSequence,
                    cancellationToken));
            }

            ValidateLiveSet(live, files);
            var deleted = 0;
            foreach (var file in files)
            {
                if (live.ContainsKey(file.LiveReference.Reference))
                {
                    continue;
                }

                if (WindowsNativeAtomicFileCommitter.DeleteExact(file.Path) ==
                    WindowsNativeFileDeleteResult.Deleted)
                {
                    deleted = checked(deleted + 1);
                }
            }

            var kept = files.Where(file => live.ContainsKey(file.LiveReference.Reference)).ToArray();
            var keptBytes = kept.Aggregate(
                0L,
                static (total, file) => checked(total + checked((long)file.LiveReference.Reference.Length)));
            EnsureUsageWithinBudget(kept.Length, keptBytes);
            livePayloadCount = kept.Length;
            livePayloadBytes = keptBytes;
            return new NativeTransactionJournalPayloadReconciliationResult(
                kept.Length,
                keptBytes,
                deleted);
        }
        finally
        {
            gate.Release();
        }
    }

    private static Dictionary<
        NativeTransactionJournalPayloadReference,
        NativeTransactionJournalPayloadProvenance> BuildLiveSet(
        ReadOnlySpan<NativeTransactionJournalPayloadLiveReference> liveReferences)
    {
        var live = new Dictionary<
            NativeTransactionJournalPayloadReference,
            NativeTransactionJournalPayloadProvenance>(liveReferences.Length);
        foreach (var item in liveReferences)
        {
            if (!item.IsValid)
            {
                throw new ArgumentException(
                    "The rollback payload live-set contains an invalid reference.",
                    nameof(liveReferences));
            }
            if (live.TryGetValue(item.Reference, out var existing) &&
                existing != item.Provenance)
            {
                throw new InvalidDataException(
                    "The rollback payload live-set contains conflicting provenance.");
            }
            live[item.Reference] = item.Provenance;
        }
        return live;
    }

    private static void ValidateLiveSet(
        IReadOnlyDictionary<
            NativeTransactionJournalPayloadReference,
            NativeTransactionJournalPayloadProvenance> live,
        IReadOnlyList<CanonicalPayloadFile> files)
    {
        var observed = new Dictionary<
            NativeTransactionJournalPayloadReference,
            NativeTransactionJournalPayloadProvenance>(files.Count);
        foreach (var file in files)
        {
            var item = file.LiveReference;
            if (!observed.TryAdd(item.Reference, item.Provenance))
            {
                throw new InvalidDataException(
                    "The payload store contains duplicate canonical references.");
            }
            if (live.TryGetValue(item.Reference, out var expected) &&
                expected != item.Provenance)
            {
                throw new InvalidDataException(
                    "A live rollback payload has different durable provenance.");
            }
        }

        foreach (var item in live)
        {
            if (!observed.TryGetValue(item.Key, out var provenance) ||
                provenance != item.Value)
            {
                throw new InvalidDataException(
                    "A live rollback payload is missing or does not match its durable provenance.");
            }
        }
    }

    private async Task<CanonicalPayloadFile> ReadCanonicalFileAsync(
        string path,
        ulong allocationSequence,
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
        ValidateReference(reference);
        if (reference.Slot != slot ||
            reference.Generation != generation ||
            stream.Length != checked((long)PayloadHeaderSize + checked((long)reference.Length)) ||
            ToSequence(reference.Slot, reference.Generation) > allocationSequence)
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
        ValidateProvenance(provenance);
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
