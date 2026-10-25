// 资源表列宽把手（协议第 4.4 节第 1 处迁移；来自 Lodestone components/resizeHandle.ts）。
// 拖动中连续调用 resize(当前值, 否)，停住时调用一次最终值 resize(停住点, 是)：松开停在落点，方向键一步，取消回起点宽度。
// 和演示的差别：最小、最大宽度和方向键步长取产品现值；产品没有默认宽度可复原，所以没有双击复原那一行。

import { CURRENT, LANDING, ORIGIN, STOP_POINT } from "../protocol/definition.ts";
import type { Definition } from "../protocol/definition.ts";
import { CUBIC, NO_FIELD, toTerminal } from "../protocol/field.ts";
import { call, CLAIM, dragBasics, FOCUS, stopAt } from "./common.ts";

export interface ColumnResizeLimits {
  readonly min: number;
  readonly max: number;
  /** 方向键每步多少 px。 */
  readonly keyStep: number;
}

export function columnResizeHandleDefinition(limits: ColumnResizeLimits): Definition {
  const clamp = (width: number) => Math.min(limits.max, Math.max(limits.min, width));
  const drag = dragBasics({ cancel: [stopAt(ORIGIN)] });
  return {
    id: "resource-table-column-resize",
    name: "列宽把手",
    modes: drag.modes,
    exclusive: drag.exclusive,
    held: drag.held,
    rows: [
      { from: ["静止"], on: ["pointer.left↑"], to: "按下", out: [CLAIM, FOCUS] },
      { from: ["静止"], on: ["key.ArrowLeft↑", "key.ArrowLeft⟳"], to: "=", out: [CLAIM, stopAt("窄一步")] },
      { from: ["静止"], on: ["key.ArrowRight↑", "key.ArrowRight⟳"], to: "=", out: [CLAIM, stopAt("宽一步")] },
      { from: ["静止"], on: ["key.ArrowLeft↓", "key.ArrowRight↓"], to: "=" },
      { from: ["按下"], on: ["pointer.position~"], when: { "离开拖动框": true }, to: "拖动", out: [call("resize", CURRENT, false)] },
      { from: ["按下"], on: ["pointer.left↓"], to: "静止", note: "单击不改宽度" },
      { from: ["拖动"], on: ["pointer.position~"], to: "=", out: [call("resize", CURRENT, false)], note: "连续调用" },
      { from: ["拖动"], on: ["pointer.left↓"], to: "静止", out: [stopAt(LANDING)] },
      ...drag.rows
    ],
    // 没有力场：松手不带速度，停在松手处；最小、最大宽度是边界。
    channel: {
      id: "width", label: "列宽", unit: "px", min: limits.min, max: limits.max, step: 1, hand: drag.hand, field: toTerminal(NO_FIELD, CUBIC),
      stopCall: call("resize", STOP_POINT, true)
    },
    ports: [{ name: "resize", params: ["width", "final"], continuous: true }],
    values: {
      "窄一步": (ctx) => ({ value: clamp(ctx.hostValue - limits.keyStep), explain: `宿主值 ${ctx.hostValue} − ${limits.keyStep}，限制在 ${limits.min}–${limits.max}` }),
      "宽一步": (ctx) => ({ value: clamp(ctx.hostValue + limits.keyStep), explain: `宿主值 ${ctx.hostValue} + ${limits.keyStep}，限制在 ${limits.min}–${limits.max}` })
    }
  };
}
