using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using System.Text.Json;
using ResourceManager.Shared.Localization;
using ResourceManager.NativeUi.Overlay;
using ResourceManager.NativeUi.Preferences;

namespace Resource_Manager_APP.Tests;

public sealed class PerformanceOverlayNativeUiTests
{
    private static readonly FrontendOverlayPresentation Absolute = new() { FontSizePx = 16, Metrics = ["target.fps"] };

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
        var target = new OverlayTarget("test", 42, 1337, new("test", true, "external"), [Metric()]);
        var image = Assert.IsType<OverlayImage>(OverlayBitmapRenderer.Render(target, new Size(300, 120), Absolute));
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
        var target = new OverlayTarget("test", process.Id, id,
            new("test", true, "external"), [Metric()]);
        using var dispatcher = new Control();
        using var coordinator = new PerformanceOverlayCoordinator(dispatcher);
        coordinator.ApplySnapshot(new OverlaySnapshot(3, [target]));
        Assert.Equal(1, coordinator.TargetCount);
        Assert.True(coordinator.WindowRefreshTimerEnabled);
        coordinator.ApplySnapshot(new OverlaySnapshot(3, []));
        Assert.Equal(0, coordinator.TargetCount);
        Assert.False(coordinator.WindowRefreshTimerEnabled);
        coordinator.ApplySnapshot(new OverlaySnapshot(3, [target with { ProcessStartKey = id + 1 }]));
        Assert.Equal(0, coordinator.TargetCount);
        Assert.False(coordinator.WindowRefreshTimerEnabled);
        coordinator.ApplySnapshot(new OverlaySnapshot(3, [target]));
        Assert.True(coordinator.WindowRefreshTimerEnabled);
        coordinator.ApplySnapshot(new OverlaySnapshot(2, [target])); // Obsolete envelope must clear the active target.
        Assert.Equal(0, coordinator.TargetCount);
        Assert.False(coordinator.WindowRefreshTimerEnabled);
        coordinator.ApplySnapshot(new OverlaySnapshot(3, [target]));
        Assert.True(coordinator.WindowRefreshTimerEnabled);
        coordinator.Stop();
        Assert.False(coordinator.WindowRefreshTimerEnabled);
    }

    [Fact]
    public void StreamJsonAndNineMetricLabelsReachTheRenderer()
    {
        const string json = """
            {"version":3,"targets":[{"softwareId":"test",
              "processId":42,"processStartKey":1337,
              "metrics":[{"metricId":"target.fps","value":100,"unit":"FPS",
                "state":"current","reason":null}]}]}
            """;
        var snapshot = JsonSerializer.Deserialize<OverlaySnapshot>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var metric = Assert.Single(Assert.Single(snapshot!.Targets).Metrics);
        var expectedLabels = new Dictionary<string, string>
        {
            ["zh-CN"] = "目标帧率", ["zh-TW"] = "目標影格率", ["en-US"] = "Target FPS",
            ["ja-JP"] = "対象の FPS", ["ko-KR"] = "대상 FPS", ["fr-FR"] = "FPS du logiciel cible",
            ["de-DE"] = "FPS der Zielsoftware", ["es-ES"] = "FPS del software de destino",
            ["ru-RU"] = "FPS целевой программы"
        };
        Assert.Equal(AppLanguage.SupportedIds.Order(), expectedLabels.Keys.Order());
        foreach (var language in AppLanguage.SupportedIds)
            Assert.Equal($"{expectedLabels[language]}  100 FPS", OverlayBitmapRenderer.FormatLine(metric, language));
        Assert.DoesNotContain("labels", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChosenSourceShowsItsRawFieldUnderTheItemLabelAndNeverThePartAddress()
    {
        // The overlay stream's raw-field values (fields=) and the item's chosen field on one device.
        const string json = """
            {"version":2,"targets":[{"softwareId":"test","processId":42,"processStartKey":1337,
              "settings":{"softwareId":"test","enabled":true,"mode":"external"},
              "metrics":[{"metricId":"cpu.temperature","value":70,"unit":"°C","state":"current","reason":null}]}],
             "fields":[{"deviceIdentity":"gpu|1","device":{"kind":"gpu_adapter"},"fields":[
               {"name":"gpu_amd_edge_temperature_celsius","unit":"°C","numericValue":50,"integerValue":null,"status":1,
                "sourceId":"amd.adlx","valueKind":"float64"},
               {"name":"memory_all_physical_available_bytes","unit":"B","numericValue":null,"integerValue":"9007199254740993",
                "status":1,"sourceId":"windows.memory","valueKind":"unsigned64"}]},
               {"deviceIdentity":"gpu|0","fields":[{"name":"gpu_amd_edge_temperature_celsius","unit":"°C","numericValue":null,
                "integerValue":null,"status":3,"sourceId":"amd.adlx","valueKind":"float64"}]}]}
            """;
        var snapshot = JsonSerializer.Deserialize<OverlaySnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var target = Assert.Single(snapshot.Targets) with { Fields = snapshot.Fields };
        FrontendOverlayPresentation Choose(string device, string field) => new()
        {
            Metrics = ["cpu.temperature"],
            MetricSources = new Dictionary<string, FrontendOverlayMetricSource> { ["cpu.temperature"] = new(field, device) }
        };
        const string language = "en-US";
        Assert.Equal([OverlayBitmapRenderer.FormatLine(new("cpu.temperature", 50, "°C", "current", null), language)],
            OverlayBitmapRenderer.Lines(target, Choose("gpu|1", "gpu_amd_edge_temperature_celsius"), language));
        // The same field on another device is that device's own value: unavailable there, not borrowed.
        var unavailable = OverlayBitmapRenderer.FormatLine(new("cpu.temperature", null, "°C", "unavailable", null), language);
        Assert.Equal([unavailable], OverlayBitmapRenderer.Lines(target, Choose("gpu|0", "gpu_amd_edge_temperature_celsius"), language));
        Assert.Equal([OverlayBitmapRenderer.FormatLine(new("cpu.temperature", null, "", "unavailable", null), language)],
            OverlayBitmapRenderer.Lines(target, Choose("gpu|9", "gpu_amd_edge_temperature_celsius"), language));
        // Exact integers are read from their decimal text.
        Assert.Equal([OverlayBitmapRenderer.FormatLine(new("cpu.temperature", 9007199254740993d, "B", "current", null), language)],
            OverlayBitmapRenderer.Lines(target, Choose("gpu|1", "memory_all_physical_available_bytes"), language));
        // Automatic keeps the part address value.
        Assert.Equal([OverlayBitmapRenderer.FormatLine(new("cpu.temperature", 70, "°C", "current", null), language)],
            OverlayBitmapRenderer.Lines(target, new() { Metrics = ["cpu.temperature"] }, language));
        Assert.DoesNotContain("70", OverlayBitmapRenderer.Lines(target, Choose("gpu|0", "gpu_amd_edge_temperature_celsius"), language)[0]);
    }

    internal static OverlayMetric Metric() => new("target.fps", 100, "FPS", "current", null);
}
