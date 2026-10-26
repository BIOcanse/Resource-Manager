import { formatFixedDigits } from "../../presentation/interfaceNumbers.ts";
import { formatBytes, formatBytesPerSecond } from "../../presentation/byteUnits.ts";
import { byteQuantityKindForMetric } from "../../presentation/metricLabels.ts";
import type { ResourceBreakdownBar } from "../../types";
import { formatPercent } from "../../utils.ts";

export function formatResourceBarValue(
  bar: Pick<ResourceBreakdownBar, "metricId" | "unit">,
  value: number | null
) {
  if (value === null) return "-";
  if (bar.unit === "B") {
    // 条形本身知道自己是哪条指标：内存类走二进制天性，其余（磁盘、网络流量）走十进制天性。
    return formatBytes(value, byteQuantityKindForMetric(bar.metricId));
  }
  if (bar.unit === "B/s") {
    return formatBytesPerSecond(value);
  }
  if (bar.unit === "%") {
    return formatPercent(value);
  }

  const digits = Math.abs(value) >= 10 ? 1 : 2;
  return `${formatFixedDigits(value, digits)}${bar.unit ? ` ${bar.unit}` : ""}`;
}
