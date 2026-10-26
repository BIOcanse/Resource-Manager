import assert from "node:assert/strict";
import { deviceLabel } from "../src/features/monitor/fieldCards.ts";
import {
  metricFieldCatalogDecoder,
  metricFieldDevicesDecoder
} from "../src/data/metricFields/metricFieldSource.ts";

const address = {
  kind: "gpu_engine", adapterLuid: "0000000000000001",
  pnpInstanceIds: ["PCI\\GPU_A", "PCI\\GPU_B"],
  physicalIndex: 1, engineIndex: 2, engineClass: 0
};
const field = {
  name: "gpu_all_engine_usage_percent", unit: "%", valueKind: "float64", reading: null,
  status: 1, numericValue: 12, observedAt: "2026-10-25T12:00:00Z"
};
const batch = [
  { deviceIdentity: "cpu", device: { kind: "cpu_logical_processor", ordinal: 0,
    address: { kind: "cpu_logical_processor", processorGroup: 0, groupRelativeIndex: 0 } }, fields: [] },
  { deviceIdentity: "linked-engine", device: { kind: "gpu_engine", address, ordinal: 0 }, fields: [field] },
  { deviceIdentity: "linked-adapter", device: { kind: "gpu_adapter", ordinal: 0, displayName: "Linked GPU",
    address: { kind: "gpu_adapter_group", adapterLuid: address.adapterLuid, pnpInstanceIds: address.pnpInstanceIds } }, fields: [] }
];
const values = metricFieldDevicesDecoder.decode(batch);
const catalog = metricFieldCatalogDecoder.decode(batch);
assert.equal(values.length, 3, "a linked GPU must not reject unrelated CPU rows");
assert.deepEqual(values[1].address, address, "engine membership and physical/engine coordinates remain intact");
assert.deepEqual(catalog[1].address, address, "catalog and value addresses use the same contract");
assert.deepEqual(values[2].address, batch[2].device.address);
assert.equal(values[1].fields[0].numericValue, 12);
assert.ok(deviceLabel(catalog[1], catalog).includes("Linked GPU"), "linked engine name follows its adapter LUID");

const single = metricFieldDevicesDecoder.decode([{
  deviceIdentity: "single-engine",
  device: { kind: "gpu_engine", ordinal: 0, address: {
    kind: "gpu_engine", pnpInstanceId: "PCI\\GPU_A", adapterLuid: "0000000000000002",
    physicalIndex: 0, engineIndex: 1, engineClass: 2
  } }, fields: []
}]);
assert.equal(single[0].address?.kind, "gpu_engine");
assert.equal(single[0].address && "pnpInstanceId" in single[0].address
  ? single[0].address.pnpInstanceId : null, "PCI\\GPU_A");
assert.throws(() => metricFieldDevicesDecoder.decode([{
  ...batch[1], device: { ...batch[1].device, address: { ...address, pnpInstanceIds: ["PCI\\GPU_A", 7] } }
}]), /pnpInstanceIds/);
const { adapterLuid: _omitted, ...withoutLuid } = address;
assert.throws(() => metricFieldDevicesDecoder.decode([{ ...batch[1], device: { ...batch[1].device, address: withoutLuid } }]),
  /adapterLuid/, "the backend always sends the engine's adapter LUID");
assert.throws(() => metricFieldDevicesDecoder.decode([{ ...batch[1], device: { ...batch[1].device,
  address: { ...address, pnpInstanceId: "PCI\GPU_A" } } }]), /exactly one/, "an engine carries one member form, not both");
console.log("metricFieldAddress: ok");
