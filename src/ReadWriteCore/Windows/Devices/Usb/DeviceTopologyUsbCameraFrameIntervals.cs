namespace ResourceManager.ReadWrite.Windows.Devices.Usb;

/// <summary>Device-declared intervals for one current configuration/interface/format/frame.
/// Indices belong to that descriptor directory; they are not permanent device identities.</summary>
public sealed record DeviceTopologyUsbCameraFrameIntervals(
    byte ConfigurationValue,
    byte InterfaceNumber,
    byte AlternateSetting,
    byte FormatIndex,
    byte FrameIndex,
    uint DefaultFrameInterval100Nanoseconds,
    IReadOnlyList<uint>? DiscreteIntervals100Nanoseconds,
    DeviceTopologyUsbCameraContinuousFrameInterval? ContinuousInterval)
{
    internal bool IsComplete()
    {
        if (ConfigurationValue == 0 || FormatIndex == 0 || FrameIndex == 0
            || DefaultFrameInterval100Nanoseconds == 0)
            return false;
        if (DiscreteIntervals100Nanoseconds is { } discrete)
        {
            // bLength is one byte and the common frame descriptor is 26 bytes.
            if (ContinuousInterval is not null || discrete.Count == 0
                || discrete.Count > (byte.MaxValue - 26) / sizeof(uint))
                return false;
            var hasDefault = false;
            foreach (var interval in discrete)
            {
                if (interval == 0) return false;
                hasDefault |= interval == DefaultFrameInterval100Nanoseconds;
            }
            return hasDefault;
        }
        if (ContinuousInterval is not { } range || range.Minimum100Nanoseconds == 0
            || range.Maximum100Nanoseconds < range.Minimum100Nanoseconds
            || DefaultFrameInterval100Nanoseconds < range.Minimum100Nanoseconds
            || DefaultFrameInterval100Nanoseconds > range.Maximum100Nanoseconds)
            return false;
        // Keep the device's step and endpoints verbatim. The payload table does not
        // prescribe a default/max modulo test or a special interpretation of step zero.
        return true;
    }
}

public sealed record DeviceTopologyUsbCameraContinuousFrameInterval(
    uint Minimum100Nanoseconds,
    uint Maximum100Nanoseconds,
    uint Step100Nanoseconds);
