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

    // One line per item in the user's order. An item with a chosen source shows that raw field on
    // that device under the item's own label; when it cannot be read the line shows no value
    // instead of falling back to the part address.
    internal static string[] Lines(OverlayTarget target, FrontendOverlayPresentation presentation, string language) =>
        presentation.Metrics.Select(id => presentation.SourceFor(id) is { } source
                ? SourceMetric(id, source, target)
                : target.Metrics.FirstOrDefault(metric => metric.MetricId.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Where(static metric => metric is not null)
            .Select(metric => FormatLine(metric!, language)).ToArray();

    private static OverlayMetric SourceMetric(string id, FrontendOverlayMetricSource source, OverlayTarget target)
    {
        var field = target.Fields.FirstOrDefault(device => device.DeviceIdentity == source.DeviceIdentity)?
            .Fields.FirstOrDefault(value => value.Name == source.FieldName);
        if (field is null || field.Status != OverlayFieldValue.CurrentStatus)
            return new(id, null, field?.Unit ?? "", "unavailable", null);
        var value = field.IntegerValue is { } integer
            && double.TryParse(integer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed : field.NumericValue;
        return new(id, value, field.Unit, value is null ? "unavailable" : "current", null);
    }

    internal static string FormatLine(OverlayMetric metric, string language)
    {
        var label = OverlayMetricLabels.ForMetric(metric.MetricId, language);
        if (metric.Value is not { } number || !double.IsFinite(number)) return $"{label}  —";
        var unit = metric.Unit;
        if (unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || unit.Equals("B", StringComparison.OrdinalIgnoreCase))
        {
            if (Math.Abs(number) >= 1_073_741_824) { number /= 1_073_741_824; unit = "GiB"; }
            else if (Math.Abs(number) >= 1_048_576) { number /= 1_048_576; unit = "MiB"; }
        }
        return $"{label}  {number.ToString("0.#", CultureInfo.CurrentCulture)} {unit}".TrimEnd();
    }
}
