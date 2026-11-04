import { findMetricField, type MetricFieldDevice, type MetricFieldValue } from "../../data/metricFields/metricFieldSource.ts";
import { formatFieldValue } from "../../presentation/fieldValueFormat.ts";
import type { CpuLogicalProcessorModel } from "../../types";

/* CPU topology fields include NUMA capacity/free space and Intel per-thread effective frequency. Hide unbound fields; bound fields without readings remain distinct from zero. */

export const numaFieldNames = Object.freeze([
  "memory_all_numa_node_capacity_mib",
  "memory_all_numa_node_available_bytes"
]);
export const effectiveFrequencyFieldName = "cpu_intel_effective_frequency_mhz";

export interface NumaNodeRow {
  readonly nodeNumber: number;
  /** Undefined means unbound and hidden; null means bound without a reading. */
  readonly capacity: string | null | undefined;
  readonly available: string | null | undefined;
}

/** One row per NUMA node, sorted by node number. */
export function numaNodeRows(devices: readonly MetricFieldDevice[] | null | undefined): NumaNodeRow[] {
  const rows: NumaNodeRow[] = [];
  for (const device of devices ?? []) {
    if (device.address?.kind !== "numa_node") continue;
    const capacity = field(device, numaFieldNames[0]);
    const available = field(device, numaFieldNames[1]);
    rows.push({
      nodeNumber: device.address.nodeNumber,
      // The OS reports per-node capacity as truncated integer MiB; zero is valid.
      capacity: capacity === null ? undefined : formatFieldValue(capacity),
      available: available === null ? undefined : formatFieldValue(available)
    });
  }
  return rows.sort((left, right) => left.nodeNumber - right.nodeNumber);
}

export interface EffectiveFrequencyRow {
  readonly logicalProcessorId: number;
  /** MHz text, or null for a bound but unavailable reading. */
  readonly frequency: string | null;
}

/** Effective frequencies for a core's logical processors; empty when none bind this field. */
export function effectiveFrequencyRows(
  devices: readonly MetricFieldDevice[] | null | undefined,
  logicalProcessors: readonly CpuLogicalProcessorModel[]
): EffectiveFrequencyRow[] {
  const rows: EffectiveFrequencyRow[] = [];
  for (const logical of logicalProcessors) {
    const value = findMetricField(devices, (address) => address.kind === "cpu_logical_processor"
      && address.processorGroup === logical.processorGroup
      && address.groupRelativeIndex === logical.groupRelativeIndex, effectiveFrequencyFieldName);
    if (value === null) continue;
    rows.push({
      logicalProcessorId: logical.id,
      frequency: formatFieldValue(value)
    });
  }
  return rows;
}

function field(device: MetricFieldDevice, name: string): MetricFieldValue | null {
  return device.fields.find((entry) => entry.name === name) ?? null;
}
