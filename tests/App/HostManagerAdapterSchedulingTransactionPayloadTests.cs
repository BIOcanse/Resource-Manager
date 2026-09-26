using System.Buffers.Binary;
using System.Security.Cryptography;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace ResourceManager.App.Tests;

public sealed class HostManagerAdapterSchedulingTransactionPayloadTests
{
    private const int MaximumEnvelopeBytes = 4096;
    private const int SoftwareIdLengthOffset = 60;
    private const int PayloadLengthOffset = 72;
    private const int ReservedHeaderOffset = 144;

    [Theory]
    [InlineData(1UL, 0UL, 0UL, 2UL)]
    [InlineData(0UL, 3UL, 4UL, 0UL)]
    public void RoundTrip_PreservesSingleHighOrLowIdentityAndOpaqueState(
        ulong instanceHigh,
        ulong instanceLow,
        ulong leaseHigh,
        ulong leaseLow)
    {
        var export = CreateExport(
            new AdapterInstanceId(instanceHigh, instanceLow),
            new AdapterInstanceLeaseId(leaseHigh, leaseLow));

        Assert.True(HostManagerAdapterSchedulingTransactionPayloadCodec.TryEncode(
            export,
            MaximumEnvelopeBytes,
            out var envelope));
        Assert.True(HostManagerAdapterSchedulingTransactionPayloadCodec.TryDecode(
            envelope,
            MaximumEnvelopeBytes,
            out var decoded));

        Assert.Equal(export.Identity, decoded.Identity);
        Assert.Equal(export.Payload!.Version, decoded.PayloadVersion);
        Assert.Equal(export.Payload.Sha256Digest, decoded.PayloadSha256Digest);
        Assert.Equal(export.Payload.Bytes, decoded.Payload.Bytes);
        Assert.Equal(export.CurrentCpuGrade, decoded.OriginalCpuGrade);
        Assert.Equal(export.CurrentGpuGrade, decoded.OriginalGpuGrade);
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData((int)AdapterCpuSchedulingGrade.Freeze, -1)]
    [InlineData(-1, (int)AdapterGpuSchedulingGrade.Extreme)]
    [InlineData((int)AdapterCpuSchedulingGrade.Optimize, (int)AdapterGpuSchedulingGrade.Normal)]
    public void RoundTrip_PreservesNullableGradeValidity(int cpuValue, int gpuValue)
    {
        var cpuGrade = cpuValue < 0 ? null : (AdapterCpuSchedulingGrade?)cpuValue;
        var gpuGrade = gpuValue < 0 ? null : (AdapterGpuSchedulingGrade?)gpuValue;
        var export = CreateExport(cpuGrade: cpuGrade, gpuGrade: gpuGrade);

        Assert.True(HostManagerAdapterSchedulingTransactionPayloadCodec.TryEncode(
            export,
            MaximumEnvelopeBytes,
            out var envelope));
        Assert.True(HostManagerAdapterSchedulingTransactionPayloadCodec.TryDecode(
            envelope,
            MaximumEnvelopeBytes,
            out var decoded));

        Assert.Equal(cpuGrade, decoded.OriginalCpuGrade);
        Assert.Equal(gpuGrade, decoded.OriginalGpuGrade);
    }

    [Theory]
    [InlineData(AdapterSchedulingStateExportStatus.Unsupported)]
    [InlineData(AdapterSchedulingStateExportStatus.Unavailable)]
    [InlineData(AdapterSchedulingStateExportStatus.Conflict)]
    public void Encode_RejectsEveryNonExportedStatus(AdapterSchedulingStateExportStatus status)
    {
        var export = CreateExport() with { Status = status };

        Assert.False(HostManagerAdapterSchedulingTransactionPayloadCodec.TryEncode(
            export,
            MaximumEnvelopeBytes,
            out var envelope));
        Assert.Empty(envelope);
    }

    [Fact]
    public void Encode_RejectsWrongPayloadDigestAndNonCanonicalDigestText()
    {
        var export = CreateExport();
        var wrongDigest = export with
        {
            Payload = export.Payload! with { Sha256Digest = new string('0', 64) }
        };
        var uppercaseDigest = export with
        {
            Payload = export.Payload! with { Sha256Digest = export.Payload.Sha256Digest.ToUpperInvariant() }
        };

        Assert.False(HostManagerAdapterSchedulingTransactionPayloadCodec.TryEncode(
            wrongDigest,
            MaximumEnvelopeBytes,
            out _));
        Assert.False(HostManagerAdapterSchedulingTransactionPayloadCodec.TryEncode(
            uppercaseDigest,
            MaximumEnvelopeBytes,
            out _));
    }

