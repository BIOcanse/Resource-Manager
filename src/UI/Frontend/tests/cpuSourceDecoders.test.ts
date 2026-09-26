import assert from "node:assert/strict";
import {
  cpuExclusiveBindingsDecoder,
  cpuResidencyDecoder,
  cpuTopologyDecoder
} from "../src/data/cpu/cpuSourceDecoders.ts";

const capturedAt = "2026-08-22T12:00:00.000Z";

assert.equal(cpuTopologyDecoder.decode(null), null);
assert.equal(cpuExclusiveBindingsDecoder.decode(null), null);

const topology = cpuTopologyDecoder.decode({
  capturedAt,
  cpuName: "Fixture CPU",
  specification: {
    name: "Fixture CPU",
    vendor: "Fixture",
    family: "Fixture",
    physicalCoreCount: 0,
    logicalProcessorCount: 0,
    source: "fixture"
  },
  topologySource: "fixture",
  usageSource: "fixture",
  affinityTargetKind: "logical",
  visualLayoutKind: "Grid",
  visualLayoutSource: "fixture",
  physicalCoreCount: 0,
  logicalProcessorCount: 0,
  ccdCount: 0,
  simultaneousMultithreading: false,
  ccds: [],
  physicalCores: [],
  logicalProcessors: [],
  notes: []
});
assert.ok(topology);
assert.equal(topology.cpuName, "Fixture CPU");
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
assert.ok(residency);
assert.equal(residency.processes[0]?.executionTimeMilliseconds, 750.25);
assert.equal(residency.processes[0]?.threads[0]?.threadInstanceId, "7:1");
assert.equal(cpuResidencyDecoder.decode(null), null);
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
assert.ok(bindings);
assert.deepEqual(bindings.bindings, []);

console.log("CPU source decoder tests passed");
