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
    processSumValue: 64,
    residualValue: 0,
    additiveAcrossProcesses: true,
    capturedAt: "2026-07-12T00:00:00Z",
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
    processSumValue: 0,
    residualValue: 0,
    software: []
  }]
};

// metric-source round 4: every bar carries its own facts; keys the converter writes only when set read as null.
assert.deepEqual(
  [bar?.totalMetricId, bar?.processFieldName, bar?.quantity, bar?.unavailableReason, bar?.windowMilliseconds, bar?.gpuEngine, bar?.engineClass],
  [null, null, null, null, null, null, null]);
assert.deepEqual([bar?.processSumValue, bar?.residualValue, bar?.additiveAcrossProcesses, bar?.capturedAt], [64, 0, true, "2026-07-12T00:00:00Z"]);
const engineWire = structuredClone(wire);
Object.assign(engineWire.bars[0], { metricId: "process_all_gpu_residency_attributed_gpu_0_luid_0000000000000017_physical_0_engine_1",
  unit: "%", totalMetricId: "gpu.0.usage", processFieldName: "process_all_gpu_residency_attributed_gpu_0_luid_0000000000000017_physical_0_engine_1",
  quantity: "engine_busy_percent", windowMilliseconds: 1000, gpuEngine: { adapterLuid: 23, physicalIndex: 0, engineIndex: 1 }, engineClass: null,
  totalValue: 100, capacityValue: 100, totalSystemPercent: 100, processSumValue: 64, residualValue: 36 });
const engineBar = decodeResourceBreakdownWireSnapshot(engineWire).bars![0];
assert.deepEqual([engineBar.totalMetricId, engineBar.quantity, engineBar.windowMilliseconds, engineBar.gpuEngine, engineBar.engineClass],
  ["gpu.0.usage", "engine_busy_percent", 1000, { physicalIndex: 0, engineIndex: 1 }, null]);
// A gpuEngine block always carries engineClass (null when not unique); a written optional key is never null.
assert.throws(() => decodeResourceBreakdownWireSnapshot({ ...engineWire, bars: [{ ...engineWire.bars[0], engineClass: undefined }] }), /engineClass/);
assert.throws(() => decodeResourceBreakdownWireSnapshot({ ...wire, bars: [{ ...wire.bars[0], quantity: null as unknown as string }] }), /quantity/);
// Always-written keys are required, and a non-additive bar has no residual.
for (const key of ["processSumValue", "residualValue", "additiveAcrossProcesses", "capturedAt"] as const) {
  const missing = structuredClone(wire);
  delete (missing.bars[0] as Partial<typeof missing.bars[0]>)[key];
  assert.throws(() => decodeResourceBreakdownWireSnapshot(missing), new RegExp(key));
}
assert.throws(() => decodeResourceBreakdownWireSnapshot({ ...wire, bars: [{ ...wire.bars[0], additiveAcrossProcesses: false }] }), /residualValue/);
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
    processSumValue: null, residualValue: null, additiveAcrossProcesses: true, capturedAt: null,
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
// 条头的总量（含空余时还有容量）也按用户选的进制格式化：条头经 resourceBarValueText，总量都经 resourceBarTotalText
// 交给 formatResourceBarValue；读不到时换成原因。
assert.match(breakdownSource, /\{resourceBarValueText\(props\.bar\)\}/);
assert.match(breakdownSource, /return resourceBarTotalText\(bar\);/);
assert.match(breakdownSource, /formatResourceBarValue\(bar, bar\.totalValue\)/);
assert.match(breakdownSource, /formatResourceBarValue\(bar, capacity\)/);
