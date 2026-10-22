import type { MetricFieldValue } from "../data/metricFields/metricFieldSource.ts";
import { formatBytes, formatBytesPerSecond, type ByteQuantityKind } from "./byteUnits.ts";
import { uiText } from "../text.ts";

/*
 * 规范字段数值的统一显示：按字段自带的单位格式化。
 *
 * 不可用返回 null，由调用方写「不可用」，不当 0。百分比不截断（NPU 忙碌率等原值可超过 100%）。
 * 字节类走 byteUnits.ts（唯一的字节单位所有者）；容量属于内存还是存储由调用方按设备种类给出。
 * 单位不认识时原样拼上单位，不猜换算。
 */
export function formatFieldValue(value: MetricFieldValue, quantityKind: ByteQuantityKind = "memory"): string | null {
  if (value.booleanValue !== null) return value.booleanValue ? uiText.shellBridge.yes : uiText.shellBridge.no;
  const integer = value.integerValue;
  const number = integer !== null ? Number(integer) : value.numericValue;
  if (number === null) return null;
  switch (value.unit) {
    case "B": return formatBytes(number, quantityKind);
    case "MiB": return formatBytes(number * 1024 * 1024, "memory");
    // NVMe 数据单位：1 个单位 = 512000 字节。
    case "512000 B": return formatBytes(number * 512000, "storage");
    case "B/s": return formatBytesPerSecond(number);
    case "%": return `${number.toFixed(1)}%`;
    case "°C": return `${number.toFixed(1)} °C`;
    case "MHz":
    case "RPM":
    case "mV": return `${Math.round(number)} ${value.unit}`;
    case "W":
    case "V":
    case "J":
    case "s":
    case "ms":
    case "min":
    case "h": return `${trimmed(number, 1)} ${value.unit}`;
    case "A": return `${trimmed(number, 2)} A`;
    case "µs": return `${integer !== null ? integer.toString() : Math.round(number)} µs`;
    case "count":
    case "generation":
    case "lanes":
    case "enum": return integer !== null ? integer.toLocaleString() : trimmed(number, 2);
    case "bitmask64": return `0x${(integer ?? BigInt(Math.trunc(number))).toString(16).toUpperCase()}`;
    case "1":
    case "ratio": return trimmed(number, 3);
    default: return `${integer !== null ? integer.toString() : plain(number)} ${value.unit}`.trim();
  }
}

/** 很小的数（例如每计数焦耳）用有效数字，避免定点格式把它显示成 0。 */
function plain(value: number): string {
  return value !== 0 && Math.abs(value) < 0.01 ? value.toPrecision(4) : trimmed(value, 3);
}

/** 定点小数去掉末尾的 0，例如 12.50 → 12.5，3.0 → 3。 */
function trimmed(value: number, digits: number): string {
  return Number(value.toFixed(digits)).toString();
}
