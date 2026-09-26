using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal static class PawnIoRuntimeProbe
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
    private const string Wow6432UninstallKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    /// <summary>
    /// 探这条路能不能用。
    ///
    /// **要真的读一次 PM table**，不能停在"设备能打开"。
    /// 设备打得开只说明驱动装了；能不能出读数还要 RyzenSMU 模块在位、
    /// 而且这颗 CPU 的表版本做过索引映射 —— 三者缺一，指标就是空的。
    /// 先前这里到第一步就宣称"还需要桥接验证"然后再也不验证，
    /// 于是 Provider 永远不激活，指标永远进不了采集计划。
    /// </summary>
    /// <param name="contentRootPath">
    /// 给深度验证用。传 null 就只做便宜的检查。
    /// </param>
    /// <param name="deepVerify">
    /// 是否真的去读一次 PM table。
    ///
    /// **默认不读**：那一步会加载内核模块，代价高，而且这台机器上的安全软件
    /// 会把启动期加载内核模块的进程直接杀掉 —— 探测组件状态不该有这种副作用。
    /// 只有用户在组件页显式点「验证」时才做。
    /// </param>
    public static PawnIoRuntimeStatus Probe(
        string? contentRootPath = null,
        bool deepVerify = false)
    {
        var installed = TryReadInstallRecord();
        var device = TryOpenDevice(out var deviceError);
        var runtimeAvailable = installed is not null || device;
        if (!device)
        {
            return new PawnIoRuntimeStatus(
                runtimeAvailable,
                false,
                installed?.Version,
                installed is not null ? "InstalledUnverified" : "Missing",
                installed is not null
                    ? $"检测到 PawnIO 安装记录，但设备暂不可打开：{deviceError ?? "未知错误"}。"
                    : "未检测到 PawnIO 安装记录；需要通过组件安装显式安装官方签名驱动。",
                installed?.InstallLocation ?? installed?.DisplayName);
        }

        if (!deepVerify)
        {
            return new PawnIoRuntimeStatus(
                runtimeAvailable,
                true,
                installed?.Version,
                "RuntimeAvailable",
                "PawnIO 设备可打开。点「验证」确认 RyzenSMU 读数。",
                installed?.InstallLocation ?? installed?.DisplayName);
        }

        var bridge = AmdSmuBridgeVerification.Verify(contentRootPath);
        return new PawnIoRuntimeStatus(
            runtimeAvailable,
            bridge.Verified,
            installed?.Version,
            bridge.Verified ? "RuntimeActive" : "RuntimeAvailable",
            bridge.Message,
            installed?.InstallLocation ?? installed?.DisplayName);
    }

    private static PawnIoInstallRecord? TryReadInstallRecord()
    {
        return TryReadInstallRecord(UninstallKey)
            ?? TryReadInstallRecord(Wow6432UninstallKey);
    }

    private static PawnIoInstallRecord? TryReadInstallRecord(string keyPath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null)
            {
                return null;
            }

            var displayName = key.GetValue("DisplayName")?.ToString() ?? "PawnIO";
            var version = key.GetValue("DisplayVersion")?.ToString();
            var installLocation = key.GetValue("InstallLocation")?.ToString();
            return new PawnIoInstallRecord(displayName, version, installLocation);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryOpenDevice(out string? error)
    {
        error = null;
        try
        {
            using var handle = CreateFile(
                DevicePath,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero);

            if (!handle.IsInvalid)
            {
                return true;
            }

            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or Win32Exception)
        {
            error = ex.Message;
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}

internal sealed record PawnIoRuntimeStatus(
    bool RuntimeAvailable,
    bool DeviceAvailable,
    string? Version,
    string State,
    string Message,
    string? RuntimePath);

internal sealed record PawnIoInstallRecord(
    string DisplayName,
    string? Version,
    string? InstallLocation);
