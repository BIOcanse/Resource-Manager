namespace ResourceManager.ReadWrite.Windows.Devices.Usb;

internal static class UsbLinkSpeedRateDecoder
{
    private const uint Connected = 1;
    private const uint ExpectedLaneInfoLength = 24;

    public static double? NegotiatedMegabitsPerSecond(
        uint connectionStatus,
        byte legacySpeed,
        DeviceTopologyUsbPortCapability? capability,
        UsbSuperSpeedPlusLaneInformation? superSpeedPlus,
        uint expectedPortNumber)
    {
        if (connectionStatus != Connected || expectedPortNumber == 0)
        {
            return null;
        }

        if (capability is null)
        {
            return legacySpeed switch
            {
                0 => 1.5d,
                1 => 12d,
                // EX alone may classify a SuperSpeed connection as HighSpeed.
                2 => null,
                _ => null
            };
        }

        if (capability.OperatingAtSuperSpeedPlusOrHigher)
        {
            if (!capability.OperatingAtSuperSpeedOrHigher
                || legacySpeed is not (2 or 3)
                || superSpeedPlus is not { } laneInfo
                || laneInfo.ConnectionIndex != expectedPortNumber)
            {
                return null;
            }

            return DecodeSymmetricScalarRate(laneInfo);
        }

        if (capability.OperatingAtSuperSpeedOrHigher)
        {
            return legacySpeed is 2 or 3 ? 5000d : null;
        }

        return legacySpeed switch
        {
            0 => 1.5d,
            1 => 12d,
            2 => 480d,
            // EX_V2 explicitly contradicts the legacy SuperSpeed classification.
            3 => null,
            _ => null
        };
    }

    /// <summary>The catalog has one scalar; asymmetric direction totals stay unavailable.</summary>
    public static double? DecodeSymmetricScalarRate(UsbSuperSpeedPlusLaneInformation laneInfo)
    {
        if (laneInfo.ConnectionIndex == 0 || laneInfo.Length != ExpectedLaneInfoLength)
        {
            return null;
        }

        if (!TryDecodeDirection(laneInfo.RxRateAttribute, expectedTransmit: false,
                laneInfo.RxLaneCountZeroBased, out var rx, out var rxMode, out var rxProtocol)
            || !TryDecodeDirection(laneInfo.TxRateAttribute, expectedTransmit: true,
                laneInfo.TxLaneCountZeroBased, out var tx, out var txMode, out var txProtocol))
        {
            return null;
        }

        if (rxMode != 0 || txMode != 0 || rxMode != txMode
            || rxProtocol != txProtocol || rx != tx)
        {
            return null;
        }

        return (double)rx;
    }

    private static bool TryDecodeDirection(
        uint attribute,
        bool expectedTransmit,
        uint laneCountZeroBased,
        out decimal aggregateMegabitsPerSecond,
        out uint mode,
        out uint protocol)
    {
        aggregateMegabitsPerSecond = 0;
        mode = (attribute >> 6) & 0x1;
        var transmit = ((attribute >> 7) & 0x1) != 0;
        var reserved = (attribute >> 8) & 0x3F;
        protocol = (attribute >> 14) & 0x3;
        var exponent = (attribute >> 4) & 0x3;
        var mantissa = (attribute >> 16) & 0xFFFF;

        if (transmit != expectedTransmit || reserved != 0 || protocol > 1
            || laneCountZeroBased > 1 || mantissa == 0)
        {
            return false;
        }

        var megabitsPerSecondPerLane = exponent switch
        {
            0 => mantissa / 1_000_000m,
            1 => mantissa / 1_000m,
            2 => (decimal)mantissa,
            3 => mantissa * 1_000m,
            _ => 0m
        };

        aggregateMegabitsPerSecond = megabitsPerSecondPerLane * (laneCountZeroBased + 1);
        return aggregateMegabitsPerSecond > 0;
    }
}
