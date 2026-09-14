using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;

namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

internal sealed class HostManagerAdapterSchedulingTransactionPayload
{
    private readonly byte[] payloadBytes;

    internal HostManagerAdapterSchedulingTransactionPayload(
        AdapterSchedulingStateIdentity identity,
        uint payloadVersion,
        string payloadSha256Digest,
        ReadOnlySpan<byte> payloadBytes,
        AdapterCpuSchedulingGrade? originalCpuGrade,
        AdapterGpuSchedulingGrade? originalGpuGrade)
    {
        Identity = identity;
        PayloadVersion = payloadVersion;
        PayloadSha256Digest = payloadSha256Digest;
        this.payloadBytes = payloadBytes.ToArray();
        OriginalCpuGrade = originalCpuGrade;
        OriginalGpuGrade = originalGpuGrade;
    }

    internal AdapterSchedulingStateIdentity Identity { get; }

    internal uint PayloadVersion { get; }

    internal string PayloadSha256Digest { get; }

    internal AdapterSchedulingStatePayload Payload =>
        new(PayloadVersion, PayloadSha256Digest, payloadBytes.ToArray());

    internal AdapterCpuSchedulingGrade? OriginalCpuGrade { get; }

    internal AdapterGpuSchedulingGrade? OriginalGpuGrade { get; }
}

internal static class HostManagerAdapterSchedulingTransactionPayloadCodec
{
    internal const ushort Version = 1;
    internal const int HeaderSize = 160;
    internal const int EnvelopeDigestOffset = 112;
    internal const int EnvelopeDigestSize = SHA256.HashSizeInBytes;

    private const uint Magic = 0x3153_4148;
    private const uint CpuGradeValid = 1U << 0;
    private const uint GpuGradeValid = 1U << 1;
    private const uint KnownValidFlags = CpuGradeValid | GpuGradeValid;
    private const int TotalLengthOffset = 8;
    private const int ValidFlagsOffset = 12;
    private const int PayloadDigestOffset = 80;
    private const int ReservedHeaderOffset = 144;
    private const int SoftwareIdLengthOffset = 60;
    private const int AdapterIdLengthOffset = 64;
    private const int ApplicationIdLengthOffset = 68;
    private const int PayloadLengthOffset = 72;
    private const int CpuGradeOffset = 76;
    private const int GpuGradeOffset = 77;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryEncode(
        AdapterSchedulingStateExportResult export,
        int maximumEnvelopeBytes,
        out byte[] envelope)
    {
        envelope = [];
        if (maximumEnvelopeBytes < HeaderSize
            || export is null
            || export.Status != AdapterSchedulingStateExportStatus.Exported
            || export.Identity is null
            || export.Payload is null
            || !IsCanonicalIdentity(export.Identity)
            || !IsCanonicalGrade(export.CurrentCpuGrade)
            || !IsCanonicalGrade(export.CurrentGpuGrade)
            || export.Payload.Version == 0
            || export.Payload.Bytes is null
            || export.Payload.Bytes.Length == 0)
        {
            return false;
        }

        Span<byte> payloadDigest = stackalloc byte[SHA256.HashSizeInBytes];
        if (!TryDecodeCanonicalSha256(export.Payload.Sha256Digest, payloadDigest))
        {
            return false;
        }

        Span<byte> actualPayloadDigest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(export.Payload.Bytes, actualPayloadDigest);
        if (!CryptographicOperations.FixedTimeEquals(payloadDigest, actualPayloadDigest)
            || !TryGetUtf8ByteCount(export.Identity.SoftwareId, out var softwareIdLength)
            || !TryGetUtf8ByteCount(export.Identity.AdapterId, out var adapterIdLength)
            || !TryGetUtf8ByteCount(export.Identity.ApplicationId, out var applicationIdLength))
        {
            return false;
        }

        int totalLength;
        try
        {
            totalLength = checked(
                HeaderSize
                + softwareIdLength
                + adapterIdLength
                + applicationIdLength
                + export.Payload.Bytes.Length);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (totalLength > maximumEnvelopeBytes)
        {
            return false;
        }

        var encoded = new byte[totalLength];
        var destination = encoded.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..8], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[TotalLengthOffset..12], checked((uint)totalLength));

