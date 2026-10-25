import type { RawMetricDefinition, RawMetricValue } from "../data/monitor/metricWire.ts";
import type { MetricDefinition, MetricValue } from "../types.ts";
import { uiText } from "../text.ts";
import { localizedMetricLabel } from "./metricLabels.ts";
import { componentDisplayName } from "./userFacingText.ts";

/** The frontend owns these view fields; none are read from the wire. */
export function presentMetricDefinition(fact: RawMetricDefinition): MetricDefinition {
  return {
    ...fact,
    get label() { return metricName(fact.id, fact.fieldName); },
    group: metricGroup(fact.id, fact.scopeKind),
    preferredSlot: mainMetrics.test(fact.id) ? "main" : "small",
    get requiredComponentName() {
      return fact.requiredComponentId ? componentDisplayName(fact.requiredComponentId) : null;
    }
  };
}

export function presentMetricValue(fact: RawMetricValue): MetricValue {
  return {
    ...fact,
    get label() { return metricName(fact.id, fact.fieldName); },
    group: metricGroup(fact.id)
  };
}

/** Canonical fields use the existing device-field picker (with device names and source marks). */
export function metricOfferedInPicker(metric: MetricDefinition): boolean {
  return metric.readingSource?.listed !== false
    // iGPU sensor aliases were already excluded from the CPU picker; the GPU/device fields own them.
    && !cpuGpuAliases.has(metric.id)
    && (!metric.fieldName || localizedMetricLabel(metric.id) !== metric.id);
}

const cpuGpuAliases = new Set(["cpu.igpuFrequency", "cpu.igpuVoltage", "cpu.igpuTemperature"]);

function metricName(id: string, fieldName?: string | null): string {
  return localizedMetricLabel(id, fieldName ? uiText.fieldLabel[fieldName]?.label ?? fieldName : undefined);
}

const mainMetrics = /^(?:cpu\.usage|memory\.usage|virtualMemory\.usage|gpu\.\d+\.(?:usage|vram)|disk\.total\.activePercent|network\.total\.utilizationPercent|target\.fps)$/;

/** Legacy request aliases remain stable; grouping is entirely a frontend choice. */
export function metricGroup(id: string, scopeKind?: string | null): string {
  if (/^(?:fan\.|cpu\.fan|gpu\.\d+\.fan)/.test(id)) return "fan";
  const gpu = /^gpu\.(\d+)\./.exec(id);
  if (gpu) return `gpu.${gpu[1]}`;
  if (id.startsWith("target.")) return "target-software";
  if (id.startsWith("virtualMemory.")) return "virtual-memory";
  if (id.startsWith("system.motherboard") || id.startsWith("system.vrm") || id.startsWith("system.chipset")) return "motherboard";
  const resource = /^(cpu|memory|disk|network)\./.exec(id)?.[1];
  return resource ?? scopeKind ?? "system";
}
