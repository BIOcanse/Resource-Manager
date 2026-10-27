using Microsoft.Win32;
using ResourceManager.App.Domain.Components;
using ResourceManager.ReadWrite.Vendors.Mechrevo;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
namespace ResourceManager.App.Infrastructure.Monitoring;
internal sealed class MechrevoUwAcpiSensorReader : IDisposable
{
    private readonly MechrevoUwAcpiMetricResource resource;
    public MechrevoUwAcpiSensorReader() : this(ResolveOfficialDllPath, static path => new MechrevoUwAcpiReader(path)) { }
    internal MechrevoUwAcpiSensorReader(Func<string?> resolveLibraryPath, Func<string, MechrevoUwAcpiReader> createReader)
        => resource = new(resolveLibraryPath, createReader, Describe, "未检测到机械革命/同方官方 UWACPI 运行库。");
    internal void ReleaseSession() => resource.ReleaseSession();
    public MechrevoUwAcpiSensorSnapshot Read() => resource.Read();
    public void Dispose() => resource.Dispose();
    private static string Describe(MechrevoUwAcpiReadResult result, string libraryPath)
        => result.Status switch
        {
            MechrevoUwAcpiReadStatus.Active => $"从官方 UWACPI ReadEC 只读接口读取风扇；DLL：{libraryPath}",
            MechrevoUwAcpiReadStatus.LibraryUnavailable => $"无法加载 UWACPI ReadEC 导出：{libraryPath}",
            MechrevoUwAcpiReadStatus.ReadFailed => $"UWACPI ReadEC 读取失败：{result.Error}",
            _ => "UWACPI ReadEC 可调用，但未返回可用风扇读数。"
        };    public static HardwareSensorDependencyRuntimeStatus ProbeRuntime()
    {
        var dllPath = ResolveOfficialDllPath();
        var driver = FindService("UWACPIDriver");
        var bridge = FindService("GCUBridge");
        if (dllPath is null)
        {
            return new HardwareSensorDependencyRuntimeStatus(
                false,
                false,
                ComponentProviderStates.Missing,
                "未检测到机械革命/同方控制中心的 ACPIDriverDll.dll。",
                bridge ?? driver);
        }

        if (!MechrevoUwAcpiReader.ProbeLibrary(dllPath))
        {
            return new HardwareSensorDependencyRuntimeStatus(
                true,
                false,
                ComponentProviderStates.InstalledUnverified,
                $"检测到 ACPIDriverDll.dll，但无法加载或未找到 ReadEC 导出。驱动：{driver ?? "未检测到"}",
                dllPath);
        }

        return new HardwareSensorDependencyRuntimeStatus(
            true,
            driver is not null,
            driver is not null ? ComponentProviderStates.RuntimeAvailable : ComponentProviderStates.InstalledUnverified,
            driver is not null
                ? $"检测到 UWACPIDriver 和 ReadEC 导出；GCUBridge：{bridge ?? "未检测到"}。"
                : "检测到 ReadEC 导出，但未检测到 UWACPIDriver 服务。",
            dllPath);
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

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows device-bound driver package changed")]
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

}


