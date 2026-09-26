using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal static class AmdAdlxRuntimeProbe
{
    public static AmdAdlxRuntimeStatus Probe()
    {
        foreach (var candidate in CandidateLibraryPaths())
        {
            if (!TryLoad(candidate, out var handle))
            {
                continue;
            }

            try
            {
                var hasInitializeExport =
                    NativeLibrary.TryGetExport(handle, "ADLXInitialize", out _)
                    || NativeLibrary.TryGetExport(handle, "ADLXInitializeWithCallerAdl", out _);
                var state = hasInitializeExport ? "RuntimeAvailable" : "InstalledUnverified";
                var message = hasInitializeExport
                    ? "AMD ADLX 运行库可加载，初始化导出存在；Provider 桥接和实时读数验证尚未完成。"
                    : "AMD ADLX DLL 可加载，但未找到预期初始化导出；需要后续桥接验证。";

                return new AmdAdlxRuntimeStatus(true, hasInitializeExport, state, message, candidate);
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }

        return new AmdAdlxRuntimeStatus(
            false,
            false,
            "Missing",
            "未能加载 AMD ADLX 运行库；如果已安装 AMD 驱动，后续 Provider 仍需显式验证。",
            null);
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

    private static IEnumerable<string> CandidateLibraryPaths()
    {
        var fileName = Environment.Is64BitProcess ? "amdadlx64.dll" : "amdadlx32.dll";
        yield return fileName;

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(systemDirectory))
        {
            yield return Path.Combine(systemDirectory, fileName);
        }

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            yield return Path.Combine(windowsDirectory, "SysWOW64", "amdadlx32.dll");
        }
    }
}

internal sealed record AmdAdlxRuntimeStatus(
    bool RuntimeAvailable,
    bool InitializeExportAvailable,
    string State,
    string Message,
    string? LibraryPath);
