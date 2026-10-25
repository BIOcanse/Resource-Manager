import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  metricFieldCatalogDecoder,
  metricFieldDevicesDecoder
} from "../src/data/metricFields/metricFieldSource.ts";
import {
  deviceLabel,
  fieldCardDefinitions,
  fieldCardSlot,
  fieldCardSlots,
  fieldCardValues
} from "../src/features/monitor/fieldCards.ts";
import { cardExcludedFields } from "../src/features/monitor/canonicalFields.ts";
import { applyByteUnitMode } from "../src/presentation/byteUnits.ts";
import {
  metricCardReading,
  metricDisplayValue,
  metricUnitLabel,
  resourceTableCellText,
  splitMetricUnit
} from "../src/presentation/metricLabels.ts";
import { uiText } from "../src/text.ts";

/*
 * 监视控制台卡片上的规范字段：选择器条目、槽位识别、卡片数值和「设备不在」。
 */

const binding = (scopeKey: string) => ({ scopeKind: "device-field", scopeKey });

// 槽位：绑定是 device-field，且 id 以「@设备身份」结尾；字段名从 id 去掉后缀得到，不解析身份。
assert.deepEqual(fieldCardSlot("gpu_all_temperature_celsius@gpu|a@b", binding("gpu|a@b")),
  { metricId: "gpu_all_temperature_celsius@gpu|a@b", fieldName: "gpu_all_temperature_celsius", deviceIdentity: "gpu|a@b" });
assert.equal(fieldCardSlot("gpu_all_temperature_celsius@gpu|a", binding("gpu|b")), null, "id must end with the bound device");
assert.equal(fieldCardSlot("@gpu|a", binding("gpu|a")), null, "an empty field name is not a slot");
assert.equal(fieldCardSlot("gpu.0.usage", { scopeKind: "gpu", scopeKey: "luid" }), null, "legacy GPU bindings stay legacy");
assert.equal(fieldCardSlot("x@y", null), null);
assert.deepEqual(fieldCardSlots([
  { id: "a", main: "f1@d1", mainBinding: binding("d1"), small: ["cpu.usage", "f2@d2"], smallBindings: [null, binding("d2")] },
  { id: "b", main: "cpu.usage", small: [] }
]).map((slot) => slot.metricId), ["f1@d1", "f2@d2"]);

const excluded = Object.keys(cardExcludedFields)[0];
assert.ok(excluded, "the exclusion list is not empty");
const catalog = metricFieldCatalogDecoder.decode([
  { deviceIdentity: "disk|1", device: { kind: "storage", address: null, displayName: "Fixture SSD", ordinal: 0 }, fields: [
    { name: "storage_all_data_units_written", unit: "512000 B", valueKind: "unsigned64", meaning: "" }
  ] },
  { deviceIdentity: "cpu|t1", device: { kind: "cpu_logical_processor", address: { kind: "cpu_logical_processor", processorGroup: 0, groupRelativeIndex: 1 }, displayName: null, ordinal: 1 }, fields: [
    { name: "cpu_intel_effective_frequency_mhz", unit: "MHz", valueKind: "float64" }
  ] },
  { deviceIdentity: "cpu|t0", device: { kind: "cpu_logical_processor", address: { kind: "cpu_logical_processor", processorGroup: 0, groupRelativeIndex: 0 }, displayName: "  ", ordinal: 0 }, fields: [
    { name: "cpu_intel_effective_frequency_mhz", unit: "MHz", valueKind: "float64" },
    { name: excluded, unit: "count", valueKind: "unsigned64" }
  ] },
  { deviceIdentity: "future|1", device: { kind: "future_kind", address: { kind: "future_kind" }, displayName: null, ordinal: 3 }, fields: [
    { name: "unknown_future_field", unit: "%", valueKind: "float64" }
  ] }
]);
assert.equal(catalog[2].displayName, null, "a blank device name counts as no name");
assert.equal(catalog[3].address, null, "an address kind without a consumer is no address");

