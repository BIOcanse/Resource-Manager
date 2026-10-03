using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using System.Text.Json;
using ResourceManager.Shared.Localization;
using ResourceManager.NativeUi.Overlay;

namespace Resource_Manager_APP.Tests;

public sealed class PerformanceOverlayNativeUiTests
{
    private static readonly OverlaySettings Absolute = new(
        "test", true, "external", ["target.fps"], "absolutePixels",
        16, 0.25, 0.2, "topLeft", 12);

    private static SizeF Measure(string line, int fontSize) =>
        new(line.Length * fontSize * 0.55f, fontSize * 1.3f);

    [Fact]
    public void AbsolutePixelsKeepsFontSizeWhenTargetResizes()
    {
        var lines = new[] { "FPS  100", "Frame time  10 ms" };
        var small = OverlayLayout.Calculate(new Size(640, 360), Absolute, lines, Measure);
        var large = OverlayLayout.Calculate(new Size(1920, 1080), Absolute, lines, Measure);
        Assert.Equal(16, small.FontSizePx);
        Assert.Equal(16, large.FontSizePx);
        Assert.Equal(small.TextBounds.Size, large.TextBounds.Size);
    }

    [Fact]
    public void WindowRatioRemeasuresFontToFitRegionAfterResize()
    {
        var settings = Absolute with { SizeMode = "windowRatio" };
        var lines = new[] { "FPS  100", "Frame time  10 ms" };
        var small = OverlayLayout.Calculate(new Size(640, 360), settings, lines, Measure);
        var large = OverlayLayout.Calculate(new Size(1280, 720), settings, lines, Measure);
        Assert.True(small.FontSizePx > 0);
        Assert.True(large.FontSizePx > small.FontSizePx);
        Assert.True(small.BackgroundBounds.Width <= 640 * settings.RegionWidthRatio);
        Assert.True(small.BackgroundBounds.Height <= 360 * settings.RegionHeightRatio);
        Assert.True(large.BackgroundBounds.Width <= 1280 * settings.RegionWidthRatio);
        Assert.True(large.BackgroundBounds.Height <= 720 * settings.RegionHeightRatio);
        Assert.True(large.TextBounds.Width > small.TextBounds.Width);
        var crowded = OverlayLayout.Calculate(new Size(640, 360),
            settings with { RegionWidthRatio = 0.05, RegionHeightRatio = 0.05 },
            Enumerable.Repeat("A very long selected metric", 64).ToArray(), Measure);
        Assert.True(crowded.BackgroundBounds.Width <= 640 * 0.05);
        Assert.True(crowded.BackgroundBounds.Height <= 360 * 0.05);
    }

    [Fact]
    public void AnchorsRespectMarginAndClientBounds()
    {
        var lines = new[] { "FPS  100" };
        var upper = OverlayLayout.Calculate(new Size(640, 360), Absolute, lines, Measure);
        var lower = OverlayLayout.Calculate(new Size(640, 360),
            Absolute with { Anchor = "bottomRight" }, lines, Measure);
        Assert.Equal(12, upper.BackgroundBounds.Left);
        Assert.Equal(12, upper.BackgroundBounds.Top);
        Assert.Equal(640 - 12, lower.BackgroundBounds.Right);
        Assert.Equal(360 - 12, lower.BackgroundBounds.Bottom);
    }

    [Fact]
    public void RendererOutputsGrayscalePremultipliedBgra()
    {
        var target = new OverlayTarget("test", "Test", 42, 1337, Absolute, [Metric()]);
        var image = Assert.IsType<OverlayImage>(OverlayBitmapRenderer.Render(target, new Size(300, 120)));
        Assert.Equal(300 * 120 * 4, image.Pixels.Length);
        var translucent = false;
        for (var offset = 0; offset < image.Pixels.Length; offset += 4)
        {
            var blue = image.Pixels[offset];
            var green = image.Pixels[offset + 1];
            var red = image.Pixels[offset + 2];
            var alpha = image.Pixels[offset + 3];
            Assert.Equal(blue, green);
            Assert.Equal(green, red);
            Assert.True(red <= alpha);
            translucent |= alpha is > 0 and < 255;
        }
        Assert.True(translucent);
    }

    [Fact]
    public void SectionWriterPublishesOddEvenGenerationsAndCanDisable()
    {
        using var process = Process.GetCurrentProcess();
        var id = new OverlayTargetId(process.Id, checked((ulong)process.StartTime.ToFileTimeUtc()));
        using var section = new InjectedOverlaySection(id, 0);
        var image = new OverlayImage(2, 2, Enumerable.Repeat((byte)128, 16).ToArray());
        section.Publish(image);
        Assert.Equal(2, section.Generation);
        Assert.True(section.Enabled);
        section.Publish(image);
        Assert.Equal(4, section.Generation);
        using (var resumed = new InjectedOverlaySection(id, 0))
        {
            Assert.Equal(4, resumed.Generation);
            Assert.False(resumed.Enabled);
            resumed.Publish(image);
            Assert.Equal(6, resumed.Generation);
        }
        section.Disable();
        Assert.False(section.Enabled);
    }

    [Fact]
    public void StreamSnapshotsAddAndRemoveTargetsByProcessStartKey()
    {
        using var process = Process.GetCurrentProcess();
        var id = checked((ulong)process.StartTime.ToFileTimeUtc());
        var target = new OverlayTarget("test", "Test", process.Id, id,
            Absolute, [Metric()]);
        using var dispatcher = new Control();
        using var coordinator = new PerformanceOverlayCoordinator(dispatcher);
        coordinator.ApplySnapshot(new OverlaySnapshot(1, [target]));
        Assert.Equal(1, coordinator.TargetCount);
        coordinator.ApplySnapshot(new OverlaySnapshot(1, []));
        Assert.Equal(0, coordinator.TargetCount);
        coordinator.ApplySnapshot(new OverlaySnapshot(1, [target with { ProcessStartKey = id + 1 }]));
        Assert.Equal(0, coordinator.TargetCount);
    }

    [Fact]
    public void StreamJsonAndNineMetricLabelsReachTheRenderer()
    {
        const string json = """
            {"version":1,"targets":[{"softwareId":"test","displayName":"Test",
              "processId":42,"processStartKey":1337,
              "settings":{"softwareId":"test","enabled":true,"mode":"external",
                "metrics":["target.fps"],"sizeMode":"absolutePixels","fontSizePx":14,
                "regionWidthRatio":0.25,"regionHeightRatio":0.2,"anchor":"topLeft","marginPx":12},
              "metrics":[{"metricId":"target.fps","value":100,"unit":"FPS",
                "state":"current","reason":null,
                "labels":{"zh-CN":"zh-CN label","zh-TW":"zh-TW label",
                  "en-US":"en-US label","ja-JP":"ja-JP label","ko-KR":"ko-KR label",
                  "fr-FR":"fr-FR label","de-DE":"de-DE label",
                  "es-ES":"es-ES label","ru-RU":"ru-RU label"}}]}]}
            """;
        var snapshot = JsonSerializer.Deserialize<OverlaySnapshot>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var metric = Assert.Single(Assert.Single(snapshot!.Targets).Metrics);
        foreach (var language in AppLanguage.SupportedIds)
            Assert.StartsWith($"{language} label", OverlayBitmapRenderer.FormatLine(metric, language));
    }

    internal static OverlayMetric Metric() => new("target.fps", 100, "FPS", "current", null,
        new Dictionary<string, string> { ["en-US"] = "FPS", ["zh-CN"] = "帧率" });
}
