import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireSafeInteger,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import { itemKinds, syncPrecisions } from "../../features/itemSelection/itemSelectionModel.ts";
import type { PerformanceOverlayAppSettings, PerformanceOverlayEditingConstraints, PerformanceOverlayEntry, PerformanceOverlayItem } from "../../types.ts";

/*
 * 后端设置 performanceOverlay 一节的解码（docs/frontend_block_trees_2026-10-26.md「浮窗设置归后端」）。
 * 后端归一化后每条都是完整的，这里只核对形状和枚举值；范围和项的规则由后端在写入前校验，界面保存前另用
 * overlayItemsIssue 拦住不合规的项。
 */

const overlayModes = ["external", "injected"] as const;
const overlaySizeModes = ["absolutePixels", "windowRatio"] as const;
const overlayAnchors = ["topLeft", "topRight", "bottomLeft", "bottomRight"] as const;

export function decodePerformanceOverlayEntry(value: unknown, path: string): PerformanceOverlayEntry {
  const row = requireRecord(value, path);
  return {
    enabled: requireBoolean(row.enabled, `${path}.enabled`),
    mode: requireOneOf(row.mode, `${path}.mode`, overlayModes),
    items: requireArray(row.items, `${path}.items`).map((item, index) => decodeOverlayItem(item, `${path}.items[${index}]`)),
    sizeMode: requireOneOf(row.sizeMode, `${path}.sizeMode`, overlaySizeModes),
    fontSizePx: requireSafeInteger(row.fontSizePx, `${path}.fontSizePx`),
    regionWidthRatio: requireFiniteNumber(row.regionWidthRatio, `${path}.regionWidthRatio`),
    regionHeightRatio: requireFiniteNumber(row.regionHeightRatio, `${path}.regionHeightRatio`),
    anchor: requireOneOf(row.anchor, `${path}.anchor`, overlayAnchors),
    marginPx: requireSafeInteger(row.marginPx, `${path}.marginPx`)
  };
}

export function decodePerformanceOverlayAppSettings(value: unknown, path: string): PerformanceOverlayAppSettings {
  const row = requireRecord(value, path);
  const software = requireRecord(row.software, `${path}.software`);
  const entries: Array<[string, PerformanceOverlayEntry]> = [];
  const seen = new Set<string>();
  for (const [softwareId, entry] of Object.entries(software)) {
    const key = softwareId.toLowerCase();
    if (softwareId.trim() === "" || softwareId !== softwareId.trim() || seen.has(key)) {
      throw new ResponseDecodeError(`${path}.software`, "unique non-empty software IDs (case-insensitive)");
    }
    seen.add(key);
    entries.push([softwareId, decodePerformanceOverlayEntry(entry, `${path}.software.${softwareId}`)]);
  }
  return { software: Object.fromEntries(entries) };
}

/** GET /api/performance-overlay/software/{softwareId}：存着的条目，或没有条目时后端的默认条目（关）。 */
export interface PerformanceOverlaySoftwareRead {
  readonly softwareId: string;
  readonly stored: boolean;
  readonly revision: string;
  readonly settings: PerformanceOverlayEntry;
  readonly editingConstraints: PerformanceOverlayEditingConstraints;
}

export const performanceOverlaySoftwareDecoder = defineResponseDecoder<PerformanceOverlaySoftwareRead>(
  "performance-overlay.software.v1",
  (value) => {
    const row = requireRecord(value);
    return {
      softwareId: requireNonEmptyString(row.softwareId, "$.softwareId"),
      stored: requireBoolean(row.stored, "$.stored"),
      revision: requireNonEmptyString(row.revision, "$.revision"),
      settings: decodePerformanceOverlayEntry(row.settings, "$.settings"),
      editingConstraints: decodePerformanceOverlayEditingConstraints(row.editingConstraints, "$.editingConstraints")
    };
  });

export function decodePerformanceOverlayEditingConstraints(value: unknown, path: string): PerformanceOverlayEditingConstraints {
  const row = requireRecord(value, path);
  const result = {
    maximumItems: requireSafeInteger(row.maximumItems, `${path}.maximumItems`),
    fontSizePxMinimum: requireSafeInteger(row.fontSizePxMinimum, `${path}.fontSizePxMinimum`),
    fontSizePxMaximum: requireSafeInteger(row.fontSizePxMaximum, `${path}.fontSizePxMaximum`),
    marginPxMinimum: requireSafeInteger(row.marginPxMinimum, `${path}.marginPxMinimum`),
    marginPxMaximum: requireSafeInteger(row.marginPxMaximum, `${path}.marginPxMaximum`),
    regionRatioMinimum: requireFiniteNumber(row.regionRatioMinimum, `${path}.regionRatioMinimum`),
    regionRatioMaximum: requireFiniteNumber(row.regionRatioMaximum, `${path}.regionRatioMaximum`),
    regionRatioStep: requireFiniteNumber(row.regionRatioStep, `${path}.regionRatioStep`)
  };
  if (result.maximumItems <= 0 || result.fontSizePxMinimum > result.fontSizePxMaximum
      || result.marginPxMinimum > result.marginPxMaximum || result.regionRatioMinimum > result.regionRatioMaximum
      || result.regionRatioStep <= 0) throw new ResponseDecodeError(path, "ordered editor bounds, positive item count and ratio step");
  return Object.freeze(result);
}

/** 后端设置里这个软件的条目（软件 ID 不区分大小写）；没有条目返回 null。 */
export function findPerformanceOverlayEntry(settings: PerformanceOverlayAppSettings | undefined, softwareId: string) {
  const key = softwareId.toLowerCase();
  const found = Object.entries(settings?.software ?? {}).find(([id]) => id.toLowerCase() === key);
  return found ? { softwareId: found[0], entry: found[1] } : null;
}

function decodeOverlayItem(value: unknown, path: string): PerformanceOverlayItem {
  const row = requireRecord(value, path);
  return {
    kind: requireOneOf(row.kind, `${path}.kind`, itemKinds),
    id: requireNonEmptyString(row.id, `${path}.id`),
    source: requireNonEmptyString(row.source, `${path}.source`),
    intervalMs: requireNullable(row.intervalMs, `${path}.intervalMs`, requireSafeInteger),
    syncPrecision: requireOneOf(row.syncPrecision, `${path}.syncPrecision`, syncPrecisions)
  };
}
