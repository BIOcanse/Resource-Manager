// 浮窗一项先选项目、再选来源：来源和卡片选择器同一出处（部件地址的后端候选），自动选择不记来源，
// 具体来源记字段名和设备身份；项目移除或换掉时来源一起去掉；记下的来源不在选项里时不勾任何一项。
import assert from "node:assert/strict";
import { overlaySourceChoices, overlaySourceIndex, overlaySourcesFor, withOverlaySource } from "../src/features/overlay/overlaySources.ts";
import { deviceFieldScopeKind, fieldCardId } from "../src/features/monitor/fieldCards.ts";
import { metricSourceName } from "../src/features/monitor/metricSources.ts";
import type { MetricFieldReading } from "../src/data/metricFields/metricFieldSource.ts";
import type { MetricDefinition, MetricSourceCandidate } from "../src/types.ts";
import { uiText } from "../src/text.ts";

const reading = (sourceRank: number, isPreferredSource = false): MetricFieldReading => ({
  id: "edge", quantity: "gpu.edge_temperature", sourceRank, sourceAvailability: "available", isPreferredSource,
  preferredDeviceIdentity: "gpu|0", preferredFieldName: "smu_edge"
});
const field = (name: string, device: string, value: MetricFieldReading): MetricDefinition => ({
  id: fieldCardId(name, device), label: name, group: `device ${device}`, unit: "°C", preferredSlot: "small",
  scopeKind: deviceFieldScopeKind, scopeKey: device, reading: value
});
const smu = field("smu_edge", "gpu|0", reading(10, true));
const adlx = field("adlx_edge", "gpu|0", reading(20));
const candidate = (metric: MetricDefinition, isSelected: boolean): MetricSourceCandidate => ({
  fieldName: metric.id.split("@")[0], deviceIdentity: metric.scopeKey!, sourceId: "fixture", reading: metric.reading ?? null,
  sourceAvailability: "available", isSelected
});
const address: MetricDefinition = { id: "gpu.0.temperature", label: "", group: "gpu.0", unit: "°C", preferredSlot: "main",
  candidateSources: [candidate(smu, false), candidate(adlx, true)] };
const fps: MetricDefinition = { id: "target.fps", label: "", group: "Target", unit: "FPS", preferredSlot: "main" };
const catalog = [fps, address, smu, adlx];

const choices = overlaySourceChoices("gpu.0.temperature", catalog);
assert.deepEqual(choices.map((choice) => [choice.source, choice.preferred]), [
  [null, false],
  [{ fieldName: "smu_edge", deviceIdentity: "gpu|0" }, true],
  [{ fieldName: "adlx_edge", deviceIdentity: "gpu|0" }, false]
], "Automatic records no source; a chosen candidate records its field and device");
assert.equal(choices[0].label, uiText.metricPicker.sourceAutomatic(metricSourceName(adlx)), "Automatic names the backend's current selection");
assert.equal(choices[2].label, metricSourceName(adlx));
assert.deepEqual(overlaySourceChoices("target.fps", catalog), [], "Target metrics have no sources to choose");
assert.deepEqual(overlaySourceChoices("gpu.0.temperature", [fps, address]), [], "Candidates not in the field catalog yet are not offered");

assert.equal(overlaySourceIndex(choices, undefined), 0);
assert.equal(overlaySourceIndex(choices, { fieldName: "adlx_edge", deviceIdentity: "gpu|0" }), 2);
assert.equal(overlaySourceIndex(choices, { fieldName: "adlx_edge", deviceIdentity: "gpu|1" }), -1,
  "A stored source that is no longer offered checks nothing instead of showing automatic");

const metrics = ["target.fps", "gpu.0.temperature", "cpu.temperature"];
const chosen = withOverlaySource({}, metrics, "gpu.0.temperature", { fieldName: "adlx_edge", deviceIdentity: "gpu|0" });
assert.deepEqual(chosen, { "gpu.0.temperature": { fieldName: "adlx_edge", deviceIdentity: "gpu|0" } });
const both = withOverlaySource(chosen, metrics, "cpu.temperature", { fieldName: "tctl", deviceIdentity: "cpu|0" });
assert.deepEqual(Object.keys(both), ["cpu.temperature", "gpu.0.temperature"], "Sources are kept in a stable order");
assert.deepEqual(withOverlaySource(both, metrics, "gpu.0.temperature", null), { "cpu.temperature": { fieldName: "tctl", deviceIdentity: "cpu|0" } },
  "Automatic removes the item's source");
assert.deepEqual(overlaySourcesFor(both, ["target.fps", "cpu.temperature"]), { "cpu.temperature": { fieldName: "tctl", deviceIdentity: "cpu|0" } },
  "Removing an item removes its source");

console.log("overlaySources: overlay item sources, stored choice and pruning passed");
