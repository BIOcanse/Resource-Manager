using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed class AmdAdlxBridgeReader
{
    internal const uint BridgeAbiVersion = 2;
    private const int UsageFlag = 1;
    private const int ClockFlag = 2;
    private const int VramClockFlag = 4;
    private const int TemperatureFlag = 8;
    private const int PowerFlag = 16;
    private const int VoltageFlag = 32;
    private const int VramFlag = 64;
    private const int HotspotTemperatureFlag = 128;
    private const int FanFlag = 256;
    private const int BoardPowerFlag = 512;
    private const int IntakeTemperatureFlag = 1024;
    private const int GpuTypeIntegrated = 1;
    private const int GpuTypeDiscrete = 2;

    public Task<IReadOnlyList<GpuMetrics>> ReadAsync(
        AmdAdlxReadRequest request,
        WindowsGpuAdapterInventoryRead windowsInventory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.IncludesAnyMetric)
        {
            return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
        }

        var flags = BuildNativeFlags(request);
        if (flags == 0)
        {
            return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
        }

        try
        {
            if (NativeMethods.ResourceManagerAdlxGetAbiVersion()
                    != BridgeAbiVersion
                || NativeMethods.ResourceManagerAdlxGetGpuMetricsSize()
                    != Marshal.SizeOf<NativeAdlxGpuMetrics>())
            {
                return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
            }

            var buffer = new NativeAdlxGpuMetrics[8];
            var message = new StringBuilder(512);
            var count = NativeMethods.ResourceManagerAdlxReadGpuMetrics(buffer, buffer.Length, flags, message, message.Capacity);
            if (count <= 0)
            {
                return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
            }

            var byDisplayIndex = new Dictionary<int, GpuMetrics>();
            var ambiguousDisplayIndexes = new HashSet<int>();
            foreach (var native in buffer.Take(Math.Min(count, buffer.Length)))
            {
                var gpu = ToGpuMetrics(
                    native,
                    request,
                    windowsInventory);
                if (gpu is null
                    || ambiguousDisplayIndexes.Contains(gpu.Index))
                {
                    continue;
                }
                if (!byDisplayIndex.TryAdd(gpu.Index, gpu))
                {
                    byDisplayIndex.Remove(gpu.Index);
                    ambiguousDisplayIndexes.Add(gpu.Index);
                }
            }

            return Task.FromResult<IReadOnlyList<GpuMetrics>>(
                byDisplayIndex.Values
                    .OrderBy(static gpu => gpu.Index)
                    .ToArray());
        }
        catch (DllNotFoundException)
        {
            return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
        }
        catch (EntryPointNotFoundException)
        {
            return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
        }
        catch (BadImageFormatException)
        {
            return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
        }
    }

    private static int BuildNativeFlags(AmdAdlxReadRequest request)
    {
        var flags = 0;
        if (request.IncludeUsage)
        {
            flags |= UsageFlag;
        }

        if (request.IncludeGraphicsClocks)
        {
            flags |= ClockFlag;
        }

        if (request.IncludeMemoryClock)
        {
            flags |= VramClockFlag;
        }

        if (request.IncludeMemory)
        {
            flags |= VramFlag;
        }

        if (request.IncludePower)
        {
            flags |= PowerFlag;
        }

        if (request.IncludeBoardPower)
        {
            flags |= BoardPowerFlag;
        }

        if (request.IncludeTemperature)
        {
            flags |= TemperatureFlag;
        }

        if (request.IncludeHotspotTemperature)
        {
            flags |= HotspotTemperatureFlag;
        }

        if (request.IncludeIntakeTemperature)
        {
            flags |= IntakeTemperatureFlag;
        }

        if (request.IncludeFan)
        {
            flags |= FanFlag;
        }

        if (request.IncludeVoltage)
        {
            flags |= VoltageFlag;
        }

        return flags;
    }

    private static GpuMetrics? ToGpuMetrics(
        NativeAdlxGpuMetrics native,
        AmdAdlxReadRequest request,
        WindowsGpuAdapterInventoryRead windowsInventory)
    {
        if (native.StructSize != Marshal.SizeOf<NativeAdlxGpuMetrics>()
            || native.AbiVersion != BridgeAbiVersion)
        {
            return null;
        }

        var name = Decode(native.Name).Trim();
        var pnpInstanceId = Decode(native.PnpString);
        var match = WindowsGpuProviderIdentityResolver.ResolvePnp(
            windowsInventory,
            pnpInstanceId);
        if (!match.IsCurrent
            || !request.IncludesDisplayIndex(match.Binding.Adapter.Index))
        {
            return null;
        }

        var adapter = match.Binding.Adapter;
        var hasUsage = HasAvailable(native, UsageFlag);
        var usage = hasUsage ? native.UsagePercent : 0;
        var graphicsClock = HasAvailable(native, ClockFlag) ? native.GraphicsClockMhz : 0;
        var maxGraphicsClock = native.MaxGraphicsClockMhz > 0 ? native.MaxGraphicsClockMhz : 0;
        var graphicsPercent = maxGraphicsClock > 0 ? graphicsClock * 100d / maxGraphicsClock : 0;
        var memoryClock = HasAvailable(native, VramClockFlag) ? native.MemoryClockMhz : 0;
        var usedMemoryBytes = HasAvailable(native, VramFlag) ? (ulong)Math.Max(0, native.VramUsedMb) * 1024UL * 1024UL : 0;
        var totalMemoryBytes = native.TotalVramMb > 0
            ? native.TotalVramMb * 1024UL * 1024UL
            : 0;
        var memoryPercent = totalMemoryBytes > 0 ? usedMemoryBytes * 100d / totalMemoryBytes : 0;
        var sensors = CreateSensors(native, name);

        return new GpuMetrics(
            adapter.Index,
            string.IsNullOrWhiteSpace(name) ? adapter.Name : name,
            usage,
            graphicsClock,
            maxGraphicsClock,
            graphicsPercent,
            memoryClock,
            usedMemoryBytes,
            totalMemoryBytes,
            memoryPercent,
            sensors,
            UsageProvider: "AMD ADLX",
            IsUsageAvailable: hasUsage);
    }

    private static GpuSensorMetrics CreateSensors(NativeAdlxGpuMetrics native, string deviceName)
    {
        var hasAnyValue = HasAvailable(native, PowerFlag)
            || HasAvailable(native, BoardPowerFlag)
            || HasAvailable(native, TemperatureFlag)
            || HasAvailable(native, HotspotTemperatureFlag)
            || HasAvailable(native, IntakeTemperatureFlag)
            || HasAvailable(native, FanFlag)
            || HasAvailable(native, VoltageFlag);
        var state = hasAnyValue ? "Partial" : "Unavailable";
        var message = hasAnyValue
            ? "AMD ADLX: 当前已接入可用传感项；电流仍不推导。"
            : "AMD ADLX 可用，但当前硬件/驱动未返回请求的传感读数。";

        return new GpuSensorMetrics(
            new HardwareSensorProviderState("AMD ADLX", state, message),
            HasAvailable(native, PowerFlag) ? native.PowerWatts : null,
            null,
            HasAvailable(native, BoardPowerFlag) ? native.BoardPowerWatts : null,
            HasAvailable(native, TemperatureFlag) ? native.TemperatureCelsius : null,
            HasAvailable(native, HotspotTemperatureFlag) ? native.HotspotTemperatureCelsius : null,
            HasAvailable(native, IntakeTemperatureFlag) ? native.IntakeTemperatureCelsius : null,
            HasAvailable(native, FanFlag) ? native.FanSpeedPercent : null,
            null,
            HasAvailable(native, VoltageFlag) ? native.VoltageMillivolts / 1000d : null,
            null);
    }

    private static bool HasAvailable(NativeAdlxGpuMetrics native, int flag)
    {
        return (native.AvailableFlags & flag) != 0;
    }

    private static unsafe string Decode(AdlxFixedString32 value)
    {
        return DecodeNullTerminated(value.Value, 32);
    }

    private static unsafe string Decode(AdlxFixedString128 value)
    {
        return DecodeNullTerminated(value.Value, 128);
    }

    private static unsafe string Decode(AdlxFixedString512 value)
    {
        return DecodeNullTerminated(value.Value, 512);
    }

    private static unsafe string DecodeNullTerminated(byte* source, int capacity)
    {
        var length = 0;
        while (length < capacity && source[length] != 0)
        {
            length++;
        }

        return Encoding.UTF8.GetString(source, length);
    }

    private static class NativeMethods
    {
        [DllImport("ResourceManager.AdlxBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint ResourceManagerAdlxGetAbiVersion();

        [DllImport("ResourceManager.AdlxBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ResourceManagerAdlxGetGpuMetricsSize();

        [DllImport("ResourceManager.AdlxBridge.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int ResourceManagerAdlxReadGpuMetrics(
            [Out] NativeAdlxGpuMetrics[] outputs,
            int capacity,
            int requestedFlags,
            StringBuilder message,
            int messageCapacity);
    }
}

internal sealed record AmdAdlxReadRequest(
    IReadOnlySet<int>? DisplayIndexes,
    bool IncludeUsage,
    bool IncludeGraphicsClocks,
    bool IncludeMemory,
    bool IncludeMemoryClock,
    bool IncludePower,
    bool IncludeBoardPower,
    bool IncludeTemperature,
    bool IncludeHotspotTemperature,
    bool IncludeIntakeTemperature,
    bool IncludeFan,
    bool IncludeVoltage)
{
    public static AmdAdlxReadRequest All { get; } = new(
        null,
        IncludeUsage: true,
        IncludeGraphicsClocks: true,
        IncludeMemory: true,
        IncludeMemoryClock: true,
        IncludePower: true,
        IncludeBoardPower: true,
        IncludeTemperature: true,
        IncludeHotspotTemperature: true,
        IncludeIntakeTemperature: true,
        IncludeFan: true,
        IncludeVoltage: true);

    public bool IncludesAnyMetric =>
        IncludeUsage
        || IncludeGraphicsClocks
        || IncludeMemory
        || IncludeMemoryClock
        || IncludePower
        || IncludeBoardPower
        || IncludeTemperature
        || IncludeHotspotTemperature
        || IncludeIntakeTemperature
        || IncludeFan
        || IncludeVoltage;

    public bool IncludesDisplayIndex(int displayIndex)
    {
        return DisplayIndexes is null || DisplayIndexes.Contains(displayIndex);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeAdlxGpuMetrics
{
    public int StructSize;
    public uint AbiVersion;
    public int AdlxIndex;
    public int GpuType;
    public int UniqueId;
    public uint TotalVramMb;
    public AdlxFixedString128 Name;
    public AdlxFixedString32 VendorId;
    public AdlxFixedString32 DeviceId;
    public AdlxFixedString512 PnpString;
    public int SupportedFlags;
    public int AvailableFlags;
    public double UsagePercent;
    public int GraphicsClockMhz;
    public int MaxGraphicsClockMhz;
    public int MemoryClockMhz;
    public int MaxMemoryClockMhz;
    public double TemperatureCelsius;
    public double PowerWatts;
    public int VoltageMillivolts;
    public int VramUsedMb;
    public double HotspotTemperatureCelsius;
    public double BoardPowerWatts;
    public int FanSpeedPercent;
    public double IntakeTemperatureCelsius;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AdlxFixedString128
{
    public fixed byte Value[128];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AdlxFixedString32
{
    public fixed byte Value[32];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AdlxFixedString512
{
    public fixed byte Value[512];
}
