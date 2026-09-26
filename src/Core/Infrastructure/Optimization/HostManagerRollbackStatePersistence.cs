using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal enum HostManagerRollbackStateCommitOutcome : byte
{
    NotCommitted = 1,
    CommitAmbiguous = 2
}

internal sealed class HostManagerRollbackStateCommitException(
    HostManagerRollbackStateCommitOutcome outcome,
    Exception innerException)
    : IOException(
        outcome == HostManagerRollbackStateCommitOutcome.CommitAmbiguous
            ? "The Host Manager rollback state may have been committed, but verification failed."
            : "The Host Manager rollback state was not committed.",
        innerException)
{
    internal HostManagerRollbackStateCommitOutcome Outcome { get; } = outcome;
}

internal sealed class HostManagerRollbackStateOwnerLeaseException(
    string leasePath,
    Exception innerException)
    : IOException(
        $"Another Host Manager already owns rollback state lease '{leasePath}'.",
        innerException);

internal interface IHostManagerRollbackStateFileCommitter
{
    void Commit(
        string temporaryPath,
        string canonicalPath,
        ReadOnlySpan<byte> expectedImage,
        bool replaceExisting);
}

internal sealed class WindowsHostManagerRollbackStateFileCommitter
    : IHostManagerRollbackStateFileCommitter
{
    private const int IoBufferSize = 64 * 1024;

    internal static WindowsHostManagerRollbackStateFileCommitter Instance { get; } = new();

    private WindowsHostManagerRollbackStateFileCommitter()
    {
    }

    public void Commit(
        string temporaryPath,
        string canonicalPath,
        ReadOnlySpan<byte> expectedImage,
        bool replaceExisting)
    {
        try
        {
            if (replaceExisting)
            {
                WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, canonicalPath);
            }
            else
            {
                WindowsNativeAtomicFileCommitter.CommitNew(temporaryPath, canonicalPath);
            }
        }
        catch (Exception exception)
        {
            throw new HostManagerRollbackStateCommitException(
                HostManagerRollbackStateCommitOutcome.NotCommitted,
                exception);
        }

        try
        {
            VerifyCommittedImage(canonicalPath, expectedImage);
        }
        catch (Exception exception)
        {
            throw new HostManagerRollbackStateCommitException(
                HostManagerRollbackStateCommitOutcome.CommitAmbiguous,
                exception);
        }
    }

    private static void VerifyCommittedImage(
        string canonicalPath,
        ReadOnlySpan<byte> expectedImage)
    {
        using var stream = new FileStream(
            canonicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            IoBufferSize,
            FileOptions.SequentialScan);
        if (stream.Length != expectedImage.Length)
        {
            throw new InvalidDataException(
                "The committed Host Manager rollback state has an unexpected length.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
        try
        {
            var offset = 0;
            while (offset < expectedImage.Length)
            {
                var length = Math.Min(buffer.Length, expectedImage.Length - offset);
                var read = stream.Read(buffer, 0, length);
                if (read == 0
                    || !buffer.AsSpan(0, read).SequenceEqual(
                        expectedImage.Slice(offset, read)))
                {
                    throw new InvalidDataException(
                        "The committed Host Manager rollback state differs from its expected image.");
                }

                offset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}

internal static class HostManagerRollbackStateEnvelopeCodec
{
    internal const string Magic = "RMROLLBACK01";
    internal const uint SchemaVersion = 1;
    internal const int MaximumCanonicalBytes = 4 * 1024 * 1024;
    internal const int MaximumJsonDepth = 32;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = MaximumJsonDepth
    };

    internal static byte[] Encode(
        HostManagerRollbackStateDocument state,
        ulong revision)
    {
        if (revision == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        var normalized = JsonHostManagerRollbackStateStore.NormalizeDocument(state);
        var stateImage = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
        var envelope = new HostManagerRollbackStateEnvelope(
            Magic,
            SchemaVersion,
            revision,
            stateImage.Length,
            normalized,
            ComputeChecksum(stateImage, revision));
        var image = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (image.Length > MaximumCanonicalBytes)
        {
            throw new InvalidDataException(
                "The Host Manager rollback state exceeds its bounded canonical size.");
        }

        return image;
    }

    internal static HostManagerRollbackStateDecodedEnvelope Decode(ReadOnlySpan<byte> image)
    {
        if (image.Length is <= 0 or > MaximumCanonicalBytes)
        {
            throw new InvalidDataException(
                "The Host Manager rollback state has an invalid canonical size.");
        }

        var envelope = JsonSerializer.Deserialize<HostManagerRollbackStateEnvelope>(
            image,
            JsonOptions)
            ?? throw new InvalidDataException(
                "The Host Manager rollback state envelope cannot be JSON null.");
        if (!string.Equals(envelope.Magic, Magic, StringComparison.Ordinal)
            || envelope.SchemaVersion != SchemaVersion
            || envelope.Revision == 0
            || envelope.PayloadLength <= 0
            || envelope.PayloadLength > MaximumCanonicalBytes
            || envelope.State is null)
        {
            throw new InvalidDataException(
                "The Host Manager rollback state envelope identity is invalid.");
        }

        var normalized = JsonHostManagerRollbackStateStore.NormalizeDocument(envelope.State);
        var stateImage = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
        if (envelope.PayloadLength != stateImage.Length)
        {
            throw new InvalidDataException(
                "The Host Manager rollback state payload length does not match its canonical state.");
        }
        byte[] suppliedChecksum;
        try
        {
            suppliedChecksum = Convert.FromHexString(envelope.ChecksumSha256);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new InvalidDataException(
                "The Host Manager rollback state checksum is not canonical SHA-256.",
                exception);
        }

        var expectedChecksum = ComputeChecksum(stateImage, envelope.Revision);
        if (suppliedChecksum.Length != SHA256.HashSizeInBytes
            || !CryptographicOperations.FixedTimeEquals(
                suppliedChecksum,
                Convert.FromHexString(expectedChecksum)))
        {
            throw new InvalidDataException(
                "The Host Manager rollback state checksum does not match its payload.");
        }

        var canonicalImage = Encode(normalized, envelope.Revision);
        if (!image.SequenceEqual(canonicalImage))
        {
            throw new InvalidDataException(
                "The Host Manager rollback state envelope is not canonically encoded.");
        }

        return new HostManagerRollbackStateDecodedEnvelope(
            envelope.Revision,
            normalized,
            canonicalImage);
    }

    private static string ComputeChecksum(
        ReadOnlySpan<byte> stateImage,
        ulong revision)
    {
        Span<byte> header = stackalloc byte[sizeof(uint) + sizeof(ulong) + sizeof(int)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, SchemaVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(header[sizeof(uint)..], revision);
        BinaryPrimitives.WriteInt32LittleEndian(
            header[(sizeof(uint) + sizeof(ulong))..],
            stateImage.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        hash.AppendData(stateImage);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record HostManagerRollbackStateEnvelope(
        string Magic,
        uint SchemaVersion,
        ulong Revision,
        int PayloadLength,
        HostManagerRollbackStateDocument State,
        string ChecksumSha256);
}

internal sealed record HostManagerRollbackStateDecodedEnvelope(
    ulong Revision,
    HostManagerRollbackStateDocument State,
    byte[] CanonicalImage);
