using System.Runtime.InteropServices;
using Microsoft.Win32;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal static class AmdRyzenMasterRuntimeProbe
{
    private const string ComponentId = "amd-ryzen-master-monitoring-sdk";
    private const string DriverServiceName = "AMDRyzenMasterDriverV31";
    private const string DriverFileName = "AMDRyzenMasterDriver.sys";
    private const string DeviceLibraryName = "Device.dll";
    private const string PlatformLibraryName = "Platform.dll";
    private const uint ServiceQueryStatus = 0x0004;
    private const int ServiceRunning = 4;

    public static AmdRyzenMasterRuntimeStatus Probe()
    {
        var driver = ProbeDriver();
        foreach (var candidate in CandidateRuntimePairs())
        {
            if (!File.Exists(candidate.DeviceLibraryPath) || !File.Exists(candidate.PlatformLibraryPath))
            {
                continue;
            }

            var deviceLoadable = TryLoad(candidate.DeviceLibraryPath, out var deviceHandle);
            if (deviceLoadable)
            {
                NativeLibrary.Free(deviceHandle);
            }

            var platformLoadable = TryLoad(candidate.PlatformLibraryPath, out var platformHandle);
            var hasReadExport = false;
            var hasGetPlatformExport = false;
            if (platformLoadable)
            {
                try
                {
                    hasReadExport = NativeLibrary.TryGetExport(platformHandle, "GetRmCpuParameters", out _);
                    hasGetPlatformExport = NativeLibrary.TryGetExport(platformHandle, "?GetPlatform@@YAAEAVIPlatform@@XZ", out _);
                }
                finally
                {
                    NativeLibrary.Free(platformHandle);
                }
            }

            var available = deviceLoadable && platformLoadable && hasReadExport;
            var state = available && driver.Installed ? "RuntimeAvailable" : "InstalledUnverified";
            var message = available
                ? driver.Installed
                    ? "AMD Ryzen Master SDK 运行库和只读 CPU 参数导出可加载；实时读数还需要 Provider 桥接验证和权限。"
                    : "AMD Ryzen Master SDK 运行库可加载，但未找到驱动记录；实时读数可能不可用。"
                : "找到 AMD Ryzen Master SDK 文件，但 Device/Platform 运行库或只读导出不可用。";

            return new AmdRyzenMasterRuntimeStatus(
                available,
                deviceLoadable,
                platformLoadable,
                hasReadExport,
                hasGetPlatformExport,
                driver.Installed,
                driver.Running,
                state,
                message,
                candidate.DeviceLibraryPath,
                candidate.PlatformLibraryPath,
                driver.DriverPath);
        }

        return new AmdRyzenMasterRuntimeStatus(
            false,
            false,
            false,
            false,
            false,
            driver.Installed,
            driver.Running,
            "Missing",
            driver.Installed
                ? "已找到 AMD Ryzen Master driver 记录，但未找到可加载的 Device.dll/Platform.dll。"
                : "未找到 AMD Ryzen Master Monitoring SDK 运行库。需要通过组件安装显式安装。",
            null,
            null,
            driver.DriverPath);
    }

    public static IEnumerable<AmdRyzenMasterRuntimePair> CandidateRuntimePairs()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var packageRoot = PackagePathResolver.ResolvePackageRoot(baseDirectory);
        var dependencyRoot = Path.Combine(packageRoot, "Dependencies", ComponentId);

        foreach (var directory in CandidateRuntimeDirectories(dependencyRoot))
        {
            yield return new AmdRyzenMasterRuntimePair(
                Path.Combine(directory, DeviceLibraryName),
                Path.Combine(directory, PlatformLibraryName));
        }
    }

    private static IEnumerable<string> CandidateRuntimeDirectories(string dependencyRoot)
    {
        yield return Path.Combine(dependencyRoot, "bin");
        yield return dependencyRoot;

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(systemDirectory))
        {
            yield return systemDirectory;
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "AMD",
            "RyzenMasterMonitoringSDK",
            "bin");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "AMD",
            "CNext",
            "CNext");
    }

    private static AmdRyzenMasterDriverState ProbeDriver()
    {
        var path = TryReadServiceImagePath(DriverServiceName);
        if (path is null)
        {
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var fallback = string.IsNullOrWhiteSpace(systemDirectory)
                ? null
                : Path.Combine(systemDirectory, "drivers", DriverFileName);
            if (fallback is not null && File.Exists(fallback))
            {
                return new AmdRyzenMasterDriverState(true, false, fallback);
            }

            fallback = string.IsNullOrWhiteSpace(systemDirectory)
                ? null
                : Path.Combine(systemDirectory, DriverFileName);
            return fallback is not null && File.Exists(fallback)
                ? new AmdRyzenMasterDriverState(true, false, fallback)
                : new AmdRyzenMasterDriverState(false, false, null);
        }

        var normalizedPath = NormalizeNtDevicePath(path);
        return new AmdRyzenMasterDriverState(true, IsDriverRunning(DriverServiceName), normalizedPath);
    }

    private static string? TryReadServiceImagePath(string serviceName)
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

    private static bool IsDriverRunning(string serviceName)
    {
        var manager = OpenSCManager(null, null, ServiceQueryStatus);
        if (manager == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return QueryServiceStatus(service, out var status)
                    && status.CurrentState == ServiceRunning;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static string? NormalizeNtDevicePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = value.Trim().Trim('"');
        if (path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
        {
            path = path[4..];
        }

        return Environment.ExpandEnvironmentVariables(path);
    }

    private static bool TryLoad(string candidate, out IntPtr handle)
    {
        try
        {
            return NativeLibrary.TryLoad(candidate, out handle);
        }
        catch (BadImageFormatException)
        {
            handle = IntPtr.Zero;
            return false;
        }
        catch (DllNotFoundException)
        {
            handle = IntPtr.Zero;
            return false;
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(
        IntPtr serviceControlManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(
        IntPtr service,
        out ServiceStatus serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public int CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }
}

internal sealed record AmdRyzenMasterRuntimePair(
    string DeviceLibraryPath,
    string PlatformLibraryPath);

internal sealed record AmdRyzenMasterRuntimeStatus(
    bool RuntimeAvailable,
    bool DeviceLibraryLoadable,
    bool PlatformLibraryLoadable,
    bool CpuParametersExportAvailable,
    bool GetPlatformExportAvailable,
    bool DriverInstalled,
    bool DriverRunning,
    string State,
    string Message,
    string? DeviceLibraryPath,
    string? PlatformLibraryPath,
    string? DriverPath);

internal sealed record AmdRyzenMasterDriverState(
    bool Installed,
    bool Running,
    string? DriverPath);
