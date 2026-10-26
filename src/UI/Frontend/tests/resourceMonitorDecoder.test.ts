import assert from "node:assert/strict";
import {
  decodeResourceBreakdownWireSnapshot,
  decodeResourceMonitorWireSnapshot,
  decodeResourceProcessDetailsWireSnapshot
} from "../src/api/resourceBreakdownWire.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import {
  capturedAt,
  readyResourceMonitorWire
} from "./resourceMonitorFixture.ts";
import { detailsMetricKeys, factsMetricKeys } from "./resourceFactsWireFixture.ts";

{
  const row = readyResourceMonitorWire().breakdown.bars[0].software[0][5][0];
  const wire = { version: 1, capturedAt, metrics: [{ metricId: "memory.usage", ...detailsMetricKeys({ totalMetricId: "memory.usage", totalValue: 64, capacityValue: 1024,
    processSumValue: 64, residualValue: 0, additiveAcrossProcesses: true, capturedAt }), software: [{ softwareId: "software:test", processes: [row] }] }] };
  const decoded = decodeResourceProcessDetailsWireSnapshot(wire);
  assert.equal(decoded.metrics[0].software[0].processes[0].processId, 42);
  assert.equal(decoded.metrics[0].software[0].processes[0].processStartKey, "134325768300000000");
  assert.deepEqual(Object.keys(decoded).sort(), ["capturedAt", "metrics"]);
  // Details keep the metric's own total, sum, signed residual and capture time (metric-source round 4).
  assert.deepEqual([decoded.metrics[0].totalValue, decoded.metrics[0].processSumValue, decoded.metrics[0].residualValue,
    decoded.metrics[0].additiveAcrossProcesses, decoded.metrics[0].capturedAt], [64, 64, 0, true, capturedAt]);
  for (const key of ["totalMetricId", "totalValue", "capacityValue", "processSumValue", "residualValue", "additiveAcrossProcesses", "capturedAt"]) {
    const { [key]: _removed, ...rest } = wire.metrics[0] as Record<string, unknown>;
    assert.throws(() => decodeResourceProcessDetailsWireSnapshot({ ...wire, metrics: [rest] }), new RegExp(key));
  }
  // A metric with no requested owner keeps its facts with no software (gpu.0/3/12.vram before round 4 were dropped).
  const residualOnly = decodeResourceProcessDetailsWireSnapshot({ ...wire, metrics: [{ ...wire.metrics[0], metricId: "gpu.0.vram", totalMetricId: "gpu.0.vram",
    totalValue: 2037866496, processSumValue: 1765957632, residualValue: 271908864, software: [] }] });
  assert.deepEqual([residualOnly.metrics[0].residualValue, residualOnly.metrics[0].software], [271908864, []]);
  assert.deepEqual(decodeResourceProcessDetailsWireSnapshot({ version: 1, capturedAt: null, metrics: [] }).metrics, []);
  assert.throws(() => decodeResourceProcessDetailsWireSnapshot({ ...wire, version: 9 }), ResponseDecodeError);
  assert.throws(() => decodeResourceProcessDetailsWireSnapshot({ ...wire, metrics: [{ metricId: "memory.usage", software: [{ softwareId: "a", processes: [[42]] }] }] }), ResponseDecodeError);
}

{
  const decoded = decodeResourceMonitorWireSnapshot(readyResourceMonitorWire());
  assert.equal(decoded.breakdown.bars?.[0]?.software[0]?.processes[0]?.processId, 42);
  assert.equal(decoded.breakdown.bars?.[0]?.software[0]?.processes[0]?.processStartKey, "134325768300000000");
}

