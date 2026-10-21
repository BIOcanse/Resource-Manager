using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ResourceManager.ReadWrite.Windows.SystemInformation;

/// <summary>The raw system ACLineStatus byte returned by GetSystemPowerStatus.</summary>
public readonly record struct WindowsAcPowerStatusReading(byte AcLineStatus);

/// <summary>Typed boundary for the selected machine AC-line status read.</summary>
public interface IWindowsAcPowerStatusReader
{
    WindowsAcPowerStatusReading? Read();
}

/// <summary>Reads only SYSTEM_POWER_STATUS.ACLineStatus. Battery fields are not projected.</summary>
public sealed class WindowsAcPowerStatusReader : IWindowsAcPowerStatusReader
{
    public WindowsAcPowerStatusReading? Read()
        => OperatingSystem.IsWindows() ? ReadWindows() : null;

    [SupportedOSPlatform("windows")]
    private static WindowsAcPowerStatusReading? ReadWindows()
    {
        if (!GetSystemPowerStatus(out var status)) return null;
        return new(status.ACLineStatus);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetSystemPowerStatus", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    // winbase.h: four BYTE fields followed by two DWORD fields. Pack 4 gives
    // offsets 0, 1, 2, 3, 4, and 8, and the SDK structure size of 12 bytes.
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 12)]
    internal struct SystemPowerStatus
    {
        internal byte ACLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        internal byte SystemStatusFlag;
        internal uint BatteryLifeTime;
        internal uint BatteryFullLifeTime;
    }
}
