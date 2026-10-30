using System.Runtime.InteropServices;
namespace ResourceManager.ReadWrite.Windows.Processes;
internal sealed partial class WindowsProcessDetailsNativeApi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ModuleInformation
    {
        internal IntPtr Base;
        internal uint SizeOfImage;
        internal IntPtr Entry;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleInformation(IntPtr process, IntPtr module, out ModuleInformation information, uint size);
}
