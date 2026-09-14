using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal readonly record struct NativeTransactionJournalPayloadReference(
    uint Slot,
    uint Generation,
    ulong Length,
    ulong DigestLow,
    ulong DigestHigh)
{
    public bool IsValid =>
        Slot != 0 &&
        Generation != 0 &&
        Length is > 0 and <= int.MaxValue &&
        DigestLow != 0 &&
        DigestHigh != 0;
}

internal sealed partial class NativeTransactionJournalPayloadStore : IDisposable
{
    private const uint FormatVersion = 2;
    private const int IoBufferSize = 64 * 1024;
    private const int SequenceFileSize = 64;
    private const int PayloadHeaderSize = 256;
    private const int BindingOffset = 56;
    private const int HeaderChecksumOffset = 224;
    private const ulong SequenceMagic = 0x3151_4553_504A_4D52;
    private const ulong PayloadMagic = 0x3144_414F_4C50_4D52;
    private const string SequenceFileName = "payload-sequence.bin";
    private const string OwnerLockFileName = "payload-store.lock";
    private const ulong MaximumSequence = (ulong)uint.MaxValue * uint.MaxValue;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object disposeSync = new();
    private readonly string directoryPath;
    private readonly string sequencePath;
    private readonly int maximumPayloadCount;
    private readonly long maximumPayloadBytes;
    private readonly FileStream ownerLock;
    private int livePayloadCount;
    private long livePayloadBytes;
    private bool disposed;

