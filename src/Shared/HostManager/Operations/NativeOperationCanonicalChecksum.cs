using System.Numerics;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

// The v1 and v2 native layouts/checksum algorithm are identical; only the ABI identity and fact handles differ.
internal static class NativeOperationCanonicalChecksum
{
    internal static void Validate(NativeOperationCanonicalImage image)
    {
        var expected = Compute(image.Header, image.Records);
        if (expected.High != image.Header.ChecksumHigh || expected.Low != image.Header.ChecksumLow)
            throw new InvalidDataException("The legacy operation native checksum is invalid.");
    }

    internal static NativeOperationCanonicalImage Rewrite(
        NativeOperationPersistenceHeader header, NativeOperationOutput[] records, uint abiVersion)
    {
        header.AbiVersion = abiVersion;
        var checksum = Compute(header, records);
        header.ChecksumHigh = checksum.High;
        header.ChecksumLow = checksum.Low;
        return new(header, records);
    }

    private static NativeOperationHandle128 Compute(NativeOperationPersistenceHeader header, NativeOperationOutput[] records)
    {
        header.ChecksumHigh = 0;
        header.ChecksumLow = 0;
        ulong high = 0xcbf29ce484222325, low = 0x84222325cbf29ce4;
        Hash(MemoryMarshal.AsBytes(new[] { header }.AsSpan()), ref high, ref low);
        Hash(MemoryMarshal.AsBytes(records.AsSpan()), ref high, ref low);
        if (high == 0 && low == 0) low = 1;
        return new(high, low);
    }

    private static void Hash(ReadOnlySpan<byte> bytes, ref ulong high, ref ulong low)
    {
        foreach (var value in bytes)
        {
            high = unchecked((high ^ value) * 0x100000001b3);
            low = BitOperations.RotateLeft(unchecked((low ^ (ulong)(value + 0x9e)) * 0x100000001b3), 7);
        }
    }
}
