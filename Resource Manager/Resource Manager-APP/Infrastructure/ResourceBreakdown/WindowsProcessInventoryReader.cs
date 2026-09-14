using System.Diagnostics;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

internal interface IWindowsProcessInventoryReader
{
    Process[] GetProcesses();
}

internal sealed class WindowsProcessInventoryReader : IWindowsProcessInventoryReader
{
    internal static WindowsProcessInventoryReader Instance { get; } = new();

    private WindowsProcessInventoryReader()
    {
    }

    public Process[] GetProcesses() => Process.GetProcesses();
}
