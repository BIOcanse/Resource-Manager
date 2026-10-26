import type { MetricFieldDevice } from "../../data/metricFields/metricFieldSource.ts";
import { formatFieldValue } from "../../presentation/fieldValueFormat.ts";
import { readingSourceDisplays, type ReadingSourceMark } from "../../presentation/readingSources.ts";
import { fieldHint, fieldLabel } from "../monitor/fieldCards.ts";
import { uiText } from "../../text.ts";

/*
 * 设备详情里的实时数值：NPU 设备的 OS 忙碌率与内存，AMD 显卡上 ADLX 报告的 NPU 数值。
 *
 * 设备按地址里的 PnP 实例 ID 与拓扑端口的 deviceId 相等匹配。两类数值分开成两节：
 * AMD 的是厂商对这块显卡报告的值，不并进 NPU 设备那一节，也不推断它对应哪个 NPU。
 * 只列这台设备实际绑定的字段；绑定了但当前没有值写「不可用」。忙碌率原值可超过 100%，不截断。
 * 每行的名字和说明用字段的九语显示名（fieldLabel），和监视控制台卡片上的一致。
 * 读数来源统一筛选、加标记，规则和监视卡片选择器相同（presentation/readingSources.ts）。
 */

const npuFields = [
  "npu_all_engine_busy_percent",
  "npu_all_dedicated_memory_committed",
  "npu_all_dedicated_memory_resident",
  "npu_all_shared_memory_committed",
  "npu_all_shared_memory_resident"
] as const;
const amdNpuFields = ["gpu_amd_npu_activity_percent", "gpu_amd_npu_frequency_mhz"] as const;

export const deviceMetricFieldNames = Object.freeze([...npuFields, ...amdNpuFields]);

export interface DeviceMetricRow {
  readonly label: string;
  readonly value: string;
  readonly hint: string | null;
  /** 读数来源标记（首选／待确认）；不加标记为 null。 */
  readonly mark: ReadingSourceMark | null;
}

export interface DeviceMetricSection {
  readonly title: string;
  readonly hint: string | null;
  readonly rows: readonly DeviceMetricRow[];
}

export function deviceMetricSections(
  devices: readonly MetricFieldDevice[] | null | undefined,
  deviceId: string
): DeviceMetricSection[] {
  const text = uiText.deviceSpecialized.metricFields;
  const readingSource = readingSourceDisplays((devices ?? []).flatMap((device) => device.fields.map((field) => field.reading)));
  const sections: DeviceMetricSection[] = [];
  for (const device of devices ?? []) {
    if (device.address === null || !("pnpInstanceId" in device.address) || device.address.pnpInstanceId !== deviceId) continue;
    const npu = device.address.kind === "npu";
    const rows = (npu ? npuFields : amdNpuFields).flatMap((name) => {
      const field = device.fields.find((entry) => entry.name === name);
      const source = field ? readingSource(field.reading) : null;
      return field && source?.listed
        ? [{ label: fieldLabel(name), value: formatFieldValue(field) ?? text.unavailable, hint: fieldHint(name), mark: source.mark }]
        : [];
    });
    if (rows.length === 0) continue;
    sections.push(npu
      ? { title: text.npuTitle, hint: null, rows }
      : { title: text.amdNpuTitle, hint: text.amdNpuHint, rows });
  }
  return sections;
}

