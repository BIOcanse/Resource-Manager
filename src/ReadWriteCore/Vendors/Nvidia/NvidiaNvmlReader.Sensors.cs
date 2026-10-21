using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
using ResourceManager.App.Domain.Metrics;
namespace ResourceManager.ReadWrite.Vendors.Nvidia;
public sealed partial class NvidiaNvmlReader
{
    private GpuSensorMetrics ReadSensors(IntPtr device, NvidiaNvmlReadRequest request)
    {
        if (!request.IncludesAnySensor)
        {
            return new GpuSensorMetrics(
                new HardwareSensorProviderState("NVIDIA NVML", "NotRequested", describeProviderMessage?.Invoke("NVIDIA NVML", "NotRequested")),
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
        var message = describeProviderMessage?.Invoke("NVIDIA NVML", state);

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
            return NativeMethods.nvmlDeviceGetPowerManagementLimit(device, out var milliwatts) == NvmlSuccess
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
