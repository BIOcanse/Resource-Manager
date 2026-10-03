using System.Drawing;
using System.Runtime.InteropServices;

namespace ResourceManager.NativeUi.Overlay;

internal sealed class ExternalOverlayWindow : NativeWindow, IDisposable
{
    private const int ExtendedStyle = 0x00080000 | 0x00000020 | 0x00000008 | 0x08000000 | 0x00000080;
    private const int PopupStyle = unchecked((int)0x80000000);
    private const uint LayeredAlpha = 0x00000002;
    private const uint NoActivateShow = 0x0010 | 0x0004 | 0x0040;
    private const uint NoActivateHide = 0x0080 | 0x0010 | 0x0004;
    private static readonly nint TopMost = new(-1);
    private bool shown;
    internal bool IsShown => shown;

    public ExternalOverlayWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = string.Empty,
            Style = PopupStyle,
            ExStyle = ExtendedStyle,
            X = 0, Y = 0, Width = 1, Height = 1
        });
    }

    public void Update(OverlayImage image, Rectangle bounds)
    {
        if (Handle == 0) return;
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = image.Width,
                    Height = -image.Height, Planes = 1, BitCount = 32, Compression = 0
                }
            };
            bitmap = CreateDIBSection(memory, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Marshal.Copy(image.Pixels, 0, bits, image.Pixels.Length);
            previous = SelectObject(memory, bitmap);
            var origin = new NativePoint();
            var destination = new NativePoint(bounds.Left, bounds.Top);
            var size = new NativeSize(image.Width, image.Height);
            var blend = new BlendFunction(0, 0, 255, 1);
            if (!UpdateLayeredWindow(Handle, screen, ref destination, ref size,
                memory, ref origin, 0, ref blend, LayeredAlpha))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Show(bounds);
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }

    public void Move(Rectangle bounds)
    {
        if (Handle != 0)
            shown = SetWindowPos(Handle, TopMost, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                NoActivateShow);
    }

    private void Show(Rectangle bounds)
    {
        shown = SetWindowPos(Handle, TopMost, bounds.X, bounds.Y,
            bounds.Width, bounds.Height, NoActivateShow);
    }

    public void Hide()
    {
        if (!shown || Handle == 0) return;
        SetWindowPos(Handle, 0, 0, 0, 0, 0, NoActivateHide);
        shown = false;
    }

    public void Dispose()
    {
        Hide();
        if (Handle != 0) DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y)
    { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize(int width, int height)
    { public int Width = width; public int Height = height; }
    [StructLayout(LayoutKind.Sequential)] private struct BlendFunction(byte operation, byte flags, byte alpha, byte format)
    { public byte Operation = operation; public byte Flags = flags; public byte Alpha = alpha; public byte Format = format; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width; public int Height; public ushort Planes;
        public ushort BitCount; public uint Compression; public uint SizeImage;
        public int XPelsPerMeter; public int YPelsPerMeter; public uint ClrUsed; public uint ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    { public BitmapInfoHeader Header; public uint Colors; }
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(
        nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(
        nint window, nint screen, ref NativePoint destination, ref NativeSize size,
        nint source, ref NativePoint origin, uint colorKey, ref BlendFunction blend, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        nint window, nint after, int x, int y, int width, int height, uint flags);
}
