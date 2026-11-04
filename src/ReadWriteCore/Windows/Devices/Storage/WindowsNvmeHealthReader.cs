using ResourceManager.ReadWrite.Windows.Devices.Models;
using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Windows.Devices;

// QueryProperty only: no pass-through, self-test, reset, threshold or firmware commands.
public static partial class WindowsNvmeHealthReader
{
    internal const uint QueryPropertyIoctl = 0x002D1400;
    internal const int ProtocolHeaderSize = 48;
    internal const int HealthLogSize = 512;

    public static NvmeHealthRead? Read(int diskNumber, string expectedSerial, string? expectedUniqueId, int? uniqueIdFormat)
        => OperatingSystem.IsWindows() ? Read(diskNumber, expectedSerial, expectedUniqueId, uniqueIdFormat, Open) : null;

    internal static NvmeHealthRead? Read(int diskNumber, string expectedSerial, string? expectedUniqueId, int? uniqueIdFormat,
        Func<int, IWindowsNvmeHealthQuery?> open)
    {
        if (diskNumber < 0 || string.IsNullOrWhiteSpace(expectedSerial)) return null;
        using var device = open(diskNumber);
        if (device is null) return null;
        // Verify the opened object, not just the earlier WMI row or a display ordinal.
        var descriptor = device.Query(WindowsStoragePropertyQuery.Create(0), 4096);
        var serial = descriptor is null ? null : ParseDeviceSerial(descriptor);
        if (!string.Equals(serial, expectedSerial.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrWhiteSpace(expectedUniqueId))
        {
            if (uniqueIdFormat is not (0 or 1 or 2 or 3 or 8)) return null;
            var identifiers = device.Query(WindowsStoragePropertyQuery.Create(2), 4096);
            if (identifiers is null || !WindowsAtaSmartReader.MatchesUniqueId(identifiers, expectedUniqueId, uniqueIdFormat.Value)) return null;
        }
        var response = device.Query(CreateHealthQuery(), ProtocolHeaderSize + HealthLogSize);
        return response is null ? null : ParseHealthResponse(response);
    }

    internal static byte[] CreateHealthQuery()
    {
        var query = new byte[ProtocolHeaderSize + HealthLogSize];
        Write(query, 0, 49); // StorageAdapterProtocolSpecificProperty: controller/global health.
        Write(query, 4, 0);  // PropertyStandardQuery
        Write(query, 8, 3);  // ProtocolTypeNvme
        Write(query, 12, 2); // NVMeDataTypeLogPage
        Write(query, 16, 2); // NVME_LOG_PAGE_HEALTH_INFO
        Write(query, 24, 40); // Offset from ProtocolSpecificData, not from the whole buffer.
        Write(query, 28, HealthLogSize);
        return query;
    }

    public static NvmeHealthRead? ParseHealthResponse(ReadOnlySpan<byte> response)
    {
        if (response.Length < ProtocolHeaderSize || Read(response, 0) != ProtocolHeaderSize
            || Read(response, 4) != ProtocolHeaderSize || Read(response, 8) != 3
            || Read(response, 12) != 2 || Read(response, 16) != 2) return null;
        var offset = Read(response, 24);
        var length = Read(response, 28);
        if (offset < 40 || length < HealthLogSize || (ulong)8 + offset + length > (ulong)response.Length) return null;
        return ParseHealthLog(response.Slice(checked((int)(8 + offset)), HealthLogSize));
    }

    public static NvmeHealthRead? ParseHealthLog(ReadOnlySpan<byte> log)
    {
        if (log.Length != HealthLogSize) return null;
        var integers = new Dictionary<MetricFieldCode, ulong>
        {
            [MetricFieldCode.NvmeCriticalWarning] = log[0],
            [MetricFieldCode.NvmePercentageUsed] = log[5],
            [MetricFieldCode.NvmeWarningTemperatureMinutes] = Read(log, 192),
            [MetricFieldCode.NvmeCriticalTemperatureMinutes] = Read(log, 196)
        };
        if (log[3] <= 100) integers[MetricFieldCode.NvmeAvailableSpare] = log[3];
        if (log[4] <= 100) integers[MetricFieldCode.NvmeSpareThreshold] = log[4];
        var values = new Dictionary<MetricFieldCode, double>();
        AddTemperature(values, MetricFieldCode.NvmeCompositeTemperature, log, 1);
        for (var i = 0; i < SensorCodes.Length; i++) AddTemperature(values, SensorCodes[i], log, 200 + i * 2);
        var wide = new Dictionary<MetricFieldCode, UInt128>();
        AddCounter(wide, MetricFieldCode.NvmeDataUnitsRead, log, 32, true);
        AddCounter(wide, MetricFieldCode.NvmeDataUnitsWritten, log, 48, true);
        AddCounter(wide, MetricFieldCode.NvmeHostReadCommands, log, 64, false);
        AddCounter(wide, MetricFieldCode.NvmeHostWriteCommands, log, 80, false);
        AddCounter(wide, MetricFieldCode.NvmeControllerBusyMinutes, log, 96, false);
        AddCounter(wide, MetricFieldCode.NvmePowerCycles, log, 112, false);
        AddCounter(wide, MetricFieldCode.NvmePowerOnHours, log, 128, false);
        AddCounter(wide, MetricFieldCode.NvmeUnsafeShutdowns, log, 144, false);
        AddCounter(wide, MetricFieldCode.NvmeMediaErrors, log, 160, false);
        AddCounter(wide, MetricFieldCode.NvmeErrorLogEntries, log, 176, false);
        return new NvmeHealthRead(values, integers)
        {
            WideIntegerValues = wide,
            BytesValues = new Dictionary<MetricFieldCode, byte[]> { [MetricFieldCode.NvmeHealthLogRaw] = log[..HealthLogSize].ToArray() }
        };
    }

    internal static string? ParseDeviceSerial(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < 36) return null;
        var size = Read(descriptor, 4);
        var offset = Read(descriptor, 24);
        if (size < 36 || size > descriptor.Length || Read(descriptor, 28) != 17
            || offset < 36 || offset >= size) return null;
        var bytes = descriptor.Slice((int)offset, (int)(size - offset));
        var end = bytes.IndexOf((byte)0);
        if (end < 0) return null;
        foreach (var value in bytes[..end]) if (value > 127 || value < 32) return null;
        var serial = Encoding.ASCII.GetString(bytes[..end]).Trim();
        return serial.Length == 0 ? null : serial;
    }

