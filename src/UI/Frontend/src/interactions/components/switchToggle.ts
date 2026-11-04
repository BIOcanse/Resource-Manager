// The native checkbox owns clicks, Space, and label activation.

// This definition animates host changes and thumb dragging.
// Claim release after dragging so the following click cannot toggle the checkbox again.
// Disclosures share this definition without geometry or dragging.

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
  // Endpoints are output mapping points; spring release preserves momentum toward the nearest one.
  // Host changes target the terminal endpoint; easing release selects by position.
  channel: {
    id: "openness", label: "开度", unit: "", min: 0, max: 1, step: 1, mappingPoints: [0, 1], hand: drag.hand, field: springOrEaseToPoints(SPRING),
    stopCall: call("select", STOP_POINT)
  },
  ports: [{ name: "select", params: ["value"], continuous: false }],
  // Preserve velocity on release.
  params: { releaseAbsorb: 0 },
  values: {}
};
