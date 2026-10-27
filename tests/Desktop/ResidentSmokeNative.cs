using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Window-scoped diagnostics only. This helper deliberately has no desktop-capture API.
public static class ResidentSmokeNative
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public bool Contains(Rect value) => value.Left >= Left && value.Top >= Top && value.Right <= Right && value.Bottom <= Bottom;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MonitorInfo
    { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct IconIdentifier
    { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ProcessEntry
    {
        public uint Size, Usage, Pid; public UIntPtr Heap; public uint Module, Threads, Parent;
        public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Image;
    }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important;
    }
    public sealed class Monitor { public Rect Bounds; public Rect Work; public bool Primary; }
    public sealed class Child { public uint Pid; public string Image; }
    delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    delegate bool WindowCallback(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, IntPtr data);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessageW(IntPtr window, uint message, UIntPtr wp, IntPtr lp);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, UIntPtr wp, IntPtr lp, uint flags, uint timeout, out UIntPtr result);
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref IconIdentifier id, out Rect rect);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint first, uint lines, byte[] bits, ref BitmapInfo info, uint usage);

    public static Monitor[] Monitors()
    {
        var result = new List<Monitor>();
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, dc, rect, data) => {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(monitor, ref info)) throw new Win32Exception();
            result.Add(new Monitor { Bounds = info.Monitor, Work = info.Work, Primary = (info.Flags & 1) != 0 });
            return true;
        }, IntPtr.Zero)) throw new Win32Exception();
        return result.ToArray();
    }
    public static uint Owner(IntPtr window) { uint pid; GetWindowThreadProcessId(window, out pid); return pid; }
    public static string ClassName(IntPtr window) { var text = new StringBuilder(256); GetClassNameW(window, text, text.Capacity); return text.ToString(); }
    public static Rect Bounds(IntPtr window) { Rect rect; if (!GetWindowRect(window, out rect)) throw new Win32Exception(); return rect; }
    public static IntPtr Menu(uint pid)
    {
        var result = IntPtr.Zero;
        EnumWindows((window, data) => { if (Owner(window) == pid && ClassName(window) == "#32768" && IsWindowVisible(window)) { result = window; return false; } return true; }, IntPtr.Zero);
        return result;
    }
    public static Rect TrayRect(IntPtr window)
    {
        var id = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = window, Id = 1 };
        Rect rect; int status = Shell_NotifyIconGetRect(ref id, out rect);
        if (status != 0) Marshal.ThrowExceptionForHR(status);
        return rect;
    }
    public static void Post(IntPtr window, uint message, ulong wp, long lp)
    { if (!PostMessageW(window, message, new UIntPtr(wp), new IntPtr(lp))) throw new Win32Exception(); }
    public static ulong Send(IntPtr window, uint message, ulong wp, long lp)
    {
        UIntPtr result;
        if (SendMessageTimeoutW(window, message, new UIntPtr(wp), new IntPtr(lp), 2, 3000, out result) == IntPtr.Zero) throw new Win32Exception();
        return result.ToUInt64();
    }
    public static Child[] Children(uint parent)
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new Win32Exception();
        try {
            var result = new List<Child>(); var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            bool more = Process32FirstW(snapshot, ref entry); int count = 0;
            while (more) {
                if (++count > 65536) throw new InvalidOperationException("Process snapshot exceeds bound.");
                if (entry.Parent == parent) result.Add(new Child { Pid = entry.Pid, Image = entry.Image });
                more = Process32NextW(snapshot, ref entry);
            }
            return result.ToArray();
        } finally { CloseHandle(snapshot); }
    }
    public static void CaptureWindow(IntPtr window, uint expectedPid, string file)
    {
        if (!IsWindow(window) || Owner(window) != expectedPid) throw new InvalidOperationException("Window ownership changed.");
        Rect rect = Bounds(window); int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) throw new InvalidOperationException("Invalid window bounds.");
        IntPtr source = GetDC(window), dc = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        if (source == IntPtr.Zero) throw new Win32Exception();
        try {
            dc = CreateCompatibleDC(source); bitmap = CreateCompatibleBitmap(source, width, height);
            if (dc == IntPtr.Zero || bitmap == IntPtr.Zero) throw new Win32Exception();
            previous = SelectObject(dc, bitmap);
            if (!PrintWindow(window, dc, 2)) throw new InvalidOperationException("PrintWindow failed; desktop fallback is prohibited.");
            SelectObject(dc, previous); previous = IntPtr.Zero;
            var bits = new byte[checked(width * height * 4)];
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32, SizeImage = (uint)bits.Length };
            if (GetDIBits(dc, bitmap, 0, (uint)height, bits, ref info, 0) != height) throw new Win32Exception();
            bool varied = false;
            for (int offset = 4; offset < bits.Length; offset += 4) {
                if (bits[offset] != bits[0] || bits[offset + 1] != bits[1] || bits[offset + 2] != bits[2]) { varied = true; break; }
            }
            if (!varied) throw new InvalidOperationException("PrintWindow returned a uniform image; capture is not accepted.");
            using (var writer = new BinaryWriter(File.Create(file))) {
                writer.Write((ushort)0x4d42); writer.Write(checked(54 + bits.Length)); writer.Write(0); writer.Write(54);
                writer.Write(40); writer.Write(width); writer.Write(-height); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(0); writer.Write(bits.Length); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(bits);
            }
        } finally {
            if (previous != IntPtr.Zero) SelectObject(dc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            ReleaseDC(window, source);
        }
    }
}
