import { formatFixedDigits } from "./interfaceNumbers.ts";
/** Owns byte conversion and labels. Raw capacities are bytes; physical kind and display mode determine the radix. Labels always match the divisor. Link rates remain decimal bit/s. */
import { createSignal } from "solid-js";
import { ResourceManager } from "../preferences/generated/frontendPoints.values.ts";
import type { ByteUnitMode } from "../types";

// App publishes the settings-selected radix mode here.
// Formatters read it at call time so mode changes affect all readings immediately.
const [activeMode, setActiveMode] = createSignal<ByteUnitMode>(ResourceManager.FrontendPoints.ByteUnitMode);

export function currentByteUnitMode() {
  return activeMode();
}

export function applyByteUnitMode(mode?: string | null) {
  setActiveMode(normalizeByteUnitMode(mode));
}

/** Native radix of a capacity kind. */
export type ByteQuantityKind =
  /** Memory, VRAM, CPU cache, pagefiles, and memory-resident allocations use binary units. */
  | "memory"
  /** Disk/partition capacities, file sizes, and cumulative disk/network bytes use decimal native units. */
  | "storage";

const binaryUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"] as const;
const decimalUnits = ["B", "kB", "MB", "GB", "TB", "PB"] as const;

export const byteUnitModes = ["native", "binary", "decimal"] as const;

export function normalizeByteUnitMode(value: unknown): ByteUnitMode {
  return value === "native" || value === "binary" || value === "decimal" ? value : ResourceManager.FrontendPoints.ByteUnitMode;
}

/** Whether this kind uses binary units in the current mode. */
export function usesBinaryUnits(
  kind: ByteQuantityKind,
  mode: ByteUnitMode = activeMode()
) {
  if (mode === "binary") {
    return true;
  }
  if (mode === "decimal") {
    return false;
  }
  return kind === "memory";
}

export interface ByteUnitValue {
  /** Value converted to the selected scale. */
  value: number;
  /** Unit label matching the conversion radix. */
  unit: string;
}

/** Choose a scale automatically from the value. */
export function scaleBytes(
  bytes: number,
  kind: ByteQuantityKind,
  mode: ByteUnitMode = activeMode()
): ByteUnitValue {
  const units = usesBinaryUnits(kind, mode) ? binaryUnits : decimalUnits;
  const step = usesBinaryUnits(kind, mode) ? 1024 : 1000;
  let value = Number.isFinite(bytes) && bytes > 0 ? bytes : 0;
  let index = 0;
  while (value >= step && index < units.length - 1) {
    value /= step;
    index += 1;
  }
  return { value, unit: units[index] };
}

/** Select one unit and divisor from a reference value, usually the group maximum, so related values share a scale. */
export function byteUnitScale(
  referenceBytes: number,
  kind: ByteQuantityKind,
  mode: ByteUnitMode = activeMode()
): { unit: string; divisor: number } {
  const scaled = scaleBytes(referenceBytes, kind, mode);
  const binary = usesBinaryUnits(kind, mode);
  const units: readonly string[] = binary ? binaryUnits : decimalUnits;
  return { unit: scaled.unit, divisor: (binary ? 1024 : 1000) ** Math.max(0, units.indexOf(scaled.unit)) };
}

export function formatByteUnitValue(scaled: ByteUnitValue) {
  const digits = byteDigits(scaled);
  return `${formatFixedDigits(scaled.value, digits)} ${scaled.unit}`;
}

/** Format one capacity. */
export function formatBytes(
  bytes: number | null | undefined,
  kind: ByteQuantityKind,
  mode: ByteUnitMode = activeMode(),
  fallback = "--"
) {
  if (typeof bytes !== "number" || !Number.isFinite(bytes) || bytes < 0) {
    return fallback;
  }
  return formatByteUnitValue(scaleBytes(bytes, kind, mode));
}

export function formatSignedBytes(
  bytes: number | null | undefined,
  kind: ByteQuantityKind,
  mode: ByteUnitMode = activeMode(),
  fallback = "--"
) {
  if (typeof bytes !== "number" || !Number.isFinite(bytes)) {
    return fallback;
  }
  const sign = bytes > 0 ? "+" : bytes < 0 ? "-" : "";
  return `${sign}${formatBytes(Math.abs(bytes), kind, mode, fallback)}`;
}

/** Scale used and total by the total value so both remain comparable; print the unit once. */
export function formatBytePair(
  usedBytes: number | null | undefined,
  totalBytes: number | null | undefined,
  kind: ByteQuantityKind,
  mode: ByteUnitMode = activeMode(),
  fallback = "--"
) {
  if (typeof totalBytes !== "number" || !Number.isFinite(totalBytes) || totalBytes <= 0) {
    return formatBytes(usedBytes, kind, mode, fallback);
  }
  const total = scaleBytes(totalBytes, kind, mode);
  const step = usesBinaryUnits(kind, mode) ? 1024 : 1000;
  const units = usesBinaryUnits(kind, mode) ? binaryUnits : decimalUnits;
  const divisor = step ** Math.max(0, units.indexOf(total.unit as never));
  const used = typeof usedBytes === "number" && Number.isFinite(usedBytes) && usedBytes > 0
    ? usedBytes / divisor
    : 0;
  const digits = byteDigits(total);
  return `${formatFixedDigits(used, digits)} / ${formatFixedDigits(total.value, digits)} ${total.unit}`;
}

/** Backend link rates are bit/s; scale by decimal units independently of capacity mode. */
export function formatBitsPerSecond(
  bitsPerSecond: number | null | undefined,
  fallback = "--"
) {
  if (typeof bitsPerSecond !== "number"
    || !Number.isFinite(bitsPerSecond)
    || bitsPerSecond <= 0) {
    return fallback;
  }
  const units = ["bit/s", "kbit/s", "Mbit/s", "Gbit/s", "Tbit/s"];
  let value = bitsPerSecond;
  let index = 0;
  while (value >= 1000 && index < units.length - 1) {
    value /= 1000;
    index += 1;
  }
  const digits = value >= 100 ? 0 : value >= 10 ? 1 : 2;
  return `${formatFixedDigits(value, digits)} ${units[index]}`;
}

/** Byte throughput uses capacity conversion followed by /s. */
export function formatBytesPerSecond(
  bytesPerSecond: number | null | undefined,
  mode: ByteUnitMode = activeMode(),
  fallback = "--"
) {
  if (typeof bytesPerSecond !== "number"
    || !Number.isFinite(bytesPerSecond)
    || bytesPerSecond < 0) {
    return fallback;
  }
  return `${formatBytes(bytesPerSecond, "storage", mode)}/s`;
}

// Precision by magnitude: integers for three digits or bytes, one decimal for two digits, two for one digit.
function byteDigits(scaled: ByteUnitValue) {
  if (scaled.unit === "B") {
    return 0;
  }
  return scaled.value >= 100 ? 0 : scaled.value >= 10 ? 1 : 2;
}
