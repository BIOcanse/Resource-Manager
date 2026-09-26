import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const dashboard = readSource("../src/components/Dashboard.tsx");
const metricModal = readSource("../src/components/MetricModal.tsx");
const resourceBreakdown = readSource("../src/components/ResourceBreakdown.tsx");
const presentation = readSource("../src/presentation/userFacingText.ts");
const monitorCss = readSource("../src/styles/monitor.css");
const metricSurfaces = `${dashboard}\n${metricModal}\n${resourceBreakdown}`;

assert.doesNotMatch(metricSurfaces, /userFacingMetricDescription/);
assert.doesNotMatch(presentation, /export function userFacingMetricDescription/);
assert.doesNotMatch(metricSurfaces, /当前(?:读数|使用情况|占用情况|运行状态|运行频率|温度|网络活动|磁盘活动)/);
assert.doesNotMatch(dashboard, /metric-detail/);
assert.doesNotMatch(monitorCss, /\.metric-detail\s*\{/);
assert.match(metricModal, /userFacingMetricUnavailableReason/);
assert.match(metricModal, /userFacingMetricGroup/);
assert.match(dashboard, /definition\(\)\?\.label \?\? snapshotLabel\(\)/);
assert.match(dashboard, /label !== props\.metricId/);
assert.doesNotMatch(dashboard, /metric\(\)\?\.label \?\? definition\(\)\?\.label/);

function readSource(relativePath: string) {
  return readFileSync(new URL(relativePath, import.meta.url), "utf8");
}
