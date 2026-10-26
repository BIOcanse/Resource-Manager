// 资源表列宽把手（协议第 4.4 节第 1 处迁移；来自 Lodestone components/resizeHandle.ts）。
// 通道是把手（列的右边缘）离表头左边缘多少屏幕 px，1 个单位 = 1 px，和拖动排序项同一个做法（框架线程 10-25）：
// 表格把多余宽度按权重分给各列，存的宽度和把手位置不成比例（没铺满时把手走得比宽度慢，最后一列铺满前根本不动），
// 几何两端只能是直线，所以通道不取宽度，取位置；位置换成存的宽度由宿主做。
// 拖动中连续调用 resize(位置, 否)，松开停在落点，调用一次 resize(落点, 是)；方向键调用 step(±1)，宿主按存的宽度加减一步；
// 取消调用 cancel()，宿主回到这次拖动前存的宽度（位置换回宽度不唯一，所以不按起点位置回）。
// 和演示的差别：产品没有默认宽度可复原，所以没有双击复原那一行。

import { CURRENT, LANDING, STOP_POINT } from "../protocol/definition.ts";
import type { Definition } from "../protocol/definition.ts";
import { CUBIC, NO_FIELD, toTerminal } from "../protocol/field.ts";
import { call, CLAIM, dragBasics, FOCUS, stopAt } from "./common.ts";

/** 位置通道的边界：远大于屏幕，几何两端就是边界两端。 */
export const columnEdgeBound = 100000;

const drag = dragBasics({ cancel: [call("cancel")] });

export const columnResizeHandleDefinition: Definition = {
  id: "resource-table-column-resize",
  name: "列宽把手",
  modes: drag.modes,
  exclusive: drag.exclusive,
  held: drag.held,
  rows: [
    { from: ["静止"], on: ["pointer.left↑"], to: "按下", out: [CLAIM, FOCUS] },
    { from: ["静止"], on: ["key.ArrowLeft↑", "key.ArrowLeft⟳"], to: "=", out: [CLAIM, call("step", -1)] },
    { from: ["静止"], on: ["key.ArrowRight↑", "key.ArrowRight⟳"], to: "=", out: [CLAIM, call("step", 1)] },
    { from: ["静止"], on: ["key.ArrowLeft↓", "key.ArrowRight↓"], to: "=" },
    { from: ["按下"], on: ["pointer.position~"], when: { "离开拖动框": true }, to: "拖动", out: [call("resize", CURRENT, false)] },
    { from: ["按下"], on: ["pointer.left↓"], to: "静止", note: "单击不改宽度" },
    { from: ["拖动"], on: ["pointer.position~"], to: "=", out: [call("resize", CURRENT, false)], note: "连续调用" },
    { from: ["拖动"], on: ["pointer.left↓"], to: "静止", out: [stopAt(LANDING)] },
    ...drag.rows
  ],
  // 没有力场：松手不带速度，停在松手处。最小、最大宽度由宿主换算时限制。
  channel: {
    id: "edge", label: "右边缘位置", unit: "px", min: -columnEdgeBound, max: columnEdgeBound, step: 1, hand: drag.hand, field: toTerminal(NO_FIELD, CUBIC),
    stopCall: call("resize", STOP_POINT, true)
  },
  ports: [
    { name: "resize", params: ["edge", "final"], continuous: true },
    { name: "step", params: ["direction"], continuous: false },
    { name: "cancel", params: [], continuous: false }
  ],
  values: {},
  // 到位时间 0（框架线程 10-25 的写法，协议第 4.4 节第 1 处）：列宽由宿主值重排表格画，通道不画。
  // 取消、宿主改值后通道立刻等于宿主值，紧接着再按下才从把手实际所在处起算；结构上必须的值，写在定义里，动画风格切换不改它。
  params: { settleTime: 0 }
};
