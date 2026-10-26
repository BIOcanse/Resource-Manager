import { byteUnitScale, formatByteUnitValue } from "../../presentation/byteUnits.ts";

/**
 * 定向报告图表的纵轴：单位、换算和刻度。图表组件只负责画，这里决定刻度落在哪些值上。
 *
 * unit 写在轴顶；刻度值是原始值除以 divisor（字节曲线由 curveScale 按同一档选好单位和除数，
 * 和卡片标题一致）；integerTicks 为 true 时只出整数刻度（计数）。
 */
export interface ChartAxis { unit: string; divisor: number; integerTicks: boolean }

/**
 * 刻度取 1、2、2.5、5 乘 10 的幂（整数轴不取 2.5，步长至少为 1），最多 4 格，顶格不小于最大值。
 * largest 是换算后（已除以 divisor）的最大值；全为 0 时按最大值 1 画，轴不塌成一条线。
 */
export function axisTicks(largest: number, integerTicks: boolean) {
  const span = largest > 0 ? largest : 1;
  const target = span / 4;
  const magnitude = 10 ** Math.floor(Math.log10(target));
  const factors = integerTicks ? [1, 2, 5, 10] : [1, 2, 2.5, 5, 10];
  let step = magnitude * (factors.find((factor) => factor * magnitude >= target - 1e-12) ?? 10);
  if (integerTicks) step = Math.max(1, Math.round(step));
  const count = Math.max(1, Math.ceil(span / step - 1e-9));
  return { step, count, top: step * count };
}

/** 刻度文字：最多两位小数，去掉末尾的 0。 */
export function formatTick(value: number) {
  return String(Number(value.toFixed(2)));
}

/** 资源指标的原始单位（后端发原始数，单位按指标 ID 的类别决定）。 */
export function metricUnit(id: string) {
  if (/^(disk\.|network\.)/.test(id)) return "B/s";
  if (/^(memory\.|virtualMemory\.)/.test(id) || /\.vram$/.test(id)) return "B";
  if (/temperature$/.test(id)) return "°C";
  if (/frequency$/.test(id)) return "MHz";
  return "%";
}

/**
 * 一条资源曲线的单位：轴和卡片标题共用一档。字节按曲线最大值经 byteUnitScale 选档
 * （内存、显存按内存类，磁盘、网络吞吐按存储类，与其他页面的字节显示同一规则），其余单位原样。
 */
export function curveScale(id: string, points: readonly { y: number | null }[]): { axis: ChartAxis; format: (value: number) => string } {
  const unit = metricUnit(id);
  if (unit === "B" || unit === "B/s") {
    const largest = points.reduce((value, point) => point.y !== null && point.y > value ? point.y : value, 0);
    const scale = byteUnitScale(largest, unit === "B" ? "memory" : "storage");
    const suffix = unit === "B/s" ? "/s" : "";
    return {
      axis: { unit: scale.unit + suffix, divisor: scale.divisor, integerTicks: false },
      format: (value) => formatByteUnitValue({ value: value / scale.divisor, unit: scale.unit }) + suffix
    };
  }
  return { axis: { unit, divisor: 1, integerTicks: false }, format: (value) => `${value.toFixed(1)} ${unit}` };
}
