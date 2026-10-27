// 设置开关的滑块（来自 Lodestone components/toggle.ts 的滑球开关）。
// 和演示的差别：开关是原生复选框，点击、空格、点标签文字照旧由浏览器切换，这份定义不读点击和激活请求；
// 它只管滑块：宿主值变了由回点弹簧带到另一端，按住滑块拖动，松手停在落点（甩得够快越过中点就到另一端）。
// 拖完松手的那一行认领，接入层拦掉紧随其后的 click，复选框不会再被切换一次（协议第 4.4 节“拖完不触发点击”）。
// 折叠标题（ui/primitives/Disclosure.tsx）也用这份定义（协议 4.6：演示的折叠面板和开关同一份定义）：没有几何，不能拖，开度跟宿主值走。

import { LANDING, STOP_POINT } from "../protocol/definition.ts";
import { SPRING } from "../protocol/field.ts";
import { springOrEaseToPoints } from "./springOrEase.ts";
import { call, CLAIM, dragBasics, stopAt } from "./common.ts";
import type { PartDefinition } from "./common.ts";

const drag = dragBasics();

export const switchToggleDefinition: PartDefinition<number, "平面移动"> = {
  id: "settings-switch",
  name: "设置开关",
  motion: "平面移动",
  modes: drag.modes,
  exclusive: drag.exclusive,
  held: drag.held,
  rows: [
    { from: ["静止"], on: ["pointer.left↑"], to: "按下", note: "不认领：浏览器照常聚焦、点击切换复选框" },
    { from: ["按下"], on: ["pointer.position~"], when: { "离开拖动框": true, "可拖": true }, to: "拖动" },
    { from: ["按下"], on: ["pointer.position~"], when: { "离开拖动框": true, "可拖": false }, to: "=", note: "不能拖，只能点" },
    { from: ["按下"], on: ["pointer.left↓"], to: "静止", note: "点击：复选框由浏览器切换，宿主值变了滑块再过去" },
    { from: ["拖动"], on: ["pointer.position~"], to: "=", note: "通道跟手" },
    { from: ["拖动"], on: ["pointer.left↓"], to: "静止", out: [CLAIM, stopAt(LANDING)], note: "认领：这次松开不再产生点击" },
    ...drag.rows
  ],
  // 两端是输出映射点，也是可能的终态。流畅平滑用回点弹簧：松手时拉向离得近的一端（带着速度，甩得够快越过中点就到另一端），
  // 宿主值变了拉向终态；桌面常规、终端顿挫用缓动，松手按位置到近的一端，不接速度（springOrEase.ts 的开关）。
  channel: {
    id: "openness", label: "开度", unit: "", min: 0, max: 1, step: 1, mappingPoints: [0, 1], hand: drag.hand, field: springOrEaseToPoints(SPRING),
    stopCall: call("select", STOP_POINT)
  },
  ports: [{ name: "select", params: ["value"], continuous: false }],
  // 滑块甩出去要带着速度：松手不吸能。
  params: { releaseAbsorb: 0 },
  values: {}
};
