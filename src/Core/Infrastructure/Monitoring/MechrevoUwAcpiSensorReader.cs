using System.Runtime.InteropServices;
using Microsoft.Win32;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed class MechrevoUwAcpiSensorReader
{
    private const int CpuFanRpmHighAddress = 1124;
    private const int CpuFanRpmLowAddress = 1125;
    private const int GpuFanRpmHighAddress = 1132;
    private const int GpuFanRpmLowAddress = 1131;
    private const int CpuFanDutyAddress = 1883;
    private const int GpuFanDutyAddress = 1884;
    private const string ProviderName = "Mechrevo UWACPI";
    private readonly object gate = new();
    private RuntimeState? runtimeState;

    public MechrevoUwAcpiSensorSnapshot Read()
    {
        var runtime = EnsureRuntime();
        if (runtime.Reader is null)
        {
            return new MechrevoUwAcpiSensorSnapshot(
                new HardwareSensorProviderState(ProviderName, runtime.State, runtime.Message),
                null,
                null,
                null);
        }

        try
        {
            var cpuFanRpm = ReadRpm(runtime.Reader, CpuFanRpmHighAddress, CpuFanRpmLowAddress);
            var gpuFanRpm = ReadRpm(runtime.Reader, GpuFanRpmHighAddress, GpuFanRpmLowAddress);
            var cpuFanPercent = ReadDutyPercent(runtime.Reader, CpuFanDutyAddress);
            var gpuFanPercent = ReadDutyPercent(runtime.Reader, GpuFanDutyAddress);
            var hasValue = cpuFanRpm is not null
                || gpuFanRpm is not null
                || cpuFanPercent is not null
                || gpuFanPercent is not null;
            var providerState = hasValue
                ? new HardwareSensorProviderState(
                    ProviderName,
                    "Active",
                    $"从官方 UWACPI ReadEC 只读接口读取风扇；DLL：{runtime.DllPath}")
                : new HardwareSensorProviderState(
                    ProviderName,
                    "Unavailable",
                    "UWACPI ReadEC 可调用，但未返回可用风扇读数。");

            var gpuSensor = gpuFanRpm is null && gpuFanPercent is null
                ? null
                : new PlatformGpuSensor(
                    0,
                    "NVIDIA AMD dGPU fan",
                    "Mechrevo UWACPI / dGPU fan / official ACPI EC read-only provider",
                    null,
                    null,
                    gpuFanRpm,
                    gpuFanPercent,
                    null,
                    null);

            return new MechrevoUwAcpiSensorSnapshot(providerState, cpuFanRpm, cpuFanPercent, gpuSensor);
        }
        catch (Exception ex) when (ex is BadImageFormatException or DllNotFoundException or EntryPointNotFoundException or SEHException or AccessViolationException)
        {
            return new MechrevoUwAcpiSensorSnapshot(
                new HardwareSensorProviderState(ProviderName, "Unavailable", $"UWACPI ReadEC 读取失败：{ex.Message}"),
                null,
                null,
                null);
        }
    }

    public static HardwareSensorDependencyRuntimeStatus ProbeRuntime()
    {
        var dllPath = ResolveOfficialDllPath();
        var driver = FindService("UWACPIDriver");
        var bridge = FindService("GCUBridge");
        if (dllPath is null)
        {
            return new HardwareSensorDependencyRuntimeStatus(
                false,
                false,
                "Missing",
                "未检测到机械革命/同方控制中心的 ACPIDriverDll.dll。",
                bridge ?? driver);
        }

        if (!TryCreateReader(dllPath, out var handle, out _))
        {
            return new HardwareSensorDependencyRuntimeStatus(
                true,
                false,
                "InstalledUnverified",
                $"检测到 ACPIDriverDll.dll，但无法加载或未找到 ReadEC 导出。驱动：{driver ?? "未检测到"}",
                dllPath);
        }

        NativeLibrary.Free(handle);
        return new HardwareSensorDependencyRuntimeStatus(
            true,
            driver is not null,
            driver is not null ? "RuntimeAvailable" : "InstalledUnverified",
            driver is not null
                ? $"检测到 UWACPIDriver 和 ReadEC 导出；GCUBridge：{bridge ?? "未检测到"}。"
                : "检测到 ReadEC 导出，但未检测到 UWACPIDriver 服务。",
            dllPath);
    }

    private RuntimeState EnsureRuntime()
    {
        lock (gate)
        {
            if (runtimeState is not null)
            {
                return runtimeState;
            }

            var dllPath = ResolveOfficialDllPath();
            if (dllPath is null)
            {
                runtimeState = RuntimeState.Unavailable("Missing", "未检测到机械革命/同方官方 UWACPI 运行库。");
                return runtimeState;
            }

            if (!TryCreateReader(dllPath, out var handle, out var reader))
            {
                runtimeState = RuntimeState.Unavailable("Unavailable", $"无法加载 UWACPI ReadEC 导出：{dllPath}");
                return runtimeState;
            }

            runtimeState = new RuntimeState(handle, reader, dllPath, "RuntimeAvailable", "UWACPI ReadEC 可用。");
            return runtimeState;
        }
    }

    private static bool TryCreateReader(string dllPath, out IntPtr handle, out ReadEcDelegate? reader)
    {
        handle = IntPtr.Zero;
        reader = null;
        try
        {
            if (!NativeLibrary.TryLoad(dllPath, out handle))
            {
                return false;
            }

            if (!NativeLibrary.TryGetExport(handle, "ReadEC", out var export))
            {
                NativeLibrary.Free(handle);
                handle = IntPtr.Zero;
                return false;
            }

            reader = Marshal.GetDelegateForFunctionPointer<ReadEcDelegate>(export);
            return true;
        }
        catch (Exception ex) when (ex is BadImageFormatException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (handle != IntPtr.Zero)
            {
                NativeLibrary.Free(handle);
            }

            handle = IntPtr.Zero;
            reader = null;
            return false;
        }
    }

    private static double? ReadRpm(ReadEcDelegate reader, int highAddress, int lowAddress)
    {
        var high = ReadByte(reader, highAddress);
        var low = ReadByte(reader, lowAddress);
        if (high is null || low is null)
        {
            return null;
        }

        var rpm = (high.Value << 8) | low.Value;
        return rpm is >= 0 and < 20000 ? rpm : null;
    }

    private static double? ReadDutyPercent(ReadEcDelegate reader, int address)
    {
        var raw = ReadByte(reader, address);
        if (raw is null)
        {
            return null;
        }

        if (raw.Value <= 100)
        {
            return raw.Value;
        }

        if (raw.Value <= 200)
        {
            return raw.Value / 2d;
        }

        return null;
    }

    private static int? ReadByte(ReadEcDelegate reader, int address)
    {
        var value = reader(address);
        return value is >= 0 and <= 255 ? value : null;
    }

    private static string? ResolveOfficialDllPath()
    {
        foreach (var candidate in CandidateDllPaths())
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
            {
                continue;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateDllPaths()
    {
        var packageRoot = PackagePathResolver.ResolvePackageRoot(AppContext.BaseDirectory);
        yield return Path.Combine(packageRoot, "Dependencies", NotebookOemFanSensorReader.ComponentId, "mechrevo-uwacpi", "ACPIDriverDll.dll");

        var bridgePath = ExtractExecutablePath(ReadServiceImagePath("GCUBridge"));
        if (!string.IsNullOrWhiteSpace(bridgePath))
        {
            var bridgeDirectory = Path.GetDirectoryName(bridgePath);
            if (!string.IsNullOrWhiteSpace(bridgeDirectory))
            {
                yield return Path.Combine(bridgeDirectory, "MyControlCenter", "ACPIDriverDll.dll");
            }
        }

        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(static item => !string.IsNullOrWhiteSpace(item)))
        {
            var oemRoot = Path.Combine(root, "OEM");
            if (!Directory.Exists(oemRoot))
            {
                continue;
            }

            foreach (var controlCenterDirectory in SafeEnumerateDirectories(oemRoot))
            {
                yield return Path.Combine(controlCenterDirectory, "AiStoneService", "MyControlCenter", "ACPIDriverDll.dll");
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
        {
            return [];
        }
    }

    private static string? FindService(string serviceName)
    {
        var imagePath = ReadServiceImagePath(serviceName);
        return string.IsNullOrWhiteSpace(imagePath) ? null : $"{serviceName} / {imagePath}";
    }

    private static string? ReadServiceImagePath(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return key?.GetValue("ImagePath")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractExecutablePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (expanded.StartsWith('"'))
        {
            var end = expanded.IndexOf('"', 1);
            return end > 1 ? expanded[1..end] : null;
        }

        var exeIndex = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0 ? expanded[..(exeIndex + 4)] : expanded.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadEcDelegate(int address);

    private sealed record RuntimeState(
        IntPtr Handle,
        ReadEcDelegate? Reader,
        string? DllPath,
        string State,
        string Message)
    {
        public static RuntimeState Unavailable(string state, string message)
        {
            return new RuntimeState(IntPtr.Zero, null, null, state, message);
        }
    }
}

internal sealed record MechrevoUwAcpiSensorSnapshot(
    HardwareSensorProviderState ProviderState,
    double? CpuFanSpeedRpm,
    double? CpuFanSpeedPercent,
    PlatformGpuSensor? GpuFanSensor);
