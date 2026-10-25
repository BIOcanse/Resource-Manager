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

{
  const row = readyResourceMonitorWire().breakdown.bars[0].software[0][5][0];
  const wire = { version: 1, capturedAt, metrics: [{ metricId: "memory.usage", software: [{ softwareId: "software:test", processes: [row] }] }] };
  const decoded = decodeResourceProcessDetailsWireSnapshot(wire);
  assert.equal(decoded.metrics[0].software[0].processes[0].processId, 42);
  assert.equal(decoded.metrics[0].software[0].processes[0].processStartKey, "134325768300000000");
  assert.deepEqual(Object.keys(decoded).sort(), ["capturedAt", "metrics"]);
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
    metrics: [{ metricId: "cpu.usage", unit: "%", totalMetricId: null, processSumValue: null, processFieldName: null, totalValue: 12, capacityValue: 100, totalSystemPercent: 12, sharedValue: null,
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