{
  // The neutral facts frame carries identity and readings, never table cells.
  const wire = {
    version: 2, capturedAt, attributions: [],
    software: [{ softwareId: "software:test", name: "Test App", kind: "other", attributionKind: "other" }],
    processes: [{ processId: 4242, processStartKey: "134325768300000000", name: "worker.exe",
      executablePath: "C:/Apps/worker.exe", userName: "TEST-PC\\user", architecture: "x64", attributionKind: "process" }],
    metrics: [{ metricId: "cpu.usage", unit: "%", ...factsMetricKeys({ totalMetricId: "cpu.usage", processSumValue: 12, residualValue: 0,
      additiveAcrossProcesses: true, capturedAt }), processFieldName: null, totalValue: 12, capacityValue: 100, totalSystemPercent: 12, sharedValue: null,
      software: [{ softwareId: "software:test", value: 12, systemPercent: 12, processCount: 1, baseScore: 0, sharedValue: null,
        processes: [{ processIndex: 0, value: 12, systemPercent: 12, softwarePercent: 100, baseScore: 0, sharedValue: null }] }] }]
  };
  const decoded = decodeResourceMonitorWireSnapshot(wire);
  const metric = decoded.facts!.metrics[0];
  const process = metric.software[0].processes[0];
  assert.equal(decoded.capturedAt, capturedAt);
  assert.equal(process.processId, 4242);
  assert.equal(process.processStartKey, "134325768300000000");
  assert.equal(process.userName, "TEST-PC\\user");
  assert.equal(process.architecture, "x64");
  assert.equal(process.value, 12);
  assert.equal("displayValue" in process, false);
  assert.deepEqual([metric.totalMetricId, metric.processSumValue, metric.residualValue, metric.additiveAcrossProcesses, metric.capturedAt],
    ["cpu.usage", 12, 0, true, capturedAt]);
  // System.Text.Json writes every key of a facts metric; a missing key is a changed wire, never a default.
  for (const key of ["totalMetricId", "processSumValue", "residualValue", "additiveAcrossProcesses", "capturedAt", "residualSource",
    "quantity", "unavailableReason", "windowMilliseconds", "gpuEngine", "engineClass"]) {
    const { [key]: _removed, ...rest } = wire.metrics[0] as Record<string, unknown>;
    assert.throws(() => decodeResourceMonitorWireSnapshot({ ...wire, metrics: [rest] }), new RegExp(key));
  }
  assert.throws(() => decodeResourceMonitorWireSnapshot({ ...wire, processes: [{ ...wire.processes[0], processStartKey: 42 }] }), ResponseDecodeError);
  assert.throws(() => decodeResourceMonitorWireSnapshot({ ...wire, processes: [] }), ResponseDecodeError);
}

{
  const wire = readyResourceMonitorWire();
  wire.breakdown.bars = [];
  const decoded = decodeResourceMonitorWireSnapshot(wire);
  assert.deepEqual(decoded.breakdown.bars, []);
}

{
  const wire = readyResourceMonitorWire();
  wire.capturedAt = null;
  wire.breakdown.capturedAt = null;
  wire.breakdown.bars = [];
  wire.breakdown.softwareCatalog = [];
  const decoded = decodeResourceMonitorWireSnapshot(wire);
  assert.equal(decoded.breakdown.capturedAt, undefined);
  assert.equal(decoded.capturedAt, undefined);
}

{
  const wire = readyResourceMonitorWire();
  wire.breakdown = null as never;
  assert.throws(
    () => decodeResourceMonitorWireSnapshot(wire),
    ResponseDecodeError);
}

{
  const wire = readyResourceMonitorWire();
  const decoded = decodeResourceMonitorWireSnapshot(wire);
  assert.equal(decoded.capturedAt, capturedAt);
  assert.equal(decoded.breakdown.capturedAt, capturedAt);
}

{
  const wire = readyResourceMonitorWire();
  wire.breakdown.bars[0].software[0] = [0, 64] as never;
  assert.throws(
    () => decodeResourceMonitorWireSnapshot(wire),
    ResponseDecodeError);
}

{
  const breakdown = readyResourceMonitorWire().breakdown;
  breakdown.version = 6 as never;
  assert.throws(
    () => decodeResourceBreakdownWireSnapshot(breakdown),
    ResponseDecodeError);
}
