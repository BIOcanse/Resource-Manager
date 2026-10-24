using System.Buffers.Binary;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Operations;

internal static class HostManagerOperationEffectEvidenceCodec
{
    internal static byte[] EncodeEffectExpectedBefore(
        NativeOperationHandle128 requestHandle)
    {
        if (requestHandle.IsZero)
        {
            throw new ArgumentException(
                "The operation request evidence handle is zero.",
                nameof(requestHandle));
        }
        var payload = new byte[16];
        HostManagerOperationPayloadCatalog.WriteHandle(payload, requestHandle);
        return payload;
    }

    internal static NativeOperationHandle128 DecodeEffectExpectedBefore(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 16)
        {
            throw new InvalidDataException(
                "The operation request evidence payload length is invalid.");
        }
        var result = new NativeOperationHandle128(
            BinaryPrimitives.ReadUInt64LittleEndian(payload),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]));
        return result.IsZero
            ? throw new InvalidDataException(
                "The operation request evidence handle is zero.")
            : result;
    }

}
