using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

internal enum HostManagerAuthorityKind : uint
{
    AppliedOwnership = 1,
    TransactionJournal = 2
}

internal sealed record HostManagerAuthorityRetirementRecord(
    Guid TicketId,
    HostManagerAuthorityKind Kind,
    string CanonicalRelativePath,
    string? PayloadRelativeDirectory,
    Guid RootIncarnation,
    long CanonicalLength,
    byte[] CanonicalSha256,
    long ManifestLength,
    byte[] ManifestSha256)
{
    internal const int MaximumImageLength = 8 * 1024;
    internal const int MaximumRelativePathBytes = 1024;

    private const uint SchemaVersion = 1;
    private const int FixedHeaderLength = 136;
    private const int ChecksumLength = SHA256.HashSizeInBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static ReadOnlySpan<byte> Magic => "RMRET001"u8;

    internal byte[] Encode()
    {
        Validate();
        var canonicalPath = StrictUtf8.GetBytes(CanonicalRelativePath);
        var payloadPath = PayloadRelativeDirectory is null
            ? []
            : StrictUtf8.GetBytes(PayloadRelativeDirectory);
        var imageLength = checked(
            FixedHeaderLength +
            canonicalPath.Length +
            payloadPath.Length +
            ChecksumLength);
        if (imageLength > MaximumImageLength)
        {
            throw new InvalidDataException(
                "The authority-retirement tombstone exceeds its image limit.");
        }

        var image = new byte[imageLength];
        Magic.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8, 4), SchemaVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12, 4), (uint)Kind);
        TicketId.TryWriteBytes(image.AsSpan(16, 16));
        RootIncarnation.TryWriteBytes(image.AsSpan(32, 16));
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(48, 8), CanonicalLength);
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(56, 8), ManifestLength);
        CanonicalSha256.CopyTo(image, 64);
        ManifestSha256.CopyTo(image, 96);
        BinaryPrimitives.WriteUInt32LittleEndian(
            image.AsSpan(128, 4),
            checked((uint)canonicalPath.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            image.AsSpan(132, 4),
            checked((uint)payloadPath.Length));
        canonicalPath.CopyTo(image, FixedHeaderLength);
        payloadPath.CopyTo(image, FixedHeaderLength + canonicalPath.Length);
        SHA256.HashData(image.AsSpan(0, imageLength - ChecksumLength))
            .CopyTo(image, imageLength - ChecksumLength);
        return image;
    }

    internal static HostManagerAuthorityRetirementRecord Decode(
        ReadOnlySpan<byte> image)
    {
        if (image.Length < FixedHeaderLength + ChecksumLength
            || image.Length > MaximumImageLength
            || !image[..Magic.Length].SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(8, 4)) != SchemaVersion)
        {
            throw new InvalidDataException(
                "The authority-retirement tombstone header is invalid.");
        }

        var expectedChecksum = SHA256.HashData(image[..^ChecksumLength]);
        if (!CryptographicOperations.FixedTimeEquals(
                expectedChecksum,
                image[^ChecksumLength..]))
        {
            throw new InvalidDataException(
                "The authority-retirement tombstone checksum is invalid.");
        }

        var canonicalPathLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            image.Slice(128, 4)));
        var payloadPathLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            image.Slice(132, 4)));
        if (canonicalPathLength <= 0
            || canonicalPathLength > MaximumRelativePathBytes
            || payloadPathLength > MaximumRelativePathBytes
            || FixedHeaderLength + canonicalPathLength + payloadPathLength + ChecksumLength
                != image.Length)
        {
            throw new InvalidDataException(
                "The authority-retirement tombstone path lengths are invalid.");
        }

        string canonicalPath;
        string? payloadPath;
        try
        {
            canonicalPath = StrictUtf8.GetString(
                image.Slice(FixedHeaderLength, canonicalPathLength));
            payloadPath = payloadPathLength == 0
                ? null
                : StrictUtf8.GetString(image.Slice(
                    FixedHeaderLength + canonicalPathLength,
                    payloadPathLength));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The authority-retirement tombstone path encoding is invalid.",
                exception);
        }

        var record = new HostManagerAuthorityRetirementRecord(
            new Guid(image.Slice(16, 16)),
            (HostManagerAuthorityKind)BinaryPrimitives.ReadUInt32LittleEndian(
                image.Slice(12, 4)),
            canonicalPath,
            payloadPath,
            new Guid(image.Slice(32, 16)),
            BinaryPrimitives.ReadInt64LittleEndian(image.Slice(48, 8)),
            image.Slice(64, SHA256.HashSizeInBytes).ToArray(),
            BinaryPrimitives.ReadInt64LittleEndian(image.Slice(56, 8)),
            image.Slice(96, SHA256.HashSizeInBytes).ToArray());
        record.Validate();
        return record;
    }

    internal void Validate()
    {
        if (TicketId == Guid.Empty
            || RootIncarnation == Guid.Empty
            || Kind is not HostManagerAuthorityKind.AppliedOwnership
                and not HostManagerAuthorityKind.TransactionJournal
            || CanonicalLength <= 0
            || ManifestLength <= 0
            || CanonicalSha256.Length != SHA256.HashSizeInBytes
            || ManifestSha256.Length != SHA256.HashSizeInBytes
            || IsZeroDigest(CanonicalSha256)
            || IsZeroDigest(ManifestSha256))
        {
            throw new InvalidDataException(
                "The authority-retirement tombstone identity is invalid.");
        }

        ValidateRelativePath(CanonicalRelativePath);
        if (Kind == HostManagerAuthorityKind.AppliedOwnership)
        {
            if (PayloadRelativeDirectory is not null)
            {
                throw new InvalidDataException(
                    "Applied ownership cannot own a transaction payload directory.");
            }
            return;
        }

        if (PayloadRelativeDirectory is null)
        {
            throw new InvalidDataException(
                "A transaction-journal retirement must include its payload directory.");
        }
        ValidateRelativePath(PayloadRelativeDirectory);
        if (PathsOverlap(CanonicalRelativePath, PayloadRelativeDirectory))
        {
            throw new InvalidDataException(
                "The retired canonical and payload paths must be disjoint.");
        }
    }

    internal static void ValidateRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || value.Contains('\\')
            || StrictUtf8.GetByteCount(value) > MaximumRelativePathBytes)
        {
            throw new InvalidDataException(
                "An authority-retirement path must be a bounded canonical relative path.");
        }

        var segments = value.Split('/', StringSplitOptions.None);
        if (segments.Any(static segment =>
                string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new InvalidDataException(
                "An authority-retirement path contains an invalid segment.");
        }
    }

    internal static bool PathsOverlap(string first, string second)
    {
        var normalizedFirst = first.Replace('\\', '/').TrimEnd('/');
        var normalizedSecond = second.Replace('\\', '/').TrimEnd('/');
        return string.Equals(
                normalizedFirst,
                normalizedSecond,
                StringComparison.OrdinalIgnoreCase)
            || normalizedFirst.StartsWith(
                normalizedSecond + '/',
                StringComparison.OrdinalIgnoreCase)
            || normalizedSecond.StartsWith(
                normalizedFirst + '/',
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsZeroDigest(ReadOnlySpan<byte> digest)
    {
        foreach (var value in digest)
        {
            if (value != 0)
            {
                return false;
            }
        }
        return true;
    }
}

internal readonly record struct HostManagerAuthorityRetirementTicket(
    Guid TicketId,
    HostManagerAuthorityKind Kind);
