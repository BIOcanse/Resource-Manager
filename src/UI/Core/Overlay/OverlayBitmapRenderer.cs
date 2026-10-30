using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using ResourceManager.NativeUi.Localization;
using ResourceManager.NativeUi.Preferences;
using ResourceManager.Shared.Overlay;

namespace ResourceManager.NativeUi.Overlay;

internal sealed record OverlayImage(int Width, int Height, byte[] Pixels)
{
    public int Stride => checked(Width * 4);
}

internal static class OverlayBitmapRenderer
{
    public static OverlayImage? Render(OverlayTarget target, Size targetSize, FrontendOverlayPresentation presentation)
    {
        if (targetSize.Width <= 0 || targetSize.Height <= 0
            || targetSize.Width > 8192 || targetSize.Height > 8192
            || (long)targetSize.Width * targetSize.Height * 4 > PerformanceOverlaySharedMemory.MaximumPixelBytes)
            return null;

        var lines = Lines(target, presentation, NativeUiText.Language);
        if (lines.Length == 0) return null;
        using var bitmap = new Bitmap(targetSize.Width, targetSize.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        SizeF Measure(string line, int size)
        {
            using var font = new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Pixel);
            return graphics.MeasureString(line, font, int.MaxValue, format);
        }
        var layout = OverlayLayout.Calculate(targetSize, presentation, lines, Measure);
        if (layout.FontSizePx <= 0) return null;
        using var background = new SolidBrush(Color.FromArgb(168, 0, 0, 0));
        using var foreground = new SolidBrush(Color.White);
        using var drawFont = new Font("Segoe UI", layout.FontSizePx, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.FillRectangle(background, layout.BackgroundBounds);
        graphics.SetClip(layout.BackgroundBounds);
        for (var index = 0; index < lines.Length; index++)
            graphics.DrawString(lines[index], drawFont, foreground, layout.LineOrigins[index], format);

        var stride = checked(targetSize.Width * 4);
        var pixels = new byte[checked(stride * targetSize.Height)];
        var data = bitmap.LockBits(new Rectangle(Point.Empty, targetSize), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            for (var row = 0; row < targetSize.Height; row++)
                Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * stride, stride);
        }
        finally { bitmap.UnlockBits(data); }
        return new OverlayImage(targetSize.Width, targetSize.Height, pixels);
    }

    // One line per item in the user's stored order: the item's name, then its value and unit, or why it has no value.
    // An item the stream has not answered yet (the preferences changed and the new subscription has not pushed) is
    // not drawn until it arrives.
    internal static string[] Lines(OverlayTarget target, FrontendOverlayPresentation presentation, string language) =>
        presentation.Items.Select(item => target.Items.FirstOrDefault(value =>
                value.Kind.Equals(item.Kind, StringComparison.Ordinal) && value.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)))
            .OfType<OverlayItemValue>()
            .Select(value => FormatLine(value, language)).ToArray();

    // Numbers keep the old overlay's formatting: one decimal at most in the current culture, bytes scaled to MiB or GiB.
    // A value that is missing or not finite shows the reason text, or a dash when the backend gave no reason.
    internal static string FormatLine(OverlayItemValue item, string language)
    {
        var label = OverlayItemLabels.ForItem(item.Kind, item.Id, language);
        if (item.Value is not { } number || !double.IsFinite(number))
            return item.Reason is { Length: > 0 } reason ? $"{label}  {OverlayItemLabels.ForReason(reason, language)}" : $"{label}  —";
        var unit = item.Unit ?? string.Empty;
        if (unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || unit.Equals("B", StringComparison.OrdinalIgnoreCase))
        {
            if (Math.Abs(number) >= 1_073_741_824) { number /= 1_073_741_824; unit = "GiB"; }
            else if (Math.Abs(number) >= 1_048_576) { number /= 1_048_576; unit = "MiB"; }
        }
        return $"{label}  {number.ToString("0.#", CultureInfo.CurrentCulture)} {unit}".TrimEnd();
    }
}
