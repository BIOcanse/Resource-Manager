import assert from "node:assert/strict";
import {
  metricFieldCatalogDecoder,
  metricFieldDevicesDecoder,
  type MetricFieldReading
} from "../src/data/metricFields/metricFieldSource.ts";
import { deviceMetricSections } from "../src/features/deviceTopology/deviceMetricFields.ts";
import { fieldCardDefinitions } from "../src/features/monitor/fieldCards.ts";
import {
  applyReadingSourceMode,
  currentReadingSourceMode,
  normalizeReadingSourceMode,
  readingSourceDisplays
} from "../src/presentation/readingSources.ts";

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
// NVMe 综合温度：还没核定首选，两个来源都待确认（其中可靠性计数不支持）。
const nvmeLog = reading("nvme", { sourceAvailability: "unverified", preferredDeviceIdentity: null, preferredFieldName: null });
const nvmeCounter = reading("nvme", { sourceAvailability: "unsupported", sourceRank: 20, preferredDeviceIdentity: null, preferredFieldName: null });
// 单一来源的读取项。
const single = reading("usage", { isPreferredSource: true, preferredDeviceIdentity: "cpu|0", preferredFieldName: "usage" });
const all = [apuPreferred, apuAdlx, apuCpuAlias, tctlSmu, tctlLhm, nvmeLog, nvmeCounter, single, null];

const preferredMode = readingSourceDisplays(all, "preferred");
assert.deepEqual(preferredMode(apuPreferred), { listed: true, mark: null }, "the preferred source is listed without a mark by default");
assert.deepEqual(preferredMode(apuAdlx), { listed: false, mark: null }, "an alternate source is not listed by default");
assert.deepEqual(preferredMode(apuCpuAlias), { listed: false, mark: null }, "the cross-device alias is not listed by default");
assert.deepEqual(preferredMode(tctlLhm), { listed: false, mark: null }, "an unsupported source is not listed");
assert.deepEqual(preferredMode(nvmeLog), { listed: true, mark: "pending" }, "a reading without a preferred source is listed as pending");
assert.deepEqual(preferredMode(nvmeCounter), { listed: false, mark: null }, "an unsupported source of a pending reading is not listed");
assert.deepEqual(preferredMode(single), { listed: true, mark: null });
assert.deepEqual(preferredMode(null), { listed: true, mark: null }, "a field without a reading is listed as before");

const allMode = readingSourceDisplays(all, "all");
assert.deepEqual(allMode(apuPreferred), { listed: true, mark: "preferred" }, "showing all marks the preferred source of a multi-source reading");
assert.deepEqual(allMode(apuAdlx), { listed: true, mark: null });
assert.deepEqual(allMode(apuCpuAlias), { listed: true, mark: null }, "the alias shares the reading id and is listed when showing all");
assert.deepEqual(allMode(tctlSmu), { listed: true, mark: null }, "no Preferred mark when the only other source is unsupported");
assert.deepEqual(allMode(tctlLhm), { listed: false, mark: null }, "unsupported sources stay hidden when showing all");
assert.deepEqual(allMode(nvmeLog), { listed: true, mark: "pending" });
assert.deepEqual(allMode(single), { listed: true, mark: null }, "a single-source reading carries no Preferred mark");
assert.deepEqual(allMode(null), { listed: true, mark: null });

// 读取项只按 id 相等归组：字段名、身份字符串相同但 id 不同的不算同一读取项。
const lone = readingSourceDisplays([reading("a", { isPreferredSource: true }), reading("b")], "all");
assert.equal(lone(reading("a", { isPreferredSource: true })).mark, null, "sources of another reading id are not counted");

// 设置值：认不出的一律回到只列首选。
assert.equal(normalizeReadingSourceMode("all"), "all");
assert.equal(normalizeReadingSourceMode("All"), "preferred");
assert.equal(normalizeReadingSourceMode(undefined), "preferred");
assert.equal(currentReadingSourceMode(), "preferred", "the default lists only the preferred source");

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
    { name: "storage_all_reliability_temperature_celsius", unit: "°C", valueKind: "float64", reading: wireReading(nvmeCounter) }
  ] }
]);
const listedIds = (definitions: ReturnType<typeof fieldCardDefinitions>) =>
  definitions.filter((metric) => metric.readingSource?.listed !== false).map((metric) => metric.id);
applyReadingSourceMode("preferred");
const preferredDefinitions = fieldCardDefinitions(catalog);
assert.equal(preferredDefinitions.length, 6, "every catalog field still has a definition for cards");
assert.deepEqual(listedIds(preferredDefinitions), [
  "cpu_all_usage_percent@cpu|0",
  "gpu_amd_apu_temperature_reported_celsius@gpu|apu",
  "storage_all_nvme_composite_temperature_celsius@disk|0"
]);
assert.equal(preferredDefinitions.find((metric) => metric.id.startsWith("storage_all_nvme"))?.readingSource?.mark, "pending");
applyReadingSourceMode("all");
const allDefinitions = fieldCardDefinitions(catalog);
assert.deepEqual(listedIds(allDefinitions), [
  "cpu_amd_apu_temperature_reported_celsius@cpu|0",
  "cpu_all_usage_percent@cpu|0",
  "gpu_amd_apu_temperature_reported_celsius@gpu|apu",
  "gpu_amd_edge_temperature_celsius@gpu|apu",
  "storage_all_nvme_composite_temperature_celsius@disk|0"
]);
assert.equal(allDefinitions.find((metric) => metric.id === "gpu_amd_apu_temperature_reported_celsius@gpu|apu")?.readingSource?.mark, "preferred");
assert.equal(allDefinitions.find((metric) => metric.id === "cpu_all_usage_percent@cpu|0")?.readingSource?.mark, null);

// 设备详情：同样的规则作用在订阅到的字段值上。
const value = (name: string, extra: Record<string, unknown>) =>
  ({ name, unit: "%", valueKind: "float64", numericValue: 1, integerValue: null, status: 1, observedAt: "2026-10-22T00:00:00Z", ...extra });
const pnp = "ACPI\\NPU0";
const npuDevices = metricFieldDevicesDecoder.decode([
  { deviceIdentity: "npu|0", device: { kind: "npu", address: { kind: "npu", pnpInstanceId: pnp }, displayName: null, ordinal: 0 }, fields: [
    value("npu_all_engine_busy_percent", { reading: wireReading(reading("busy", { isPreferredSource: true, preferredDeviceIdentity: "npu|0", preferredFieldName: "npu_all_engine_busy_percent" })) }),
    value("npu_all_dedicated_memory_committed", { reading: wireReading(reading("busy", { preferredDeviceIdentity: "npu|0", preferredFieldName: "npu_all_engine_busy_percent" })) }),
    value("npu_all_shared_memory_committed", { reading: wireReading(reading("shared", { sourceAvailability: "unverified", preferredDeviceIdentity: null, preferredFieldName: null })) }),
    value("npu_all_shared_memory_resident", {})
  ] }
]);
applyReadingSourceMode("preferred");
assert.deepEqual(deviceMetricSections(npuDevices, pnp)[0].rows.map((row) => row.mark),
  [null, "pending", null], "device details hide the alternate source and mark the pending one");
applyReadingSourceMode("all");
assert.deepEqual(deviceMetricSections(npuDevices, pnp)[0].rows.map((row) => row.mark),
  ["preferred", null, "pending", null], "showing all lists the alternate source and marks the preferred one");
applyReadingSourceMode("preferred");

console.log("readingSources: ok");
