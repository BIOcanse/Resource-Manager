using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Vendors.Amd;

public sealed partial class AmdAdlxBridgeReader
{
    private readonly IAmdAdlxNativeBridge bridge;
    private readonly Func<string, string, string?>? describeProviderMessage;
    private static readonly AdlxReadBoundary NativeReadBoundary = new();
    private readonly AdlxReadBoundary readBoundary;
    private readonly TimeSpan maximumReadDuration;
    public AmdAdlxBridgeReader(Func<string, string, string?>? describeProviderMessage = null)
        : this(null, describeProviderMessage, null) { }
    public AmdAdlxBridgeReader(TimeSpan maximumReadDuration,
        Func<string, string, string?>? describeProviderMessage = null)
        : this(null, describeProviderMessage, maximumReadDuration) { }
    internal AmdAdlxBridgeReader(IAmdAdlxNativeBridge bridge, TimeSpan? maximumReadDuration = null)
        : this(bridge, null, maximumReadDuration) { }
    private AmdAdlxBridgeReader(IAmdAdlxNativeBridge? bridge, Func<string, string, string?>? describeProviderMessage,
        TimeSpan? maximumReadDuration)
    {
        this.bridge = bridge ?? new NativeBridge();
        this.describeProviderMessage = describeProviderMessage;
        this.maximumReadDuration = maximumReadDuration ?? TimeSpan.FromSeconds(2);
        if (this.maximumReadDuration <= TimeSpan.Zero || this.maximumReadDuration.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumReadDuration));
        readBoundary = bridge is null ? NativeReadBoundary : new();
    }
    internal const uint BridgeAbiVersion = 6;
    internal const int TimestampFlag = 4096;
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
    private const int MemoryTemperatureFlag = 2048;
    internal const int NpuActivityLevelFlag = 8192;
    internal const int NpuFrequencyFlag = 16384;
    private const int GpuTypeIntegrated = 1;
    private const int GpuTypeDiscrete = 2;

    public Task<IReadOnlyList<GpuMetrics>> ReadAsync(AmdAdlxReadRequest request,
        WindowsGpuAdapterInventoryRead windowsInventory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.IncludesAnyMetric) return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
        request = request with { DisplayIndexes = request.DisplayIndexes?.ToHashSet(),
            NativeFlagsByDisplayIndex = request.NativeFlagsByDisplayIndex?.ToDictionary(item => item.Key, item => item.Value) };
        windowsInventory = CopyInventory(windowsInventory);
        return readBoundary.RunAsync(() => ReadUnboundedAsync(request, windowsInventory, cancellationToken).GetAwaiter().GetResult(),
            (IReadOnlyList<GpuMetrics>)[], maximumReadDuration, cancellationToken);
    }

    private static WindowsGpuAdapterInventoryRead CopyInventory(WindowsGpuAdapterInventoryRead inventory)
        => inventory with { Adapters = inventory.Adapters.Select(adapter => adapter with
            { PhysicalIdentities = adapter.PhysicalIdentities.ToArray() }).ToArray() };

    private Task<IReadOnlyList<GpuMetrics>> ReadUnboundedAsync(
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
            if (bridge.GetAbiVersion()
                    != BridgeAbiVersion
                || bridge.GetMetricsSize() != Marshal.SizeOf<NativeAdlxGpuMetrics>()
                || bridge.GetSelectionSize() != Marshal.SizeOf<NativeAdlxGpuSelection>())
            {
                return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
            }

            var buffer = new NativeAdlxGpuMetrics[32];
            var message = new StringBuilder(512);
            var count = bridge.ReadMetadata(buffer, message);
            ThrowIfFaulted(count);
            if (count <= 0 || count > buffer.Length)
            {
                return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
            }

            var candidates = buffer.Take(count).Select(native => (Native: native,
                Gpu: ToGpuMetrics(native, request, windowsInventory, describeProviderMessage)))
                .Where(item => item.Gpu is not null).ToArray();
            var duplicateIds = buffer.Take(count).Where(static item => item.IdentityValid == 1)
                .GroupBy(static item => item.UniqueId)
                .Where(static group => group.Count() > 1).Select(static group => group.Key).ToHashSet();
            var duplicateIndexes = candidates.GroupBy(item => item.Gpu!.Index)
                .Where(static group => group.Count() > 1).Select(static group => group.Key).ToHashSet();
            var selections = candidates.Where(item => !duplicateIds.Contains(item.Native.UniqueId)
                    && !duplicateIndexes.Contains(item.Gpu!.Index))
                .Select(item => new NativeAdlxGpuSelection { UniqueId = item.Native.UniqueId, PnpString = item.Native.PnpString,
                    RequestedFlags = request.NativeFlagsByDisplayIndex is { } selectedFlags
                        ? selectedFlags.GetValueOrDefault(item.Gpu!.Index) : flags })
                .Where(static selection => selection.RequestedFlags != 0).ToArray();
            if (selections.Length == 0) return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(buffer);
            count = bridge.ReadSelected(buffer, selections, message);
            ThrowIfFaulted(count);
            if (count <= 0 || count > selections.Length) return Task.FromResult<IReadOnlyList<GpuMetrics>>([]);
            var byDisplayIndex = new Dictionary<int, GpuMetrics>();
            var ambiguousDisplayIndexes = new HashSet<int>();
            foreach (var native in buffer.Take(Math.Min(count, buffer.Length)))
            {
                if (!selections.Any(selection => selection.UniqueId == native.UniqueId
                    && Decode(selection.PnpString) == Decode(native.PnpString))) continue;
                var gpu = ToGpuMetrics(
                    native,
                    request,
                    windowsInventory, describeProviderMessage);
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

    private static void ThrowIfFaulted(int result)
    {
        if (result == -5) throw new SEHException("ADLX instance faulted; process restart is required before reuse.");
    }

    internal static int BuildNativeFlags(AmdAdlxReadRequest request)
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
        if (request.IncludeMemoryTemperature) flags |= MemoryTemperatureFlag;
        if (request.IncludeNpuActivityLevel) flags |= NpuActivityLevelFlag;
        if (request.IncludeNpuFrequency) flags |= NpuFrequencyFlag;

        if (request.IncludeFan)
        {
            flags |= FanFlag;
        }

        if (request.IncludeVoltage)
        {
            flags |= VoltageFlag;
        }
        if (request.IncludeSampleTimestamp) flags |= TimestampFlag;

        return flags;
    }

    internal static GpuMetrics? ToGpuMetrics(
        NativeAdlxGpuMetrics native,
        AmdAdlxReadRequest request,
        WindowsGpuAdapterInventoryRead windowsInventory, Func<string, string, string?>? describeProviderMessage = null)
    {
        if (native.StructSize != Marshal.SizeOf<NativeAdlxGpuMetrics>()
            || native.AbiVersion != BridgeAbiVersion || native.IdentityValid != 1)
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
        if (adapter.VendorId != 0x1002) return null;
        var hasUsage = HasAvailable(native, UsageFlag) && double.IsFinite(native.UsagePercent)
            && native.UsagePercent is >= 0 and <= 100;
        var usage = hasUsage ? native.UsagePercent : 0;
        var hasGraphicsClock = request.IncludeGraphicsClocks && HasAvailable(native, ClockFlag) && native.GraphicsClockMhz >= 0;
        var graphicsClock = hasGraphicsClock ? native.GraphicsClockMhz : 0;
        var maxGraphicsClock = native.MaxGraphicsClockMhz > 0 ? native.MaxGraphicsClockMhz : 0;
        var graphicsPercent = maxGraphicsClock > 0 ? graphicsClock * 100d / maxGraphicsClock : 0;
        var hasMemoryClock = request.IncludeMemoryClock && HasAvailable(native, VramClockFlag) && native.MemoryClockMhz >= 0;
        var memoryClock = hasMemoryClock ? native.MemoryClockMhz : 0;
        var hasUsedMemory = request.IncludeMemory && HasAvailable(native, VramFlag) && native.VramUsedMb >= 0;
        var usedMemoryBytes = hasUsedMemory ? (ulong)native.VramUsedMb * 1024UL * 1024UL : 0;
        var totalMemoryBytes = request.IncludeMemory && native.TotalVramMb > 0
            ? native.TotalVramMb * 1024UL * 1024UL
            : 0;
        var memoryPercent = totalMemoryBytes > 0 ? usedMemoryBytes * 100d / totalMemoryBytes : 0;
        var sensors = CreateSensors(native, name, describeProviderMessage);
        var integerReadings = new Dictionary<MetricFieldCode, ulong>();
        if (HasAvailable(native, TimestampFlag) && native.SampleTimestampMilliseconds >= 0)
            integerReadings[MetricFieldCode.AdlxSampleTimestamp] = (ulong)native.SampleTimestampMilliseconds;
        if (hasUsedMemory)
            integerReadings[MetricFieldCode.AdlxDedicatedMemoryMb] = (ulong)native.VramUsedMb;

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
            IsUsageAvailable: hasUsage)
        {
            HasDedicatedMemoryMetrics = adapter.SupportsDedicatedMemoryMetrics,
            AdditionalIntegerReadings = integerReadings,
            AdditionalReadings = new Dictionary<MetricFieldCode, double?>
            {
                [MetricFieldCode.GpuGraphicsClock] = hasGraphicsClock ? graphicsClock : null,
                [MetricFieldCode.GpuGraphicsClockPercent] = hasGraphicsClock && maxGraphicsClock > 0 ? graphicsPercent : null,
                [MetricFieldCode.GpuMemoryClock] = hasMemoryClock ? memoryClock : null,
                [MetricFieldCode.GpuMemoryUsed] = hasUsedMemory ? usedMemoryBytes : null,
                [MetricFieldCode.GpuMemoryTotal] = totalMemoryBytes > 0 ? totalMemoryBytes : null,
                [MetricFieldCode.GpuMemoryPercent] = hasUsedMemory && totalMemoryBytes > 0 ? memoryPercent : null,
                [MetricFieldCode.GpuMemoryTemperature] = HasAvailable(native, MemoryTemperatureFlag)
                    && double.IsFinite(native.MemoryTemperatureCelsius) && native.MemoryTemperatureCelsius is >= 0 and <= 1000
                    ? native.MemoryTemperatureCelsius : null,
                [MetricFieldCode.AdlxNpuActivityPercent] = request.IncludeNpuActivityLevel
                    && HasAvailable(native, NpuActivityLevelFlag) && native.NpuActivityLevelPercent is >= 0 and <= 100
                    ? native.NpuActivityLevelPercent : null,
                [MetricFieldCode.AdlxNpuFrequencyMhz] = request.IncludeNpuFrequency
                    && HasAvailable(native, NpuFrequencyFlag) && native.NpuFrequencyMhz >= 0
                    ? native.NpuFrequencyMhz : null
            }
        };
    }

    internal static GpuSensorMetrics CreateSensors(NativeAdlxGpuMetrics native, string deviceName, Func<string, string, string?>? describeProviderMessage = null)
    {
        var hasAnyValue = HasAvailable(native, PowerFlag)
            || HasAvailable(native, BoardPowerFlag)
            || HasAvailable(native, TemperatureFlag)
            || HasAvailable(native, HotspotTemperatureFlag)
            || HasAvailable(native, IntakeTemperatureFlag)
            || HasAvailable(native, FanFlag)
            || HasAvailable(native, VoltageFlag);
        var state = hasAnyValue ? HardwareSensorProviderStates.Partial : HardwareSensorProviderStates.Unavailable;
        var message = describeProviderMessage?.Invoke("AMD ADLX", state);

        return new GpuSensorMetrics(
            new HardwareSensorProviderState("AMD ADLX", state, message),
            HasAvailable(native, PowerFlag) ? native.PowerWatts : null,
            null,
            HasAvailable(native, BoardPowerFlag) ? native.BoardPowerWatts : null,
            HasAvailable(native, TemperatureFlag) ? native.TemperatureCelsius : null,
            HasAvailable(native, HotspotTemperatureFlag) ? native.HotspotTemperatureCelsius : null,
            HasAvailable(native, IntakeTemperatureFlag) ? native.IntakeTemperatureCelsius : null,
            null,
            HasAvailable(native, FanFlag) && native.FanSpeedRpm >= 0 ? native.FanSpeedRpm : null,
            HasAvailable(native, VoltageFlag) && native.VoltageMillivolts >= 0 ? native.VoltageMillivolts / 1000d : null,
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

    private static partial class NativeMethods
    {
        [DllImport("ResourceManager.AdlxBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint ResourceManagerAdlxGetAbiVersion();

        [DllImport("ResourceManager.AdlxBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ResourceManagerAdlxGetGpuMetricsSize();

        [DllImport("ResourceManager.AdlxBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ResourceManagerAdlxGetGpuSelectionSize();

        [DllImport("ResourceManager.AdlxBridge.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int ResourceManagerAdlxReadSelectedGpuMetrics([Out] NativeAdlxGpuMetrics[] outputs,
            int capacity, [In] NativeAdlxGpuSelection[] selections, int selectionCount, StringBuilder message, int messageCapacity);

        [DllImport("ResourceManager.AdlxBridge.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int ResourceManagerAdlxReadGpuMetrics(
            [Out] NativeAdlxGpuMetrics[] outputs,
            int capacity,
            int requestedFlags,
            StringBuilder message,
            int messageCapacity);
    }

    private sealed partial class NativeBridge : IAmdAdlxNativeBridge
    {
        public uint GetAbiVersion() => NativeMethods.ResourceManagerAdlxGetAbiVersion();
        public int GetMetricsSize() => NativeMethods.ResourceManagerAdlxGetGpuMetricsSize();
        public int GetSelectionSize() => NativeMethods.ResourceManagerAdlxGetGpuSelectionSize();
        public int ReadMetadata(NativeAdlxGpuMetrics[] output, StringBuilder message)
            => NativeMethods.ResourceManagerAdlxReadGpuMetrics(output, output.Length, 0, message, message.Capacity);
        public int ReadSelected(NativeAdlxGpuMetrics[] output, NativeAdlxGpuSelection[] selections, StringBuilder message)
            => NativeMethods.ResourceManagerAdlxReadSelectedGpuMetrics(output, output.Length, selections, selections.Length, message, message.Capacity);
    }
}

internal interface IAmdAdlxNativeBridge
{
    uint GetAbiVersion();
    int GetMetricsSize();
    int GetSelectionSize();
    int ReadMetadata(NativeAdlxGpuMetrics[] output, StringBuilder message);
    int ReadSelected(NativeAdlxGpuMetrics[] output, NativeAdlxGpuSelection[] selections, StringBuilder message);
    uint GetDeclarationAbiVersion() => 0;
    int GetDeclarationSize() => 0;
    int ReadDeclarations(NativeAdlxGpuDeclaration[] output, NativeAdlxGpuSelection[] selections, StringBuilder message) => 0;
}

public sealed record AmdAdlxReadRequest(
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
    bool IncludeVoltage,
    bool IncludeMemoryTemperature = false,
    bool IncludeSampleTimestamp = false,
    bool IncludeNpuActivityLevel = false,
    bool IncludeNpuFrequency = false)
{
    internal IReadOnlyDictionary<int, int>? NativeFlagsByDisplayIndex { get; init; }
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
        IncludeVoltage: true,
        IncludeMemoryTemperature: true,
        IncludeSampleTimestamp: true,
        IncludeNpuActivityLevel: true,
        IncludeNpuFrequency: true);

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
        || IncludeVoltage
        || IncludeMemoryTemperature
        || IncludeSampleTimestamp
        || IncludeNpuActivityLevel
        || IncludeNpuFrequency;

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
    public int FanSpeedRpm;
    public double IntakeTemperatureCelsius;
    public double MemoryTemperatureCelsius;
    public int IdentityValid;
    public long SampleTimestampMilliseconds;
    public int NpuActivityLevelPercent;
    public int NpuFrequencyMhz;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAdlxGpuSelection
{
    public int UniqueId;
    public AdlxFixedString512 PnpString;
    public int RequestedFlags;
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
