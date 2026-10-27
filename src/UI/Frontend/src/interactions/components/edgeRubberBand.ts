// 滚动区的边缘回弹（docs/animation_style_2026-10-26.md「各风格的行为」：流畅平滑在滚动到边缘时回弹）。
// 滚动照旧是浏览器的原生滚动；只有越过边缘的那一段由这里带：滚轮在边缘还往外滚，越过的量按 iOS 列表复刻的橡皮筋
// （位移 = (1 − 1/(越过的量·系数/d + 1))·d，d 是滚动区的高，docs/ui_interaction_replicas.md）算出内容该偏出去多少，
// 这就是宿主值；滚轮停了，宿主值回到 0，内容按临界阻尼弹簧回到边缘（ω = 2π ÷ 400 ms，和复刻的越界回弹相同）。
// 通道是内容的偏移（px，正是往下）。跟着滚轮时用一个快的临界阻尼弹簧把偏移带到宿主值，免得滚轮一格一跳。
// 橡皮筋系数是声明的参数 edgeBandRubber：组件给的默认 0 是不回弹（桌面常规），流畅平滑在全局层把它换成复刻的 0.55。
// 平面移动（docs/animation_style_2026-10-26.md「总原则」）：偏的是已经在场的滚动内容，平移写在真实节点上，回弹期间命中区域随内容偏。

import type { PartDefinition } from "./common.ts";
import { byCase, springField } from "../protocol/field.ts";
import { declared } from "../protocol/params.ts";

/** 偏移的边界：远大于任何滚动区的高。 */
export const EDGE_BAND_BOUND = 100000;
/** 参数名：橡皮筋系数，0 不回弹。 */
export const EDGE_BAND_RUBBER = "edgeBandRubber";
/** 复刻的值：iOS 列表的橡皮筋系数、越界回弹的周期。 */
export const IOS_RUBBER = 0.55;
export const IOS_BOUNCE_PERIOD_MS = 400;
/** 跟着滚轮时的弹簧响应时间：比回弹快得多，只为把一格一格的滚轮连成一段。 */
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
