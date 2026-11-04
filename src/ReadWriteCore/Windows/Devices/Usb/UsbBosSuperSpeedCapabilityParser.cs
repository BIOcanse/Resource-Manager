using System.Buffers.Binary;
using System.Collections.Immutable;

namespace ResourceManager.ReadWrite.Windows.Devices.Usb;

/// <summary>
/// Draft BOS parser. It emits a number only for an attached device whose valid BOS
/// declares ordinary SuperSpeed and has no SuperSpeedPlus capability descriptor.
/// All other cases remain unavailable until a complete aggregate-lane contract exists.
/// </summary>
internal static class UsbBosSuperSpeedCapabilityParser
{
    private const byte BosDescriptorType = 0x0F;
    private const byte DeviceCapabilityDescriptorType = 0x10;
    private const byte SuperSpeedCapabilityType = 0x03;
    private const byte SuperSpeedPlusCapabilityType = 0x0A;
    private const byte Usb20ExtensionCapabilityType = 0x02;
    private const byte ContainerIdCapabilityType = 0x04;
    private const int BosHeaderLength = 5;
    private const int SuperSpeedCapabilityLength = 10;
    private const int Usb20ExtensionCapabilityLength = 7;
    private const int ContainerIdCapabilityLength = 20;
    private const ushort SuperSpeedRateBit = 0x0008;
    private const ushort ReservedSpeedBitsMask = 0xFFF0;
    private const byte ReservedSuperSpeedAttributesMask = 0xFD;
    private const byte ReservedFunctionalityBitsMask = 0xFC;
    private const uint ReservedUsb20ExtensionAttributesMask = 0xFFFF00E1;
    private const byte MaximumU1ExitLatency = 0x0A;
    private const ushort MaximumU2ExitLatency = 0x07FF;

    public static double? DeclaredMaximumMegabitsPerSecond(ReadOnlySpan<byte> bos)
    {
        if (bos.Length < BosHeaderLength
            || bos[0] != BosHeaderLength
            || bos[1] != BosDescriptorType)
        {
            return null;
        }

        var totalLength = BinaryPrimitives.ReadUInt16LittleEndian(bos.Slice(2, sizeof(ushort)));
        var capabilityCount = bos[4];
        if (totalLength != bos.Length || totalLength < BosHeaderLength)
        {
            return null;
        }

        var offset = BosHeaderLength;
        var superspeedCount = 0;
        var superSpeedPlusFound = false;
        ushort supportedSpeeds = 0;

        for (var index = 0; index < capabilityCount; index++)
        {
            if (offset > totalLength - 3)
            {
                return null;
            }

            var descriptorLength = bos[offset];
            var descriptorType = bos[offset + 1];
            var capabilityType = bos[offset + 2];
            if (descriptorLength < 3 || descriptorLength > totalLength - offset
                || descriptorType != DeviceCapabilityDescriptorType)
            {
                return null;
            }

            if (descriptorType == DeviceCapabilityDescriptorType
                && capabilityType == SuperSpeedCapabilityType)
            {
                if (++superspeedCount != 1 || descriptorLength != SuperSpeedCapabilityLength)
                {
                    return null;
                }

                if ((bos[offset + 3] & ReservedSuperSpeedAttributesMask) != 0)
                {
                    return null;
                }

                supportedSpeeds = BinaryPrimitives.ReadUInt16LittleEndian(
                    bos.Slice(offset + 4, sizeof(ushort)));
                var functionalitySupport = bos[offset + 6];
                var u1ExitLatency = bos[offset + 7];
                var u2ExitLatency = BinaryPrimitives.ReadUInt16LittleEndian(
                    bos.Slice(offset + 8, sizeof(ushort)));
                if ((supportedSpeeds & ReservedSpeedBitsMask) != 0
                    || (functionalitySupport & ReservedFunctionalityBitsMask) != 0
                    || functionalitySupport > 3
                    || (supportedSpeeds & (1 << functionalitySupport)) == 0
                    || u1ExitLatency > MaximumU1ExitLatency
                    || u2ExitLatency > MaximumU2ExitLatency)
                {
                    return null;
                }
            }
            else if (descriptorType == DeviceCapabilityDescriptorType
                && capabilityType == SuperSpeedPlusCapabilityType)
            {
                // Presence means ordinary SuperSpeed is not the maximum. This draft
                // intentionally does not collapse per-lane attributes into a total.
                if (!IsValidSuperSpeedPlusCapability(bos.Slice(offset, descriptorLength)))
                {
                    return null;
                }

                superSpeedPlusFound = true;
            }
            else if (descriptorType == DeviceCapabilityDescriptorType
                && capabilityType == Usb20ExtensionCapabilityType)
            {
                if (descriptorLength != Usb20ExtensionCapabilityLength
                    || (BinaryPrimitives.ReadUInt32LittleEndian(
                        bos.Slice(offset + 3, sizeof(uint))) & ReservedUsb20ExtensionAttributesMask) != 0)
                {
                    return null;
                }
            }
            else if (descriptorType == DeviceCapabilityDescriptorType
                && capabilityType == ContainerIdCapabilityType)
            {
                if (descriptorLength != ContainerIdCapabilityLength || bos[offset + 3] != 0)
                {
                    return null;
                }
            }
            else
            {
                // A capability outside the layouts covered here could change the
                // declared speed interpretation. Keep the scalar unavailable.
                return null;
            }

            offset += descriptorLength;
        }

        if (offset != totalLength || superspeedCount != 1 || superSpeedPlusFound
            || (supportedSpeeds & SuperSpeedRateBit) == 0)
        {
            return null;
        }

        return 5000d;
    }

