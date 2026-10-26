// 选中指示条：一级页签、二级分页条、对话框里的页签组、设置导航、分段选择共用（docs/page_motion_2026-10-25.md）。
// 不读输入：点击、键盘照旧由按钮、页签、单选按钮自己处理，选中项变了就是宿主值变了（协议第 4.5 节“缓存值被改”），
// 力场把指示条从这一刻画到的位置带到新选中的那一项。
// 通道是选中项的序号（分页树或列表里的次序），不是屏幕位置：两项之间的位置、宽度由呈现层按“这一段走了多少”插出来，
// 所以换一组分页（二级分页条换一页）时指示条也是从旧的那一项滑到新的那一项。
// 到终态由动画风格选弹簧或缓动（springOrEase.ts 的部件 selection）：流畅平滑是弹簧，快速连点时接住当时的速度；
// 桌面常规、终端顿挫是 --motion-medium 时长的缓动。

import type { Definition } from "../protocol/definition.ts";
import { carryField } from "./springOrEase.ts";

/** 序号的边界：远大于任何一组的项数。 */
export const SELECTION_ORDER_BOUND = 100000;

export const selectionIndicatorDefinition: Definition = {
  id: "selection-indicator",
  name: "选中指示条",
  modes: ["静止"],
  exclusive: [],
  rows: [],
  channel: { id: "order", label: "选中项的序号", unit: "", min: 0, max: SELECTION_ORDER_BOUND, hand: [], field: carryField("selection") },
  ports: [],
  values: {}
};
