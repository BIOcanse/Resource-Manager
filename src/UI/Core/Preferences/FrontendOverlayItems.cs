using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ResourceManager.NativeUi.Preferences;

// 浮层的一项（docs/targeted_recording_views_2026-10-30.md §3.9）：和定向录制同一个项合同。Source 是 "auto" 或目录里的来源
// （"字段@设备身份" 等）；IntervalMs 留空表示用目录的自然间隔或浮层订阅的默认间隔；帧项不带间隔。SyncPrecision 只存、只回显。
// 浮窗设置 2026-10-31 起归后端设置 performanceOverlay，后端做同一份校验。这里只剩旧偏好缓存的浮窗条目写进后端时用的
// 换算和检查（Migration frontend-preferences-overlays-to-backend-v1，FrontendPreferencesOwner.OverlayMigration.cs），
// 和那个迁移一起删除。
internal sealed record FrontendOverlayItem(string Kind, string Id, string Source, int? IntervalMs, string SyncPrecision);

internal static class FrontendOverlayItems
{
    // 与后端 TargetedRecordingItemReader.MaximumOverlayItems 相同。
    internal const int MaximumItems = 64;
    internal const int MinimumIntervalMs = 100;
    internal const int MaximumIntervalMs = 60_000;
    internal const string AutoSource = "auto";
    internal const string DefaultSyncPrecision = "mid";
    internal static readonly string[] Kinds = ["frames", "metric", "software"];
    internal static readonly string[] SyncPrecisions = ["low", "mid", "high"];

    // 一组项不合规的原因；合规返回 null。检查顺序和前端 itemListIssue 相同，原因码也相同。
    internal static string? Issue(IReadOnlyList<FrontendOverlayItem>? items)
    {
        if (items is null) return "kind";
        if (items.Count > MaximumItems) return "too-many";
        var seen = new HashSet<(string Kind, string Id)>();
        foreach (var item in items)
        {
            if (item is null || !Kinds.Contains(item.Kind, StringComparer.Ordinal)) return "kind";
            if (string.IsNullOrWhiteSpace(item.Id)) return "id";
            if (string.IsNullOrWhiteSpace(item.Source)) return "source";
            if (!SyncPrecisions.Contains(item.SyncPrecision, StringComparer.Ordinal)) return "precision";
            if (item.Kind == "frames" && item.IntervalMs is not null) return "frames-interval";
            if (item.IntervalMs is < MinimumIntervalMs or > MaximumIntervalMs) return "interval";
            if (!seen.Add((item.Kind, item.Id.ToLowerInvariant()))) return "duplicate";
        }
        return null;
    }

    // Migration frontend-preferences-overlay-items-v1. 2026-10-30 之前的浮层按指标 id 存 metrics（顺序）和
    // metricSources（按 id 存 {fieldName, deviceIdentity}）。读到时换成 items，保持原来的顺序；换出来的项间隔留空、
    // 同步精度中。旧 id 到项的对应（PerformanceOverlayMetricIds）：
    // - target.fps → frames.fps，target.frameTime → frames.present，target.onePercentLow → frames.low1，
    //   target.pointOnePercentLow → frames.low01（帧项，来源自动）；
    // - target.cpu → software cpu.usage，target.memory → software memory.usage（来源自动）；
    // - target.gpu → software gpu.usage，target.vram → software gpu.vram（目标在所有 GPU 上的合计，来源自动）；
    // - 其余 id 是整机和硬件指标 → metric 同一 id，选过来源时来源写成 "字段@设备身份"，没选是 "auto"。
    // 删除条件：下一个不再读旧版本缓存的发行，最晚和 frontend-preferences-overlays-to-backend-v1 一起删除。
    private static readonly Dictionary<string, (string Kind, string Id)> LegacyTargetItems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["target.fps"] = ("frames", "frames.fps"),
        ["target.frameTime"] = ("frames", "frames.present"),
        ["target.onePercentLow"] = ("frames", "frames.low1"),
        ["target.pointOnePercentLow"] = ("frames", "frames.low01"),
        ["target.cpu"] = ("software", "cpu.usage"),
        ["target.memory"] = ("software", "memory.usage"),
        ["target.gpu"] = ("software", "gpu.usage"),
        ["target.vram"] = ("software", "gpu.vram")
    };

    internal static (IReadOnlyList<FrontendOverlayItem> Items, IReadOnlyList<string> Dropped) ConvertLegacy(
        JsonArray? metrics, JsonObject? metricSources)
    {
        var items = new List<FrontendOverlayItem>();
        var dropped = new List<string>();
        var seen = new HashSet<(string Kind, string Id)>();
        foreach (var node in metrics ?? [])
        {
            if (node is not JsonValue text || !text.TryGetValue<string>(out var value) || string.IsNullOrWhiteSpace(value)) continue;
            var kind = "metric";
            var id = value;
            var source = AutoSource;
            if (LegacyTargetItems.TryGetValue(value, out var target))
            {
                (kind, id) = target;
            }
            else if (metricSources?[value] is JsonObject chosen
                && chosen["fieldName"] is JsonValue field && field.TryGetValue<string>(out var fieldName) && fieldName.Length > 0
                && chosen["deviceIdentity"] is JsonValue device && device.TryGetValue<string>(out var deviceIdentity) && deviceIdentity.Length > 0)
                source = $"{fieldName}@{deviceIdentity}";
            if (items.Count >= MaximumItems || !seen.Add((kind, id.ToLowerInvariant()))) { dropped.Add(value); continue; }
            items.Add(new FrontendOverlayItem(kind, id, source, null, DefaultSyncPrecision));
        }
        return (items, dropped);
    }

    // 一份浮层显示设置（缓存或旧的后端文件里的 JSON）：有旧的 metrics 或 metricSources 就换成 items 并去掉旧字段；
    // 已经有 items 的只去掉旧字段。返回是否改过。
    internal static bool MigrateLegacyPresentation(JsonObject overlay, string softwareId)
    {
        if (!overlay.ContainsKey("metrics") && !overlay.ContainsKey("metricSources")) return false;
        var metrics = overlay["metrics"] as JsonArray;
        var sources = overlay["metricSources"] as JsonObject;
        if (overlay["items"] is not JsonArray)
        {
            var (items, dropped) = ConvertLegacy(metrics, sources);
            if (dropped.Count > 0)
                Trace.TraceWarning("Overlay {0}: legacy overlay metrics without an item were left out: {1}",
                    softwareId, string.Join(", ", dropped));
            overlay["items"] = new JsonArray(items.Select(static item => (JsonNode)new JsonObject
            {
                ["kind"] = item.Kind,
                ["id"] = item.Id,
                ["source"] = item.Source,
                ["intervalMs"] = null,
                ["syncPrecision"] = item.SyncPrecision
            }).ToArray());
        }
        overlay.Remove("metrics");
        overlay.Remove("metricSources");
        return true;
    }
}
