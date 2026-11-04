import type { MetricFieldDevice } from "../../data/metricFields/metricFieldSource.ts";
import { formatFieldValue } from "../../presentation/fieldValueFormat.ts";
import { readingSourceDisplays, type ReadingSourceMark } from "../../presentation/readingSources.ts";
import { fieldHint, fieldLabel } from "../monitor/fieldCards.ts";
import { uiText } from "../../text.ts";

/* Match fields by PnP identity. Keep OS NPU metrics separate from GPU-reported AMD NPU metrics without inferring a shared device; preserve unclamped values and shared field/source presentation. */

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
  /** Preferred or unverified source marker; null means unmarked. */
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

