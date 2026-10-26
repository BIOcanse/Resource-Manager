import assert from "node:assert/strict";
import {
  metricFieldCatalogDecoder,
  metricFieldDevicesDecoder,
  type MetricFieldReading
} from "../src/data/metricFields/metricFieldSource.ts";
import { deviceMetricSections } from "../src/features/deviceTopology/deviceMetricFields.ts";
import { fieldCardDefinitions } from "../src/features/monitor/fieldCards.ts";
import { readingSourceDisplays } from "../src/presentation/readingSources.ts";

/*
 * 读数来源的列出与标记（presentation/readingSources.ts）：首选、次选、待确认、不支持、
 * 跨设备别名、reading 为 null；以及选择器条目和设备详情按它筛选。
 */

const reading = (id: string, extra: Partial<MetricFieldReading> = {}): MetricFieldReading => ({
  id,
  quantity: "q",
  sourceRank: 10,
  sourceAvailability: "available",
  isPreferredSource: false,
  preferredDeviceIdentity: "gpu|apu",
  preferredFieldName: "gpu_amd_apu_temperature_reported_celsius",
  ...extra
});

// APU 温度：GPU 设备上的 SMU 字段是首选，同设备的 ADLX 是次选，CPU 设备下的同一路 SMU 是显示别名（同一个 id）。
const apuPreferred = reading("apu-edge", { isPreferredSource: true });
const apuAdlx = reading("apu-edge", { sourceRank: 20 });
const apuCpuAlias = reading("apu-edge");
// Tctl：本机只有 SMU 可用，LHM 来源不支持。
const tctlSmu = reading("tctl", { isPreferredSource: true, preferredDeviceIdentity: "cpu|0", preferredFieldName: "tctl_smu" });
const tctlLhm = reading("tctl", { sourceAvailability: "unsupported", sourceRank: 20, preferredDeviceIdentity: "cpu|0", preferredFieldName: "tctl_smu" });
// NVMe 综合温度：还没核定首选，健康日志和可靠性计数两个来源都待确认；传感器 1 不支持。
const nvmeLog = reading("nvme", { sourceAvailability: "unverified", preferredDeviceIdentity: null, preferredFieldName: null });
const nvmeReliability = reading("nvme", { sourceAvailability: "unverified", sourceRank: 20, preferredDeviceIdentity: null, preferredFieldName: null });
const nvmeCounter = reading("nvme", { sourceAvailability: "unsupported", sourceRank: 30, preferredDeviceIdentity: null, preferredFieldName: null });
// 单来源、还没核定的读取项：没有可选的来源，不标「待确认」。
const lonePending = reading("fan", { sourceAvailability: "unverified", preferredDeviceIdentity: null, preferredFieldName: null });
// 单一来源的读取项。
const single = reading("usage", { isPreferredSource: true, preferredDeviceIdentity: "cpu|0", preferredFieldName: "usage" });
const all = [apuPreferred, apuAdlx, apuCpuAlias, tctlSmu, tctlLhm, nvmeLog, nvmeReliability, nvmeCounter, lonePending, single, null];

const display = readingSourceDisplays(all);
assert.deepEqual(display(apuPreferred), { listed: true, mark: null }, "the preferred source is listed without a mark in the first step");
assert.deepEqual(display(apuAdlx), { listed: false, mark: null }, "an alternate source is not listed in the first step");
assert.deepEqual(display(apuCpuAlias), { listed: false, mark: null }, "the cross-device alias is not listed in the first step");
assert.deepEqual(display(tctlLhm), { listed: false, mark: null }, "an unsupported source is not listed");
assert.deepEqual(display(nvmeLog), { listed: true, mark: "pending" }, "a multi-source reading without a preferred source lists every source as pending");
assert.deepEqual(display(nvmeReliability), { listed: true, mark: "pending" });
assert.deepEqual(display(lonePending), { listed: true, mark: null }, "a single-source reading awaiting confirmation is listed without a mark");
assert.deepEqual(display(nvmeCounter), { listed: false, mark: null }, "an unsupported source of a pending reading is not listed");
assert.deepEqual(display(single), { listed: true, mark: null });
assert.deepEqual(display(null), { listed: true, mark: null }, "a field without a reading is listed as before");

// 读取项只按 id 相等归组，别的读取项不使单来源多出标记。
const lone = readingSourceDisplays([reading("a"), reading("b")]);
assert.equal(lone(reading("a")).mark, null);

