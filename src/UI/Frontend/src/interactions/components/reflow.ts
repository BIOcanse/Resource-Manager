// 重排：已经在场的节点换位置或大小（监视卡片的槽位在增删、换监控项、改大小、拖动排序放下之后），
// docs/animation_style_2026-10-26.md「总原则」：稍大一些的变化都立即生效，是进场退场一类。
// 交互上真实节点当即在终态，可命中；画面上由呈现层（src/presentation/nodeAnimation.ts 的 reflowNode）放一份不可命中的副本，
// 从这一刻画到的样子滑到终态，播完撤掉、真实节点显出来。
// 不读输入。通道是走了多少（0–1）：每次重排是一个新的部件，宿主值从 0 变成 1；副本按这个比例在起点、终点之间插出位置和大小。
// 到终态由动画风格选弹簧或缓动（springOrEase.ts 的部件 slot）：流畅平滑是拖动排序复刻的弹簧，桌面常规、终端顿挫是 --motion-medium 时长的缓动；
// 无动画、超高性能不放副本，当场就是终态。

import type { PartDefinition } from "./common.ts";
import { carryField } from "./springOrEase.ts";

export const reflowDefinition: PartDefinition<number, "进场退场"> = {
  id: "reflow",
  name: "重排",
  motion: "进场退场",
  modes: ["静止"],
  exclusive: [],
  rows: [],
  channel: { id: "progress", label: "走了多少", unit: "", min: 0, max: 1, hand: [], field: carryField("slot") },
  ports: [],
  values: {}
};
