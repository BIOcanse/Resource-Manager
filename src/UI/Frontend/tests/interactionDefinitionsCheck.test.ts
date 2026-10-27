import assert from "node:assert/strict";
import { readdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { checkDefinition } from "../src/interactions/protocol/check.ts";
import type { Definition, Geometry } from "../src/interactions/protocol/definition.ts";
import { globalLayer, partLayer, resolveParams } from "../src/interactions/protocol/params.ts";
import type { Value } from "../src/interactions/protocol/vec.ts";
import { controlSliderDefinition } from "../src/interactions/components/controlSlider.ts";
import { columnEdgeBound, columnResizeHandleDefinition } from "../src/interactions/components/columnResizeHandle.ts";
import { curvePointDefinition } from "../src/interactions/components/curvePoint.ts";
import { REORDER_BOUND, reorderItemDefinition } from "../src/interactions/components/reorderItem.ts";
import { switchToggleDefinition } from "../src/interactions/components/switchToggle.ts";
import { reflowDefinition } from "../src/interactions/components/reflow.ts";
import { selectionIndicatorDefinition } from "../src/interactions/components/selectionIndicator.ts";
import { entranceDefinitions } from "../src/interactions/components/entrance.ts";
import { edgeRubberBandDefinition } from "../src/interactions/components/edgeRubberBand.ts";
import { declaredSpecs } from "../src/interactions/protocol/definition.ts";
import { animationStyles } from "../src/presentation/animationStyle.ts";
import { MOTION_KINDS } from "../src/interactions/components/common.ts";

// 产品里每份磁石定义都过一遍机器检查（协议第 4.3 节）：发行用“优化”执行方式，不跑检查，所以开发期在这里跑。
// 系统参数和产品一样不读（null）；几何取和产品呈现层同方向的两端，只为几何那几项检查，像素数不影响完备和确定。
const definitions: readonly [Definition, Geometry][] = [
  [switchToggleDefinition, { center: "滑块中心", atMin: { x: 0, y: 0 }, atMax: { x: 20, y: 0 } }],
  [controlSliderDefinition, { center: "滑块圆心", atMin: { x: 8, y: 0 }, atMax: { x: 192, y: 0 } }],
  [curvePointDefinition, { center: "点的圆心", atMin: { x: 0, y: 240 }, atMax: { x: 0, y: 0 } }],
  [columnResizeHandleDefinition, { center: "列的右边缘", atMin: { x: -columnEdgeBound, y: 0 }, atMax: { x: columnEdgeBound, y: 0 } }],
  [reorderItemDefinition as unknown as Definition, { center: "指针（排序项不跟手）", atMin: { x: -REORDER_BOUND, y: -REORDER_BOUND }, atMax: { x: REORDER_BOUND, y: REORDER_BOUND } }]
];
// 不读输入、只由宿主值带的（选中指示条、内容进场、弹窗进场、重排、滚动区边缘回弹）：没有几何，通道是序号、开度、走了多少或偏移。
const hostDriven: readonly Definition[] = [selectionIndicatorDefinition, entranceDefinitions.page, entranceDefinitions.dialog, reflowDefinition, edgeRubberBandDefinition];

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
// 动画类别（docs/animation_style_2026-10-26.md「总原则」）：components 下导出的每份定义都要写 motion，没有默认值；
// 每份定义也都要在上面两张表里，过机器检查。新加的定义漏了任何一项，这里直接报错。
const componentsRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../src/interactions/components");
const isDefinition = (value: unknown): value is Definition & { motion?: unknown } =>
  typeof value === "object" && value !== null && "id" in value && "rows" in value && "channel" in value;
const exported: (Definition & { motion?: unknown })[] = [];
for (const name of readdirSync(componentsRoot).filter((file) => file.endsWith(".ts"))) {
  const module: Record<string, unknown> = await import(pathToFileURL(join(componentsRoot, name)).href);
  for (const value of Object.values(module)) {
    if (isDefinition(value)) exported.push(value);
    else if (typeof value === "object" && value !== null) exported.push(...Object.values(value).filter(isDefinition));
  }
}
const checked = new Set<unknown>([...definitions.map(([definition]) => definition), ...hostDriven]);
for (const definition of new Set(exported)) {
  assert.ok((MOTION_KINDS as readonly unknown[]).includes(definition.motion),
    `${definition.name} declares no animation kind (motion: ${MOTION_KINDS.join(" | ")}), got ${String(definition.motion)}`);
  assert.ok(checked.has(definition), `${definition.name} is not in the machine check above`);
}
assert.equal(new Set(exported).size, checked.size, "every checked definition is exported from src/interactions/components");

// 两类按变化大小分（docs「总原则」，用户 2026-10-27「稍大一些的都用立即生效最好」）：平面移动只给控件自己的小幅运动。
// 允许声明平面移动的部件只有这一张表：开关的滑块（开关和折叠箭头）、数值滑条的滑块、分段与页签的选中指示条、拖动中跟着指针的排序项、
// 列宽把手、风扇曲线点、滚动区边缘回弹。卡片、槽位的重排和放下后的落位、展开收起、换页、弹窗都是进场退场。
// （资源条分段的生长是样式表里的过渡，裁定的例外登记在 presentation/animationStyle.ts 的 CSS_MOTIONS。）
const CONTROL_LOCAL_PARTS = ["settings-switch", "control-number-slider", "selection-indicator", "reorder-item", "resource-table-column-resize", "curve-point", "edge-rubber-band"];
const planarParts = [...new Set(exported)].filter((definition) => definition.motion === "平面移动").map((definition) => definition.id).sort();
assert.deepEqual(planarParts, [...CONTROL_LOCAL_PARTS].sort(), "only the control-local parts declare a planar move");
console.log(`PASS interaction definitions: ${definitions.length + hostDriven.length} checked in ${animationStyles.length} animation styles, each declares its animation kind`);
