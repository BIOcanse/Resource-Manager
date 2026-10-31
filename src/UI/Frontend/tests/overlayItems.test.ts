import assert from "node:assert/strict";
import { overlayItemsIssue as validateOverlayItems, overlayDefaultIntervalMs } from "../src/features/overlay/overlayItems.ts";
import { ResourceManager } from "../src/preferences/generated/desktopPoints.values.ts";
import {
  decodePerformanceOverlayAppSettings, findPerformanceOverlayEntry, performanceOverlaySoftwareDecoder
} from "../src/data/appSettings/performanceOverlaySettingsDecoder.ts";
import { appSettingsResultDecoder } from "../src/data/appSettings/appSettingsResultDecoder.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import type { PerformanceOverlayItem } from "../src/types.ts";

const item = (kind: PerformanceOverlayItem["kind"], id: string, source = "auto"): PerformanceOverlayItem =>
  ({ kind, id, source, intervalMs: null, syncPrecision: "mid" });
const editingConstraints = { maximumItems: 7, fontSizePxMinimum: 9, fontSizePxMaximum: 55,
  marginPxMinimum: 3, marginPxMaximum: 99, regionRatioMinimum: 0.1, regionRatioMaximum: 0.8, regionRatioStep: 0.025 };
const overlayItemsIssue = (items: readonly PerformanceOverlayItem[]) => validateOverlayItems(items, editingConstraints);

// ---------- 后端设置 performanceOverlay 的解码（docs/frontend_block_trees_2026-10-26.md「浮窗设置归后端」） ----------
// 后端归一化后的完整条目：默认的项（帧率、1% Low、帧时间、软件 CPU、软件 GPU）和默认外观。
const backendEntry = {
  enabled: true, mode: "injected",
  items: [item("frames", "frames.fps"), item("frames", "frames.low1"), item("frames", "frames.present"),
    item("software", "cpu.usage"), { ...item("software", "gpu.usage"), intervalMs: 1000, syncPrecision: "high" }],
  sizeMode: "absolutePixels", fontSizePx: 14, regionWidthRatio: 0.25, regionHeightRatio: 0.2, anchor: "topLeft", marginPx: 12
};
const section = decodePerformanceOverlayAppSettings({ software: { "Game-1": backendEntry } }, "$.performanceOverlay");
assert.deepEqual(section.software["Game-1"], backendEntry);
// 软件 ID 不区分大小写：按小写找得到，原样的键随结果返回；没有条目是 null（= 关）。
assert.deepEqual(findPerformanceOverlayEntry(section, "game-1"), { softwareId: "Game-1", entry: backendEntry });
assert.equal(findPerformanceOverlayEntry(section, "other"), null);
assert.equal(findPerformanceOverlayEntry(undefined, "game-1"), null);
// 只差大小写的两个键、不认识的方式、缺字段都拒绝（后端给的总是完整条目）。
assert.throws(() => decodePerformanceOverlayAppSettings({ software: { a: backendEntry, A: backendEntry } }, "$"), ResponseDecodeError);
assert.throws(() => decodePerformanceOverlayAppSettings({ software: { " game ": backendEntry } }, "$"), ResponseDecodeError);
// IDs are data, including Object.prototype names. None may be lost or change the decoded dictionary's prototype.
const specialIds = decodePerformanceOverlayAppSettings({ software: Object.fromEntries(
  ["__proto__", "constructor", "toString"].map(id => [id, backendEntry])) }, "$");
assert.deepEqual(Object.keys(specialIds.software), ["__proto__", "constructor", "toString"]);
assert.equal(Object.getPrototypeOf(specialIds.software), Object.prototype);
assert.deepEqual(findPerformanceOverlayEntry(specialIds, "__PROTO__")?.entry, backendEntry);
assert.throws(() => decodePerformanceOverlayAppSettings({ software: { a: { ...backendEntry, mode: "hooked" } } }, "$"), ResponseDecodeError);
assert.throws(() => decodePerformanceOverlayAppSettings({ software: { a: { ...backendEntry, anchor: "center" } } }, "$"), ResponseDecodeError);
const { marginPx: _margin, ...withoutMargin } = backendEntry;
assert.throws(() => decodePerformanceOverlayAppSettings({ software: { a: withoutMargin } }, "$"), ResponseDecodeError);
assert.throws(() => decodePerformanceOverlayAppSettings({ software: { a: { ...backendEntry, items: [{ ...item("metric", "x"), kind: "device" }] } } }, "$"),
  ResponseDecodeError);
// 设置读取的结果解码时同样检查这一节。
const settingsResult = { revision: "r1", settings: { version: "1", performanceOverlay: { software: { "Game-1": backendEntry } } } };
assert.deepEqual(appSettingsResultDecoder.decode(settingsResult).settings.performanceOverlay?.software["Game-1"], backendEntry);
const wireEntry = { ...backendEntry, items: [{ ...item("metric", " cpu.usage ", " auto "), extra: "ignored" }], extra: "ignored" };
const normalized = appSettingsResultDecoder.decode({ revision: "r2", settings: {
  version: "1", performanceOverlay: { software: { game: wireEntry } }
} }).settings.performanceOverlay!.software.game!;
assert.deepEqual(normalized.items, [item("metric", "cpu.usage")]);
assert.equal("extra" in normalized, false, "return the decoded entry rather than the unchecked wire object");
assert.equal(Object.isFrozen(normalized), true);
assert.equal(Object.isFrozen(normalized.items[0]), true);
assert.equal(wireEntry.items[0]!.id, " cpu.usage ", "decoding must not mutate the transport payload");
assert.throws(() => appSettingsResultDecoder.decode({ ...settingsResult,
  settings: { version: "1", performanceOverlay: { software: { "Game-1": { ...backendEntry, sizeMode: "auto" } } } } }), ResponseDecodeError);
// 一个软件的读取：存着的条目或后端的默认条目，带修订号。
const softwareRead = { softwareId: "game-1", stored: false, revision: "r1", settings: backendEntry, editingConstraints };
assert.deepEqual(performanceOverlaySoftwareDecoder.decode(softwareRead), softwareRead);
assert.throws(() => performanceOverlaySoftwareDecoder.decode({ ...softwareRead, editingConstraints: undefined }), ResponseDecodeError);
assert.throws(() => performanceOverlaySoftwareDecoder.decode({ ...softwareRead,
  editingConstraints: { ...editingConstraints, regionRatioStep: 0 } }), ResponseDecodeError);
assert.throws(() => performanceOverlaySoftwareDecoder.decode({ ...softwareRead,
  editingConstraints: { ...editingConstraints, fontSizePxMaximum: editingConstraints.fontSizePxMinimum - 1 } }), ResponseDecodeError);
assert.throws(() => performanceOverlaySoftwareDecoder.decode({ softwareId: "game-1", stored: false, settings: backendEntry }), ResponseDecodeError);

// ---------- 校验 ----------
assert.equal(overlayDefaultIntervalMs, ResourceManager.Desktop.DesktopPoints.overlay_sample_interval_ms);
const many = Array.from({ length: editingConstraints.maximumItems }, (_, index) => item("metric", `metric.${index}`));
assert.equal(overlayItemsIssue(many), null);
assert.equal(overlayItemsIssue([...many, item("metric", "metric.overflow")]), "too-many");
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

console.log("Overlay items: backend section decoding and validation passed.");
