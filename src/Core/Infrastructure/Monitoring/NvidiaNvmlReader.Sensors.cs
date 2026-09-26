using ResourceManager.App.Domain.Metrics;
namespace ResourceManager.App.Infrastructure.Monitoring;
internal sealed partial class NvidiaNvmlReader
{
    private static GpuSensorMetrics ReadSensors(IntPtr device, NvidiaNvmlReadRequest request)
    {
        if (!request.IncludesAnySensor)
        {
            return new GpuSensorMetrics(
                new HardwareSensorProviderState("NVIDIA NVML", "NotRequested", "当前快照未请求 NVIDIA 传感指标。"),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        var power = request.IncludePowerUsage ? ReadPowerUsageWatts(device) : null;
        var powerLimit = request.IncludePowerLimit ? ReadPowerLimitWatts(device) : null;
        var temperature = request.IncludeTemperature ? ReadTemperatureCelsius(device) : null;
        var fanSpeed = request.IncludeFan ? ReadFanSpeedPercent(device) : null;
        var hasAnyValue = power is not null
            || powerLimit is not null
            || temperature is not null
            || fanSpeed is not null;
        var state = hasAnyValue ? "Partial" : "Unavailable";
        var message = hasAnyValue
            ? "NVIDIA NVML 未返回电压/电流；需要 NVAPI 或可靠的厂商字段接口。"
            : "NVIDIA NVML 可用，但当前硬件/驱动未返回传感读数。";

        return new GpuSensorMetrics(
            new HardwareSensorProviderState("NVIDIA NVML", state, message),
            power,
            powerLimit,
            null,
            temperature,
            null,
            null,
            fanSpeed,
            null,
            null,
            null);
    }

    private static double? ReadPowerUsageWatts(IntPtr device)
    {
        try
        {
            return NativeMethods.nvmlDeviceGetPowerUsage(device, out var milliwatts) == NvmlSuccess
                ? milliwatts / 1000d
                : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static double? ReadPowerLimitWatts(IntPtr device)
    {
        try
        {
            if (NativeMethods.nvmlDeviceGetPowerManagementLimit(device, out var milliwatts) == NvmlSuccess)
            {
                return milliwatts / 1000d;
            }

            if (NativeMethods.nvmlDeviceGetEnforcedPowerLimit(device, out milliwatts) == NvmlSuccess)
            {
                return milliwatts / 1000d;
            }

            return NativeMethods.nvmlDeviceGetPowerManagementDefaultLimit(device, out milliwatts) == NvmlSuccess
                ? milliwatts / 1000d
                : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static double? ReadTemperatureCelsius(IntPtr device)
    {
        try
        {
            return NativeMethods.nvmlDeviceGetTemperature(device, NvmlTemperatureSensor.Gpu, out var temperature) == NvmlSuccess
                ? temperature
                : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static double? ReadFanSpeedPercent(IntPtr device)
    {
        var legacySpeed = ReadLegacyFanSpeedPercent(device);
        if (legacySpeed is not null)
        {
            return legacySpeed;
        }

        var fanCount = ReadFanCount(device);
        if (fanCount is null)
        {
            return ReadFanSpeedV2Percent(device, 0);
        }

        var speeds = new List<double>((int)Math.Min(fanCount.Value, 8));
        for (uint fan = 0; fan < fanCount.Value && fan < 8; fan++)
        {
            var speed = ReadFanSpeedV2Percent(device, fan);
            if (speed is not null)
            {
                speeds.Add(speed.Value);
            }
        }

        return speeds.Count == 0 ? null : speeds.Max();
    }

    private static double? ReadLegacyFanSpeedPercent(IntPtr device)
    {
        try
        {
            return NativeMethods.nvmlDeviceGetFanSpeed(device, out var speed) == NvmlSuccess
                ? speed
                : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static uint? ReadFanCount(IntPtr device)
    {
        try
        {
            return NativeMethods.nvmlDeviceGetNumFans(device, out var fanCount) == NvmlSuccess
                ? fanCount
                : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static double? ReadFanSpeedV2Percent(IntPtr device, uint fan)
    {
        try
        {
            return NativeMethods.nvmlDeviceGetFanSpeedV2(device, fan, out var speed) == NvmlSuccess
                ? speed
                : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }
}