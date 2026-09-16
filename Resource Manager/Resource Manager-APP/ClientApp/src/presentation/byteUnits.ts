/**
 * 字节单位的唯一所有者。
 *
 * 一句话说清规则：后端只发原始数（容量是字节，链路速率是 bit/s），单位写在字段名或
 * 单位标记里；一个容量数字属于一个物理类别；类别加上当前模式唯一决定用哪个进制；
 * 换算、跳档和标签都在这里一次性给出。
 *
 * 这里不会出现「标签写 GB、算的却是 1024」：标签永远跟着实际用的进制走。
 *
 * 别处不要再写 `/ 1024`。要显示字节就用这里的函数。
 */
import { createSignal } from "solid-js";
import type { ByteUnitMode } from "../types";

// 当前进制模式的唯一运行时所有者：设置里的选择是唯一来源，由 App 应用到这里，
// 各处的换算函数在调用时读取它，所以切换模式后所有数字一起变，不用重建任何缓存。
const [activeMode, setActiveMode] = createSignal<ByteUnitMode>("native");

export function currentByteUnitMode() {
  return activeMode();
}

export function applyByteUnitMode(mode?: string | null) {
  setActiveMode(normalizeByteUnitMode(mode));
}

/** 一个容量数字「本来」是几进制的。 */
export type ByteQuantityKind =
  /** 内存、显存、CPU 缓存、页面文件，以及跑在内存里的占用量。颗粒容量天然是 2 的幂。 */
  | "memory"
  /** 磁盘与分区容量、文件大小、累计读写与网络流量。厂商标称与传输统计历来是 10 的幂。 */
  | "storage";

const binaryUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"] as const;
const decimalUnits = ["B", "kB", "MB", "GB", "TB", "PB"] as const;

export const byteUnitModes = ["native", "binary", "decimal"] as const;

export function normalizeByteUnitMode(value: unknown): ByteUnitMode {
  return value === "binary" || value === "decimal" ? value : "native";
}

/** 这一类量在当前模式下是不是二进制的。 */
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
  /** 换算到所选档位之后的数值。 */
  value: number;
  /** 这个数值的单位标签，永远和换算用的进制一致。 */
  unit: string;
}

/** 自适应跳档：选到最合适的一档。 */
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

export function formatByteUnitValue(scaled: ByteUnitValue) {
  return `${scaled.value.toFixed(byteDigits(scaled))} ${scaled.unit}`;
}

/** 单个容量。 */
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

/**
 * 「已用 / 总量」。两边必须落在同一档才能直接比较，所以按总量选档，
 * 再用同一档换算已用量，单位只写一次。
 */
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
  return `${used.toFixed(digits)} / ${total.value.toFixed(digits)} ${total.unit}`;
}

/**
 * 链路速率天生按位计，后端发的就是 bit/s，这里只跳档不换进制 ——
 * 传输速率历来是十进制，不随容量模式改变。
 */
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
  return `${value.toFixed(value >= 100 ? 0 : value >= 10 ? 1 : 2)} ${units[index]}`;
}

/** 字节吞吐率：数值部分按容量规则走，后面加 /s。 */
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

// 小数位按量级收敛：三位数及以上取整，两位数留一位，个位数留两位，字节本身不留小数。
function byteDigits(scaled: ByteUnitValue) {
  if (scaled.unit === "B") {
    return 0;
  }
  return scaled.value >= 100 ? 0 : scaled.value >= 10 ? 1 : 2;
}
