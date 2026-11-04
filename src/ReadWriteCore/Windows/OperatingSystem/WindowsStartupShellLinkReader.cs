using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.SystemInformation;

/// <summary>Loads only a local read-only shell-link declaration. GetPath uses
/// SLGP_RAWPATH; no Resolve, Save, SetPath or launch operation is called.</summary>
internal static unsafe class WindowsStartupShellLinkReader
{
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-c000-000000000046");
    private static readonly Guid ShellLinkId = new("000214f9-0000-0000-c000-000000000046");
    private static readonly Guid PersistFileId = new("0000010b-0000-0000-c000-000000000046");

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Startup registry key, folder or boot/logon task registration changed")]
    internal static WindowsStartupFileDeclaration Read(string file)
    {
        nint link = 0, persistence = 0; var clsid = ShellLinkClass; var iid = ShellLinkId;
        try
        {
            Check(CoCreateInstance(in clsid, 0, 1, in iid, out link));
            var persist = PersistFileId;
            var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(link, 0);
            Check(query(link, &persist, &persistence));
            // IPersistFile.Load=5, STGM_READ | STGM_SHARE_DENY_NONE=0x40.
            var load = (delegate* unmanaged[Stdcall]<nint, char*, uint, int>)Slot(persistence, 5);
            fixed (char* path = file) Check(load(persistence, path, 0x40));
            var target = GetText(link, 3, true); // IShellLinkW.GetPath, SLGP_RAWPATH=4.
            if (target.Length == 0) throw new InvalidDataException("Shell-link target is not a readable filesystem path declaration.");
            return new(file, "shell-link", target, GetText(link, 10, false), GetText(link, 8, false));
        }
        finally { Release(persistence); Release(link); }
    }
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Startup registry key, folder or boot/logon task registration changed")]
    private static string GetText(nint value, int slot, bool path)
    {
        // GetPath's documented native ceiling is MAX_PATH; detect that ceiling
        // rather than assuming a larger caller buffer proves a complete path.
        var buffer = new char[path || slot == 8 ? 260 : 32768];
        fixed (char* text = buffer)
        {
            if (path) Check(((delegate* unmanaged[Stdcall]<nint, char*, int, nint, uint, int>)Slot(value, slot))(value, text, buffer.Length, 0, 4));
            else Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Slot(value, slot))(value, text, buffer.Length));
        }
        var end = Array.IndexOf(buffer, '\0');
        if (end < 0 || end >= buffer.Length - 1) throw new InvalidDataException("Shell-link declaration may be truncated.");
        return new string(buffer, 0, end);
    }
    private static nint Slot(nint value, int index)
        => value != 0 ? (*(nint**)value)[index] : throw new COMException("Shell-link query returned null.");
    private static void Release(nint value)
    { if (value != 0) _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(value, 2))(value); }
    private static void Check(int hr) { if (hr < 0) throw new COMException("Read-only shell-link declaration failed.", hr); }
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint value);
}
