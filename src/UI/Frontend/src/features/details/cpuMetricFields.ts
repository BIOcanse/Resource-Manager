import { findMetricField, type MetricFieldDevice, type MetricFieldValue } from "../../data/metricFields/metricFieldSource.ts";
import { formatFieldValue } from "../../presentation/fieldValueFormat.ts";
import type { CpuLogicalProcessorModel } from "../../types";

/*
 * CPU 拓扑页上用到的规范字段：NUMA 节点容量／可用量，Intel 逐线程有效频率。
 *
 * 没有绑定（这台机器不适用，例如 AMD 上没有 Intel 有效频率）就整行不显示；
 * 绑定了但当前没有值就显示「无读数」，不显示 0。
 */

export const numaFieldNames = Object.freeze([
  "memory_all_numa_node_capacity_mib",
  "memory_all_numa_node_available_bytes"
]);
export const effectiveFrequencyFieldName = "cpu_intel_effective_frequency_mhz";

export interface NumaNodeRow {
  readonly nodeNumber: number;
  /** 未绑定为 undefined（不显示这一项），绑定了但当前没有值为 null（显示「无读数」）。 */
  readonly capacity: string | null | undefined;
  readonly available: string | null | undefined;
}

/** 每个 NUMA 节点一行，按节点号排序。 */
export function numaNodeRows(devices: readonly MetricFieldDevice[] | null | undefined): NumaNodeRow[] {
  const rows: NumaNodeRow[] = [];
  for (const device of devices ?? []) {
    if (device.address?.kind !== "numa_node") continue;
    const capacity = field(device, numaFieldNames[0]);
    const available = field(device, numaFieldNames[1]);
    rows.push({
      nodeNumber: device.address.nodeNumber,
      // 容量是 OS 按节点向下取整的整数 MiB，零也是有效值。
      capacity: capacity === null ? undefined : formatFieldValue(capacity),
      available: available === null ? undefined : formatFieldValue(available)
    });
  }
  return rows.sort((left, right) => left.nodeNumber - right.nodeNumber);
}

export interface EffectiveFrequencyRow {
  readonly logicalProcessorId: number;
  /** MHz 文本；绑定了但当前不可用时为 null。 */
  readonly frequency: string | null;
}

/**
 * 一个物理核心下各逻辑处理器的有效频率。
 * 这些逻辑处理器都没有绑定该字段时返回空数组，界面不显示这一行。
 */
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
