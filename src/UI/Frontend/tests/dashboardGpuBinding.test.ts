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
// 绑定跟着槽位：每个槽位自己带它的绑定（types.ts DashboardCardSlot）。
assert.match(types, /interface DashboardCardSlot[\s\S]*?binding\?: DashboardMetricBinding \| null/);
assert.match(types, /interface ResourceBarSettings[\s\S]*?binding\?: DashboardMetricBinding \| null/);
assert.match(types, /interface ResourceTableColumnSettings[\s\S]*?binding\?: DashboardMetricBinding \| null/);
assert.match(config, /metricBindingForDefinition/);
assert.match(config, /binding: normalizeMetricBinding\(bar\.metricId, bar\.binding\)/);
assert.match(config, /binding: normalizeMetricBinding\(column\.id, column\.binding\)/);
// 拖动移的是整个槽位对象（监控项、大小、绑定一起走）；选监控项时新绑定写进那个槽位。
assert.match(config, /const moving = from\?\.slots\[source\.index\]/);
assert.match(config, /slots\.splice\(Math\.min\(target\.index, slots\.length\), 0, moving\)/);
assert.match(config, /slots\[ref\.index\] = \{ \.\.\.slots\[ref\.index\], metricId, binding \}/);
assert.match(store, /replaceCardSlotMetric\(items, target, metricId, binding\)/);
assert.match(store, /addCardSlot\(items, target\.cardId, \{ metricId, binding, size: target\.size \}\)/);
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
