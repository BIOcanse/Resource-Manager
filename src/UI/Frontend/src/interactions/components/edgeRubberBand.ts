// Native scrolling handles the interior; this channel animates only overscroll.
// Rubber-band displacement approaches the scroll area height asymptotically.

// After wheel input stops, the host returns to zero and the spring restores the edge.
// Offsets use pixels, positive downward; a faster spring smooths wheel notches.
// edgeBandRubber controls overscroll strength; zero disables it.
// Transform the live content so hit regions follow the visible displacement.

import type { PartDefinition } from "./common.ts";
import { byCase, springField } from "../protocol/field.ts";
import { declared } from "../protocol/params.ts";

/** Offset bounds exceed any scroll area height. */
export const EDGE_BAND_BOUND = 100000;
/** Rubber-band coefficient; zero disables overscroll. */
export const EDGE_BAND_RUBBER = "edgeBandRubber";
/** iOS-style rubber-band coefficient and return period. */
export const IOS_RUBBER = 0.55;
export const IOS_BOUNCE_PERIOD_MS = 400;
/** A faster tracking spring smooths discrete wheel input. */
const FOLLOW_RESPONSE_MS = 100;

const follow = springField("边缘回弹跟着滚轮", "edgeBandFollow", FOLLOW_RESPONSE_MS, 1, 0.1);
const back = springField("边缘回弹回到边缘", "edgeBandReturn", IOS_BOUNCE_PERIOD_MS, 1, 0.1);
const field = byCase("边缘回弹", "宿主值不是 0（滚轮还在边缘往外滚）时", (r) => r.target !== null && r.target.x !== 0, follow, "否则（滚轮停了）", back);

export const edgeRubberBandDefinition: PartDefinition<number, "平面移动"> = {
  id: "edge-rubber-band",
  name: "滚动区边缘回弹",
  motion: "平面移动",
  modes: ["静止"],
  exclusive: [],
  rows: [],
  channel: { id: "offset", label: "内容的偏移", unit: "px", min: -EDGE_BAND_BOUND, max: EDGE_BAND_BOUND, hand: [], field },
  ports: [],
  values: {},
  declares: [declared("组件「滚动区边缘回弹」", EDGE_BAND_RUBBER, "边缘回弹橡皮筋系数", "", 0,
    "组件「滚动区边缘回弹」：越过边缘的滚轮量按橡皮筋换成偏移，越小越拉不动；0 不回弹（动画风格在全局层换）")]
};
