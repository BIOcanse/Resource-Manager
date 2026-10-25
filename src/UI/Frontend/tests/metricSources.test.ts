// 先选项目、再选来源：同一读取项的来源按后端排序列出，自动选择是部件地址，不支持的来源不列。
import assert from "node:assert/strict";
import { metricSourceChoices } from "../src/features/monitor/metricSources.ts";
import { deviceFieldScopeKind, fieldCardId } from "../src/features/monitor/fieldCards.ts";
import type { MetricFieldReading } from "../src/data/metricFields/metricFieldSource.ts";
import type { MetricDefinition } from "../src/types.ts";

const reading = (id: string, sourceRank: number, extra: Partial<MetricFieldReading> = {}): MetricFieldReading => ({
  id, quantity: "gpu.edge_temperature", sourceRank, sourceAvailability: "available", isPreferredSource: false,
  preferredDeviceIdentity: "gpu-0", preferredFieldName: "smu_edge", ...extra
});
const field = (name: string, device: string, value: MetricFieldReading | null): MetricDefinition => ({
  id: fieldCardId(name, device), label: name, group: device, unit: "°C", preferredSlot: "small",
  scopeKind: deviceFieldScopeKind, scopeKey: device, reading: value
});

const smu = field("smu_edge", "gpu-0", reading("edge", 10, { isPreferredSource: true }));
const adlx = field("adlx_edge", "gpu-0", reading("edge", 20));
const unsupported = field("wmi_edge", "gpu-0", reading("edge", 30, { sourceAvailability: "unsupported" }));
const hotspot = field("smu_hotspot", "gpu-0", reading("hotspot", 10, { isPreferredSource: true }));
const single = field("fan_rpm", "fan-0", null);
const address: MetricDefinition = {
  id: "gpu.0.temperature", label: "GPU temperature", group: "gpu.0", unit: "°C", preferredSlot: "main",
  fieldName: "smu_edge", deviceIdentity: "gpu-0"
};
// Listed in reverse rank order so the test also checks that the backend rank orders the choices.
const catalog = [address, unsupported, adlx, hotspot, smu, single];

const fromAddress = metricSourceChoices("gpu.0.temperature", catalog);
assert.deepEqual(fromAddress.map((choice) => [choice.metricId, choice.automatic, choice.preferred]), [
  ["gpu.0.temperature", true, false],
  [smu.id, false, true],
  [adlx.id, false, false]
], "Automatic first, then the sources by backend rank; the unsupported source is not offered");
assert.equal(fromAddress[0].source, smu, "Automatic names the source the backend currently maps the address to");

assert.deepEqual(metricSourceChoices(adlx.id, catalog).map((choice) => choice.metricId),
  fromAddress.map((choice) => choice.metricId), "Choosing a source keeps the same item and choices");
assert.deepEqual(metricSourceChoices(hotspot.id, catalog), [], "A different quantity with one source has nothing to choose");
assert.deepEqual(metricSourceChoices(single.id, catalog), [], "A field without a reading has nothing to choose");
assert.deepEqual(metricSourceChoices(null, catalog), []);
assert.deepEqual(metricSourceChoices("missing", catalog), []);

// Without a part address the sources are still offered, without an automatic choice.
assert.deepEqual(metricSourceChoices(smu.id, [smu, adlx]).map((choice) => choice.automatic), [false, false]);

console.log("metricSources: item then source choices follow the backend reading groups");
