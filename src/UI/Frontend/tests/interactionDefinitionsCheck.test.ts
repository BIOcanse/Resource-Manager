import assert from "node:assert/strict";
import { checkDefinition } from "../src/interactions/protocol/check.ts";
import type { Definition, Geometry } from "../src/interactions/protocol/definition.ts";
import { partLayer, resolveParams } from "../src/interactions/protocol/params.ts";
import type { Value } from "../src/interactions/protocol/vec.ts";
import { columnEdgeBound, columnResizeHandleDefinition } from "../src/interactions/components/columnResizeHandle.ts";
import { curvePointDefinition } from "../src/interactions/components/curvePoint.ts";
import { REORDER_BOUND, reorderItemDefinition } from "../src/interactions/components/reorderItem.ts";
import { switchToggleDefinition } from "../src/interactions/components/switchToggle.ts";

// 产品里每份磁石定义都过一遍机器检查（协议第 4.3 节）：发行用“优化”执行方式，不跑检查，所以开发期在这里跑。
// 系统参数和产品一样不读（null）；几何取和产品呈现层同方向的两端，只为几何那几项检查，像素数不影响完备和确定。
const definitions: readonly [Definition, Geometry][] = [
  [switchToggleDefinition, { center: "滑块中心", atMin: { x: 0, y: 0 }, atMax: { x: 20, y: 0 } }],
  [curvePointDefinition, { center: "点的圆心", atMin: { x: 0, y: 240 }, atMax: { x: 0, y: 0 } }],
  [columnResizeHandleDefinition, { center: "列的右边缘", atMin: { x: -columnEdgeBound, y: 0 }, atMax: { x: columnEdgeBound, y: 0 } }],
  [reorderItemDefinition as unknown as Definition, { center: "指针（排序项不跟手）", atMin: { x: -REORDER_BOUND, y: -REORDER_BOUND }, atMax: { x: REORDER_BOUND, y: REORDER_BOUND } }]
];

for (const [definition, geometry] of definitions) {
  const params = resolveParams(null, [partLayer(definition.name, definition.params ?? {})]);
  const result = checkDefinition(definition as unknown as Definition<Value>, geometry, params);
  assert.ok(result.ok, `${definition.name} failed the machine check:\n${result.lines.join("\n")}`);
  assert.equal(result.counts.missing, 0, `${definition.name} has missing cells`);
  assert.equal(result.counts.conflict, 0, `${definition.name} has conflicting cells`);
}
console.log(`PASS interaction definitions: ${definitions.length} checked`);
