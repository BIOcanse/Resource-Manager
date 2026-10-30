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
    private static readonly FrontendOverlayPresentation Absolute = new() { FontSizePx = 16, Items = [Item("frames", "frames.fps")] };

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
        var target = new OverlayTarget("test", 42, 1337, new("test", true, "external")) { Items = [Fps()] };
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
            new("test", true, "external")) { Items = [Fps()] };
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
    public void StreamJsonAndNineItemLabelsReachTheRenderer()
    {
        // Snapshot version 3: the subscription's item values sit next to its targets.
        const string json = """
            {"version":3,"capturedAt":"2026-10-30T08:00:00Z","targets":[{"softwareId":"test",
              "processId":42,"processStartKey":1337,"metrics":[],"frameStreams":[]}],
             "fields":[],
             "items":[{"kind":"frames","id":"frames.fps","unit":"FPS","selectedSource":"auto","actualSource":"present-mon",
               "intervalMs":500,"intervalOrigin":"profile-default","syncPrecision":"mid","value":100,"state":"current",
               "reason":null,"observedAt":"2026-10-30T08:00:00Z"}]}
            """;
        var snapshot = JsonSerializer.Deserialize<OverlaySnapshot>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var item = Assert.Single(snapshot!.Items);
        Assert.Equal(new OverlayItemValue("frames", "frames.fps", "FPS", "auto", "present-mon", 500, "profile-default", "mid",
            100, "current", null, DateTimeOffset.Parse("2026-10-30T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture)), item);
        Assert.Single(snapshot.Targets);
        // frames.fps keeps the old overlay's FPS name in every language.
        var expectedLabels = new Dictionary<string, string>
        {
            ["zh-CN"] = "目标帧率", ["zh-TW"] = "目標影格率", ["en-US"] = "Target FPS",
            ["ja-JP"] = "対象の FPS", ["ko-KR"] = "대상 FPS", ["fr-FR"] = "FPS du logiciel cible",
            ["de-DE"] = "FPS der Zielsoftware", ["es-ES"] = "FPS del software de destino",
            ["ru-RU"] = "FPS целевой программы"
        };
        Assert.Equal(AppLanguage.SupportedIds.Order(), expectedLabels.Keys.Order());
        foreach (var language in AppLanguage.SupportedIds)
        {
            Assert.Equal($"{expectedLabels[language]}  100 FPS", OverlayBitmapRenderer.FormatLine(item, language));
            // Every reason the backend sends has text in every language.
            foreach (var reason in new[] { "target-not-running", "frame-events-lost", "no-frame-data", "metric-unavailable",
                "process-cpu-unavailable", "process-metric-unavailable" })
                Assert.NotEqual(reason, OverlayItemLabels.ForReason(reason, language));
        }
        Assert.DoesNotContain("labels", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LinesFollowTheStoredOrderAndShowTheReasonWhenAnItemHasNoValue()
    {
        const string language = "en-US";
        var target = new OverlayTarget("test", 42, 1337, new("test", true, "external"))
        {
            // The stream's order is not the drawing order: the stored items are.
            Items =
            [
                Value("software", "cpu.usage", "%", 12.34),
                Value("frames", "frames.low1", "FPS", null, "frame-events-lost"),
                Value("metric", "gpu.0.temperature", "°C", 61),
                Value("frames", "frames.present", "ms", 8.25),
                Value("software", "process_all_cpu_usage_actual", "%", null, "target-not-running"),
                Value("metric", "memory.available", "B", 3d * 1_073_741_824),
                Value("metric", "made.up", null, null, "some-new-reason"),
                Value("metric", "no.reason", null, null, null)
            ]
        };
        var presentation = new FrontendOverlayPresentation
        {
            Items =
            [
                Item("frames", "frames.present"), Item("frames", "frames.low1"), Item("metric", "gpu.0.temperature", "gpu_amd_edge_temperature_celsius@gpu|0"),
                Item("software", "CPU.Usage"), Item("software", "process_all_cpu_usage_actual"), Item("metric", "memory.available"),
                Item("metric", "made.up"), Item("metric", "no.reason"),
                // Not answered by the stream yet (the subscription is being replaced): not drawn.
                Item("frames", "frames.fps")
            ]
        };
        Assert.Equal(new[]
        {
            $"Target frame time  {8.25.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)} ms",
            "Target 1% low  Frame events lost",
            "GPU0 temperature  61 °C",
            // The four software items the old overlay had keep its names.
            $"Target process CPU  {12.34.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)} %",
            "Target process · Process CPU usage  Target not running",
            // Bytes keep the old scaling to GiB / MiB.
            $"{OverlayItemLabels.ForItem("metric", "memory.available", language)}  3 GiB",
            // An unknown reason is shown as its code; no reason at all is a dash.
            "made.up  some-new-reason",
            "no.reason  —"
        }, OverlayBitmapRenderer.Lines(target, presentation, language));
        // The same id under another kind is another item.
        Assert.Empty(OverlayBitmapRenderer.Lines(target, new() { Items = [Item("metric", "cpu.usage")] }, language));
    }

    private static FrontendOverlayItem Item(string kind, string id, string source = "auto") =>
        new(kind, id, source, null, "mid");

    private static OverlayItemValue Value(string kind, string id, string? unit, double? value, string? reason = null) =>
        new(kind, id, unit, "auto", value is null ? null : "auto", 500, "profile-default", "mid", value,
            value is null ? "unavailable" : "current", reason, value is null ? null : DateTimeOffset.UnixEpoch);

    internal static OverlayItemValue Fps() => Value("frames", "frames.fps", "FPS", 100);
}
