using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Operations;

internal sealed record HostManagerOperationCanonicalEnvelope(
    ulong ConfigurationGeneration,
    ulong PublicationRevision,
    NativeOperationHandle128 SessionInstanceId,
    NativeOperationHandle128 SourceClockInstanceId,
    NativeOperationCanonicalImage NativeImage,
    HostManagerOperationPayloadCatalog Payloads,
    HostManagerOperationEffectReceiptTable Receipts,
    byte[] PriorEnvelopeSha256,
    byte[] FullEnvelopeSha256);

internal static class HostManagerOperationCanonicalEnvelopeCodec
{
    private const ulong Magic = 0x0031_5650_4F48_4D52;
    private const uint EnvelopeVersion = 1;
    private const uint HeaderSize = 320;
    private const uint PayloadCatalogVersion = 1;
    private const uint ReceiptCatalogVersion = 1;
    private const int PayloadDescriptorSize = 128;
    private const int ReceiptSize = 208;

    internal static byte[] Encode(
        HostManagerOperationCanonicalEnvelope source,
        CompiledHostManagerOperationCoordinatorCapacityPlan capacity)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(capacity);
        ValidateIdentity(source);
        ValidateCatalogBindings(
            source.NativeImage.Records,
            source.Payloads,
            source.Receipts);
        var payloads = source.Payloads.ExportCanonical();
        var receipts = source.Receipts.ExportCanonical();
        if ((uint)payloads.Length > capacity.MaximumPayloadCount
            || source.Payloads.PayloadByteCount > capacity.MaximumPayloadByteCount
            || (uint)receipts.Length > capacity.MaximumEffectReceiptCount)
        {
            throw new InvalidDataException(
                "The operation canonical envelope exceeds its configured catalog capacity.");
        }

        var nativeLength = checked(
            SizeOf<NativeOperationPersistenceHeader>()
            + source.NativeImage.Records.Length * SizeOf<NativeOperationOutput>());
        if ((ulong)nativeLength > capacity.MaximumPersistenceByteCount)
        {
            throw new InvalidDataException(
                "The operation native persistence image exceeds its configured capacity.");
        }
        var payloadIndexLength = checked(payloads.Length * PayloadDescriptorSize);
        var payloadBytesLength = checked((int)source.Payloads.PayloadByteCount);
        var receiptLength = checked(receipts.Length * ReceiptSize);
        var bodyLength = checked(
            (int)HeaderSize
            + nativeLength
            + payloadIndexLength
            + payloadBytesLength
            + receiptLength);
        var imageLength = checked(bodyLength + SHA256.HashSizeInBytes);
        if ((ulong)imageLength > capacity.MaximumEnvelopeByteCount)
        {
            throw new InvalidDataException(
                "The operation canonical envelope exceeds its configured byte capacity.");
        }

        var image = new byte[imageLength];
        var nativeOffset = (int)HeaderSize;
        var payloadIndexOffset = checked(nativeOffset + nativeLength);
        var payloadBytesOffset = checked(payloadIndexOffset + payloadIndexLength);
        var receiptOffset = checked(payloadBytesOffset + payloadBytesLength);
        WriteNativeSection(
            image.AsSpan(nativeOffset, nativeLength),
            source.NativeImage);
        WritePayloadSections(
            image,
            payloadIndexOffset,
            payloadBytesOffset,
            payloads);
        WriteReceiptSection(image.AsSpan(receiptOffset, receiptLength), receipts);

