import { localizedMetricLabel } from "../../presentation/metricLabels.ts";
import { uiText } from "../../text.ts";
import type { ItemKind } from "../itemSelection/itemSelectionModel.ts";

/*
 * 浮层项的名称。常驻进程的浮层画的是同一份文字（取不到值的原因也在那份表里，来自 performanceOverlay.itemReasons）：
 * scripts/generateOverlayMetricLabels.mjs 从这里用到的文案生成 src/UI/Desktop/src/text/generated/overlay_metric_labels.zig
 * （指标名、模板、进程字段名、原因），src/UI/Desktop/src/overlay/text.zig 的 itemLabel 按同样的顺序查。
 * - 帧项沿用旧浮层的名称：frames.fps、frames.present、frames.low1、frames.low01 → 指标名 target.fps、target.frameTime、
 *   target.onePercentLow、target.pointOnePercentLow；
 * - 旧浮层就有的四个目标软件项也沿用旧名称：cpu.usage、memory.usage、gpu.usage、gpu.vram → 指标名 target.cpu、
 *   target.memory、target.gpu、target.vram；
 * - 其他目标软件项：itemSoftware(名称)，名称先查进程字段名（resourceTable.processFieldNames），再查指标名，都没有用 id；
 * - 整机和硬件项：指标名，没有用 id。
 */
export const overlayFrameLabelKeys: Readonly<Record<string, string>> = {
  "frames.fps": "target.fps",
  "frames.present": "target.frameTime",
  "frames.low1": "target.onePercentLow",
  "frames.low01": "target.pointOnePercentLow"
};

export const overlaySoftwareLabelKeys: Readonly<Record<string, string>> = {
  "cpu.usage": "target.cpu",
  "memory.usage": "target.memory",
  "gpu.usage": "target.gpu",
  "gpu.vram": "target.vram"
};

export function overlayItemLabel(kind: ItemKind, id: string): string {
  if (kind === "frames") {
    const key = overlayFrameLabelKeys[id.toLowerCase()];
    return key ? localizedMetricLabel(key) : id;
  }
  if (kind === "software") {
    const legacyKey = overlaySoftwareLabelKeys[id.toLowerCase()];
    if (legacyKey) return localizedMetricLabel(legacyKey);
    const name = uiText.resourceTable.processFieldNames[id] ?? localizedMetricLabel(id);
    return uiText.performanceOverlay.itemSoftware(name);
  }
  return localizedMetricLabel(id);
}
