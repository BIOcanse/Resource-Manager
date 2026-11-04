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
    // Use memory byte scaling for memory metrics and storage scaling for disk and network metrics.
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
