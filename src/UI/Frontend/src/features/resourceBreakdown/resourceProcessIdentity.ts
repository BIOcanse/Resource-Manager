import type { ResourceProcessSegment } from "../../types.ts";

/** Backend displayKind identifies system residuals; render them and their leaves as reserved. */
export function isReservedSegment(segment: { displayKind?: string | null }) {
  return segment.displayKind === "system-residual";
}

/** Identify reserved leaves by owning entry and attribution kind, never by ID prefixes. */
export function isReservedProcess(software: { displayKind?: string | null }, process: ResourceProcessSegment) {
  return isReservedSegment(software) || process.attributionKind === "system-residual";
}

/** Include PID, start key, attribution kind, and name in identity; PID alone cannot distinguish residual leaves. */
export function resourceProcessKey(process: ResourceProcessSegment) {
  return [process.processId, process.processStartKey ?? "", process.attributionKind ?? "", process.name].join("|");
}
