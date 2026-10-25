import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const config = readSource("../src/stores/monitorConfig.ts");
const store = readSource("../src/stores/monitorStore.ts");
const page = readSource("../src/features/monitor/MonitorPage.tsx");
const dashboard = readSource("../src/features/monitor/Dashboard.tsx");
const resourceBreakdown = readSource("../src/features/resourceBreakdown/ResourceBreakdown.tsx");
const resourceTable = readSource("../src/features/resourceTable/ResourceTable.tsx");
const reorderGroup = readSource("../src/interactions/solid/reorderGroup.ts");
const types = readSource("../src/types.ts");

assert.match(types, /scopeKind\?: string \| null/);
assert.match(types, /scopeKey\?: string \| null/);
assert.match(types, /mainBinding\?: DashboardMetricBinding \| null/);
assert.match(types, /smallBindings\?: Array<DashboardMetricBinding \| null>/);
assert.match(types, /interface ResourceBarSettings[\s\S]*?binding\?: DashboardMetricBinding \| null/);
assert.match(types, /interface ResourceTableColumnSettings[\s\S]*?binding\?: DashboardMetricBinding \| null/);
assert.match(config, /metricBindingForDefinition/);
assert.match(config, /binding: normalizeMetricBinding\(bar\.metricId, bar\.binding\)/);
assert.match(config, /binding: normalizeMetricBinding\(column\.id, column\.binding\)/);
assert.match(config, /readDashboardSlotBinding/);
assert.match(config, /card\.smallBindings\?\.\[index\]/);
assert.match(store, /const sourceBinding = readDashboardSlotBinding/);
assert.match(store, /const targetBinding = readDashboardSlotBinding/);
assert.match(store, /writeDashboardSlot\(sourceCard, source, targetValue, targetBinding\)/);
assert.match(store, /writeDashboardSlot\(targetCard, target, sourceValue, sourceBinding\)/);
assert.match(store, /mainBinding: binding/);
assert.match(store, /smallBindings\[target\.index\] = binding/);
assert.match(store, /binding: metricBindingForDefinition\([\s\S]*?catalog\(\)\.find/);
assert.match(store, /resourceBarSaveState/);
assert.match(store, /resourceTableSaveState/);
assert.match(page, /saveState=\{monitor\.resourceBarSaveState\(\)\}/);
assert.match(page, /saveState=\{monitor\.resourceTableSaveState\(\)\}/);
assert.match(config, /"virtualMemory\.usage"/);
assert.match(resourceBreakdown, /resourceMetric\("virtualMemory\.usage"/);
for (const source of [dashboard, resourceBreakdown, resourceTable]) {
  assert.match(source, /createReorderGroup|\.reorder\.item\(/);
  assert.doesNotMatch(source, /\bdraggable=|onDragStart=|onDragOver=|onDrop=/);
}
// 拖动排序走交互协议的运行时：同一组共用的宿主映射按指针处的元素找目标项。
assert.match(reorderGroup, /reorderItemDefinition/);
assert.match(reorderGroup, /document\s*\.elementFromPoint/);

function readSource(relativePath: string) {
  return readFileSync(new URL(relativePath, import.meta.url), "utf8");
}
