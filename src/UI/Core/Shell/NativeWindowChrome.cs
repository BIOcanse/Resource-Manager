using System.Runtime.InteropServices;

namespace ResourceManager.NativeUi;

internal static class NativeWindowChrome
{
    private const int WsCaption = 0x00C00000;
    private const int WsSysMenu = 0x00080000;
    private const int WsThickFrame = 0x00040000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    internal const int WindowStyles = WsCaption | WsSysMenu | WsThickFrame | WsMinimizeBox | WsMaximizeBox;
    private const int DwmwaTransitionsForceDisabled = 3;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;
    private const uint DarkBorderColor = 0x00242220;

    internal static Rectangle CalculateClientBounds(Rectangle window, Rectangle workArea, bool maximized)
        => maximized ? Rectangle.Intersect(window, workArea) : window;

    internal static Rectangle CalculateMaximizedBounds(Rectangle monitor, Rectangle workArea)
        => new(workArea.Left - monitor.Left, workArea.Top - monitor.Top, workArea.Width, workArea.Height);

    public static void ApplyTransitions(Form form, bool disabled)
    {
        if (!form.IsHandleCreated) return;
        _ = SetTransitions(form.Handle, disabled);
    }

    internal static int SetTransitions(nint hwnd, bool disabled)
    {
        var value = disabled ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, DwmwaTransitionsForceDisabled, ref value, sizeof(int));
    }

    internal static void CalculateNonClientArea(nint hwnd, nint parameters)
    {
        // RECT is the first member of NCCALCSIZE_PARAMS, so both wParam forms
        // use the same address. Never let DefWindowProc reintroduce a caption.
        if (parameters == 0 || !IsZoomed(hwnd)) return;
        var rect = Marshal.PtrToStructure<NativeRect>(parameters);
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        // The proposed rectangle can already be on the next monitor during a
        // move/DPI change, before the HWND's stored position has caught up.
        if (!GetMonitorInfo(MonitorFromRect(ref rect, 2), ref monitor)) return;
        var client = CalculateClientBounds(rect.Rectangle, monitor.WorkArea.Rectangle, maximized: true);
        Marshal.StructureToPtr(new NativeRect(client), parameters, false);
    }

    internal static void ApplyMaximizedBounds(nint hwnd, nint parameters)
    {
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor)) return;
        var bounds = CalculateMaximizedBounds(monitor.Monitor.Rectangle, monitor.WorkArea.Rectangle);
        var limits = Marshal.PtrToStructure<MinMaxInfo>(parameters);
        limits.MaxPosition = bounds.Location;
        limits.MaxSize = bounds.Size;
        Marshal.StructureToPtr(limits, parameters, false);
    }

    public static void ApplyBorderlessSnapChrome(Form form)
    {
        if (!form.IsHandleCreated)
        {
            return;
        }

        var darkMode = 1;
        _ = DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

        var cornerPreference = DwmwcpRound;
        _ = DwmSetWindowAttribute(form.Handle, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));

        var borderColor = DarkBorderColor;
        _ = DwmSetWindowAttribute(form.Handle, DwmwaBorderColor, ref borderColor, sizeof(uint));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public NativeRect(Rectangle value) => (Left, Top, Right, Bottom) =
            (value.Left, value.Top, value.Right, value.Bottom);
        public readonly Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Size MaxSize;
        public Point MaxPosition;
        public Size MinTrackSize, MaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint attributeValue, int attributeSize);
}
