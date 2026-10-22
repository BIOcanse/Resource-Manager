import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  metricFieldCatalogDecoder,
  metricFieldDevicesDecoder
} from "../src/data/metricFields/metricFieldSource.ts";
import {
  fieldCardDefinitions,
  fieldCardSlots,
  fieldCardValues
} from "../src/features/monitor/fieldCards.ts";
import { deviceMetricSections } from "../src/features/deviceTopology/deviceMetricFields.ts";
import { applyByteUnitMode } from "../src/presentation/byteUnits.ts";
import { uiText } from "../src/text.ts";

/*
 * 用后端真实端点的响应（Kestrel + 确定性夹具，2026-10-22 主线程抓取，未改内容）核对前端解码和卡片。
 * fixtures/metricFieldsWire/catalog.json 是 GET /api/metrics/fields/catalog 的原样响应；
 * subscription-frame.json 是统一订阅流里按字段订阅（不带 device）的一帧。
 */

const read = (name: string) => JSON.parse(readFileSync(new URL(`./fixtures/metricFieldsWire/${name}`, import.meta.url), "utf8"));
const catalog = metricFieldCatalogDecoder.decode(read("catalog.json"));
const frame = read("subscription-frame.json");
const devices = metricFieldDevicesDecoder.decode(frame.value);

assert.deepEqual(catalog.map((device) => device.kind).sort(),
  ["cpu_logical_processor", "fan", "gpu_adapter", "network_interface", "npu", "numa_node", "storage"]);
// 目录和值里同一台设备的描述一致。
for (const device of devices) {
  const listed = catalog.find((entry) => entry.deviceIdentity === device.deviceIdentity);
  assert.ok(listed, `${device.deviceIdentity} is in the catalog`);
  assert.deepEqual({ ...device, fields: [] }, { ...listed, fields: [] });
}

// 前端消费的四类地址按约定解出；其余种类的地址前端还没有消费者，当作没有地址。
const byKind = (kind: string) => devices.find((device) => device.kind === kind)!;
assert.deepEqual(byKind("cpu_logical_processor").address, { kind: "cpu_logical_processor", processorGroup: 1, groupRelativeIndex: 2 });
assert.deepEqual(byKind("numa_node").address, { kind: "numa_node", nodeNumber: 7 });
assert.deepEqual(byKind("npu").address, { kind: "npu", pnpInstanceId: "ACPI\\NPU0" });
assert.equal(byKind("gpu_adapter").address?.kind, "gpu_adapter");
assert.equal(byKind("storage").address, null);
assert.equal(byKind("fan").address, null);
assert.equal(byKind("gpu_adapter").displayName, "Hardware GPU");
assert.equal(byKind("fan").displayName, null);

// unsigned64 的十进制字符串精确保留。
const rxErrors = byKind("network_interface").fields.find((field) => field.name === "network_all_rx_errors_actual");
assert.equal(rxErrors?.integerValue, 18446744073709551615n);

// 设备页：NPU 与 AMD 显卡各一节，按地址里的 PnP 实例 ID 相等匹配。
assert.equal(deviceMetricSections(devices, "ACPI\\NPU0")[0]?.rows[0]?.value, "125.5%");
const gpuPnp = (byKind("gpu_adapter").address as { pnpInstanceId: string }).pnpInstanceId;
assert.equal(deviceMetricSections(devices, gpuPnp)[0]?.title, uiText.deviceSpecialized.metricFields.amdNpuTitle);

// 卡片：每台设备的每个字段都能选；选中后按字段订阅拿到值。
applyByteUnitMode("binary");
const definitions = fieldCardDefinitions(catalog);
assert.equal(definitions.length, catalog.reduce((count, device) => count + device.fields.length, 0));
const fan = definitions.find((metric) => metric.id === "fan_all_speed_rpm@wire-fan");
assert.equal(fan?.group, "风扇 0");
const cards = definitions.map((metric, index) => ({
  id: `c${index}`, main: metric.id, small: [], mainBinding: { scopeKind: metric.scopeKind!, scopeKey: metric.scopeKey! }
}));
const values = fieldCardValues(fieldCardSlots(cards), catalog, devices);
assert.equal(values["fan_all_speed_rpm@wire-fan"].displayValue, "1200 RPM");
assert.equal(values["memory_all_numa_node_capacity_mib@wire-numa"].displayValue, "64.0 GiB");
assert.equal(values["storage_all_nvme_composite_temperature_celsius@wire-storage"].displayValue, "40.0 °C");
assert.equal(values["network_all_rx_errors_actual@wire-network"].displayValue, (18446744073709551615n).toLocaleString());

console.log("metricFieldsWire: ok");
