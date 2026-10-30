import { byteUnitScale, formatByteUnitValue } from "../../../presentation/byteUnits.ts";

/**
 * 视图里数值的显示：一条轴（或一条序列的读数）按它的原始单位和最大值选一档。
 * 字节按 byteUnitScale 选档（B 按内存类，B/s 按存储类，与其他页面的字节显示同一规则），
 * 时间轴（ms）按秒显示，其余单位原样、最多两位小数。
 */
export interface UnitScale { unit: string; format: (value: number) => string }

const trimmed = (value: number) => String(Number(value.toFixed(2)));

export function unitScale(unit: string, largest: number): UnitScale {
  if (unit === "B" || unit === "B/s") {
    const scale = byteUnitScale(Math.abs(largest), unit === "B" ? "memory" : "storage");
    const suffix = unit === "B/s" ? "/s" : "";
    return { unit: scale.unit + suffix,
      format: (value) => formatByteUnitValue({ value: value / scale.divisor, unit: scale.unit }) + suffix };
  }
  return { unit, format: (value) => unit ? `${trimmed(value)} ${unit}` : trimmed(value) };
}

/** 时间轴的刻度：距录制开始的毫秒数 → 秒。 */
export function formatElapsed(milliseconds: number) {
  return `${trimmed(milliseconds / 1000)} s`;
}

/** 刻度只写数值（单位写在轴标题上）。 */
export function tickText(scale: UnitScale, value: number) {
  const text = scale.format(value);
  return scale.unit && text.endsWith(` ${scale.unit}`) ? text.slice(0, -scale.unit.length - 1) : text;
}
