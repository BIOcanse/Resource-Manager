using System.Globalization;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed class AmdRyzenMasterCpuSensorReader
{
    private const int RawBufferSize = 256;
    private readonly object gate = new();
    private AmdRyzenMasterNativeSession? session;
    private AmdRyzenMasterRuntimeStatus? cachedUnavailableRuntime;

    public CpuSensorMetrics Read(AmdRyzenMasterCpuSensorReadRequest request)
    {
        if (!request.IncludesAnyMetric)
        {
            return CreateNotRequested();
        }

        lock (gate)
        {
            var ready = EnsureSession();
            if (ready is not null)
            {
                return CreateUnavailable(ready);
            }

            var read = session!.ReadCpuParameters();
            if (read.ResultCode != 0)
            {
                var message = read.ResultCode == -1
                    ? "AMD Ryzen Master SDK CPU 参数读取返回 -1；当前进程可能缺少管理员/驱动访问权限，或 SDK 运行时未完成初始化。"
                    : $"AMD Ryzen Master SDK CPU 参数读取失败，返回码 {read.ResultCode.ToString(CultureInfo.InvariantCulture)}。";
                return CreateUnavailable(message);
            }

            var mapped = AmdRyzenMasterCpuParameterMapper.TryMap(read.Buffer);
            if (mapped is null)
            {
                return CreateUnavailable("AMD Ryzen Master SDK 已返回 CPU 参数，但当前版本字段布局尚未完成校验；为避免误标电流/温度，暂不展示数值。");
            }

            return new CpuSensorMetrics(
                new HardwareSensorProviderState(
                    "AMD Ryzen Master Monitoring SDK",
                    "Active",
                    "AMD Ryzen Master SDK 只读 CPU 参数。"),
                request.IncludePackagePower ? mapped.PackagePowerWatts : null,
                request.IncludeCoreVoltage ? mapped.CoreVoltageVolts : null,
                request.IncludePackageCurrent ? mapped.PackageCurrentAmps : null,
                request.IncludeTemperature ? mapped.TemperatureCelsius : null);
        }
    }

    private string? EnsureSession()
    {
        if (session is not null)
        {
            return null;
        }

        if (cachedUnavailableRuntime is not null)
        {
            return FormatRuntimeUnavailable(cachedUnavailableRuntime);
        }

        var runtime = AmdRyzenMasterRuntimeProbe.Probe();
        if (!runtime.RuntimeAvailable || string.IsNullOrWhiteSpace(runtime.DeviceLibraryPath) || string.IsNullOrWhiteSpace(runtime.PlatformLibraryPath))
        {
            cachedUnavailableRuntime = runtime;
            return FormatRuntimeUnavailable(runtime);
        }

        try
        {
            session = AmdRyzenMasterNativeSession.Open(runtime);
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or InvalidOperationException)
        {
            cachedUnavailableRuntime = runtime with
            {
                State = "Unavailable",
                Message = $"AMD Ryzen Master SDK 运行库加载失败：{ex.Message}"
            };
            return FormatRuntimeUnavailable(cachedUnavailableRuntime);
        }
    }

    private static CpuSensorMetrics CreateNotRequested()
    {
        return new CpuSensorMetrics(
            new HardwareSensorProviderState(
                "AMD Ryzen Master Monitoring SDK",
                "NotRequested",
                "当前快照未请求 CPU 传感指标。"),
            null,
            null,
            null,
            null);
    }

    private static CpuSensorMetrics CreateUnavailable(string message)
    {
        return new CpuSensorMetrics(
            new HardwareSensorProviderState(
                "AMD Ryzen Master Monitoring SDK",
                "Unavailable",
                message),
            null,
            null,
            null,
            null);
    }

    private static string FormatRuntimeUnavailable(AmdRyzenMasterRuntimeStatus runtime)
    {
        var path = string.IsNullOrWhiteSpace(runtime.PlatformLibraryPath)
            ? null
            : $" Platform: {runtime.PlatformLibraryPath}";
        var driver = string.IsNullOrWhiteSpace(runtime.DriverPath)
            ? null
            : $" Driver: {runtime.DriverPath}";
        return $"{runtime.Message}{path}{driver}";
    }

    private sealed class AmdRyzenMasterNativeSession
    {
        private readonly IntPtr deviceHandle;
        private readonly IntPtr platformHandle;
        private readonly GetRmCpuParametersDelegate getRmCpuParameters;
        private readonly GetPlatformDelegate? getPlatform;

        private AmdRyzenMasterNativeSession(
            IntPtr deviceHandle,
            IntPtr platformHandle,
            GetRmCpuParametersDelegate getRmCpuParameters,
            GetPlatformDelegate? getPlatform)
        {
            this.deviceHandle = deviceHandle;
            this.platformHandle = platformHandle;
            this.getRmCpuParameters = getRmCpuParameters;
            this.getPlatform = getPlatform;
        }

        public static AmdRyzenMasterNativeSession Open(AmdRyzenMasterRuntimeStatus runtime)
        {
            var deviceHandle = NativeLibrary.Load(runtime.DeviceLibraryPath!);
            try
            {
                var platformHandle = NativeLibrary.Load(runtime.PlatformLibraryPath!);
                try
                {
                    var read = NativeLibrary.GetExport(platformHandle, "GetRmCpuParameters");
                    var getPlatformExportAvailable = NativeLibrary.TryGetExport(platformHandle, "?GetPlatform@@YAAEAVIPlatform@@XZ", out var getPlatformExport);
                    var session = new AmdRyzenMasterNativeSession(
                        deviceHandle,
                        platformHandle,
                        Marshal.GetDelegateForFunctionPointer<GetRmCpuParametersDelegate>(read),
                        getPlatformExportAvailable
                            ? Marshal.GetDelegateForFunctionPointer<GetPlatformDelegate>(getPlatformExport)
                            : null);
                    _ = session.getPlatform?.Invoke();
                    return session;
                }
                catch
                {
                    NativeLibrary.Free(platformHandle);
                    throw;
                }
            }
            catch
            {
                NativeLibrary.Free(deviceHandle);
                throw;
            }
        }

        public AmdRyzenMasterRawRead ReadCpuParameters()
        {
            var pointer = Marshal.AllocHGlobal(RawBufferSize);
            try
            {
                Marshal.Copy(new byte[RawBufferSize], 0, pointer, RawBufferSize);
                var result = getRmCpuParameters(pointer);
                var buffer = new byte[RawBufferSize];
                Marshal.Copy(pointer, buffer, 0, buffer.Length);
                return new AmdRyzenMasterRawRead(result, buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        ~AmdRyzenMasterNativeSession()
        {
            if (platformHandle != IntPtr.Zero)
            {
                NativeLibrary.Free(platformHandle);
            }

            if (deviceHandle != IntPtr.Zero)
            {
                NativeLibrary.Free(deviceHandle);
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetRmCpuParametersDelegate(IntPtr buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetPlatformDelegate();
}

internal sealed record AmdRyzenMasterCpuSensorReadRequest(
    bool IncludePackagePower,
    bool IncludeCoreVoltage,
    bool IncludePackageCurrent,
    bool IncludeTemperature)
{
    public bool IncludesAnyMetric =>
        IncludePackagePower
        || IncludeCoreVoltage
        || IncludePackageCurrent
        || IncludeTemperature;
}

internal sealed record AmdRyzenMasterRawRead(
    int ResultCode,
    byte[] Buffer);

internal sealed record AmdRyzenMasterMappedCpuSensors(
    double? PackagePowerWatts,
    double? CoreVoltageVolts,
    double? PackageCurrentAmps,
    double? TemperatureCelsius);

internal static class AmdRyzenMasterCpuParameterMapper
{
    public static AmdRyzenMasterMappedCpuSensors? TryMap(byte[] buffer)
    {
        // The installed SDK exposes GetRmCpuParameters but not field metadata through its C exports.
        // Keep this disabled until a verified public layout or elevated raw sample confirms offsets.
        _ = buffer;
        return null;
    }
}