    [Fact]
    public void Encode_RejectsZeroIdentityAndInvalidGrade()
    {
        var export = CreateExport();
        var zeroInstance = export with
        {
            Identity = export.Identity! with { AdapterInstanceId = default }
        };
        var invalidGrade = export with
        {
            CurrentCpuGrade = (AdapterCpuSchedulingGrade)byte.MaxValue
        };

        Assert.False(HostManagerAdapterSchedulingTransactionPayloadCodec.TryEncode(
            zeroInstance,
            MaximumEnvelopeBytes,
            out _));
        Assert.False(HostManagerAdapterSchedulingTransactionPayloadCodec.TryEncode(
            invalidGrade,
            MaximumEnvelopeBytes,
            out _));
    }

    [Fact]
    public void EncodeAndDecode_OwnTheirPayloadBytes()
    {
        var export = CreateExport();
        var expectedPayload = export.Payload!.Bytes.ToArray();

        Assert.True(HostManagerAdapterSchedulingTransactionPayloadCodec.TryEncode(
            export,
            MaximumEnvelopeBytes,
            out var envelope));
        export.Payload.Bytes.AsSpan().Fill(0xff);

        Assert.True(HostManagerAdapterSchedulingTransactionPayloadCodec.TryDecode(
            envelope,
            MaximumEnvelopeBytes,
            out var decoded));
        var firstProjection = decoded.Payload;
        firstProjection.Bytes.AsSpan().Fill(0xee);
        envelope.AsSpan().Fill(0xdd);

        Assert.Equal(expectedPayload, decoded.Payload.Bytes);
    }

    [Fact]
    public void Decode_RejectsEnvelopeDigestCorruption()
    {
        var envelope = Encode(CreateExport());
        envelope[HostManagerAdapterSchedulingTransactionPayloadCodec.EnvelopeDigestOffset] ^= 0x01;

        AssertDecodeRejected(envelope);
    }

