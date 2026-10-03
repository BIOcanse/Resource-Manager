using System.Drawing;

namespace ResourceManager.NativeUi.Overlay;

internal readonly record struct OverlayLayoutResult(
    int FontSizePx, Rectangle TextBounds, Rectangle BackgroundBounds,
    IReadOnlyList<PointF> LineOrigins);

internal static class OverlayLayout
{
    private const int Padding = 6;

    public static OverlayLayoutResult Calculate(
        Size target, OverlaySettings settings, IReadOnlyList<string> lines,
        Func<string, int, SizeF> measure)
    {
        if (target.Width <= 0 || target.Height <= 0 || lines.Count == 0)
            return new OverlayLayoutResult(0, Rectangle.Empty, Rectangle.Empty, []);

        var availableWidth = Math.Max(1, target.Width - 2 * settings.MarginPx);
        var availableHeight = Math.Max(1, target.Height - 2 * settings.MarginPx);
        if (settings.SizeMode == "windowRatio")
        {
            availableWidth = Math.Min(availableWidth,
                Math.Max(1, (int)Math.Floor(target.Width * settings.RegionWidthRatio)));
            availableHeight = Math.Min(availableHeight,
                Math.Max(1, (int)Math.Floor(target.Height * settings.RegionHeightRatio)));
        }

        int fontSize;
        if (settings.SizeMode == "windowRatio")
        {
            var low = 1;
            var high = 256;
            fontSize = 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var measured = Measure(lines, middle, measure);
                if (measured.Width + 2 * Padding <= availableWidth
                    && measured.Height + 2 * Padding <= availableHeight)
                {
                    fontSize = middle;
                    low = middle + 1;
                }
                else high = middle - 1;
            }
        }
        else fontSize = Math.Max(1, settings.FontSizePx);

        var sizes = lines.Select(line => measure(line, fontSize)).ToArray();
        var textWidth = (int)Math.Ceiling(sizes.Max(static size => size.Width));
        var textHeight = (int)Math.Ceiling(sizes.Sum(static size => size.Height));
        var blockWidth = Math.Min(availableWidth, textWidth + 2 * Padding);
        var blockHeight = Math.Min(availableHeight, textHeight + 2 * Padding);
        var right = settings.Anchor.EndsWith("Right", StringComparison.Ordinal);
        var bottom = settings.Anchor.StartsWith("bottom", StringComparison.Ordinal);
        var x = right ? target.Width - blockWidth - settings.MarginPx : settings.MarginPx;
        var y = bottom ? target.Height - blockHeight - settings.MarginPx : settings.MarginPx;
        x = Math.Clamp(x, 0, Math.Max(0, target.Width - blockWidth));
        y = Math.Clamp(y, 0, Math.Max(0, target.Height - blockHeight));
        var origins = new PointF[lines.Count];
        var lineY = (float)(y + Padding);
        for (var index = 0; index < lines.Count; index++)
        {
            origins[index] = new PointF(x + Padding, lineY);
            lineY += sizes[index].Height;
        }
        return new OverlayLayoutResult(fontSize,
            new Rectangle(x + Padding, y + Padding, textWidth, textHeight),
            new Rectangle(x, y, blockWidth, blockHeight), origins);
    }

    private static SizeF Measure(
        IReadOnlyList<string> lines, int fontSize, Func<string, int, SizeF> measure)
    {
        var width = 0f;
        var height = 0f;
        foreach (var line in lines)
        {
            var size = measure(line, fontSize);
            width = Math.Max(width, size.Width);
            height += size.Height;
        }
        return new SizeF(width, height);
    }
}
