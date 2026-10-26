import assert from "node:assert/strict";
import { checkDefinition } from "../src/interactions/protocol/check.ts";
import type { Definition, Geometry } from "../src/interactions/protocol/definition.ts";
import { globalLayer, partLayer, resolveParams } from "../src/interactions/protocol/params.ts";
import type { Value } from "../src/interactions/protocol/vec.ts";
import { controlSliderDefinition } from "../src/interactions/components/controlSlider.ts";
import { columnEdgeBound, columnResizeHandleDefinition } from "../src/interactions/components/columnResizeHandle.ts";
import { curvePointDefinition } from "../src/interactions/components/curvePoint.ts";
import { REORDER_BOUND, reorderItemDefinition } from "../src/interactions/components/reorderItem.ts";
import { switchToggleDefinition } from "../src/interactions/components/switchToggle.ts";
import { CARD_SLOT_LAYOUT_BOUND, cardSlotLayoutDefinition } from "../src/interactions/components/cardSlotLayout.ts";
import { selectionIndicatorDefinition } from "../src/interactions/components/selectionIndicator.ts";
import { contentTransitionDefinition } from "../src/interactions/components/contentTransition.ts";
import { declaredSpecs } from "../src/interactions/protocol/definition.ts";
import { animationStyles } from "../src/presentation/animationStyle.ts";

// 产品里每份磁石定义都过一遍机器检查（协议第 4.3 节）：发行用“优化”执行方式，不跑检查，所以开发期在这里跑。
// 系统参数和产品一样不读（null）；几何取和产品呈现层同方向的两端，只为几何那几项检查，像素数不影响完备和确定。
const definitions: readonly [Definition, Geometry][] = [
  [switchToggleDefinition, { center: "滑块中心", atMin: { x: 0, y: 0 }, atMax: { x: 20, y: 0 } }],
  [controlSliderDefinition, { center: "滑块圆心", atMin: { x: 8, y: 0 }, atMax: { x: 192, y: 0 } }],
  [curvePointDefinition, { center: "点的圆心", atMin: { x: 0, y: 240 }, atMax: { x: 0, y: 0 } }],
  [columnResizeHandleDefinition, { center: "列的右边缘", atMin: { x: -columnEdgeBound, y: 0 }, atMax: { x: columnEdgeBound, y: 0 } }],
  [reorderItemDefinition as unknown as Definition, { center: "指针（排序项不跟手）", atMin: { x: -REORDER_BOUND, y: -REORDER_BOUND }, atMax: { x: REORDER_BOUND, y: REORDER_BOUND } }],
  // 卡片槽位不读输入，呈现层不报几何；这里给一份只为几何那几项检查。
  [cardSlotLayoutDefinition as unknown as Definition, { center: "槽位上边", atMin: { x: 0, y: 0 }, atMax: { x: CARD_SLOT_LAYOUT_BOUND * 40, y: CARD_SLOT_LAYOUT_BOUND * 40 } }]
];
// 不读输入、只由宿主值带的两种（选中指示条、内容过渡）：没有几何，通道就是序号。
const hostDriven: readonly Definition[] = [selectionIndicatorDefinition, contentTransitionDefinition];

// 每种动画风格的磁石全局层各检查一遍（docs/animation_style_2026-10-26.md）：弹簧和缓动两种走法都要完备、确定。
// 值表含定义用到的场声明的参数（协议第 5 节“场和取值自己的参数”）。
const paramsIn = (definition: Definition, layer: Readonly<Record<string, number>>) => resolveParams(null,
  [globalLayer({ ...layer }), partLayer(definition.name, definition.params ?? {})], declaredSpecs(definition as unknown as Definition<Value>));
for (const style of animationStyles) {
  for (const [definition, geometry] of definitions) {
    const result = checkDefinition(definition as unknown as Definition<Value>, geometry, paramsIn(definition, style.lodestone));
    assert.ok(result.ok, `${style.id} ${definition.name} failed the machine check:\n${result.lines.join("\n")}`);
    assert.equal(result.counts.missing, 0, `${style.id} ${definition.name} has missing cells`);
    assert.equal(result.counts.conflict, 0, `${style.id} ${definition.name} has conflicting cells`);
  }
  for (const definition of hostDriven) {
    const result = checkDefinition(definition as unknown as Definition<Value>, null, paramsIn(definition, style.lodestone));
    assert.ok(result.ok, `${style.id} ${definition.name} failed the machine check:\n${result.lines.join("\n")}`);
    assert.equal(result.counts.missing, 0, `${style.id} ${definition.name} has missing cells`);
    assert.equal(result.counts.conflict, 0, `${style.id} ${definition.name} has conflicting cells`);
    assert.deepEqual(definition.rows, [], `${definition.name} reads no input`);
  }
}
console.log(`PASS interaction definitions: ${definitions.length + hostDriven.length} checked in ${animationStyles.length} animation styles`);