const definitions = fieldCardDefinitions(catalog);
// 种类按选择器顺序，同种类按序号；不适合放卡片的字段不列出。
assert.deepEqual(definitions.map((metric) => metric.id), [
  "cpu_intel_effective_frequency_mhz@cpu|t0",
  "cpu_intel_effective_frequency_mhz@cpu|t1",
  "storage_all_data_units_written@disk|1",
  "unknown_future_field@future|1"
]);
const first = definitions[0];
assert.equal(first.scopeKind, "device-field");
assert.equal(first.scopeKey, "cpu|t0");
assert.equal(first.label, uiText.fieldLabel.cpu_intel_effective_frequency_mhz.label, "the label comes from the localized field name");
assert.equal(first.detail, uiText.fieldLabel.cpu_intel_effective_frequency_mhz.hint);
assert.equal(first.group, "逻辑处理器 0", "a device without its own name is named by kind and ordinal");
assert.equal(definitions[2].group, "磁盘 · Fixture SSD", "the device's own name is used when present");
assert.equal(definitions[3].label, "unknown_future_field", "a field without copy shows its field name, visibly");
assert.equal(definitions[3].group, "future_kind 3", "a kind without copy shows its kind name, visibly");
assert.equal(deviceLabel({ kind: "gpu_adapter", displayName: null, ordinal: 0, address: null }, []), "显卡 0");

// 显卡引擎没有自己的名字：同一 PnP 实例的显卡在目录里时带上显卡名；对不上时只写种类和序号。
const gpuCatalog = metricFieldCatalogDecoder.decode([
  { deviceIdentity: "gpu|nv", device: { kind: "gpu_adapter", address: { kind: "gpu_adapter", pnpInstanceId: "PCI\\VEN_10DE&DEV_2D19\\NV", adapterLuid: "E4A6" }, displayName: "NVIDIA GeForce RTX 5060 Laptop GPU", ordinal: 1 }, fields: [] },
  { deviceIdentity: "gpu-engine|E4A6|0|3|0", device: { kind: "gpu_engine", address: { kind: "gpu_engine", pnpInstanceId: "PCI\\VEN_10DE&DEV_2D19\\NV", physicalIndex: 0, engineIndex: 3, engineClass: 0, adapterLuid: "E4A6" }, displayName: null, ordinal: 3 }, fields: [
    { name: "gpu_all_engine_busy_percent", unit: "%", valueKind: "float64" }
  ] },
  { deviceIdentity: "gpu-engine|10B0C|0|0|0", device: { kind: "gpu_engine", address: { kind: "gpu_engine", pnpInstanceId: "PCI\\VEN_1002&DEV_164E\\AMD", physicalIndex: 0, engineIndex: 0, engineClass: 0, adapterLuid: "10B0C" }, displayName: null, ordinal: 0 }, fields: [
    { name: "gpu_all_engine_busy_percent", unit: "%", valueKind: "float64" }
  ] }
]);
assert.deepEqual(gpuCatalog[1].address, { kind: "gpu_engine", pnpInstanceId: "PCI\\VEN_10DE&DEV_2D19\\NV" }, "the engine keeps its adapter's PnP instance ID");
const engineGroups = fieldCardDefinitions(gpuCatalog).map((metric) => metric.group);
assert.deepEqual(engineGroups, ["显卡引擎 0", "显卡引擎 3 · NVIDIA GeForce RTX 5060 Laptop GPU"],
  "an engine is named after its adapter when the adapter is in the catalog, otherwise by kind and ordinal");
const engineSlots = fieldCardSlots([{ id: "e", main: "gpu_all_engine_busy_percent@gpu-engine|E4A6|0|3|0", mainBinding: binding("gpu-engine|E4A6|0|3|0"), small: [] }]);
assert.equal(fieldCardValues(engineSlots, gpuCatalog, [])["gpu_all_engine_busy_percent@gpu-engine|E4A6|0|3|0"].group,
  "显卡引擎 3 · NVIDIA GeForce RTX 5060 Laptop GPU", "the card shows the same name as the picker");

// 卡片数值。
applyByteUnitMode("binary");
const value = (name: string, unit: string, valueKind: string, extra: Record<string, unknown>) =>
  ({ name, unit, valueKind, numericValue: null, integerValue: null, status: 1, observedAt: "2026-10-22T00:00:00Z", sourceId: "s", ...extra });