// 选择器条目：目录里每个字段都有定义（卡片照常找得到设备名），listed 决定选择器是否列出。
const wireReading = (value: MetricFieldReading) => ({ ...value });
const catalog = metricFieldCatalogDecoder.decode([
  { deviceIdentity: "cpu|0", device: { kind: "cpu", address: null, displayName: null, ordinal: 0 }, fields: [
    { name: "cpu_amd_apu_temperature_reported_celsius", unit: "°C", valueKind: "float64", reading: wireReading(apuCpuAlias) },
    { name: "cpu_all_usage_percent", unit: "%", valueKind: "float64", reading: null }
  ] },
  { deviceIdentity: "gpu|apu", device: { kind: "gpu_adapter", address: null, displayName: "Radeon", ordinal: 0 }, fields: [
    { name: "gpu_amd_apu_temperature_reported_celsius", unit: "°C", valueKind: "float64", reading: wireReading(apuPreferred) },
    { name: "gpu_amd_edge_temperature_celsius", unit: "°C", valueKind: "float64", reading: wireReading(apuAdlx) }
  ] },
  { deviceIdentity: "disk|0", device: { kind: "storage", address: null, displayName: "SSD", ordinal: 0 }, fields: [
    { name: "storage_all_nvme_composite_temperature_celsius", unit: "°C", valueKind: "float64", reading: wireReading(nvmeLog) },
    { name: "disk_all_temperature_celsius", unit: "°C", valueKind: "float64", reading: wireReading(nvmeReliability) },
    { name: "storage_all_nvme_temperature_sensor_1_celsius", unit: "°C", valueKind: "float64", reading: wireReading(nvmeCounter) }
  ] }
]);
const listedIds = (definitions: ReturnType<typeof fieldCardDefinitions>) =>
  definitions.filter((metric) => metric.readingSource?.listed !== false).map((metric) => metric.id);
const definitions = fieldCardDefinitions(catalog);
assert.equal(definitions.length, 7, "every catalog field still has a definition for cards");
assert.deepEqual(listedIds(definitions), [
  "cpu_all_usage_percent@cpu|0",
  "gpu_amd_apu_temperature_reported_celsius@gpu|apu",
  "storage_all_nvme_composite_temperature_celsius@disk|0",
  "disk_all_temperature_celsius@disk|0"
]);
assert.equal(definitions.find((metric) => metric.id.startsWith("storage_all_nvme"))?.readingSource?.mark, "pending");

// 设备详情：同样的规则作用在订阅到的字段值上。
const value = (name: string, extra: Record<string, unknown>) =>
  ({ name, unit: "%", valueKind: "float64", numericValue: 1, integerValue: null, status: 1, observedAt: "2026-10-22T00:00:00Z", ...extra });
const pnp = "ACPI\\NPU0";
const npuDevices = metricFieldDevicesDecoder.decode([
  { deviceIdentity: "npu|0", device: { kind: "npu", address: { kind: "npu", pnpInstanceId: pnp }, displayName: null, ordinal: 0 }, fields: [
    value("npu_all_engine_busy_percent", { reading: wireReading(reading("busy", { isPreferredSource: true, preferredDeviceIdentity: "npu|0", preferredFieldName: "npu_all_engine_busy_percent" })) }),
    value("npu_all_dedicated_memory_committed", { reading: wireReading(reading("busy", { preferredDeviceIdentity: "npu|0", preferredFieldName: "npu_all_engine_busy_percent" })) }),
    value("npu_all_shared_memory_committed", { reading: wireReading(reading("shared", { sourceAvailability: "unverified", preferredDeviceIdentity: null, preferredFieldName: null })) }),
    value("npu_all_shared_memory_resident", { reading: wireReading(reading("shared", { sourceAvailability: "unverified", sourceRank: 20, preferredDeviceIdentity: null, preferredFieldName: null })) }),
    value("npu_all_dedicated_memory_resident", { reading: wireReading(reading("lone", { sourceAvailability: "unverified", preferredDeviceIdentity: null, preferredFieldName: null })) })
  ] }
]);
assert.deepEqual(deviceMetricSections(npuDevices, pnp)[0].rows.map((row) => row.mark),
  [null, null, "pending", "pending"], "device details hide the alternate source, leave the single pending source unmarked and mark the pending multi-source reading");
console.log("readingSources: ok");
