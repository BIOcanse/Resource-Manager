import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  decodeResourceBreakdownWireSnapshot,
  type ResourceBreakdownWireSnapshot
} from "../src/api/resourceBreakdownWire.ts";
import { formatBytesPerSecond } from "../src/presentation/byteUnits.ts";
import { localizedMetricLabel } from "../src/presentation/metricLabels.ts";
import { formatResourceBarValue } from "../src/features/resourceBreakdown/resourceBreakdownValue.ts";
import type { ResourceBreakdownBar } from "../src/types";

const wire: ResourceBreakdownWireSnapshot = {
  version: 8,
  capturedAt: "2026-07-12T00:00:00Z",
  softwareCatalog: [["software:test", "Test App", "other", "other"]],
  bars: [{
    metricId: "memory.usage",
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

const sharedWire = structuredClone(wire);
sharedWire.bars[0].sharedValue = 24;
sharedWire.bars[0].software[0][6] = 24;
sharedWire.bars[0].software[0][5][0][11] = 24;
const sharedBar = decodeResourceBreakdownWireSnapshot(sharedWire).bars![0];
assert.equal(sharedBar.sharedValue, 24);
assert.equal(sharedBar.software[0].sharedValue, 24);
assert.equal(sharedBar.software[0].processes[0].sharedValue, 24);
assert.equal(sharedBar.software[0].value, 64);

// The backend publishes facts only; the frontend names the bar by its metric ID.
assert.equal("label" in (bar as object), false);
assert.ok(localizedMetricLabel(bar?.metricId).length > 0);

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

const emptyBarWire: ResourceBreakdownWireSnapshot = {
  ...unavailableWire,
  bars: [{ metricId: "disk.io", unit: "B/s", scaleMode: "active",
    totalValue: null, capacityValue: null, totalSystemPercent: null,
    unavailableReason: "disk-io-window-incomplete", software: [] }]
};
const emptyBar = decodeResourceBreakdownWireSnapshot(emptyBarWire).bars![0];
assert.equal(emptyBar.metricId, "disk.io");
assert.equal(emptyBar.scaleMode, "active");
assert.equal(emptyBar.unavailableReason, "disk-io-window-incomplete");
assert.equal(emptyBar.totalValue, null);
assert.deepEqual(emptyBar.software, []);
assert.throws(() => decodeResourceBreakdownWireSnapshot({ ...emptyBarWire,
  bars: [{ ...emptyBarWire.bars[0], unavailableReason: null }] }));
assert.throws(() => decodeResourceBreakdownWireSnapshot({ ...emptyBarWire,
  bars: [{ ...emptyBarWire.bars[0], totalValue: 0 }] }));

// 展示串已经从线上去掉：条形自带 unit，数值由前端按用户选的进制格式化。
assert.equal("totalDisplay" in (bar as object), false);
assert.equal("displayValue" in (software as object), false);
assert.equal("displayValue" in (process as object), false);

const diskRate = { metricId: "disk.read", unit: "B/s" } satisfies Pick<ResourceBreakdownBar, "metricId" | "unit">;
const formattedDiskRate = formatResourceBarValue(diskRate, 8_388_608);
assert.equal(formattedDiskRate, formatBytesPerSecond(8_388_608));
assert.notEqual(formattedDiskRate, "8388608.0 B/s");
assert.equal(formatResourceBarValue(diskRate, null), "-");
const breakdownSource = readFileSync(
  new URL("../src/features/resourceBreakdown/ResourceBreakdown.tsx", import.meta.url),
  "utf8");
assert.match(breakdownSource, /import \{ formatResourceBarValue \} from "\.\/resourceBreakdownValue"/);
assert.match(breakdownSource, /formatResourceBarValue\(props\.bar, props\.bar\.totalValue\)/);
