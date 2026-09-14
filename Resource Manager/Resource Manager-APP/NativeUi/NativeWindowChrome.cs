using System.Runtime.InteropServices;

namespace ResourceManager.NativeUi;

internal static class NativeWindowChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;
    private const uint DarkBorderColor = 0x00242220;

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint attributeValue, int attributeSize);
}
