// 内容过渡：换页、切二级分页、对话框里的页签、设置的分区共用（docs/page_motion_2026-10-25.md）。
// 不读输入。显示的内容换了就是宿主值变了：通道是这份内容的序号（分页树或列表里的次序），
// 力场把它从这一刻画到的序号带到新内容的序号，呈现层按“这一段走了多少”画新内容进来、旧内容出去，方向就是序号变大还是变小。
// 到终态由动画风格选弹簧或缓动（springOrEase.ts 的部件 page，参数前缀 page）：流畅平滑是弹簧，带着速度、随时从当时的位置接着走；
// 桌面常规、终端顿挫是 --motion-page 时长的缓动；风格在全局层换掉。

import type { Definition } from "../protocol/definition.ts";
import { SELECTION_ORDER_BOUND } from "./selectionIndicator.ts";
import { carryField } from "./springOrEase.ts";

export const contentTransitionDefinition: Definition = {
  id: "content-transition",
  name: "内容过渡",
  modes: ["静止"],
  exclusive: [],
  rows: [],
  channel: {
    id: "order", label: "内容的序号", unit: "", min: 0, max: SELECTION_ORDER_BOUND, hand: [],
    field: carryField("page")
  },
  ports: [],
  values: {}
};
