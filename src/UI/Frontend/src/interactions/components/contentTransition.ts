// 内容过渡：换页、切二级分页、对话框里的页签、设置的分区共用（docs/page_motion_2026-10-25.md）。
// 不读输入。显示的内容换了就是宿主值变了：通道是这份内容的序号（分页树或列表里的次序），
// 力场把它从这一刻画到的序号带到新内容的序号，呈现层按“这一段走了多少”画新内容进来、旧内容出去，方向就是序号变大还是变小。
// 到终态用缓动：时长和曲线是这个场声明的参数（换页缓动），默认是 --motion-page 和 --ease-decel，手感切换在全局层换掉。

import type { Definition } from "../protocol/definition.ts";
import { easeField } from "../protocol/field.ts";
import { SELECTION_ORDER_BOUND } from "./selectionIndicator.ts";

/** 换页缓动的默认：标准手感的 --motion-page 和 --ease-decel（styles/motion.css）。 */
export const CONTENT_TRANSITION_EASE_MS = 260;
export const CONTENT_TRANSITION_CURVE = [0.1, 0.9, 0.2, 1] as const;
/** 换页缓动的参数前缀：参数名是 pageEaseMs、pageEaseX1 … pageEaseY2。 */
export const CONTENT_TRANSITION_PREFIX = "page";

export const contentTransitionDefinition: Definition = {
  id: "content-transition",
  name: "内容过渡",
  modes: ["静止"],
  exclusive: [],
  rows: [],
  channel: {
    id: "order", label: "内容的序号", unit: "", min: 0, max: SELECTION_ORDER_BOUND, hand: [],
    field: easeField("换页", CONTENT_TRANSITION_PREFIX, CONTENT_TRANSITION_EASE_MS, CONTENT_TRANSITION_CURVE)
  },
  ports: [],
  values: {}
};
