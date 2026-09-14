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

    public static PawnIoRuntimeStatus Probe()
    {
        var installed = TryReadInstallRecord();
        var device = TryOpenDevice(out var deviceError);
        var runtimeAvailable = installed is not null || device;
        var state = device
            ? "RuntimeAvailable"
            : installed is not null
                ? "InstalledUnverified"
                : "Missing";
        var message = device
            ? "PawnIO 设备可打开；AMD SMU Provider 还需要 RyzenSMU 读数桥接验证。"
            : installed is not null
                ? $"检测到 PawnIO 安装记录，但设备暂不可打开：{deviceError ?? "未知错误"}。"
                : "未检测到 PawnIO 安装记录；需要通过组件安装显式安装官方签名驱动。";

        return new PawnIoRuntimeStatus(
            runtimeAvailable,
            device,
            installed?.Version,
            state,
            message,
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
