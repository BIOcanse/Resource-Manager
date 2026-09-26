using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal static class HardwareSensorDependencyRuntimeProbe
{
    public static HardwareSensorDependencyRuntimeStatus ProbeNvidiaNvapi()
    {
        foreach (var candidate in CandidateNvapiLibraries())
        {
            if (!TryLoad(candidate, out var handle))
            {
                continue;
            }

            try
            {
                var hasQueryInterface = NativeLibrary.TryGetExport(handle, "nvapi_QueryInterface", out _);
                var state = hasQueryInterface ? "RuntimeAvailable" : "InstalledUnverified";
                var message = hasQueryInterface
                    ? "NVIDIA NVAPI DLL 可加载，QueryInterface 导出存在；风扇/电压能力以实时 NVAPI 读数验证为准。"
                    : "NVIDIA NVAPI DLL 可加载，但未找到 QueryInterface 导出；需要后续桥接验证。";
                return new HardwareSensorDependencyRuntimeStatus(true, hasQueryInterface, state, message, candidate);
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }

        return new HardwareSensorDependencyRuntimeStatus(
            false,
            false,
            "Missing",
            "未能加载 NVIDIA NVAPI DLL；通常需要安装或修复 NVIDIA Windows 驱动。",
            null);
    }

    public static HardwareSensorDependencyRuntimeStatus ProbeLibreHardwareMonitor()
    {
        var wmi = ProbeHardwareMonitorWmi();
        if (wmi is not null)
        {
            return new HardwareSensorDependencyRuntimeStatus(
                true,
                true,
                "RuntimeAvailable",
                $"{wmi.Provider} WMI 可访问；实时读数还需要由监控项采样验证。",
                wmi.Path);
        }

        var installPath = FindFirstExistingPath(CandidateLibreHardwareMonitorFiles());
        if (installPath is not null)
        {
            return new HardwareSensorDependencyRuntimeStatus(
                true,
                false,
                "InstalledUnverified",
                "检测到 LibreHardwareMonitor 文件，但当前未发现可访问 WMI 传感命名空间；需要启动并开启传感暴露。",
                installPath);
        }

        return new HardwareSensorDependencyRuntimeStatus(
            false,
            false,
            "Missing",
            "未检测到 LibreHardwareMonitor WMI 或本地依赖文件。",
            null);
    }

    public static HardwareSensorDependencyRuntimeStatus ProbeNotebookFanControl()
    {
        var service = FindService(
            "NBFCService",
            "NoteBookFanControlService",
            "NotebookFanControlService");
        if (service is not null)
        {
            return new HardwareSensorDependencyRuntimeStatus(
                true,
                true,
                "RuntimeAvailable",
                $"检测到 NBFC 服务：{service}；读取风扇前还需要机型配置和只读桥接验证。",
                service);
        }

        var installPath = FindFirstExistingPath(CandidateNotebookFanControlFiles());
        if (installPath is not null)
        {
            return new HardwareSensorDependencyRuntimeStatus(
                true,
                false,
                "InstalledUnverified",
                "检测到 Notebook FanControl 文件，但未检测到服务；需要完成安装/配置后才能作为风扇 Provider。",
                installPath);
        }

        return new HardwareSensorDependencyRuntimeStatus(
            false,
            false,
            "Missing",
            "未检测到 NBFC 服务或本地依赖文件。",
            null);
    }

    private static IEnumerable<string> CandidateNvapiLibraries()
    {
        var fileName = Environment.Is64BitProcess ? "nvapi64.dll" : "nvapi.dll";
        yield return fileName;

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(systemDirectory))
        {
            yield return Path.Combine(systemDirectory, fileName);
        }

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            yield return Path.Combine(windowsDirectory, "SysWOW64", "nvapi.dll");
        }
    }

    private static HardwareMonitorWmiState? ProbeHardwareMonitorWmi()
    {
        return ProbeWmiNamespace("LibreHardwareMonitor", @"root\LibreHardwareMonitor")
            ?? ProbeWmiNamespace("OpenHardwareMonitor", @"root\OpenHardwareMonitor");
    }

    private static HardwareMonitorWmiState? ProbeWmiNamespace(string provider, string path)
    {
        try
        {
            var scope = new ManagementScope(path);
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Identifier FROM Sensor"));
            searcher.Options.ReturnImmediately = true;
            searcher.Options.Timeout = TimeSpan.FromSeconds(2);
            using var collection = searcher.Get();
            return collection.Count > 0 ? new HardwareMonitorWmiState(provider, path) : null;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            return null;
        }
    }

    private static IEnumerable<string> CandidateLibreHardwareMonitorFiles()
    {
        foreach (var root in CandidateDependencyDirectories("librehardwaremonitor-provider", "LibreHardwareMonitor", "Libre Hardware Monitor"))
        {
            yield return Path.Combine(root, "LibreHardwareMonitor.exe");
        }
    }

    private static IEnumerable<string> CandidateNotebookFanControlFiles()
    {
        foreach (var root in CandidateDependencyDirectories("notebook-fancontrol-provider", "NoteBook FanControl", "Notebook FanControl", "NBFC"))
        {
            yield return Path.Combine(root, "NBFC.exe");
            yield return Path.Combine(root, "NoteBookFanControl.exe");
            yield return Path.Combine(root, "NotebookFanControl.exe");
        }
    }

    private static IEnumerable<string> CandidateDependencyDirectories(string componentId, params string[] names)
    {
        var packageRoot = PackagePathResolver.ResolvePackageRoot(AppContext.BaseDirectory);
        yield return Path.Combine(packageRoot, "Dependencies", componentId);

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var root in roots.Where(static item => !string.IsNullOrWhiteSpace(item)))
        {
            foreach (var name in names)
            {
                yield return Path.Combine(root, name);
            }
        }
    }

    private static string? FindService(params string[] serviceNames)
    {
        foreach (var serviceName in serviceNames)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                if (key is not null)
                {
                    var imagePath = key.GetValue("ImagePath")?.ToString();
                    return string.IsNullOrWhiteSpace(imagePath) ? serviceName : $"{serviceName} / {imagePath}";
                }
            }
            catch
            {
                // Registry probing is best-effort for optional providers.
            }
        }

        return null;
    }

    private static string? FindFirstExistingPath(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate) || Directory.Exists(candidate))
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

    private sealed record HardwareMonitorWmiState(string Provider, string Path);
}

internal sealed record HardwareSensorDependencyRuntimeStatus(
    bool RuntimeAvailable,
    bool PrimaryCapabilityAvailable,
    string State,
    string Message,
    string? RuntimePath);
