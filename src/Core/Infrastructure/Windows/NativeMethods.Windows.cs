using System.Runtime.InteropServices;
using System.Text;

namespace ResourceManager.App.Infrastructure.Windows;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetWindow(
        IntPtr window,
        uint command);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(
        IntPtr window,
        uint command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(
        IntPtr window,
        out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RedrawWindow(
        IntPtr window,
        IntPtr updateRectangle,
        IntPtr updateRegion,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("shell32.dll")]
    internal static extern int SHGetPropertyStoreForWindow(
        IntPtr window,
        ref Guid interfaceId,
        out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PropVariant propVariant);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Math.Max(0, Right - Left);

    public readonly int Height => Math.Max(0, Bottom - Top);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;

    public PropertyKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)]
    public ushort VariantType;

    [FieldOffset(8)]
    public IntPtr PointerValue;
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint propertyCount);

    [PreserveSig]
    int GetAt(uint propertyIndex, out PropertyKey propertyKey);

    [PreserveSig]
    int GetValue(ref PropertyKey propertyKey, out PropVariant value);

    [PreserveSig]
    int SetValue(ref PropertyKey propertyKey, ref PropVariant value);

    [PreserveSig]
    int Commit();
}
