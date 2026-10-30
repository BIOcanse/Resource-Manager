import assert from "node:assert/strict";
import {
  convertLegacyOverlayMetrics, maximumOverlayItems, migrateLegacyOverlayPresentation, overlayItemsIssue, pointOverlayItems
} from "../src/features/overlay/overlayItems.ts";
import { createFrontendPreferencesClient, migrateOverlayItems, pointOverlayPresentation, type FrontendPreferencesCache } from "../src/preferences/frontendPreferences.ts";
import type { PerformanceOverlayItem } from "../src/types.ts";

const item = (kind: PerformanceOverlayItem["kind"], id: string, source = "auto"): PerformanceOverlayItem =>
  ({ kind, id, source, intervalMs: null, syncPrecision: "mid" });

// ---------- 旧偏好的换算（frontend-preferences-overlay-items-v1） ----------
// 真实的旧缓存样本：帧率、帧时间、两个 Low、目标进程 CPU、内存、GPU、显存，整机指标一个选了来源、一个没选。
const legacyOverlay = {
  enabled: true, mode: "external",
  metrics: ["target.fps", "target.frameTime", "target.onePercentLow", "target.pointOnePercentLow", "target.cpu", "target.memory",
    "target.gpu", "gpu.0.temperature", "cpu.usage", "target.vram"],
  metricSources: { "gpu.0.temperature": { fieldName: "gpu_nvidia_temperature_celsius", deviceIdentity: "pci:10de:2684:0" } },
  sizeMode: "absolutePixels", fontSizePx: 18, regionWidthRatio: 0.25, regionHeightRatio: 0.2, anchor: "topLeft", marginPx: 12
};
const converted = convertLegacyOverlayMetrics(legacyOverlay.metrics, legacyOverlay.metricSources);
assert.deepEqual(converted.items, [
  item("frames", "frames.fps"),
  item("frames", "frames.present"),
  item("frames", "frames.low1"),
  item("frames", "frames.low01"),
  item("software", "cpu.usage"),
  item("software", "memory.usage"),
  item("software", "gpu.usage"),
  item("metric", "gpu.0.temperature", "gpu_nvidia_temperature_celsius@pci:10de:2684:0"),
  item("metric", "cpu.usage"),
  item("software", "gpu.vram")
]);
// 目标在所有 GPU 上的合计换成软件的 gpu.usage、gpu.vram；每个旧 id 都有对应的项。
assert.deepEqual(converted.dropped, []);
// 来源缺字段时按自动选择；没有 metricSources 的更旧缓存同样按自动选择。
assert.deepEqual(convertLegacyOverlayMetrics(["disk.read"], { "disk.read": { fieldName: "x" } }).items, [item("metric", "disk.read")]);
assert.deepEqual(convertLegacyOverlayMetrics(["disk.read"], undefined).items, [item("metric", "disk.read")]);

// 整份显示设置：其他字段不动，metrics 和 metricSources 去掉，换成 items。
const migrated = migrateLegacyOverlayPresentation(legacyOverlay) as Record<string, unknown>;
assert.equal("metrics" in migrated, false);
assert.equal("metricSources" in migrated, false);
assert.deepEqual(migrated.items, converted.items);
assert.equal(migrated.fontSizePx, 18);
assert.equal(migrated.anchor, "topLeft");
assert.equal(migrated.enabled, true);
// 已经是新形状的不动；两样都有时留 items，丢旧字段。
const current = { ...pointOverlayPresentation() };
assert.equal(migrateLegacyOverlayPresentation(current), current);
assert.deepEqual(migrateLegacyOverlayPresentation({ ...current, metrics: ["target.cpu"] }), current);

// 缓存读入：NVML 改名迁移在前，换出来的来源是新字段名。
const legacyCache = {
  appearance: undefined, refresh: undefined, notifications: undefined,
  overlays: { "game-1": { ...legacyOverlay, metrics: ["gpu.0.memoryUsed"],
    metricSources: { "gpu.0.memoryUsed": { fieldName: "gpu_nvidia_fb_used_including_reserved_bytes", deviceIdentity: "nvml:0" } } } }
} as unknown as FrontendPreferencesCache;
const storage = new Map<string, string>();
const browserStorage = {
  getItem: (key: string) => storage.get(key) ?? null,
  setItem: (key: string, value: string) => { storage.set(key, value); }
} as unknown as Storage;
storage.set("resource-manager:frontend-preferences:v1", JSON.stringify(legacyCache));
const read = await createFrontendPreferencesClient(browserStorage).read();
assert.deepEqual(read.overlays["game-1"].items, [item("metric", "gpu.0.memoryUsed", "gpu_nvidia_fb_used_excluding_reserved_bytes@nvml:0")]);
assert.equal("metrics" in read.overlays["game-1"], false);
assert.deepEqual(migrateOverlayItems({ overlays: {} } as unknown as FrontendPreferencesCache).overlays, {});

// ---------- 默认的项（构建点 overlay-metric_order） ----------
assert.deepEqual(pointOverlayItems(), [
  item("frames", "frames.fps"), item("frames", "frames.low1"), item("frames", "frames.present"), item("software", "cpu.usage"),
  item("software", "gpu.usage")
]);
assert.equal(overlayItemsIssue(pointOverlayItems()), null);

// ---------- 校验 ----------
assert.equal(maximumOverlayItems, 64);
const many = Array.from({ length: 64 }, (_, index) => item("metric", `metric.${index}`));
assert.equal(overlayItemsIssue(many), null);
assert.equal(overlayItemsIssue([...many, item("metric", "metric.64")]), "too-many");
// (kind, id) 不分大小写不能重复；同一 id 不同种类可以。
assert.equal(overlayItemsIssue([item("metric", "cpu.usage"), item("metric", "CPU.Usage")]), "duplicate");
assert.equal(overlayItemsIssue([item("metric", "cpu.usage"), item("software", "cpu.usage")]), null);
assert.equal(overlayItemsIssue([{ ...item("metric", "x"), kind: "device" as never }]), "kind");
assert.equal(overlayItemsIssue([{ ...item("metric", "x"), syncPrecision: "max" as never }]), "precision");
assert.equal(overlayItemsIssue([item("metric", " ")]), "id");
assert.equal(overlayItemsIssue([item("metric", "x", "")]), "source");
// 间隔：null 或 100–60000 的整数；帧项不带间隔。
assert.equal(overlayItemsIssue([{ ...item("metric", "x"), intervalMs: 100 }]), null);
assert.equal(overlayItemsIssue([{ ...item("metric", "x"), intervalMs: 60_000 }]), null);
assert.equal(overlayItemsIssue([{ ...item("metric", "x"), intervalMs: 99 }]), "interval");
assert.equal(overlayItemsIssue([{ ...item("metric", "x"), intervalMs: 60_001 }]), "interval");
assert.equal(overlayItemsIssue([{ ...item("metric", "x"), intervalMs: 250.5 }]), "interval");
assert.equal(overlayItemsIssue([{ ...item("frames", "frames.fps"), intervalMs: 500 }]), "frames-interval");

console.log("Overlay items: legacy conversion, cache migration, point defaults and validation passed.");
