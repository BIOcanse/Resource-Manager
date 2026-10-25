import assert from "node:assert/strict";
import {
  metricCatalogDecoder,
  metricSnapshotDecoder
} from "../src/data/monitor/monitorSourceDecoders.ts";
import {
  buildMetricSnapshotSubscriptionUrl,
  normalizeMetricSnapshotQuery
} from "../src/data/monitor/monitorSourcesApi.ts";

const catalog = metricCatalogDecoder.decode([{
  id: "cpu.usage",
  unit: "%",
  candidateSources: []
}]);
assert.equal(catalog[0].id, "cpu.usage");
assert.throws(() => metricCatalogDecoder.decode([{ id: "cpu.usage", candidateSources: [] }]));

// 部件地址的候选来源：按后端排序原样保留，reading 可空，可用性只认三种事实；缺了这个数组就不是当前 wire。
const candidateReading = {
  id: "cpu-tctl", quantity: "cpu.control_temperature", sourceRank: 10, sourceAvailability: "available",
  isPreferredSource: true, preferredDeviceIdentity: "cpu|0", preferredFieldName: "cpu_amd_tctl_celsius"
};
const [address] = metricCatalogDecoder.decode([{
  id: "cpu.temperature", unit: "°C", fieldName: "cpu_amd_tctl_celsius", deviceIdentity: "cpu|0",
  candidateSources: [
    { fieldName: "cpu_amd_tctl_celsius", deviceIdentity: "cpu|0", sourceId: "amd.smu", reading: candidateReading, sourceAvailability: "available", isSelected: true },
    { fieldName: "cpu_lhm_tctl_celsius", deviceIdentity: "cpu|0", sourceId: "lhm", reading: null, sourceAvailability: "unverified", isSelected: false }
  ]
}]);
assert.deepEqual(address.candidateSources?.map((source) => [source.fieldName, source.sourceId, source.sourceAvailability, source.isSelected]), [
  ["cpu_amd_tctl_celsius", "amd.smu", "available", true],
  ["cpu_lhm_tctl_celsius", "lhm", "unverified", false]
]);
assert.deepEqual(address.candidateSources?.[0].reading, candidateReading);
assert.equal(address.candidateSources?.[1].reading, null);
assert.throws(() => metricCatalogDecoder.decode([{ id: "cpu.usage", unit: "%" }]), /candidateSources/);
assert.throws(() => metricCatalogDecoder.decode([{ id: "cpu.temperature", unit: "°C", candidateSources: [
  { fieldName: "f", deviceIdentity: "d", sourceId: "s", reading: null, sourceAvailability: "maybe", isSelected: false }] }]), /sourceAvailability/);
assert.throws(() => metricCatalogDecoder.decode([{ id: "cpu.temperature", unit: "°C", candidateSources: [
  { fieldName: "f", deviceIdentity: "d", sourceId: "s", reading: null, sourceAvailability: "available" }] }]), /isSelected/);

const snapshot = metricSnapshotDecoder.decode({
  version: 5,
  capturedAt: "2026-08-22T12:00:00.000Z",
  catalogRevision: "catalog-a",
  items: {
    "cpu.usage": {
      id: "cpu.usage", unit: "%", status: 1,
      numericValue: 25,
      percent: 25
    }
  }
});
assert.equal(snapshot.items["cpu.usage"].numericValue, 25);
assert.equal(snapshot.catalogRevision, "catalog-a");
assert.throws(() => metricSnapshotDecoder.decode({ items: [] }));
// 目录修订号是必有的不透明标记，缺了就不是当前 wire。
assert.throws(() => metricSnapshotDecoder.decode({ ...snapshot, catalogRevision: undefined }), /catalogRevision/);
assert.throws(() => metricSnapshotDecoder.decode({ ...snapshot, catalogRevision: "" }), /catalogRevision/);
const emptySnapshot = metricSnapshotDecoder.decode({
  version: 5,
  capturedAt: null,
  catalogRevision: "catalog-a",
  items: {
    "cpu.usage": {
      id: "cpu.usage", unit: "%", status: 3,
      numericValue: null,
      percent: null
    }
  }
});
assert.equal(emptySnapshot.items["cpu.usage"].status, 3);
assert.equal(emptySnapshot.items["cpu.usage"].numericValue, null);
assert.throws(() => metricSnapshotDecoder.decode({
  ...snapshot,
  items: { "cpu.usage": { id: "cpu.usage", unit: "%", status: 1 } }
}), /numericValue/);

assert.deepEqual(normalizeMetricSnapshotQuery({
  ids: [" memory.usage ", "cpu.usage", "cpu.usage", ""]
}), {
  ids: ["cpu.usage", "memory.usage"]
});
assert.equal(
  buildMetricSnapshotSubscriptionUrl({ ids: [] }, 1_000),
  "/api/metrics/subscribe?ids=&intervalMs=1000");
assert.equal(
  buildMetricSnapshotSubscriptionUrl({ ids: null }, 1_000),
  "/api/metrics/subscribe?intervalMs=1000");

console.log("Monitor source decoder tests passed");
