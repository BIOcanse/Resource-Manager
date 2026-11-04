using System.Buffers.Binary;

namespace ResourceManager.ReadWrite.Windows.Devices;

public static partial class WindowsStorageIdentifyReader
{
    /// <summary>Reads the Windows storage-stack TrimEnabled report, not a protocol/SSD guess or TRIM command.
    /// The expected serial/unique identity and optional bus are checked on the same opened property-query object.</summary>
    public static bool? ReadTrim(int number, string serial, string? uniqueId, int? uniqueIdFormat, uint? expectedBus = null)
        => OperatingSystem.IsWindows() ? ReadTrim(number, serial, uniqueId, uniqueIdFormat, expectedBus, Open) : null;
    internal static bool? ReadTrim(int number, string serial, string? uniqueId, int? uniqueIdFormat,
        uint? expectedBus, Func<int, IWindowsStorageIdentifyQuery?> open)
    {
        if (number < 0 || string.IsNullOrWhiteSpace(serial)) return null;
        try
        {
            using var device = open(number);
            if (device is null) return null;
            var descriptor = device.Query(WindowsStoragePropertyQuery.Create(0), 4096);
            if (descriptor is null || !EqualSerial(WindowsAtaSmartReader.ParseDeviceSerial(descriptor, allowOtherBus: true), serial)
                || (expectedBus is { } bus && BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(28)) != bus)) return null;
            if (!string.IsNullOrWhiteSpace(uniqueId))
            {
                if (uniqueIdFormat is not (0 or 1 or 2 or 3 or 8)) return null;
                var ids = device.Query(WindowsStoragePropertyQuery.Create(2), 4096);
                if (ids is null || !WindowsAtaSmartReader.MatchesUniqueId(ids, uniqueId, uniqueIdFormat.Value)) return null;
            }
            return WindowsStorageTrimQuery.Read(device.Query);
        }
        catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { return null; }
    }
}