const devices = metricFieldDevicesDecoder.decode([
  { deviceIdentity: "cpu|t0", device: { kind: "cpu_logical_processor", address: null, displayName: null, ordinal: 0 }, fields: [
    value("cpu_intel_effective_frequency_mhz", "MHz", "float64", { numericValue: 4812.4 })
  ] },
  { deviceIdentity: "cpu|t1", device: { kind: "cpu_logical_processor", address: null, displayName: null, ordinal: 1 }, fields: [
    value("cpu_intel_effective_frequency_mhz", "MHz", "float64", { numericValue: 100, status: 3 })
  ] },
  { deviceIdentity: "disk|1", device: { kind: "storage", address: null, displayName: "Fixture SSD", ordinal: 0 }, fields: [
    value("storage_all_data_units_written", "512000 B", "unsigned64", { integerValue: "2" })
  ] }
]);
const slots = fieldCardSlots([{
  id: "card",
  main: "cpu_intel_effective_frequency_mhz@cpu|t0",
  mainBinding: binding("cpu|t0"),
  small: ["cpu_intel_effective_frequency_mhz@cpu|t1", "storage_all_data_units_written@disk|1", "cpu_intel_effective_frequency_mhz@cpu|gone"],
  smallBindings: [binding("cpu|t1"), binding("disk|1"), binding("cpu|gone")]
}]);
assert.deepEqual(fieldCardValues(slots, null, null), {}, "nothing is claimed before the catalog or the values arrive");
const items = fieldCardValues(slots, catalog, devices);
assert.equal(items["cpu_intel_effective_frequency_mhz@cpu|t0"].displayValue, "4812 MHz");
assert.equal(items["cpu_intel_effective_frequency_mhz@cpu|t0"].group, "逻辑处理器 0");
assert.equal(items["cpu_intel_effective_frequency_mhz@cpu|t1"].displayValue, uiText.dashboard.valueUnavailable,
  "a value that is not current is unavailable, not zero");
assert.equal(items["storage_all_data_units_written@disk|1"].displayValue, "1000 KiB", "the user's byte unit mode applies");
applyByteUnitMode("native");
assert.equal(fieldCardValues(slots, catalog, devices)["storage_all_data_units_written@disk|1"].displayValue, "1.02 MB",
  "in native mode storage amounts use decimal units");
applyByteUnitMode("binary");
assert.equal(items["cpu_intel_effective_frequency_mhz@cpu|gone"].displayValue, uiText.dashboard.deviceAbsent,
  "a device missing from the catalog shows as not present, and the slot is kept");
assert.equal(items["cpu_intel_effective_frequency_mhz@cpu|gone"].label, uiText.fieldLabel.cpu_intel_effective_frequency_mhz.label);
const beforeValues = fieldCardValues(slots, catalog, null);
assert.deepEqual(Object.keys(beforeValues), ["cpu_intel_effective_frequency_mhz@cpu|gone"],
  "before values arrive only the absent device is known");
const beforeCatalog = fieldCardValues(slots, null, devices);
assert.equal(beforeCatalog["cpu_intel_effective_frequency_mhz@cpu|gone"], undefined,
  "before the catalog arrives a device missing from the values is neither absent nor unavailable");
assert.equal(beforeCatalog["cpu_intel_effective_frequency_mhz@cpu|t0"].displayValue, "4812 MHz");

// 卡片上怎么显示：有值时数值和单位分开、拼起来与格式化结果相同；四种没有值各有短话和原因。
const definitionOf = (id: string) => definitions.find((metric) => metric.id === id);
const reading = (id: string) => metricCardReading(definitionOf(id), items[id]);
assert.deepEqual(reading("cpu_intel_effective_frequency_mhz@cpu|t0"),
  { value: "4812", gap: " ", unit: "MHz", missing: false, title: undefined });
