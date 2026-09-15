using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Windows;

internal static partial class NativeMethods
{
    /// <summary>调用方要求 ShellExecuteEx 先解析出 PIDL，「属性」这个谓词只有带它才生效。</summary>
    internal const uint SeeMaskInvokeIdList = 0x0000000C;

    internal const uint SeeMaskFlagNoUi = 0x00000400;

    // 结构里带字符串，源生成的 LibraryImport 封送不了，这里按项目里其他同类签名用 DllImport。
    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Parent;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public IntPtr InstanceApplication;
        public IntPtr IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr KeyClass;
        public uint HotKey;
        public IntPtr Icon;
        public IntPtr Process;
    }
}
