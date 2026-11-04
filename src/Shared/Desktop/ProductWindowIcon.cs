using System.Runtime.InteropServices;

namespace ResourceManager.Shared.Desktop;

/// <summary>Owns the running product's icons at one native window's current DPI.</summary>
public sealed class ProductWindowIcon : IDisposable
{
    private nint largeIcon;
    private nint smallIcon;
    private uint currentDpi;
    private bool disposed;

    private ProductWindowIcon() { }

    public static ProductWindowIcon Apply(nint window)
    {
        var icons = new ProductWindowIcon();
        icons.Refresh(window);
        return icons;
    }

    public void Refresh(nint window)
    {
        if (disposed) return;
        // RuntimeRead: Event (window creation and Windows DPI-change notifications).
        var dpi = GetDpiForWindow(window);
        if (dpi == 0 || dpi == currentDpi) return;
        var instance = GetModuleHandleW(null);
        nint nextSmall = 0, nextLarge = 0;
        ResourceNameCallback firstGroup = (_, _, name, _) =>
        {
            // Use the name while the enumeration callback owns it, including named resources.
            nextSmall = LoadImageW(instance, name, 1 /* IMAGE_ICON */,
                GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi), GetSystemMetricsForDpi(50 /* SM_CYSMICON */, dpi), 0);
            if (nextSmall != 0)
                nextLarge = LoadImageW(instance, name, 1 /* IMAGE_ICON */,
                    GetSystemMetricsForDpi(11 /* SM_CXICON */, dpi), GetSystemMetricsForDpi(12 /* SM_CYICON */, dpi), 0);
            return false; // The first group is Explorer / shortcut icon index zero.
        };
        EnumResourceNamesW(instance, 14 /* RT_GROUP_ICON */, firstGroup, 0);
        if (nextSmall == 0 || nextLarge == 0)
        {
            if (nextSmall != 0) DestroyIcon(nextSmall);
            if (nextLarge != 0 && nextLarge != nextSmall) DestroyIcon(nextLarge);
            return;
        }
        var previousLarge = largeIcon;
        var previousSmall = smallIcon;
        largeIcon = nextLarge;
        smallIcon = nextSmall;
        currentDpi = dpi;
        SendMessageW(window, 0x0080 /* WM_SETICON */, 1 /* ICON_BIG */, nextLarge);
        SendMessageW(window, 0x0080 /* WM_SETICON */, 0 /* ICON_SMALL */, nextSmall);
        // Both consumers have switched before their previous, nonshared handles are freed.
        if (previousLarge != 0) DestroyIcon(previousLarge);
        if (previousSmall != 0 && previousSmall != previousLarge) DestroyIcon(previousSmall);
    }

    public void Dispose()
    {
        disposed = true;
        var large = Interlocked.Exchange(ref largeIcon, 0);
        var small = Interlocked.Exchange(ref smallIcon, 0);
        if (large != 0) DestroyIcon(large);
        if (small != 0 && small != large) DestroyIcon(small);
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ResourceNameCallback(nint module, nint type, nint name, nint parameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumResourceNamesW(nint module, nint type, ResourceNameCallback callback, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint LoadImageW(nint instance, nint name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(nint window, uint message, nuint parameter, nint value);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
