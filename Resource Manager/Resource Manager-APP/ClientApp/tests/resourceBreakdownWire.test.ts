import assert from "node:assert/strict";
import {
  decodeResourceBreakdownWireSnapshot,
  type ResourceBreakdownWireSnapshot
} from "../src/api/resourceBreakdownWire.ts";

const wire: ResourceBreakdownWireSnapshot = {
  version: 8,
  capturedAt: "2026-07-12T00:00:00Z",
  softwareCatalog: [["software:test", "Test App", "Other", "一般应用"]],
  bars: [{
    metricId: "memory.usage",
    label: "内存占用",
    unit: "B",
    scaleMode: "capacity",
    totalValue: 64,
    capacityValue: 1024,
    totalSystemPercent: 6.25,
    software: [[
      0,
      64,
      6.25,
      1,
      125,
      [[42, "worker.exe", "C:\\Apps\\worker.exe", 64, 6.25, 100, "USER", "x64", "process", 125, "134348736000000000"]]
    ]]
  }]
};

const decoded = decodeResourceBreakdownWireSnapshot(wire);
const bar = decoded.bars?.[0];
const software = bar?.software[0];
const process = software?.processes[0];

assert.equal(bar?.metricId, "memory.usage");
assert.equal(software?.softwareId, "software:test");
assert.equal(software?.name, "Test App");
assert.equal(software?.baseScore, 125);
assert.equal(process?.processId, 42);
assert.equal(process?.processStartKey, "134348736000000000");
assert.equal(process?.executablePath, "C:\\Apps\\worker.exe");
assert.equal(process?.attributionKind, "process");
assert.equal(process?.baseScore, 125);

const emptyValueWire: ResourceBreakdownWireSnapshot = {
  ...wire,
  bars: [{
    ...wire.bars[0],
    totalValue: 0,
    totalSystemPercent: 0,
    software: []
  }]
};
const emptyValueBar = decodeResourceBreakdownWireSnapshot(emptyValueWire).bars?.[0];
assert.equal(emptyValueBar?.totalValue, 0);
assert.deepEqual(emptyValueBar?.software, []);

const unavailableWire: ResourceBreakdownWireSnapshot = {
  version: 8,
  capturedAt: null,
  softwareCatalog: [],
  bars: []
};
const unavailable = decodeResourceBreakdownWireSnapshot(unavailableWire);
assert.equal(unavailable.capturedAt, undefined);
assert.deepEqual(unavailable.bars, []);

// 展示串已经从线上去掉：条形自带 unit，数值由前端按用户选的进制格式化。
assert.equal("totalDisplay" in (bar as object), false);
assert.equal("displayValue" in (software as object), false);
assert.equal("displayValue" in (process as object), false);
