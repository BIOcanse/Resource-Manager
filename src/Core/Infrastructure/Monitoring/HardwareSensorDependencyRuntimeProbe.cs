using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.ReadWrite.Windows.Platform;

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
                var state = hasQueryInterface
                    ? ComponentProviderStates.RuntimeAvailable
                    : ComponentProviderStates.InstalledUnverified;
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
            ComponentProviderStates.Missing,
            "未能加载 NVIDIA NVAPI DLL；通常需要安装或修复 NVIDIA Windows 驱动。",
            null);
    }

    public static HardwareSensorDependencyRuntimeStatus ProbeLibreHardwareMonitor()
    {
        var library = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.Location;
        // Status reads never open the driver or enumerate sensors.
        var installed = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
        return new(true, false,
            installed ? ComponentProviderStates.InstalledUnverified : ComponentProviderStates.DriverMissing,
            installed ? "官方核心库与 PawnIO 已安装；传感器能力由实际采样确认。"
                : "官方核心库已随产品提供；需要显式安装 PawnIO 驱动。", library);
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

}

internal sealed record HardwareSensorDependencyRuntimeStatus(
    bool RuntimeAvailable,
    bool PrimaryCapabilityAvailable,
    string State,
    string Message,
    string? RuntimePath);
