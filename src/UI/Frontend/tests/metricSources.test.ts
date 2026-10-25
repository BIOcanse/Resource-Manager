// 先选项目、再选来源：部件地址的来源是后端候选来源（自动选择用 isSelected 那个），
// 不属于部件地址的设备字段按读取项分组；都按后端排序，不支持的来源不列。
import assert from "node:assert/strict";
import { metricSourceChoiceLabel, metricSourceChoices } from "../src/features/monitor/metricSources.ts";
import { localizedMetricLabel } from "../src/presentation/metricLabels.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { applyLanguage, currentLanguage, uiText } from "../src/text.ts";
import { deviceFieldScopeKind, fieldCardId } from "../src/features/monitor/fieldCards.ts";
import type { MetricFieldReading } from "../src/data/metricFields/metricFieldSource.ts";
import type { MetricDefinition, MetricSourceCandidate } from "../src/types.ts";

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
const candidate = (metric: MetricDefinition, isSelected: boolean, extra: Partial<MetricSourceCandidate> = {}): MetricSourceCandidate => ({
  fieldName: metric.id.split("@")[0], deviceIdentity: metric.scopeKey!, sourceId: "fixture", reading: metric.reading ?? null,
  sourceAvailability: metric.reading?.sourceAvailability ?? "unverified", isSelected, ...extra
});
const unverified = field("lhm_edge", "gpu-0", null);
const absent = field("gone_edge", "gpu-9", null);
// 后端的候选顺序就是展示顺序（这里故意让被选中的 ADLX 排第二）；后端已在 MetricCatalog 按 rank 排好。
const address: MetricDefinition = {
  id: "gpu.0.temperature", label: "GPU temperature", group: "gpu.0", unit: "°C", preferredSlot: "main",
  fieldName: "adlx_edge", deviceIdentity: "gpu-0",
  candidateSources: [candidate(smu, false), candidate(adlx, true), candidate(unverified, false),
    candidate(unsupported, false), candidate(absent, false)]
};
const catalog = [address, unsupported, adlx, hotspot, smu, single, unverified];

const fromAddress = metricSourceChoices("gpu.0.temperature", catalog);
assert.deepEqual(fromAddress.map((choice) => [choice.metricId, choice.automatic, choice.preferred]), [
  ["gpu.0.temperature", true, false],
  [smu.id, false, true],
  [adlx.id, false, false],
  [unverified.id, false, false]
], "Automatic first, then the backend candidates in backend order; unsupported and undiscovered candidates are not offered");
assert.equal(fromAddress[0].source, adlx, "Automatic names the candidate the backend marks selected, not the reading's preferred source");

assert.deepEqual(metricSourceChoices(adlx.id, catalog).map((choice) => choice.metricId),
  fromAddress.map((choice) => choice.metricId), "Choosing a source keeps the same item and choices");
assert.deepEqual(metricSourceChoices(unverified.id, catalog).map((choice) => choice.metricId),
  fromAddress.map((choice) => choice.metricId), "A candidate without a reading still belongs to its address");
assert.equal(metricSourceChoices("gpu.0.temperature", [{ ...address, candidateSources: address.candidateSources!.map((item) => ({ ...item, isSelected: false })) }, smu, adlx])[0].source,
  null, "No selected candidate: automatic names no source");
assert.deepEqual(metricSourceChoices("gpu.0.temperature", [{ ...address, candidateSources: [] }, smu, adlx]), [],
  "An address without candidates has nothing to choose; the reading group is not used for it");
assert.deepEqual(metricSourceChoices(hotspot.id, catalog), [], "A different quantity with one source has nothing to choose");
assert.deepEqual(metricSourceChoices(single.id, catalog), [], "A field without a reading has nothing to choose");
assert.deepEqual(metricSourceChoices(null, catalog), []);
assert.deepEqual(metricSourceChoices("missing", catalog), []);

// 不属于部件地址的字段：同一读取项按后端 rank 排，没有“自动选择”，不支持的不列。
assert.deepEqual(metricSourceChoices(smu.id, [unsupported, adlx, smu]).map((choice) => [choice.metricId, choice.automatic, choice.preferred]),
  [[smu.id, false, true], [adlx.id, false, false]]);

// 来源名里的分组：分组键（cpu、gpu.0）和列表组标题一样翻成当前语言，设备名原样；九种语言都核对，
// 自动选择里写出的来源名也一样。
const keyed = field("cpu_package_temperature", "cpu", reading("cpu-package", 10));
const named = field("smu_edge", "显卡 · Reading GPU", reading("edge", 10));
let sourceNameLanguages = 0;
for (const { id } of languageOptions) {
  if (id === "system") continue;
  const copy = await loadAppCopy(id);
  applyLanguage(id);
  for (let attempt = 0; currentLanguage() !== id && attempt < 100; attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.equal(currentLanguage(), id);
  assert.equal(uiText.status.metricGroup.cpu, copy.status.metricGroup.cpu, `${id}: the language is active`);
  const keyedName = `${localizedMetricLabel(keyed.id, keyed.label)} · ${copy.status.metricGroup.cpu}`;
  assert.equal(metricSourceChoiceLabel({ metricId: keyed.id, automatic: false, source: keyed, preferred: false }), keyedName,
    `${id}: a source's group key is shown by its localized group name`);
  assert.equal(metricSourceChoiceLabel({ metricId: "cpu.temperature", automatic: true, source: keyed, preferred: false }),
    copy.metricPicker.sourceAutomatic(keyedName), `${id}: automatic names the source with the localized group`);
  assert.equal(metricSourceChoiceLabel({ metricId: named.id, automatic: false, source: named, preferred: false }),
    `${localizedMetricLabel(named.id, named.label)} · 显卡 · Reading GPU`, `${id}: a device name stays as it is`);
  sourceNameLanguages += 1;
}
assert.equal(sourceNameLanguages, 9, "All nine languages are checked");
applyLanguage("zh-CN");

console.log("metricSources: item then source choices follow the backend candidates and reading groups");
