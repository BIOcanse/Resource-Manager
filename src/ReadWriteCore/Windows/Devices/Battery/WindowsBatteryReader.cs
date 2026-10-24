using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.ReadWrite.Windows.Devices.Battery;

internal interface IWindowsBatteryQuery : IDisposable
{
    int LastError { get; }
    byte[]? Query(uint controlCode, byte[] input, int maximumOutputBytes);
}

public sealed partial class WindowsBatteryReader
{
    internal const uint QueryTag = 0x294040;
    internal const uint QueryInformation = 0x294044;
    internal const uint QueryStatus = 0x29404C;
    private readonly Func<IReadOnlyList<string>?> enumerate;
    private readonly Func<string, IWindowsBatteryQuery?> open;

    public WindowsBatteryReader() : this(null, null) { }

    internal WindowsBatteryReader(Func<IReadOnlyList<string>?>? enumerate,
        Func<string, IWindowsBatteryQuery?>? open = null)
    {
        this.enumerate = enumerate ?? WindowsBatteryNative.Enumerate;
        this.open = open ?? WindowsBatteryNative.Open;
    }

    public BatteryTopologyRead ReadIdentities(bool includeDeclarations = false)
    {
        try
        {
            var paths = enumerate();
            if (paths is null || paths.Count > 32
                || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count)
                return new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
            var identities = new List<BatteryCatalogIdentity>();
            var rejected = 0;
            foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var device = open(path);
                    if (device is null || ReadTag(device) is not { } tag)
                    { rejected++; continue; }
                    if (tag == 0) continue; // An empty battery slot is not a device.
                    var info = ParseInformation(device.Query(QueryInformation, InformationRequest(tag, 0), 36));
                    var uniqueId = ReadString(device, tag, 7);
                    if (info is null || uniqueId is null)
                    { rejected++; continue; }
                    var manufacturer = includeDeclarations ? ReadString(device, tag, 6) : null;
                    var serial = includeDeclarations ? ReadString(device, tag, 8) : null;
                    if (ReadTag(device) != tag)
                    { rejected++; continue; }
                    identities.Add(new(identities.Count, path, uniqueId, info)
                    { Manufacturer = manufacturer, SerialNumber = serial });
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { rejected++; }
            }
            return new(NativeMetricSnapshotCatalogTopologyStatus.Complete, identities) { RejectedCount = rejected };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
        }
    }

    public BatteryMetricRead Read(BatteryCatalogIdentity identity, IReadOnlySet<MetricFieldCode> requested)
    {
        try
        {
            using var device = open(identity.DevicePath);
            if (device is null || ReadTag(device) is not { } tag || tag == 0)
                return Empty(identity, true);
            var info = ParseInformation(device.Query(QueryInformation, InformationRequest(tag, 0), 36));
            var uniqueId = ReadString(device, tag, 7);
            if (info is null || uniqueId is null) return Empty(identity, true);
            if (uniqueId != identity.UniqueId || info.IsRelative != identity.Information.IsRelative)
                return Empty(identity, true);
            byte[]? status = null;
            if (requested.Overlaps(StatusFields))
            {
                var wait = new byte[20];
                BinaryPrimitives.WriteUInt32LittleEndian(wait, tag); // Timeout=0: do not wait for a change.
                status = device.Query(QueryStatus, wait, 16);
            }
            uint? seconds = null;
            if (requested.Contains(MetricFieldCode.BatteryRemainingTime))
            {
                var result = device.Query(QueryInformation, InformationRequest(tag, 3), 4);
                if (result is { Length: 4 }) seconds = BinaryPrimitives.ReadUInt32LittleEndian(result);
            }
            if (ReadTag(device) != tag) return Empty(identity, true);
            return Project(identity.ExactIdentityKey, info, status, seconds, requested);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Empty(identity, false);
        }
    }

    public static bool AppliesTo(MetricFieldCode code, bool relative) => code switch
    {
        MetricFieldCode.BatteryDesignCapacityAbsolute or MetricFieldCode.BatteryFullChargeCapacityAbsolute
            or MetricFieldCode.BatteryRemainingCapacityAbsolute or MetricFieldCode.BatteryRateAbsolute => !relative,
        MetricFieldCode.BatteryDesignCapacityRelative or MetricFieldCode.BatteryFullChargeCapacityRelative
            or MetricFieldCode.BatteryRemainingCapacityRelative or MetricFieldCode.BatteryRateRelative => relative,
        _ => true
    };

    internal static BatteryInformation? ParseInformation(byte[]? bytes)
        => bytes is { Length: 36 } ? new(U32(bytes, 0), ParseChemistry(bytes.AsSpan(8, 4)),
            U32(bytes, 12), U32(bytes, 16), U32(bytes, 32)) : null;

    internal static BatteryMetricRead Project(string identity, BatteryInformation info, byte[]? status,
        uint? estimatedSeconds, IReadOnlySet<MetricFieldCode> requested)
    {
        var values = new Dictionary<MetricFieldCode, double>();
        Add(info.IsRelative ? MetricFieldCode.BatteryDesignCapacityRelative : MetricFieldCode.BatteryDesignCapacityAbsolute,
            Capacity(info.DesignedCapacity));
        Add(info.IsRelative ? MetricFieldCode.BatteryFullChargeCapacityRelative : MetricFieldCode.BatteryFullChargeCapacityAbsolute,
            Capacity(info.FullChargedCapacity));
        Add(MetricFieldCode.BatteryCycleCount, info.CycleCount == 0 ? null : info.CycleCount);
        if (Capacity(info.DesignedCapacity) is > 0 and var design && Capacity(info.FullChargedCapacity) is { } full)
            Add(MetricFieldCode.BatteryFullToDesignRatio, full * 100 / design);
        uint? powerState = null;
        if (status is { Length: 16 })
        {
            powerState = requested.Contains(MetricFieldCode.BatteryPowerState) ? U32(status, 0) : null;
            var remaining = Capacity(U32(status, 4));
            Add(info.IsRelative ? MetricFieldCode.BatteryRemainingCapacityRelative : MetricFieldCode.BatteryRemainingCapacityAbsolute, remaining);
            Add(MetricFieldCode.BatteryVoltage, Capacity(U32(status, 8)));
            var rate = BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(12));
            Add(info.IsRelative ? MetricFieldCode.BatteryRateRelative : MetricFieldCode.BatteryRateAbsolute,
                rate == int.MinValue ? null : rate);
            if (remaining is { } actual && Capacity(info.FullChargedCapacity) is > 0 and var charged)
                Add(MetricFieldCode.BatteryChargePercent, actual * 100 / charged);
        }
        Add(MetricFieldCode.BatteryRemainingTime, estimatedSeconds is { } seconds ? Capacity(seconds) : null);
        return new(identity, values, powerState, false);

        void Add(MetricFieldCode code, double? value)
        {
            if (requested.Contains(code) && value is { } number) values.Add(code, number);
        }
    }

    private static BatteryMetricRead Empty(BatteryCatalogIdentity identity, bool refresh)
        => new(identity.ExactIdentityKey, new Dictionary<MetricFieldCode, double>(), null, refresh);
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static double? Capacity(uint value) => value == uint.MaxValue ? null : value;
    private static uint? ReadTag(IWindowsBatteryQuery device)
    {
        var result = device.Query(QueryTag, new byte[4], 4); // Zero wait time.
        // QUERY_TAG reports an empty slot as ERROR_FILE_NOT_FOUND, not a successful zero tag.
        return result is { Length: 4 } ? U32(result, 0) : device.LastError == 2 ? 0U : null;
    }
    internal static byte[] InformationRequest(uint tag, uint level)
    {
        var request = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(request, tag);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), level);
        return request; // AtRate=0 requests the driver's estimate at its current rate.
    }
    private static string? ReadString(IWindowsBatteryQuery device, uint tag, uint level)
    {
        return ParseString(device.Query(QueryInformation, InformationRequest(tag, level), 1024));
    }
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);
    internal static string? ParseString(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 2 || bytes.Length > 1024 || bytes.Length % 2 != 0) return null;
        for (var end = 0; end < bytes.Length; end += 2)
        {
            if (bytes[end] != 0 || bytes[end + 1] != 0) continue;
            try
            {
                var text = StrictUnicode.GetString(bytes, 0, end);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (DecoderFallbackException) { return null; }
        }
        return null;
    }
    internal static string ParseChemistry(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 4) return string.Empty;
        var end = bytes.IndexOf((byte)0);
        if (end < 0) end = 4;
        foreach (var value in bytes[..end]) if (value < 32 || value > 126) return string.Empty;
        foreach (var value in bytes[end..]) if (value != 0) return string.Empty;
        return Encoding.ASCII.GetString(bytes[..end]);
    }
    private static readonly HashSet<MetricFieldCode> StatusFields =
    [MetricFieldCode.BatteryRemainingCapacityAbsolute, MetricFieldCode.BatteryRemainingCapacityRelative,
        MetricFieldCode.BatteryVoltage, MetricFieldCode.BatteryRateAbsolute, MetricFieldCode.BatteryRateRelative,
        MetricFieldCode.BatteryPowerState, MetricFieldCode.BatteryChargePercent];
}
