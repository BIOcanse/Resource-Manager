namespace ResourceManager.ReadWrite.Windows.Devices.Usb;

public static class UsbEndpointTiming
{
    public static double? CalculateServiceIntervalMicroseconds(byte? negotiatedSpeedCode, byte transferType, byte interval)
    {
        if (interval == 0 || transferType is not (1 or 3)) return null;
        return negotiatedSpeedCode is >= 2 ? interval <= 16 ? 125d * Math.Pow(2, interval - 1) : null : interval * 1_000d;
    }
}
