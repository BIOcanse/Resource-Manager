import assert from "node:assert/strict";
import {
  cpuExclusiveBindingsDecoder,
  cpuResidencyDecoder,
  cpuTopologyDecoder,
  cpuTopologySubscriptionDecoder,
  cpuResidencySubscriptionDecoder
} from "../src/data/cpu/cpuSourceDecoders.ts";

const capturedAt = "2026-08-22T12:00:00.000Z";

assert.equal(cpuTopologySubscriptionDecoder.decode(null), null);
assert.throws(() => cpuTopologySubscriptionDecoder.decode({ state: "unavailable", reasonCode: "cpu-topology-not-published" }));

// CPU residency: GET and the subscription carry the same typed observation; a bare null is a contract violation.
const residencyReasons = ["cpu-residency-not-requested", "cpu-residency-window-warming", "cpu-residency-events-lost", "cpu-residency-session-restarted", "cpu-residency-session-unavailable", "cpu-residency-capture-failed"] as const;
for (const decoder of [cpuResidencyDecoder, cpuResidencySubscriptionDecoder]) {
  for (const reasonCode of residencyReasons) {
    assert.deepEqual(decoder.decode({ state: "unavailable", reasonCode }), { state: "unavailable", reasonCode });
  }
  for (const value of [null, undefined, "", { state: "unavailable" }, { state: "unavailable", reasonCode: "" }, { state: "unknown", reasonCode: "cpu-residency-window-warming" }]) {
    assert.throws(() => decoder.decode(value));
  }
}

for (const [decoder, reasonCode] of [
  [cpuTopologyDecoder, "cpu-topology-not-published"],
  [cpuExclusiveBindingsDecoder, "cpu-topology-not-published"]
] as const) {
  assert.deepEqual(decoder.decode({ state: "unavailable", reasonCode }), { state: "unavailable", reasonCode });
  for (const value of [null, undefined, "", { state: "unavailable" }, { state: "unavailable", reasonCode: "" }, { state: "unknown", reasonCode }]) {
    assert.throws(() => decoder.decode(value));
  }
}

const topology = cpuTopologyDecoder.decode({
  capturedAt,
  cpuName: "Fixture CPU",
  specification: {
    name: "Fixture CPU",
    vendor: "Fixture",
    family: "Fixture",
    physicalCoreCount: 1,
    logicalProcessorCount: 1,
    source: "fixture"
  },
  topologySource: "fixture",
  usageSource: "fixture",
  affinityTargetKind: "logical_processor_mask",
  visualLayoutKind: "grid",
  visualLayoutSource: "fixture",
  physicalCoreCount: 1,
  logicalProcessorCount: 1,
  ccdCount: 1,
  simultaneousMultithreading: false,
  ccds: [{ id: "ccd:0", index: 0, physicalCoreIndexes: [0], logicalProcessorIds: [0], source: "fixture" }],
  physicalCores: [{ id: "core:0", index: 0, ccdId: "ccd:0", efficiencyClass: 0, performanceScore: 100,
    logicalProcessorIds: [0], cacheLevels: [] }],
  logicalProcessors: [{ id: 0, processorGroup: 0, groupRelativeIndex: 0, physicalCoreId: "core:0",
    ccdId: "ccd:0", performanceScore: 100, affinitySelectable: true }]
});
assert.ok("cpuName" in topology);
assert.deepEqual(cpuTopologySubscriptionDecoder.decode(topology), topology);
assert.equal(topology.cpuName, "Fixture CPU");
assert.equal("notes" in topology, false);
assert.equal("label" in topology.ccds[0], false);
assert.equal("label" in topology.physicalCores[0], false);
assert.throws(() => cpuTopologyDecoder.decode({ ...topology, physicalCores: null }));

const residency = cpuResidencyDecoder.decode({
  capturedAt,
  window: "00:00:05",
  sessionGeneration: 7,
  measuredFrom: "2026-08-22T11:59:55.000Z",
  measuredThrough: capturedAt,
  processes: [{
    processInstanceId: "7:1",
    processId: 123,
    processStartKey: "134000000000000000",
    processName: "fixture",
    executionTimeMilliseconds: 750.25,
    switchCount: 12,
    threadCount: 1,
    primaryCcdId: "ccd0",
    primaryPhysicalCoreId: "core0",
    primaryLogicalProcessorId: 0,
    ccds: [{
      ccdId: "ccd0",
      executionTimeMilliseconds: 750.25,
      switchCount: 12,
      sharePercent: 100
    }],
    physicalCores: [{
      physicalCoreId: "core0",
      ccdId: "ccd0",
      executionTimeMilliseconds: 750.25,
      switchCount: 12,
      sharePercent: 100
    }],
    logicalProcessors: [{
      logicalProcessorId: 0,
      physicalCoreId: "core0",
      ccdId: "ccd0",
      executionTimeMilliseconds: 750.25,
      switchCount: 12,
      sharePercent: 100
    }],
    threads: [{
      threadInstanceId: "7:1",
      threadId: 456,
      executionTimeMilliseconds: 750.25,
      switchCount: 12,
      primaryCcdId: "ccd0",
      primaryPhysicalCoreId: "core0",
      primaryLogicalProcessorId: 0,
      physicalCoreIds: ["core0"]
    }]
  }]
});
assert.ok("processes" in residency);
assert.deepEqual(cpuResidencySubscriptionDecoder.decode(residency), residency);
assert.equal(residency.processes[0]?.executionTimeMilliseconds, 750.25);
assert.equal(residency.processes[0]?.threads[0]?.threadInstanceId, "7:1");
assert.throws(() => cpuResidencyDecoder.decode({
  capturedAt,
  window: "00:00:05",
  sessionGeneration: 7,
  measuredFrom: "2026-08-22T11:59:55.000Z",
  measuredThrough: capturedAt,
  processes: {},
}));
assert.throws(() => cpuResidencyDecoder.decode({
  ...residency,
  processes: [{ ...residency.processes[0], executionTimeMilliseconds: -1 }]
}));

const bindings = cpuExclusiveBindingsDecoder.decode({
  capturedAt,
  cpuName: "Fixture CPU",
  bindings: []
});
assert.ok("bindings" in bindings);
assert.deepEqual(bindings.bindings, []);

console.log("CPU source decoder tests passed");