    [Fact]
    public void Decode_RejectsPayloadDigestCorruptionEvenWithFreshEnvelopeDigest()
    {
        var envelope = Encode(CreateExport());
        envelope[^1] ^= 0x01;
        RefreshEnvelopeDigest(envelope);

        AssertDecodeRejected(envelope);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void Decode_RejectsMagicVersionOrHeaderSizeCorruption(int offset)
    {
        var envelope = Encode(CreateExport());
        envelope[offset] ^= 0x01;
        RefreshEnvelopeDigest(envelope);

        AssertDecodeRejected(envelope);
    }

    [Fact]
    public void Decode_RejectsReservedBytesEvenWithFreshEnvelopeDigest()
    {
        var envelope = Encode(CreateExport());
        envelope[ReservedHeaderOffset] = 1;
        RefreshEnvelopeDigest(envelope);

        AssertDecodeRejected(envelope);
    }

    [Fact]
    public void Decode_RejectsLengthCorruptionEvenWithFreshEnvelopeDigest()
    {
        var wrongTotal = Encode(CreateExport());
        BinaryPrimitives.WriteUInt32LittleEndian(wrongTotal.AsSpan(8, 4), (uint)(wrongTotal.Length - 1));
        RefreshEnvelopeDigest(wrongTotal);

        var wrongPayload = Encode(CreateExport());
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(wrongPayload.AsSpan(PayloadLengthOffset, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(wrongPayload.AsSpan(PayloadLengthOffset, 4), payloadLength - 1);
        RefreshEnvelopeDigest(wrongPayload);

        AssertDecodeRejected(wrongTotal);
        AssertDecodeRejected(wrongPayload);
    }

    [Fact]
    public void Decode_RejectsTrailingBytes()
    {
        var canonical = Encode(CreateExport());
        var withTrailing = new byte[canonical.Length + 1];
        canonical.CopyTo(withTrailing, 0);

        AssertDecodeRejected(withTrailing);
    }

    [Fact]
    public void Decode_RejectsInvalidUtf8EvenWithFreshEnvelopeDigest()
    {
        var envelope = Encode(CreateExport());
        Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(
            envelope.AsSpan(SoftwareIdLengthOffset, 4)) > 0);
        envelope[HostManagerAdapterSchedulingTransactionPayloadCodec.HeaderSize] = 0xff;
        RefreshEnvelopeDigest(envelope);

        AssertDecodeRejected(envelope);
    }

    [Fact]
    public void EncodeAndDecode_EnforceTheCallerMaximumEnvelopeBytes()
    {
        var export = CreateExport();
        var canonical = Encode(export);

        Assert.False(HostManagerAdapterSchedulingTransactionPayloadCodec.TryEncode(
            export,
            canonical.Length - 1,
            out var rejected));
        Assert.Empty(rejected);
        Assert.False(HostManagerAdapterSchedulingTransactionPayloadCodec.TryDecode(
            canonical,
            canonical.Length - 1,
            out _));
        Assert.True(HostManagerAdapterSchedulingTransactionPayloadCodec.TryDecode(
            canonical,
            canonical.Length,
            out _));
    }

    [Fact]
    public void LongIdentityText_IsBoundOnlyByTheExplicitTotalEnvelopeLimit()
    {
        var export = CreateExport() with
        {
            Identity = CreateExport().Identity! with
            {
                SoftwareId = new string('软', 4096),
                AdapterId = new string('a', 8192),
                ApplicationId = new string('应', 2048)
            }
        };

        Assert.True(HostManagerAdapterSchedulingTransactionPayloadCodec.TryEncode(
            export,
            64 * 1024,
            out var envelope));
        Assert.False(HostManagerAdapterSchedulingTransactionPayloadCodec.TryEncode(
            export,
            envelope.Length - 1,
            out _));
        Assert.True(HostManagerAdapterSchedulingTransactionPayloadCodec.TryDecode(
            envelope,
            envelope.Length,
            out var decoded));
        Assert.Equal(export.Identity, decoded.Identity);
    }

    private static AdapterSchedulingStateExportResult CreateExport(
        AdapterInstanceId? instanceId = null,
        AdapterInstanceLeaseId? leaseId = null,
        AdapterCpuSchedulingGrade? cpuGrade = AdapterCpuSchedulingGrade.Optimize,
        AdapterGpuSchedulingGrade? gpuGrade = AdapterGpuSchedulingGrade.Normal)
    {
        var bytes = new byte[] { 0x00, 0x12, 0x34, 0x80, 0xfe, 0xff };
        return new AdapterSchedulingStateExportResult(
            AdapterSchedulingStateExportStatus.Exported,
            new AdapterSchedulingStateIdentity(
                instanceId ?? new AdapterInstanceId(0x0102_0304_0506_0708, 0x1112_1314_1516_1718),
                leaseId ?? new AdapterInstanceLeaseId(0x2122_2324_2526_2728, 0x3132_3334_3536_3738),
                9,
                "软件.alpha",
                "adapter.beta",
                "application.gamma"),
            new AdapterSchedulingStatePayload(7, Digest(bytes), bytes),
            cpuGrade,
            gpuGrade,
            new DateTimeOffset(2026, 7, 22, 12, 34, 56, TimeSpan.Zero),
            "exported");
    }

    private static byte[] Encode(AdapterSchedulingStateExportResult export)
    {
        Assert.True(HostManagerAdapterSchedulingTransactionPayloadCodec.TryEncode(
            export,
            MaximumEnvelopeBytes,
            out var envelope));
        return envelope;
    }

    private static void AssertDecodeRejected(byte[] envelope)
    {
        Assert.False(HostManagerAdapterSchedulingTransactionPayloadCodec.TryDecode(
            envelope,
            MaximumEnvelopeBytes,
            out _));
    }

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void RefreshEnvelopeDigest(byte[] envelope)
    {
        var digestOffset = HostManagerAdapterSchedulingTransactionPayloadCodec.EnvelopeDigestOffset;
        var digestSize = HostManagerAdapterSchedulingTransactionPayloadCodec.EnvelopeDigestSize;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(envelope.AsSpan(0, digestOffset));
        hash.AppendData(envelope.AsSpan(digestOffset + digestSize));
        var digest = hash.GetHashAndReset();
        digest.CopyTo(envelope, digestOffset);
    }
}