    public NativeTransactionJournalPayloadStore(
        string directoryPath,
        int maximumPayloadCount,
        long maximumPayloadBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!Path.IsPathFullyQualified(directoryPath))
        {
            throw new ArgumentException("The payload directory path must be absolute.", nameof(directoryPath));
        }
        if (maximumPayloadCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPayloadCount),
                "The payload count limit must be positive.");
        }
        if (maximumPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPayloadBytes),
                "The payload byte budget must be positive.");
        }

        this.directoryPath = Path.GetFullPath(directoryPath);
        this.maximumPayloadCount = maximumPayloadCount;
        this.maximumPayloadBytes = maximumPayloadBytes;
        sequencePath = Path.Combine(this.directoryPath, SequenceFileName);
        Directory.CreateDirectory(this.directoryPath);
        ownerLock = new FileStream(
            Path.Combine(this.directoryPath, OwnerLockFileName),
            new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 1,
                Options = FileOptions.WriteThrough
            });
        try
        {
            (livePayloadCount, livePayloadBytes) = ReadCanonicalUsage();
        }
        catch
        {
            ownerLock.Dispose();
            throw;
        }
    }

    public async Task<NativeTransactionJournalPayloadReference> PersistAsync(
        NativeTransactionJournalPayloadBinding binding,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ValidateBinding(binding);
        if (payload.IsEmpty)
        {
            throw new ArgumentException("A durable rollback payload cannot be empty.", nameof(payload));
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            Directory.CreateDirectory(directoryPath);
            EnsureCanPersist(payload.Length);

            var sequence = await AllocateSequenceAsync(cancellationToken);
            var reference = CreateReference(sequence, payload.Span);
            var payloadPath = ResolvePayloadPath(reference.Slot, reference.Generation);
            var header = CreatePayloadHeader(reference, binding);
            var temporaryPath = CreateTemporaryPath(payloadPath);
            try
            {
                await WriteDurableFileAsync(
                    temporaryPath,
                    header,
                    payload,
                    cancellationToken);
                CommitNewFile(temporaryPath, payloadPath);
                livePayloadCount = checked(livePayloadCount + 1);
                livePayloadBytes = checked(livePayloadBytes + payload.Length);
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }

            return reference;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<byte[]> ReadAsync(
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadBinding expectedBinding,
        CancellationToken cancellationToken)
    {
        ValidateReference(reference);
        ValidateBinding(expectedBinding);

        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await ValidateSequenceCoversReferenceAsync(reference, cancellationToken);
            return await ReadValidatedPayloadAsync(reference, expectedBinding, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<byte[]> ReadAsync(
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadProvenance expectedProvenance,
        CancellationToken cancellationToken)
    {
        ValidateReference(reference);
        ValidateProvenance(expectedProvenance);

        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await ValidateSequenceCoversReferenceAsync(reference, cancellationToken);
            return await ReadValidatedPayloadAsync(reference, expectedProvenance, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadBinding expectedBinding,
        CancellationToken cancellationToken)
    {
        ValidateReference(reference);
        ValidateBinding(expectedBinding);

        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await ValidateSequenceCoversReferenceAsync(reference, cancellationToken);

            var payloadPath = ResolvePayloadPath(reference.Slot, reference.Generation);
            try
            {
                _ = await ReadValidatedPayloadAsync(reference, expectedBinding, cancellationToken);
                if (WindowsNativeAtomicFileCommitter.DeleteExact(payloadPath) ==
                    WindowsNativeFileDeleteResult.NotFound)
                {
                    return false;
                }
                livePayloadCount = checked(livePayloadCount - 1);
                livePayloadBytes = checked(livePayloadBytes - checked((long)reference.Length));
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadProvenance expectedProvenance,
        CancellationToken cancellationToken)
    {
        ValidateReference(reference);
        ValidateProvenance(expectedProvenance);

        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await ValidateSequenceCoversReferenceAsync(reference, cancellationToken);

            var payloadPath = ResolvePayloadPath(reference.Slot, reference.Generation);
            try
            {
                _ = await ReadValidatedPayloadAsync(
                    reference,
                    expectedProvenance,
                    cancellationToken);
                if (WindowsNativeAtomicFileCommitter.DeleteExact(payloadPath) ==
                    WindowsNativeFileDeleteResult.NotFound)
                {
                    return false;
                }
                livePayloadCount = checked(livePayloadCount - 1);
                livePayloadBytes = checked(livePayloadBytes - checked((long)reference.Length));
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        lock (disposeSync)
        {
            if (disposed)
            {
                return;
            }

            gate.Wait();
            try
            {
                disposed = true;
                ownerLock.Dispose();
            }
            finally
            {
                gate.Release();
                gate.Dispose();
            }
        }
    }

    private async Task<ulong> AllocateSequenceAsync(CancellationToken cancellationToken)
    {
        var current = await ReadSequenceAsync(allowMissingForEmptyStore: true, cancellationToken);
        if (current >= MaximumSequence)
        {
            throw new InvalidOperationException("The rollback payload identity space is exhausted.");
        }

        var next = checked(current + 1);
        await PersistSequenceAsync(next, cancellationToken);
        return next;
    }

    private (int Count, long Bytes) ReadCanonicalUsage()
    {
        var count = 0;
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(
                     directoryPath,
                     "payload-*.bin",
                     SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Equals(SequenceFileName, StringComparison.Ordinal))
            {
                continue;
            }
            if (!TryParseCanonicalPayloadFileName(fileName))
            {
                throw new InvalidDataException(
                    $"The payload store contains a non-canonical payload file name: {fileName}.");
            }

            var fileLength = new FileInfo(path).Length;
            if (fileLength <= PayloadHeaderSize)
            {
                throw new InvalidDataException(
                    $"The payload store contains a truncated canonical payload file: {fileName}.");
            }
            count = checked(count + 1);
            bytes = checked(bytes + fileLength - PayloadHeaderSize);
        }
        return (count, bytes);
    }

    private void EnsureCanPersist(int payloadLength)
    {
        if (livePayloadCount >= maximumPayloadCount ||
            payloadLength > maximumPayloadBytes - livePayloadBytes)
        {
            throw new NativeTransactionJournalPayloadCapacityException(
                livePayloadCount,
                livePayloadBytes,
                maximumPayloadCount,
                maximumPayloadBytes);
        }
    }

    private void EnsureUsageWithinBudget(int payloadCount, long payloadBytes)
    {
        if (payloadCount > maximumPayloadCount || payloadBytes > maximumPayloadBytes)
        {
            throw new NativeTransactionJournalPayloadCapacityException(
                payloadCount,
                payloadBytes,
                maximumPayloadCount,
                maximumPayloadBytes);
        }
    }

    private static bool TryParseCanonicalPayloadFileName(string fileName)
        => TryParseCanonicalPayloadFileName(fileName, out _, out _);

    private static bool TryParseCanonicalPayloadFileName(
        string fileName,
        out uint generation,
        out uint slot)
    {
        generation = 0;
        slot = 0;
        const int prefixLength = 8;
        const int numericLength = 10;
        const int separatorIndex = prefixLength + numericLength;
        const int expectedLength = prefixLength + numericLength + 1 + numericLength + 4;
        return fileName.Length == expectedLength &&
            fileName.StartsWith("payload-", StringComparison.Ordinal) &&
            fileName[separatorIndex] == '-' &&
            fileName.EndsWith(".bin", StringComparison.Ordinal) &&
            uint.TryParse(
                fileName.AsSpan(prefixLength, numericLength),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out generation) &&
            generation != 0 &&
            uint.TryParse(
                fileName.AsSpan(separatorIndex + 1, numericLength),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out slot) &&
            slot != 0;
    }

    private async Task ValidateSequenceCoversReferenceAsync(
        NativeTransactionJournalPayloadReference reference,
        CancellationToken cancellationToken)
    {
        var current = await ReadSequenceAsync(allowMissingForEmptyStore: false, cancellationToken);
        var referencedSequence = ToSequence(reference.Slot, reference.Generation);
        if (referencedSequence > current)
        {
            throw new InvalidDataException(
                "The payload identity is newer than the durable allocation sequence.");
        }
    }

    private async Task<ulong> ReadSequenceAsync(
        bool allowMissingForEmptyStore,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = OpenRead(sequencePath);
        }
        catch (FileNotFoundException)
        {
            return ResolveMissingSequence(allowMissingForEmptyStore);
        }
        catch (DirectoryNotFoundException)
        {
            return ResolveMissingSequence(allowMissingForEmptyStore);
        }

        var bytes = new byte[SequenceFileSize];
        await using (stream)
        {
            if (stream.Length != SequenceFileSize)
            {
                throw new InvalidDataException("The durable payload allocation sequence has an invalid length.");
            }

            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }

        if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0, 8)) != SequenceMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)) != FormatVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)) != SequenceFileSize)
        {
            throw new InvalidDataException("The durable payload allocation sequence header is invalid.");
        }

        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16, 8));
        var inverse = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(24, 8));
        if (sequence == 0 || sequence > MaximumSequence || inverse != ~sequence)
        {
            throw new InvalidDataException("The durable payload allocation sequence is invalid.");
        }

        Span<byte> expectedHash = stackalloc byte[32];
        SHA256.HashData(bytes.AsSpan(0, 32), expectedHash);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, bytes.AsSpan(32, 32)))
        {
            throw new InvalidDataException("The durable payload allocation sequence checksum is invalid.");
        }

        return sequence;
    }

    private async Task PersistSequenceAsync(ulong sequence, CancellationToken cancellationToken)
    {
        var bytes = new byte[SequenceFileSize];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0, 8), SequenceMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), SequenceFileSize);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16, 8), sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24, 8), ~sequence);
        SHA256.HashData(bytes.AsSpan(0, 32), bytes.AsSpan(32, 32));

        var temporaryPath = CreateTemporaryPath(sequencePath);
        try
        {
            await WriteDurableFileAsync(
                temporaryPath,
                bytes,
                ReadOnlyMemory<byte>.Empty,
                cancellationToken);
            CommitReplaceableFile(temporaryPath, sequencePath);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private async Task<byte[]> ReadValidatedPayloadAsync(
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadBinding expectedBinding,
        CancellationToken cancellationToken)
    {
        var payloadPath = ResolvePayloadPath(reference.Slot, reference.Generation);
        await using var stream = OpenRead(payloadPath);
        var expectedFileLength = checked((long)PayloadHeaderSize + checked((long)reference.Length));
        if (stream.Length != expectedFileLength)
        {
            throw new InvalidDataException("The durable rollback payload length is invalid.");
        }

        var header = new byte[PayloadHeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken);
        ValidatePayloadHeader(header, reference, expectedBinding);

        var payload = GC.AllocateUninitializedArray<byte>(checked((int)reference.Length));
        await stream.ReadExactlyAsync(payload, cancellationToken);
        var actualReference = CreateReference(
            ToSequence(reference.Slot, reference.Generation),
            payload);
        if (actualReference != reference)
        {
            throw new InvalidDataException("The durable rollback payload checksum is invalid.");
        }

        return payload;
    }

    private async Task<byte[]> ReadValidatedPayloadAsync(
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadProvenance expectedProvenance,
        CancellationToken cancellationToken)
    {
        var payloadPath = ResolvePayloadPath(reference.Slot, reference.Generation);
        await using var stream = OpenRead(payloadPath);
        var expectedFileLength = checked((long)PayloadHeaderSize + checked((long)reference.Length));
        if (stream.Length != expectedFileLength)
        {
            throw new InvalidDataException("The durable rollback payload length is invalid.");
        }

        var header = new byte[PayloadHeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken);
        ValidatePayloadHeader(header, reference, expectedProvenance);

        var payload = GC.AllocateUninitializedArray<byte>(checked((int)reference.Length));
        await stream.ReadExactlyAsync(payload, cancellationToken);
        var actualReference = CreateReference(
            ToSequence(reference.Slot, reference.Generation),
            payload);
        if (actualReference != reference)
        {
            throw new InvalidDataException("The durable rollback payload checksum is invalid.");
        }

        return payload;
    }

    private static NativeTransactionJournalPayloadReference CreateReference(
        ulong sequence,
        ReadOnlySpan<byte> payload)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(payload, hash);
        var digestLow = BinaryPrimitives.ReadUInt64LittleEndian(hash[..8]);
        var digestHigh = BinaryPrimitives.ReadUInt64LittleEndian(hash.Slice(8, 8));
        if (digestLow == 0 || digestHigh == 0)
        {
            throw new InvalidOperationException(
                "The rollback payload digest cannot be represented by the transaction journal ABI.");
        }

        var zeroBased = checked(sequence - 1);
        var slot = checked((uint)(zeroBased % uint.MaxValue) + 1);
        var generation = checked((uint)(zeroBased / uint.MaxValue) + 1);
        return new NativeTransactionJournalPayloadReference(
            slot,
            generation,
            checked((ulong)payload.Length),
            digestLow,
            digestHigh);
    }

    private static ulong ToSequence(uint slot, uint generation)
    {
        if (slot == 0 || generation == 0)
        {
            throw new ArgumentException("Payload slot and generation must be nonzero.");
        }

        return checked((ulong)(generation - 1) * uint.MaxValue + slot);
    }

    private static byte[] CreatePayloadHeader(
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadBinding binding)
    {
        var header = new byte[PayloadHeaderSize];
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0, 8), PayloadMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), PayloadHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), reference.Slot);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20, 4), reference.Generation);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24, 8), reference.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32, 8), reference.DigestLow);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(40, 8), reference.DigestHigh);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(48, 4),
            NativeTransactionJournalPayloadBinding.EncodedSize);
        binding.WriteTo(
            header.AsSpan(BindingOffset, NativeTransactionJournalPayloadBinding.EncodedSize));
        SHA256.HashData(
            header.AsSpan(0, HeaderChecksumOffset),
            header.AsSpan(HeaderChecksumOffset, 32));
        return header;
    }

    private static void ValidatePayloadHeader(
        ReadOnlySpan<byte> header,
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadBinding expectedBinding)
    {
        Span<byte> encodedExpectedBinding =
            stackalloc byte[NativeTransactionJournalPayloadBinding.EncodedSize];
        expectedBinding.WriteTo(encodedExpectedBinding);
        Span<byte> expectedHeaderChecksum = stackalloc byte[32];
        if (header.Length == PayloadHeaderSize)
        {
            SHA256.HashData(header.Slice(0, HeaderChecksumOffset), expectedHeaderChecksum);
        }

        if (header.Length != PayloadHeaderSize ||
            BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(0, 8)) != PayloadMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4)) != FormatVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4)) != PayloadHeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4)) != reference.Slot ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20, 4)) != reference.Generation ||
            BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(24, 8)) != reference.Length ||
            BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(32, 8)) != reference.DigestLow ||
            BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(40, 8)) != reference.DigestHigh ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(48, 4)) !=
                NativeTransactionJournalPayloadBinding.EncodedSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(52, 4)) != 0 ||
            !header.Slice(BindingOffset, NativeTransactionJournalPayloadBinding.EncodedSize)
                .SequenceEqual(encodedExpectedBinding) ||
            !header.Slice(216, 8).SequenceEqual(stackalloc byte[8]) ||
            !CryptographicOperations.FixedTimeEquals(
                expectedHeaderChecksum,
                header.Slice(HeaderChecksumOffset, 32)))
        {
            throw new InvalidDataException("The durable rollback payload header is invalid.");
        }
    }

    private static void ValidatePayloadHeader(
        ReadOnlySpan<byte> header,
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadProvenance expectedProvenance)
    {
        Span<byte> actualBindingHash = stackalloc byte[32];
        Span<byte> expectedHeaderChecksum = stackalloc byte[32];
        if (header.Length == PayloadHeaderSize)
        {
            SHA256.HashData(
                header.Slice(BindingOffset, NativeTransactionJournalPayloadBinding.EncodedSize),
                actualBindingHash);
            SHA256.HashData(header.Slice(0, HeaderChecksumOffset), expectedHeaderChecksum);
        }

        if (header.Length != PayloadHeaderSize ||
            BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(0, 8)) != PayloadMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4)) != FormatVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4)) != PayloadHeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4)) != reference.Slot ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20, 4)) != reference.Generation ||
            BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(24, 8)) != reference.Length ||
            BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(32, 8)) != reference.DigestLow ||
            BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(40, 8)) != reference.DigestHigh ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(48, 4)) !=
                NativeTransactionJournalPayloadBinding.EncodedSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(52, 4)) != 0 ||
            BinaryPrimitives.ReadUInt64LittleEndian(actualBindingHash[..8]) !=
                expectedProvenance.DigestLow ||
            BinaryPrimitives.ReadUInt64LittleEndian(actualBindingHash.Slice(8, 8)) !=
                expectedProvenance.DigestHigh ||
            !header.Slice(216, 8).SequenceEqual(stackalloc byte[8]) ||
            !CryptographicOperations.FixedTimeEquals(
                expectedHeaderChecksum,
                header.Slice(HeaderChecksumOffset, 32)))
        {
            throw new InvalidDataException("The durable rollback payload header is invalid.");
        }
    }

    private static async Task WriteDurableFileAsync(
        string path,
        ReadOnlyMemory<byte> first,
        ReadOnlyMemory<byte> second,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = IoBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            });
        await stream.WriteAsync(first, cancellationToken);
        if (!second.IsEmpty)
        {
            await stream.WriteAsync(second, cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static FileStream OpenRead(string path)
        => new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = IoBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });

    private bool ContainsCommittedBinaryFiles()
        => Directory.Exists(directoryPath) &&
            Directory.EnumerateFiles(directoryPath, "*.bin", SearchOption.TopDirectoryOnly).Any();

    private string ResolvePayloadPath(uint slot, uint generation)
        => Path.Combine(directoryPath, $"payload-{generation:D10}-{slot:D10}.bin");

    private static void ValidateReference(NativeTransactionJournalPayloadReference reference)
    {
        if (!reference.IsValid)
        {
            throw new ArgumentException("The rollback payload reference is invalid.", nameof(reference));
        }
    }

    private static void ValidateBinding(NativeTransactionJournalPayloadBinding binding)
    {
        if (!binding.IsValid)
        {
            throw new ArgumentException("The rollback payload binding is invalid.", nameof(binding));
        }
    }

    private static void ValidateProvenance(
        NativeTransactionJournalPayloadProvenance provenance)
    {
        if (!provenance.IsValid)
        {
            throw new ArgumentException(
                "The rollback payload provenance is invalid.",
                nameof(provenance));
        }
    }

    private ulong ResolveMissingSequence(bool allowMissingForEmptyStore)
    {
        if (allowMissingForEmptyStore && !ContainsCommittedBinaryFiles())
        {
            return 0;
        }

        throw new InvalidDataException("The durable payload allocation sequence is missing.");
    }

    private static string CreateTemporaryPath(string destinationPath)
        => $"{destinationPath}.{Guid.NewGuid():N}.tmp";

    private static void CommitNewFile(string temporaryPath, string destinationPath)
        => WindowsNativeAtomicFileCommitter.CommitNew(temporaryPath, destinationPath);

    private static void CommitReplaceableFile(string temporaryPath, string destinationPath)
        => WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, destinationPath);

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);
}

internal sealed class NativeTransactionJournalPayloadCapacityException(
    int currentPayloadCount,
    long currentPayloadBytes,
    int maximumPayloadCount,
    long maximumPayloadBytes)
    : IOException(
        $"The rollback payload store capacity is exhausted: " +
        $"{currentPayloadCount}/{maximumPayloadCount} payloads, " +
        $"{currentPayloadBytes}/{maximumPayloadBytes} bytes.");
