import { ResourceManager } from "../../preferences/generated/frontendPoints.values.ts";
import type { PerformanceOverlayItem } from "../../types.ts";
import {
  automaticSource, defaultSyncPrecision, itemKey, itemKinds, itemListIssue,
  type ItemKind, type ItemListIssue
} from "../itemSelection/itemSelectionModel.ts";

/*
 * 浮层的项（docs/targeted_recording_views_2026-10-30.md §3.9）：每个软件一组 {kind, id, source, intervalMs, syncPrecision}，
 * 顺序就是绘制顺序。原生壳 FrontendPreferences.cs 的 FrontendOverlayItem 是同一份形状和校验。
 */

/** 每个软件最多 64 项，与后端 TargetedRecordingItemReader.MaximumOverlayItems 相同。 */
export const maximumOverlayItems = 64;
/**
 * 留空间隔的非帧项实际用的间隔：原生壳订阅浮层时路径里的 intervalMs（OverlaySubscriptions.DefaultIntervalMs）。
 * 选择器把它写进间隔框的占位文字，每一项都可以填自己的间隔。
 */
export const overlayDefaultIntervalMs = 500;

/** 浮层默认的项：构建点 overlay-metric_order 的每个值是 "kind:id"，来源自动、间隔留空、同步精度中，和选择器新加的项一样。 */
export function pointOverlayItems(): PerformanceOverlayItem[] {
  return ResourceManager.FrontendPoints.MetricOrder.map((value) => {
    const separator = value.indexOf(":");
    const kind = value.slice(0, separator);
    const id = value.slice(separator + 1);
    if (separator <= 0 || id === "" || !(itemKinds as readonly string[]).includes(kind))
      throw new Error(`overlay-metric_order: ${value} is not kind:id`);
    return { kind: kind as ItemKind, id, source: automaticSource, intervalMs: null, syncPrecision: defaultSyncPrecision };
  });
}

/** 一组浮层项是否合规；合规返回 null。 */
export function overlayItemsIssue(items: readonly PerformanceOverlayItem[]): ItemListIssue | null {
  return itemListIssue(items, maximumOverlayItems);
}

/**
 * Migration frontend-preferences-overlay-items-v1：2026-10-30 之前的浮层按指标 id 存 metrics（顺序）和 metricSources
 * （按 id 存 {fieldName, deviceIdentity}）。读到时换成 items，保持原来的顺序；换出来的项间隔留空、同步精度中，下次保存写进缓存。
 * 旧 id 到项的对应（PerformanceOverlayMetricIds）：
 * - target.fps → frames.fps，target.frameTime → frames.present，target.onePercentLow → frames.low1，
 *   target.pointOnePercentLow → frames.low01（帧项，来源自动）；
 * - target.cpu → 软件 cpu.usage，target.memory → 软件 memory.usage（来源自动，即资源细分）；
 * - target.gpu → 软件 gpu.usage，target.vram → 软件 gpu.vram（目标在所有 GPU 上的合计，来源自动）；
 * - 其余 id 是整机和硬件指标 → metric 同一 id，选过来源时来源写成目录的 "字段@设备身份"，没选是 "auto"。
 * 原生壳的 FrontendPreferencesOwner 对它的缓存文件做同一份迁移。删除条件：下一个不再读旧版本缓存的发行。
 */
const legacyTargetItems: Readonly<Record<string, { kind: ItemKind; id: string }>> = {
  "target.fps": { kind: "frames", id: "frames.fps" },
  "target.frametime": { kind: "frames", id: "frames.present" },
  "target.onepercentlow": { kind: "frames", id: "frames.low1" },
  "target.pointonepercentlow": { kind: "frames", id: "frames.low01" },
  "target.cpu": { kind: "software", id: "cpu.usage" },
  "target.memory": { kind: "software", id: "memory.usage" },
  "target.gpu": { kind: "software", id: "gpu.usage" },
  "target.vram": { kind: "software", id: "gpu.vram" }
};

export interface LegacyOverlayConversion {
  items: PerformanceOverlayItem[];
  /** 没有对应项、没有换过去的旧 id（按原顺序）。 */
  dropped: string[];
}

export function convertLegacyOverlayMetrics(metrics: readonly unknown[], metricSources: unknown): LegacyOverlayConversion {
  const sources = isRecord(metricSources) ? metricSources : {};
  const items: PerformanceOverlayItem[] = [];
  const dropped: string[] = [];
  const seen = new Set<string>();
  for (const value of metrics) {
    if (typeof value !== "string" || value.trim() === "") continue;
    const lower = value.toLowerCase();
    let kind: ItemKind = "metric";
    let id = value;
    let source = automaticSource;
    if (lower in legacyTargetItems) {
      ({ kind, id } = legacyTargetItems[lower]);
    } else {
      const chosen = sources[value];
      if (isRecord(chosen) && typeof chosen.fieldName === "string" && chosen.fieldName !== ""
        && typeof chosen.deviceIdentity === "string" && chosen.deviceIdentity !== "")
        source = `${chosen.fieldName}@${chosen.deviceIdentity}`;
    }
    const key = itemKey(kind, id);
    if (seen.has(key) || items.length >= maximumOverlayItems) { dropped.push(value); continue; }
    seen.add(key);
    items.push({ kind, id, source, intervalMs: null, syncPrecision: defaultSyncPrecision });
  }
  return { items, dropped };
}

/** 一份浮层显示设置读入时的迁移：有旧的 metrics 就换成 items，并去掉 metrics 和 metricSources；没有就原样返回。 */
export function migrateLegacyOverlayPresentation<T>(overlay: T): T {
  if (!isRecord(overlay) || (!("metrics" in overlay) && !("metricSources" in overlay))) return overlay;
  const { metrics, metricSources, ...rest } = overlay;
  if (Array.isArray(rest.items)) return rest as T;
  const { items, dropped } = convertLegacyOverlayMetrics(Array.isArray(metrics) ? metrics : [], metricSources);
  // 换不过去的旧 id 不编造项，写进控制台说明（原生壳写进 Trace）。
  if (dropped.length > 0) console.warn("[overlay] legacy overlay metrics without an item were left out", dropped);
  return { ...rest, items } as T;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
