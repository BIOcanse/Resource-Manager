using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ResourceManager.NativeUi.Overlay;

internal readonly record struct OverlayWindowState(
    nint Handle, Rectangle ClientBounds, bool Visible, bool Foreground, bool Exited);

internal static class OverlayTargetWindow
{
    private delegate bool EnumWindowsCallback(nint window, nint parameter);
    private static readonly EnumWindowsCallback EnumerateCallback = VisitWindow;

    public static OverlayWindowState Read(OverlayTargetId id, nint previousWindow)
    {
        if (!ProcessMatches(id)) return new(0, Rectangle.Empty, false, false, true);
        var window = IsCandidate(previousWindow, id.ProcessId)
            ? previousWindow : FindMainWindow(id.ProcessId);
        if (window == 0) return new(0, Rectangle.Empty, false, false, false);
        var client = GetClientBounds(window);
        var foreground = GetAncestor(GetForegroundWindow(), 2) == window;
        return new(window, client,
            IsWindowVisible(window) && !IsIconic(window) && client.Width > 0 && client.Height > 0,
            foreground, false);
    }

    private static bool ProcessMatches(OverlayTargetId id)
    {
        try
        {
            using var process = Process.GetProcessById(id.ProcessId);
            return !process.HasExited
                && checked((ulong)process.StartTime.ToFileTimeUtc()) == id.ProcessStartKey;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or OverflowException)
        {
            return false;
        }
    }

    private static nint FindMainWindow(int processId)
    {
        var search = new WindowSearch(processId);
        var handle = GCHandle.Alloc(search);
        try { EnumWindows(EnumerateCallback, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }
        return search.Window;
    }

    private static bool VisitWindow(nint window, nint parameter)
    {
        var search = (WindowSearch)GCHandle.FromIntPtr(parameter).Target!;
        if (!IsCandidate(window, search.ProcessId)) return true;
        var bounds = GetClientBounds(window);
        var area = (long)bounds.Width * bounds.Height;
        if (area > search.Area)
        {
            search.Window = window;
            search.Area = area;
        }
        return true;
    }

    private static bool IsCandidate(nint window, int processId)
    {
        if (window == 0 || !IsWindow(window) || !IsWindowVisible(window)
            || GetWindow(window, 4) != 0) return false;
        GetWindowThreadProcessId(window, out var owner);
        return owner == (uint)processId;
    }

    private static Rectangle GetClientBounds(nint window)
    {
        if (!GetClientRect(window, out var client)) return Rectangle.Empty;
        var origin = new NativePoint();
        if (!ClientToScreen(window, ref origin)) return Rectangle.Empty;
        return new Rectangle(origin.X, origin.Y,
            Math.Max(0, client.Right - client.Left), Math.Max(0, client.Bottom - client.Top));
    }

    private sealed class WindowSearch(int processId)
    {
        public int ProcessId { get; } = processId;
        public nint Window { get; set; }
        public long Area { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
}
