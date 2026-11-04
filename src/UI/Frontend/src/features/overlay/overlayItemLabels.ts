import { localizedMetricLabel } from "../../presentation/metricLabels.ts";
import { uiText } from "../../text.ts";
import type { ItemKind } from "../itemSelection/itemSelectionModel.ts";

/* Keep frontend and generated desktop labels aligned: known frame and software aliases first, then process-field labels, metric labels, and raw IDs. */
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