assert.deepEqual(splitMetricUnit("137.5%"), { value: "137.5", gap: "", unit: "%" });
assert.deepEqual(splitMetricUnit("21.4 / 31.2 GiB"), { value: "21.4 / 31.2", gap: " ", unit: "GiB" });
assert.deepEqual(splitMetricUnit("0.42"), { value: "0.42", gap: "", unit: "" }, "a number without a unit stays whole");
assert.deepEqual(splitMetricUnit("0x1F"), { value: "0x1F", gap: "", unit: "" }, "hex digits are not a unit");
const unavailable = reading("cpu_intel_effective_frequency_mhz@cpu|t1");
assert.equal(unavailable.missing, true, "a field without a current value is shown as missing");
assert.equal(unavailable.value, uiText.dashboard.valueUnavailable);
assert.equal(unavailable.title, uiText.dashboard.noReadingHint);
const absent = reading("cpu_intel_effective_frequency_mhz@cpu|gone");
assert.deepEqual([absent.missing, absent.value, absent.title],
  [true, uiText.dashboard.deviceAbsent, uiText.dashboard.deviceAbsentHint], "an absent device keeps its own wording and hint");
assert.deepEqual(metricCardReading(undefined, undefined),
  { value: "--", gap: "", unit: "", missing: true, title: uiText.dashboard.notInCatalogHint }, "an id the catalog does not list");
const sensor = { id: "cpu.temperature", label: "CPU", group: "cpu", unit: "°C", preferredSlot: "small", selectable: false,
  requiredComponentName: "LibreHardwareMonitor", disabledReason: { domain: 3, code: 2, args: ["LibreHardwareMonitor"] } };
const needsComponent = metricCardReading(sensor, { id: "cpu.temperature", displayValue: "N/A", numericValue: null, unit: "°C" });
assert.deepEqual([needsComponent.missing, needsComponent.value], [true, uiText.dashboard.needsComponent]);
assert.ok(needsComponent.title?.includes("LibreHardwareMonitor"), "the hint names the missing component");
const noReading = metricCardReading({ ...sensor, selectable: true, disabledReason: null },
  { id: "cpu.temperature", displayValue: "N/A", numericValue: null, unit: "°C" });
assert.deepEqual([noReading.missing, noReading.value, noReading.title], [true, "N/A", uiText.dashboard.noReadingHint]);
// 字节速率走字节单位设置，不用后端拼好的「MB/s」。
const rate = { id: "disk.total.readBytesPerSec", displayValue: "212.47 MB/s", numericValue: 222_789_000, unit: "B/s" };
assert.equal(metricDisplayValue(rate), "212 MiB/s", "binary mode");
applyByteUnitMode("native");
assert.equal(metricDisplayValue(rate), "223 MB/s", "native mode uses decimal units for transfer rates");
applyByteUnitMode("binary");
assert.equal(metricUnitLabel("MiB"), "B");
assert.equal(metricUnitLabel("count"), "", "internal unit tokens are not shown");
assert.equal(metricUnitLabel("°C"), "°C");

// 卡片设置的绑定：选择器条目给出 device-field 绑定，normalizer 用同一条槽位规则保留它。
const config = readFileSync(new URL("../src/stores/monitorConfig.ts", import.meta.url), "utf8");
assert.match(config, /if \(metric\?\.scopeKind === deviceFieldScopeKind && metric\.scopeKey\)/);
assert.match(config, /if \(fieldCardSlot\(metricId, binding\)\)/);
const store = readFileSync(new URL("../src/stores/monitorStore.ts", import.meta.url), "utf8");
assert.match(store, /metricBindingForDefinition\(\s*cardCatalog\(\)\.find/, "the picker's own catalog gives the binding");
assert.match(store, /!fieldCardSlot\(card\.main, card\.mainBinding\)/, "device fields stay out of the legacy metric subscription");

console.log("fieldCards: ok");

assert.equal(resourceTableCellText({ value: 4242, unit: "" }), "4242");
assert.equal(resourceTableCellText({ value: null, unit: "" }), "--");
assert.equal(resourceTableCellText({ displayValue: "TEST-PC\\test-user", unit: "" }), "TEST-PC\\test-user");
assert.equal(resourceTableCellText({ displayValue: "x64", unit: "" }), "x64");
assert.equal(resourceTableCellText({ displayValue: null, unit: "", availability: "Unavailable" }), "--");
assert.equal(resourceTableCellText({ value: 12, unit: "%" }), "12.0%");