    internal static bool IsValidSuperSpeedPlusCapability(ReadOnlySpan<byte> descriptor)
    {
        const int fixedHeaderLength = 12;
        const uint ReservedAttributeBitsMask = 0xFFFFFE00;
        const ushort ReservedFunctionalityMask = 0x00F0;

        if (descriptor.Length < 16 || descriptor[3] != 0)
        {
            return false;
        }

        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(4, sizeof(uint)));
        // SSAC is zero-based: an encoded value of zero describes one 4-byte
        // speed attribute, so the complete descriptor length is 12 + (SSAC + 1) * 4.
        var encodedSpeedAttributeCount = (int)(attributes & 0x1F);
        var speedAttributeCount = encodedSpeedAttributeCount + 1;
        if ((attributes & ReservedAttributeBitsMask) != 0
            || descriptor.Length != fixedHeaderLength + (speedAttributeCount * sizeof(uint)))
        {
            return false;
        }

        var functionality = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(8, sizeof(ushort)));
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(10, sizeof(ushort)));
        return (functionality & ReservedFunctionalityMask) == 0 && reserved == 0;
    }

    /// <summary>
    /// Parses one structurally bounded SSP declaration from a complete BOS. Raw
    /// words are retained, including reserved/unknown bits; this is not scalar
    /// aggregate-rate validation.
    /// </summary>
    internal static DeviceTopologyUsbSuperSpeedPlusCapability? ParseSuperSpeedPlusCapability(
        ReadOnlySpan<byte> bos)
    {
        if (bos.Length < BosHeaderLength
            || bos[0] != BosHeaderLength
            || bos[1] != BosDescriptorType)
        {
            return null;
        }

        var totalLength = BinaryPrimitives.ReadUInt16LittleEndian(bos.Slice(2, sizeof(ushort)));
        var capabilityCount = bos[4];
        if (totalLength != bos.Length || totalLength < BosHeaderLength)
        {
            return null;
        }

        var offset = BosHeaderLength;
        DeviceTopologyUsbSuperSpeedPlusCapability? parsed = null;
        for (var index = 0; index < capabilityCount; index++)
        {
            if (offset > totalLength - 3)
            {
                return null;
            }

            var descriptorLength = bos[offset];
            if (descriptorLength < 3 || descriptorLength > totalLength - offset
                || bos[offset + 1] != DeviceCapabilityDescriptorType)
            {
                return null;
            }

            if (bos[offset + 2] == SuperSpeedPlusCapabilityType)
            {
                if (parsed is not null)
                {
                    return null;
                }

                parsed = ParseSuperSpeedPlusDescriptor(bos.Slice(offset, descriptorLength));
                if (parsed is null)
                {
                    return null;
                }
            }

            offset += descriptorLength;
        }

        return offset == totalLength ? parsed : null;
    }

    private static DeviceTopologyUsbSuperSpeedPlusCapability? ParseSuperSpeedPlusDescriptor(
        ReadOnlySpan<byte> descriptor)
    {
        const int fixedHeaderLength = 12;
        if (descriptor.Length < fixedHeaderLength + sizeof(uint)
            || descriptor[0] != descriptor.Length
            || descriptor[1] != DeviceCapabilityDescriptorType
            || descriptor[2] != SuperSpeedPlusCapabilityType)
        {
            return null;
        }

        var rawAttributes = BinaryPrimitives.ReadUInt32LittleEndian(
            descriptor.Slice(4, sizeof(uint)));
        var attributeCount = (int)(rawAttributes & 0x1F) + 1;
        if (descriptor.Length != fixedHeaderLength + (attributeCount * sizeof(uint)))
        {
            return null;
        }

        var attributes = ImmutableArray.CreateBuilder<DeviceTopologyUsbSublinkSpeedAttribute>(attributeCount);
        for (var index = 0; index < attributeCount; index++)
        {
            var attributeOffset = fixedHeaderLength + (index * sizeof(uint));
            var rawAttribute = BinaryPrimitives.ReadUInt32LittleEndian(
                descriptor.Slice(attributeOffset, sizeof(uint)));
            attributes.Add(new DeviceTopologyUsbSublinkSpeedAttribute(rawAttribute));
        }

        return new DeviceTopologyUsbSuperSpeedPlusCapability(
            descriptor[0],
            descriptor[1],
            descriptor[2],
            descriptor[3],
            rawAttributes,
            BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(8, sizeof(ushort))),
            BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(10, sizeof(ushort))),
            attributes.MoveToImmutable());
    }
}
