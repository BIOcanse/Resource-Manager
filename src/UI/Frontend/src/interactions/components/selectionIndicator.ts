// Selection indicators follow host selection without handling input.

// The field carries the current visible position to the newly selected item.
// The channel uses item order; presentation interpolates item position and width.
// Order coordinates also preserve continuity when replacing a group.
// Spring styles preserve velocity during rapid selection changes.
// Other styles use the medium-duration easing.

import type { PartDefinition } from "./common.ts";
import { carryField } from "./springOrEase.ts";

/** Order bounds exceed any group size. */
export const SELECTION_ORDER_BOUND = 100000;

export const selectionIndicatorDefinition: PartDefinition<number, "平面移动"> = {
  id: "selection-indicator",
  name: "选中指示条",
  motion: "平面移动",
  modes: ["静止"],
  exclusive: [],
  rows: [],
  channel: { id: "order", label: "选中项的序号", unit: "", min: 0, max: SELECTION_ORDER_BOUND, hand: [], field: carryField("selection") },
  ports: [],
  values: {}
};