    private static void AddCounter(Dictionary<MetricFieldCode, UInt128> values,
        MetricFieldCode field, ReadOnlySpan<byte> log, int offset, bool zeroIsUnreported)
    {
        var raw = BinaryPrimitives.ReadUInt128LittleEndian(log.Slice(offset, 16));
        if (!zeroIsUnreported || raw != 0) values[field] = raw;
    }

    private static readonly MetricFieldCode[] SensorCodes =
    [
        MetricFieldCode.NvmeTemperatureSensor1, MetricFieldCode.NvmeTemperatureSensor2,
        MetricFieldCode.NvmeTemperatureSensor3, MetricFieldCode.NvmeTemperatureSensor4,
        MetricFieldCode.NvmeTemperatureSensor5, MetricFieldCode.NvmeTemperatureSensor6,
        MetricFieldCode.NvmeTemperatureSensor7, MetricFieldCode.NvmeTemperatureSensor8
    ];

    private static void AddTemperature(Dictionary<MetricFieldCode, double> values, MetricFieldCode code,
        ReadOnlySpan<byte> log, int offset)
    {
        var kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[offset..]);
        if (kelvin != 0) values[code] = kelvin - 273.15;
    }

    private static uint Read(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static void Write(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
}

internal interface IWindowsNvmeHealthQuery : IDisposable
{
    byte[]? Query(byte[] input, int outputLength);
}

public sealed record NvmeHealthRead(IReadOnlyDictionary<MetricFieldCode, double> Values,
    IReadOnlyDictionary<MetricFieldCode, ulong> IntegerValues)
{
    public IReadOnlyDictionary<MetricFieldCode, UInt128> WideIntegerValues { get; init; } = new Dictionary<MetricFieldCode, UInt128>();
    public IReadOnlyDictionary<MetricFieldCode, byte[]> BytesValues { get; init; } = new Dictionary<MetricFieldCode, byte[]>();
}
