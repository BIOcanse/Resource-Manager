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
  assert.equal(decoded.table?.rows[0]?.id, "summary:total");
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
  wire.table.capturedAt = null;
  wire.table.rows = [];
  const decoded = decodeResourceMonitorWireSnapshot(wire);
  assert.equal(decoded.table.capturedAt, undefined);
  assert.equal(decoded.capturedAt, undefined);
}

{
  const wire = readyResourceMonitorWire();
  wire.table = null as never;
  assert.throws(
    () => decodeResourceMonitorWireSnapshot(wire),
    ResponseDecodeError);
}

{
  const wire = readyResourceMonitorWire();
  const tableCapturedAt = "2026-08-22T15:20:25.000Z";
  wire.table.capturedAt = tableCapturedAt;
  const decoded = decodeResourceMonitorWireSnapshot(wire);
  assert.equal(decoded.capturedAt, capturedAt);
  assert.equal(decoded.table.capturedAt, tableCapturedAt);
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
