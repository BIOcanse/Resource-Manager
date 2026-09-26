using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

internal static class HostManagerProcessPolicyRollbackPayloadCodec
{
    internal const ushort Version = 4;
    internal const ushort PreviousVersion = 3;
    internal const ushort LegacyVersion = 2;
    internal const int PayloadSize = 128;
    internal const int DigestOffset = 96;

    private const uint Magic = 0x3454_5048;
    private const uint PreviousMagic = 0x3354_5048;
    private const uint LegacyMagic = 0x3254_5048;
    private const int FirstReservedOffset = 20;
    private const int TargetMemoryPriorityOffset = 48;
    private const int SecondReservedOffset = 52;

    internal static byte[] Encode(HostManagerProcessPolicyRollbackPayload payload)
    {
        if (!IsCanonical(payload))
        {
            throw new ArgumentException("Process policy rollback payload is not canonical.", nameof(payload));
        }

        var bytes = new byte[PayloadSize];
        var destination = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..8], PayloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], PayloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[12..16],
            (uint)payload.Fields);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..20], payload.ProcessId);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..32], payload.ProcessStartKey);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[32..36], payload.BaselinePriorityClass);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[36..40], payload.BaselineMemoryPriority);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[40..44], payload.BaselinePowerControlMask);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[44..48], payload.BaselinePowerStateMask);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[TargetMemoryPriorityOffset..SecondReservedOffset],
            payload.TargetMemoryPriority);
        SHA256.HashData(destination[..DigestOffset], destination[DigestOffset..]);
        return bytes;
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out HostManagerProcessPolicyRollbackPayload payload)
    {
        payload = default;
        if (bytes.Length != PayloadSize)
        {
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0..4]);
        var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..6]);
        var fields = (HostManagerProcessPolicyTransactionFields)
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);
        var currentEnvelope = magic == Magic && version == Version;
        var previousEnvelope = magic == PreviousMagic && version == PreviousVersion;
        var legacyEnvelope = magic == LegacyMagic
            && version == LegacyVersion
            && fields == HostManagerProcessPolicyTransactionFields.All;
        if ((!currentEnvelope && !previousEnvelope && !legacyEnvelope)
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]) != PayloadSize
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..12]) != PayloadSize
            || !bytes[FirstReservedOffset..24].IsEmptyOrAllZero()
            || currentEnvelope && !bytes[SecondReservedOffset..DigestOffset].IsEmptyOrAllZero()
            || !currentEnvelope
                && !bytes[TargetMemoryPriorityOffset..DigestOffset].IsEmptyOrAllZero())
        {
            return false;
        }

        Span<byte> expectedDigest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes[..DigestOffset], expectedDigest);
        if (!CryptographicOperations.FixedTimeEquals(expectedDigest, bytes[DigestOffset..]))
        {
            return false;
        }

        payload = new HostManagerProcessPolicyRollbackPayload(
            fields,
            BinaryPrimitives.ReadInt32LittleEndian(bytes[16..20]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[24..32]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..36]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..40]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..44]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[44..48]),
            currentEnvelope
                ? BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes[TargetMemoryPriorityOffset..SecondReservedOffset])
                : 0);
        if (IsCanonical(payload, requireSealedMemoryTarget: currentEnvelope))
        {
            return true;
        }

        payload = default;
        return false;
    }

    private static bool IsCanonical(
        HostManagerProcessPolicyRollbackPayload payload,
        bool requireSealedMemoryTarget = true)
    {
        if (payload.Fields == HostManagerProcessPolicyTransactionFields.None
            || (payload.Fields & ~HostManagerProcessPolicyTransactionFields.All) != 0
            || payload.ProcessId <= 0
            || payload.ProcessStartKey <= 0
            || !IsCanonicalPriority(payload)
            || !IsCanonicalMemoryPriority(payload)
            || !IsCanonicalPowerThrottling(payload)
            || !IsCanonicalMemoryTarget(payload, requireSealedMemoryTarget))
        {
            return false;
        }

        try
        {
            _ = DateTimeOffset.FromFileTime(payload.ProcessStartKey);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool IsKnownPriority(uint value) =>
        value <= int.MaxValue && Enum.IsDefined((ProcessPriorityClass)checked((int)value));

    private static bool IsCanonicalPriority(HostManagerProcessPolicyRollbackPayload payload)
        => payload.Fields.HasFlag(HostManagerProcessPolicyTransactionFields.PriorityClass)
            ? IsKnownPriority(payload.BaselinePriorityClass)
            : payload.BaselinePriorityClass == 0;

    private static bool IsCanonicalMemoryPriority(HostManagerProcessPolicyRollbackPayload payload)
        => payload.Fields.HasFlag(HostManagerProcessPolicyTransactionFields.MemoryPriority)
            ? payload.BaselineMemoryPriority is >= NativeMethods.MemoryPriorityVeryLow
                and <= NativeMethods.MemoryPriorityNormal
            : payload.BaselineMemoryPriority == 0;

    private static bool IsCanonicalPowerThrottling(HostManagerProcessPolicyRollbackPayload payload)
        => payload.Fields.HasFlag(HostManagerProcessPolicyTransactionFields.PowerThrottling)
            || payload.BaselinePowerControlMask == 0 && payload.BaselinePowerStateMask == 0;

    private static bool IsCanonicalMemoryTarget(
        HostManagerProcessPolicyRollbackPayload payload,
        bool requireSealedMemoryTarget)
        => payload.Fields == HostManagerProcessPolicyTransactionFields.MemoryPriority
            ? requireSealedMemoryTarget
                ? payload.TargetMemoryPriority is >= NativeMethods.MemoryPriorityVeryLow
                    and <= NativeMethods.MemoryPriorityNormal
                : payload.TargetMemoryPriority == 0
            : payload.TargetMemoryPriority == 0;

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