        var nativeDigest = SHA256.HashData(image.AsSpan(nativeOffset, nativeLength));
        var payloadDigest = SHA256.HashData(
            image.AsSpan(
                payloadIndexOffset,
                checked(payloadIndexLength + payloadBytesLength)));
        var receiptDigest =
            SHA256.HashData(image.AsSpan(receiptOffset, receiptLength));
        var header = image.AsSpan(0, checked((int)HeaderSize));
        WriteUInt64(header, 0, Magic);
        WriteUInt32(header, 8, EnvelopeVersion);
        WriteUInt32(header, 12, HeaderSize);
        WriteUInt32(header, 16, NativeOperationCoordinatorAbi.Version);
        WriteUInt32(header, 20, PayloadCatalogVersion);
        WriteUInt32(header, 24, ReceiptCatalogVersion);
        WriteUInt32(header, 28, 0);
        WriteUInt64(header, 32, source.ConfigurationGeneration);
        WriteUInt64(header, 40, source.PublicationRevision);
        WriteHandle(header, 48, source.SessionInstanceId);
        WriteHandle(header, 64, source.SourceClockInstanceId);
        WriteUInt64(header, 80, checked((ulong)nativeOffset));
        WriteUInt64(header, 88, checked((ulong)nativeLength));
        WriteUInt64(header, 96, checked((ulong)payloadIndexOffset));
        WriteUInt64(header, 104, checked((ulong)payloadIndexLength));
        WriteUInt64(header, 112, checked((ulong)payloadBytesOffset));
        WriteUInt64(header, 120, checked((ulong)payloadBytesLength));
        WriteUInt64(header, 128, checked((ulong)receiptOffset));
        WriteUInt64(header, 136, checked((ulong)receiptLength));
        WriteUInt32(
            header,
            144,
            checked((uint)source.NativeImage.Records.Length));
        WriteUInt32(header, 148, checked((uint)payloads.Length));
        WriteUInt32(header, 152, checked((uint)receipts.Length));
        WriteUInt32(header, 156, 0);
        nativeDigest.CopyTo(header[160..192]);
        payloadDigest.CopyTo(header[192..224]);
        receiptDigest.CopyTo(header[224..256]);
        source.PriorEnvelopeSha256.CopyTo(header[256..288]);
        SHA256.HashData(image.AsSpan(0, bodyLength))
            .CopyTo(image, bodyLength);
        return image;
    }

    internal static HostManagerOperationCanonicalEnvelope Decode(
        ReadOnlySpan<byte> image,
        CompiledHostManagerOperationCoordinatorCapacityPlan capacity,
        ulong maximumConfigurationGeneration)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        if (image.Length < HeaderSize + SHA256.HashSizeInBytes
            || (ulong)image.Length > capacity.MaximumEnvelopeByteCount)
        {
            throw new InvalidDataException(
                "The operation canonical envelope length is invalid.");
        }
        var body = image[..^SHA256.HashSizeInBytes];
        var storedFullDigest = image[^SHA256.HashSizeInBytes..];
        Span<byte> actualFullDigest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(body, actualFullDigest);
        if (!CryptographicOperations.FixedTimeEquals(
                storedFullDigest,
                actualFullDigest))
        {
            throw new InvalidDataException(
                "The operation canonical envelope digest is invalid.");
        }
        var header = body[..checked((int)HeaderSize)];
        if (ReadUInt64(header, 0) != Magic
            || ReadUInt32(header, 8) != EnvelopeVersion
            || ReadUInt32(header, 12) != HeaderSize
            || ReadUInt32(header, 16) != NativeOperationCoordinatorAbi.Version
            || ReadUInt32(header, 20) != PayloadCatalogVersion
            || ReadUInt32(header, 24) != ReceiptCatalogVersion
            || ReadUInt32(header, 28) != 0
            || ReadUInt32(header, 156) != 0
            || !AllZero(header[288..320]))
        {
            throw new InvalidDataException(
                "The operation canonical envelope header contract is invalid.");
        }

        var configurationGeneration = ReadUInt64(header, 32);
        var publicationRevision = ReadUInt64(header, 40);
        var sessionInstanceId = ReadHandle(header, 48);
        var sourceClockInstanceId = ReadHandle(header, 64);
        if (configurationGeneration == 0
            || configurationGeneration > maximumConfigurationGeneration
            || publicationRevision == 0
            || sessionInstanceId.IsZero
            || sourceClockInstanceId.IsZero)
        {
            throw new InvalidDataException(
                "The operation canonical envelope identity is invalid.");
        }

        var nativeOffset = ReadBoundedInt64(header, 80, image.Length);
        var nativeLength = ReadBoundedInt64(header, 88, image.Length);
        var payloadIndexOffset = ReadBoundedInt64(header, 96, image.Length);
        var payloadIndexLength = ReadBoundedInt64(header, 104, image.Length);
        var payloadBytesOffset = ReadBoundedInt64(header, 112, image.Length);
        var payloadBytesLength = ReadBoundedInt64(header, 120, image.Length);
        var receiptOffset = ReadBoundedInt64(header, 128, image.Length);
        var receiptLength = ReadBoundedInt64(header, 136, image.Length);
        var nativeRecordCount = ReadUInt32(header, 144);
        var payloadCount = ReadUInt32(header, 148);
        var receiptCount = ReadUInt32(header, 152);
        ValidateSections(
            body.Length,
            nativeOffset,
            nativeLength,
            payloadIndexOffset,
            payloadIndexLength,
            payloadBytesOffset,
            payloadBytesLength,
            receiptOffset,
            receiptLength,
            nativeRecordCount,
            payloadCount,
            receiptCount,
            capacity);
        VerifyDigest(header[160..192], body.Slice(nativeOffset, nativeLength), "native");
        VerifyDigest(
            header[192..224],
            body.Slice(
                payloadIndexOffset,
                checked(payloadIndexLength + payloadBytesLength)),
            "payload");
        VerifyDigest(
            header[224..256],
            body.Slice(receiptOffset, receiptLength),
            "receipt");

        var native = ReadNativeSection(
            body.Slice(nativeOffset, nativeLength),
            nativeRecordCount);
        if (native.Header.ConfigurationGeneration != configurationGeneration
            || native.Header.SessionInstanceId != sessionInstanceId
            || native.Header.ClockInstanceId != sourceClockInstanceId)
        {
            throw new InvalidDataException(
                "The native persistence image is not bound to its outer envelope.");
        }
        var payloads = ReadPayloadSections(
            body,
            payloadIndexOffset,
            payloadBytesOffset,
            payloadBytesLength,
            payloadCount,
            sessionInstanceId);
        var receipts = ReadReceiptSection(
            body.Slice(receiptOffset, receiptLength),
            receiptCount,
            sessionInstanceId);
        ValidateCatalogBindings(native.Records, payloads, receipts);
        return new HostManagerOperationCanonicalEnvelope(
            configurationGeneration,
            publicationRevision,
            sessionInstanceId,
            sourceClockInstanceId,
            native,
            payloads,
            receipts,
            header[256..288].ToArray(),
            storedFullDigest.ToArray());
    }

    internal static byte[] ZeroDigest() => new byte[SHA256.HashSizeInBytes];

    private static void WriteNativeSection(
        Span<byte> destination,
        NativeOperationCanonicalImage image)
    {
        WriteStruct(destination, image.Header);
        var offset = SizeOf<NativeOperationPersistenceHeader>();
        foreach (var record in image.Records)
        {
            WriteStruct(destination[offset..], record);
            offset += SizeOf<NativeOperationOutput>();
        }
    }

    private static NativeOperationCanonicalImage ReadNativeSection(
        ReadOnlySpan<byte> source,
        uint recordCount)
    {
        var header = ReadStruct<NativeOperationPersistenceHeader>(source);
        var records = new NativeOperationOutput[checked((int)recordCount)];
        var offset = SizeOf<NativeOperationPersistenceHeader>();
        for (var index = 0; index < records.Length; index++)
        {
            records[index] = ReadStruct<NativeOperationOutput>(source[offset..]);
            offset += SizeOf<NativeOperationOutput>();
        }
        return new NativeOperationCanonicalImage(header, records);
    }

    private static void WritePayloadSections(
        byte[] image,
        int descriptorOffset,
        int payloadBytesOffset,
        IReadOnlyList<HostManagerOperationPayloadEntry> payloads)
    {
        var payloadCursor = payloadBytesOffset;
        for (var index = 0; index < payloads.Count; index++)
        {
            var entry = payloads[index];
            var descriptor =
                image.AsSpan(descriptorOffset + index * PayloadDescriptorSize, PayloadDescriptorSize);
            WriteUInt32(descriptor, 0, entry.SchemaId);
            WriteUInt32(descriptor, 4, entry.SchemaVersion);
            WriteUInt32(descriptor, 8, (uint)entry.Role);
            WriteUInt32(descriptor, 12, 0);
            WriteHandle(descriptor, 16, entry.Handle);
            WriteHandle(descriptor, 32, entry.SessionInstanceId);
            WriteHandle(descriptor, 48, entry.OperationId);
            WriteHandle(descriptor, 64, entry.AttemptToken);
            WriteUInt64(descriptor, 80, checked((ulong)payloadCursor));
            WriteUInt64(descriptor, 88, checked((ulong)entry.Payload.Length));
            entry.PayloadSha256.CopyTo(descriptor[96..128]);
            entry.Payload.CopyTo(image, payloadCursor);
            payloadCursor += entry.Payload.Length;
        }
    }

    private static HostManagerOperationPayloadCatalog ReadPayloadSections(
        ReadOnlySpan<byte> body,
        int descriptorOffset,
        int payloadBytesOffset,
        int payloadBytesLength,
        uint payloadCount,
        NativeOperationHandle128 sessionInstanceId)
    {
        var result = new HostManagerOperationPayloadCatalog();
        var expectedPayloadOffset = payloadBytesOffset;
        for (var index = 0; index < payloadCount; index++)
        {
            var descriptor =
                body.Slice(
                    checked(descriptorOffset + index * PayloadDescriptorSize),
                    PayloadDescriptorSize);
            var schemaId = ReadUInt32(descriptor, 0);
            var schemaVersion = ReadUInt32(descriptor, 4);
            var role = (HostManagerOperationPayloadRole)ReadUInt32(descriptor, 8);
            var handle = ReadHandle(descriptor, 16);
            var payloadSession = ReadHandle(descriptor, 32);
            var operationId = ReadHandle(descriptor, 48);
            var attemptToken = ReadHandle(descriptor, 64);
            var payloadOffset = ReadBoundedInt64(descriptor, 80, body.Length);
            var payloadLength = ReadBoundedInt64(descriptor, 88, body.Length);
            if (ReadUInt32(descriptor, 12) != 0
                || payloadSession != sessionInstanceId
                || payloadOffset != expectedPayloadOffset
                || payloadOffset < payloadBytesOffset
                || checked(payloadOffset + payloadLength)
                    > checked(payloadBytesOffset + payloadBytesLength))
            {
                throw new InvalidDataException(
                    "An operation payload descriptor is non-canonical.");
            }
            var payload = body.Slice(payloadOffset, payloadLength);
            var entry = result.Add(
                schemaId,
                schemaVersion,
                role,
                payloadSession,
                operationId,
                attemptToken,
                payload);
            if (entry.Handle != handle
                || !CryptographicOperations.FixedTimeEquals(
                    descriptor[96..128],
                    entry.PayloadSha256))
            {
                throw new InvalidDataException(
                    "An operation payload identity digest is invalid.");
            }
            expectedPayloadOffset = checked(payloadOffset + payloadLength);
        }
        if (expectedPayloadOffset != checked(payloadBytesOffset + payloadBytesLength))
        {
            throw new InvalidDataException(
                "The operation payload byte section is not fully represented.");
        }
        return result;
    }

    private static void WriteReceiptSection(
        Span<byte> destination,
        IReadOnlyList<HostManagerOperationEffectReceipt> receipts)
    {
        for (var index = 0; index < receipts.Count; index++)
        {
            var receipt = receipts[index];
            var record = destination.Slice(index * ReceiptSize, ReceiptSize);
            WriteUInt32(record, 0, ReceiptSize);
            WriteUInt32(record, 4, 1);
            WriteUInt32(record, 8, (uint)receipt.State);
            WriteUInt32(record, 12, (uint)receipt.Outcome);
            WriteHandle(record, 16, receipt.ReceiptId);
            WriteHandle(record, 32, receipt.SessionInstanceId);
            WriteHandle(record, 48, receipt.OperationId);
            WriteHandle(record, 64, receipt.AttemptToken);
            WriteUInt64(record, 80, receipt.ActionId);
            WriteUInt64(record, 88, receipt.PlanEpoch);
            WriteUInt64(record, 96, receipt.ConfigurationGeneration);
            WriteUInt64(record, 104, receipt.ReceiptRevision);
            WriteUInt32(record, 112, (uint)receipt.ActionKind);
            WriteUInt32(record, 116, receipt.EffectKind);
            WriteUInt32(record, 120, receipt.EffectPhase);
            WriteUInt32(record, 124, 0);
            WriteInt64(record, 128, receipt.CreatedUtcMilliseconds);
            WriteInt64(record, 136, receipt.UpdatedUtcMilliseconds);
            WriteHandle(record, 144, receipt.ExpectedBeforeHandle);
            WriteHandle(record, 160, receipt.ExpectedAfterHandle);
            WriteHandle(record, 176, receipt.ObservationHandle);
            WriteHandle(record, 192, receipt.ErrorHandle);
        }
    }

    private static HostManagerOperationEffectReceiptTable ReadReceiptSection(
        ReadOnlySpan<byte> source,
        uint receiptCount,
        NativeOperationHandle128 sessionInstanceId)
    {
        var result = new HostManagerOperationEffectReceiptTable();
        for (var index = 0; index < receiptCount; index++)
        {
            var record = source.Slice(index * ReceiptSize, ReceiptSize);
            if (ReadUInt32(record, 0) != ReceiptSize
                || ReadUInt32(record, 4) != 1
                || ReadUInt32(record, 124) != 0)
            {
                throw new InvalidDataException(
                    "An operation effect receipt wire record is non-canonical.");
            }
            var receipt = new HostManagerOperationEffectReceipt(
                (HostManagerOperationEffectReceiptState)ReadUInt32(record, 8),
                (HostManagerOperationEffectReceiptOutcome)ReadUInt32(record, 12),
                ReadHandle(record, 16),
                ReadHandle(record, 32),
                ReadHandle(record, 48),
                ReadHandle(record, 64),
                ReadUInt64(record, 80),
                ReadUInt64(record, 88),
                ReadUInt64(record, 96),
                ReadUInt64(record, 104),
                (NativeOperationActionKind)ReadUInt32(record, 112),
                ReadUInt32(record, 116),
                ReadUInt32(record, 120),
                ReadInt64(record, 128),
                ReadInt64(record, 136),
                ReadHandle(record, 144),
                ReadHandle(record, 160),
                ReadHandle(record, 176),
                ReadHandle(record, 192));
            if (receipt.SessionInstanceId != sessionInstanceId)
            {
                throw new InvalidDataException(
                    "An operation effect receipt belongs to another session.");
            }
            result.Import(receipt);
        }
        return result;
    }

    private static void ValidateCatalogBindings(
        IReadOnlyList<NativeOperationOutput> records,
        HostManagerOperationPayloadCatalog payloads,
        HostManagerOperationEffectReceiptTable receipts)
    {
        var liveOperationIds = records
            .Select(static record => record.OperationId)
            .ToHashSet();
        var recordsById = records.ToDictionary(static record => record.OperationId);
        var roots = HostManagerOperationPayloadCatalog.CollectNativeRoots(records);
        var canonicalReceipts = receipts.ExportCanonical();
        if (canonicalReceipts
            .Any(receipt => !liveOperationIds.Contains(receipt.OperationId)))
        {
            throw new InvalidDataException(
                "An operation effect receipt references a pruned operation.");
        }
        foreach (var receipt in canonicalReceipts)
        {
            var record = recordsById[receipt.OperationId];
            var expectedBefore = payloads.Require(receipt.ExpectedBeforeHandle);
            if (expectedBefore.SchemaId
                    != HostManagerOperationRequestSchemas.EffectExpectedBefore
                || expectedBefore.SchemaVersion
                    != HostManagerOperationRequestSchemas.Version
                || expectedBefore.Role
                    != HostManagerOperationPayloadRole.EffectExpectedBefore
                || expectedBefore.SessionInstanceId != receipt.SessionInstanceId
                || expectedBefore.OperationId != receipt.OperationId
                || expectedBefore.AttemptToken != receipt.AttemptToken
                || HostManagerOperationRequestCodec.DecodeEffectExpectedBefore(
                    expectedBefore.Payload) != record.RequestHandle)
            {
                throw new InvalidDataException(
                    "An operation effect receipt has invalid request evidence.");
            }
            if (!receipt.ExpectedAfterHandle.IsZero)
            {
                var expectedAfter = payloads.Require(receipt.ExpectedAfterHandle);
                if (expectedAfter.Role
                        != HostManagerOperationPayloadRole.EffectExpectedAfter
                    || expectedAfter.SessionInstanceId != receipt.SessionInstanceId
                    || expectedAfter.OperationId != receipt.OperationId
                    || expectedAfter.AttemptToken != receipt.AttemptToken)
                {
                    throw new InvalidDataException(
                        "An operation effect receipt has invalid expected-after evidence.");
                }
            }
            if (!receipt.ObservationHandle.IsZero)
            {
                var observation = payloads.Require(receipt.ObservationHandle);
                if (observation.SchemaId
                        != HostManagerOperationRequestSchemas.EffectObservation
                    || observation.SchemaVersion
                        != HostManagerOperationRequestSchemas.Version
                    || observation.Role
                        != HostManagerOperationPayloadRole.EffectObservation
                    || observation.SessionInstanceId != receipt.SessionInstanceId
                    || observation.OperationId != receipt.OperationId
                    || observation.AttemptToken != receipt.AttemptToken)
                {
                    throw new InvalidDataException(
                        "An operation effect receipt has invalid observation evidence.");
                }
            }
            if (!receipt.ErrorHandle.IsZero)
            {
                var error = payloads.Require(receipt.ErrorHandle);
                if (error.SchemaId
                        != HostManagerOperationRequestSchemas.OperationError
                    || error.SchemaVersion
                        != HostManagerOperationRequestSchemas.Version
                    || error.Role != HostManagerOperationPayloadRole.Error
                    || error.SessionInstanceId != receipt.SessionInstanceId
                    || error.OperationId != receipt.OperationId
                    || error.AttemptToken != receipt.AttemptToken)
                {
                    throw new InvalidDataException(
                        "An operation effect receipt has invalid failure evidence.");
                }
            }
        }
        receipts.AddPayloadRoots(roots);
        foreach (var handle in roots)
        {
            _ = payloads.Require(handle);
        }
        if (payloads.Count != roots.Count)
        {
            throw new InvalidDataException(
                "The operation payload catalog contains unreferenced entries.");
        }
    }

    private static void ValidateIdentity(HostManagerOperationCanonicalEnvelope source)
    {
        if (source.ConfigurationGeneration == 0
            || source.PublicationRevision == 0
            || source.SessionInstanceId.IsZero
            || source.SourceClockInstanceId.IsZero
            || source.NativeImage.Header.ConfigurationGeneration
                != source.ConfigurationGeneration
            || source.NativeImage.Header.SessionInstanceId != source.SessionInstanceId
            || source.NativeImage.Header.ClockInstanceId != source.SourceClockInstanceId
            || source.PriorEnvelopeSha256.Length != SHA256.HashSizeInBytes
            || source.FullEnvelopeSha256.Length != SHA256.HashSizeInBytes)
        {
            throw new InvalidDataException(
                "The operation canonical envelope identity is invalid.");
        }
    }

    private static void ValidateSections(
        int bodyLength,
        int nativeOffset,
        int nativeLength,
        int payloadIndexOffset,
        int payloadIndexLength,
        int payloadBytesOffset,
        int payloadBytesLength,
        int receiptOffset,
        int receiptLength,
        uint nativeRecordCount,
        uint payloadCount,
        uint receiptCount,
        CompiledHostManagerOperationCoordinatorCapacityPlan capacity)
    {
        var expectedNativeLength = checked(
            SizeOf<NativeOperationPersistenceHeader>()
            + checked((int)nativeRecordCount) * SizeOf<NativeOperationOutput>());
        if (nativeRecordCount > capacity.MaximumOperationCount
            || payloadCount > capacity.MaximumPayloadCount
            || receiptCount > capacity.MaximumEffectReceiptCount
            || nativeOffset != HeaderSize
            || nativeLength != expectedNativeLength
            || (ulong)nativeLength > capacity.MaximumPersistenceByteCount
            || payloadIndexOffset != checked(nativeOffset + nativeLength)
            || payloadIndexLength != checked((int)payloadCount * PayloadDescriptorSize)
            || payloadBytesOffset != checked(payloadIndexOffset + payloadIndexLength)
            || (ulong)payloadBytesLength > capacity.MaximumPayloadByteCount
            || receiptOffset != checked(payloadBytesOffset + payloadBytesLength)
            || receiptLength != checked((int)receiptCount * ReceiptSize)
            || checked(receiptOffset + receiptLength) != bodyLength)
        {
            throw new InvalidDataException(
                "The operation canonical envelope section layout is invalid.");
        }
    }

    private static void VerifyDigest(
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> source,
        string name)
    {
        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(source, actual);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new InvalidDataException(
                $"The operation canonical envelope {name} section digest is invalid.");
        }
    }

    private static void WriteStruct<T>(Span<byte> destination, T value)
        where T : unmanaged
        => MemoryMarshal.Write(destination, in value);

    private static T ReadStruct<T>(ReadOnlySpan<byte> source)
        where T : unmanaged
        => MemoryMarshal.Read<T>(source);

    private static int SizeOf<T>() where T : unmanaged
        => Unsafe.SizeOf<T>();

    private static void WriteUInt32(Span<byte> target, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(target[offset..], value);

    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);

    private static void WriteUInt64(Span<byte> target, int offset, ulong value)
        => BinaryPrimitives.WriteUInt64LittleEndian(target[offset..], value);

    private static ulong ReadUInt64(ReadOnlySpan<byte> source, int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);

    private static void WriteInt64(Span<byte> target, int offset, long value)
        => BinaryPrimitives.WriteInt64LittleEndian(target[offset..], value);

    private static long ReadInt64(ReadOnlySpan<byte> source, int offset)
        => BinaryPrimitives.ReadInt64LittleEndian(source[offset..]);

    private static void WriteHandle(
        Span<byte> target,
        int offset,
        NativeOperationHandle128 handle)
        => HostManagerOperationPayloadCatalog.WriteHandle(target[offset..], handle);

    private static NativeOperationHandle128 ReadHandle(
        ReadOnlySpan<byte> source,
        int offset)
        => new(
            ReadUInt64(source, offset),
            ReadUInt64(source, offset + 8));

    private static int ReadBoundedInt64(
        ReadOnlySpan<byte> source,
        int offset,
        int upperBound)
    {
        var value = ReadUInt64(source, offset);
        if (value > checked((ulong)upperBound))
        {
            throw new InvalidDataException(
                "An operation canonical envelope bound exceeds the image.");
        }
        return checked((int)value);
    }

    private static bool AllZero(ReadOnlySpan<byte> source)
    {
        foreach (var value in source)
        {
            if (value != 0)
            {
                return false;
            }
        }
        return true;
    }
}