        var validFlags = 0U;
        if (export.CurrentCpuGrade.HasValue)
        {
            validFlags |= CpuGradeValid;
            destination[CpuGradeOffset] = (byte)export.CurrentCpuGrade.Value;
        }
        if (export.CurrentGpuGrade.HasValue)
        {
            validFlags |= GpuGradeValid;
            destination[GpuGradeOffset] = (byte)export.CurrentGpuGrade.Value;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination[ValidFlagsOffset..16], validFlags);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..24], export.Identity.AdapterInstanceId.High);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..32], export.Identity.AdapterInstanceId.Low);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..40], export.Identity.LeaseId.High);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[40..48], export.Identity.LeaseId.Low);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[48..56], export.Identity.LeaseGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[56..60], export.Payload.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[SoftwareIdLengthOffset..64], checked((uint)softwareIdLength));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[AdapterIdLengthOffset..68], checked((uint)adapterIdLength));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[ApplicationIdLengthOffset..72], checked((uint)applicationIdLength));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[PayloadLengthOffset..76], checked((uint)export.Payload.Bytes.Length));
        payloadDigest.CopyTo(destination[PayloadDigestOffset..EnvelopeDigestOffset]);

        var cursor = HeaderSize;
        cursor += StrictUtf8.GetBytes(export.Identity.SoftwareId, destination[cursor..]);
        cursor += StrictUtf8.GetBytes(export.Identity.AdapterId, destination[cursor..]);
        cursor += StrictUtf8.GetBytes(export.Identity.ApplicationId, destination[cursor..]);
        export.Payload.Bytes.AsSpan().CopyTo(destination[cursor..]);

        Span<byte> envelopeDigest = stackalloc byte[SHA256.HashSizeInBytes];
        ComputeEnvelopeDigest(destination, envelopeDigest);
        envelopeDigest.CopyTo(destination.Slice(EnvelopeDigestOffset, EnvelopeDigestSize));
        envelope = encoded;
        return true;
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> envelope,
        int maximumEnvelopeBytes,
        out HostManagerAdapterSchedulingTransactionPayload payload)
    {
        payload = null!;
        if (maximumEnvelopeBytes < HeaderSize
            || envelope.Length < HeaderSize
            || envelope.Length > maximumEnvelopeBytes
            || BinaryPrimitives.ReadUInt32LittleEndian(envelope[0..4]) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(envelope[4..6]) != Version
            || BinaryPrimitives.ReadUInt16LittleEndian(envelope[6..8]) != HeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(envelope[TotalLengthOffset..12]) != (uint)envelope.Length
            || !envelope[78..80].IsEmptyOrAllZero()
            || !envelope[ReservedHeaderOffset..HeaderSize].IsEmptyOrAllZero())
        {
            return false;
        }

        var validFlags = BinaryPrimitives.ReadUInt32LittleEndian(envelope[ValidFlagsOffset..16]);
        if ((validFlags & ~KnownValidFlags) != 0
            || !TryDecodeGrade(validFlags, CpuGradeValid, envelope[CpuGradeOffset], out AdapterCpuSchedulingGrade? cpuGrade)
            || !TryDecodeGrade(validFlags, GpuGradeValid, envelope[GpuGradeOffset], out AdapterGpuSchedulingGrade? gpuGrade))
        {
            return false;
        }

        Span<byte> expectedEnvelopeDigest = stackalloc byte[SHA256.HashSizeInBytes];
        ComputeEnvelopeDigest(envelope, expectedEnvelopeDigest);
        if (!CryptographicOperations.FixedTimeEquals(
                expectedEnvelopeDigest,
                envelope.Slice(EnvelopeDigestOffset, EnvelopeDigestSize)))
        {
            return false;
        }

        var instanceId = new AdapterInstanceId(
            BinaryPrimitives.ReadUInt64LittleEndian(envelope[16..24]),
            BinaryPrimitives.ReadUInt64LittleEndian(envelope[24..32]));
        var leaseId = new AdapterInstanceLeaseId(
            BinaryPrimitives.ReadUInt64LittleEndian(envelope[32..40]),
            BinaryPrimitives.ReadUInt64LittleEndian(envelope[40..48]));
        var leaseGeneration = BinaryPrimitives.ReadUInt64LittleEndian(envelope[48..56]);
        var payloadVersion = BinaryPrimitives.ReadUInt32LittleEndian(envelope[56..60]);
        var lengthsValid = TryReadLength(envelope, SoftwareIdLengthOffset, out var softwareIdLength)
            & TryReadLength(envelope, AdapterIdLengthOffset, out var adapterIdLength)
            & TryReadLength(envelope, ApplicationIdLengthOffset, out var applicationIdLength)
            & TryReadLength(envelope, PayloadLengthOffset, out var payloadLength);
        if (!instanceId.IsValid || !leaseId.IsValid || leaseGeneration == 0 || payloadVersion == 0
            || !lengthsValid
            || softwareIdLength == 0
            || adapterIdLength == 0
            || applicationIdLength == 0
            || payloadLength == 0)
        {
            return false;
        }

        int expectedLength;
        try
        {
            expectedLength = checked(
                HeaderSize
                + softwareIdLength
                + adapterIdLength
                + applicationIdLength
                + payloadLength);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (expectedLength != envelope.Length)
        {
            return false;
        }

        var cursor = HeaderSize;
        if (!TryDecodeIdentityText(envelope.Slice(cursor, softwareIdLength), out var softwareId))
        {
            return false;
        }
        cursor += softwareIdLength;
        if (!TryDecodeIdentityText(envelope.Slice(cursor, adapterIdLength), out var adapterId))
        {
            return false;
        }
        cursor += adapterIdLength;
        if (!TryDecodeIdentityText(envelope.Slice(cursor, applicationIdLength), out var applicationId))
        {
            return false;
        }
        cursor += applicationIdLength;

        var opaquePayload = envelope.Slice(cursor, payloadLength);
        Span<byte> actualPayloadDigest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(opaquePayload, actualPayloadDigest);
        var encodedPayloadDigest = envelope.Slice(PayloadDigestOffset, SHA256.HashSizeInBytes);
        if (!CryptographicOperations.FixedTimeEquals(actualPayloadDigest, encodedPayloadDigest))
        {
            return false;
        }

        var identity = new AdapterSchedulingStateIdentity(
            instanceId,
            leaseId,
            leaseGeneration,
            softwareId,
            adapterId,
            applicationId);
        payload = new HostManagerAdapterSchedulingTransactionPayload(
            identity,
            payloadVersion,
            Convert.ToHexString(encodedPayloadDigest).ToLowerInvariant(),
            opaquePayload,
            cpuGrade,
            gpuGrade);
        return true;
    }

    private static bool IsCanonicalIdentity(AdapterSchedulingStateIdentity identity)
    {
        return identity.AdapterInstanceId.IsValid
            && identity.LeaseId.IsValid
            && identity.LeaseGeneration != 0
            && IsCanonicalIdentityText(identity.SoftwareId)
            && IsCanonicalIdentityText(identity.AdapterId)
            && IsCanonicalIdentityText(identity.ApplicationId);
    }

    private static bool IsCanonicalIdentityText(string? value) =>
        !string.IsNullOrEmpty(value) && !value.Contains('\0', StringComparison.Ordinal);

    private static bool IsCanonicalGrade(AdapterCpuSchedulingGrade? grade) =>
        !grade.HasValue || Enum.IsDefined(grade.Value);

    private static bool IsCanonicalGrade(AdapterGpuSchedulingGrade? grade) =>
        !grade.HasValue || Enum.IsDefined(grade.Value);

    private static bool TryGetUtf8ByteCount(string value, out int byteCount)
    {
        byteCount = 0;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
            return byteCount > 0;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryDecodeIdentityText(ReadOnlySpan<byte> bytes, out string value)
    {
        value = string.Empty;
        try
        {
            value = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        return IsCanonicalIdentityText(value);
    }

    private static bool TryDecodeCanonicalSha256(string? value, Span<byte> digest)
    {
        if (value is null || value.Length != SHA256.HashSizeInBytes * 2)
        {
            return false;
        }

        for (var index = 0; index < digest.Length; index++)
        {
            var high = DecodeLowerHex(value[index * 2]);
            var low = DecodeLowerHex(value[(index * 2) + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }
            digest[index] = (byte)((high << 4) | low);
        }
        return true;
    }

    private static int DecodeLowerHex(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1
    };

    private static bool TryReadLength(ReadOnlySpan<byte> envelope, int offset, out int length)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(envelope.Slice(offset, sizeof(uint)));
        if (value > int.MaxValue)
        {
            length = 0;
            return false;
        }
        length = (int)value;
        return true;
    }

    private static bool TryDecodeGrade<TGrade>(
        uint validFlags,
        uint validFlag,
        byte encoded,
        out TGrade? grade)
        where TGrade : struct, Enum
    {
        grade = null;
        if ((validFlags & validFlag) == 0)
        {
            return encoded == 0;
        }
        if (!Enum.IsDefined(typeof(TGrade), encoded))
        {
            return false;
        }
        grade = (TGrade)Enum.ToObject(typeof(TGrade), encoded);
        return true;
    }

    private static void ComputeEnvelopeDigest(ReadOnlySpan<byte> envelope, Span<byte> destination)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(envelope[..EnvelopeDigestOffset]);
        hash.AppendData(envelope[(EnvelopeDigestOffset + EnvelopeDigestSize)..]);
        if (!hash.TryGetHashAndReset(destination, out var written)
            || written != SHA256.HashSizeInBytes)
        {
            throw new CryptographicException("Unable to compute the adapter scheduling rollback envelope digest.");
        }
    }

    private static bool IsEmptyOrAllZero(this ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value != 0)
            {
                return false;
            }
        }
        return true;
    }
}
