// 监视卡片上一块区域（一个槽位，或编辑时末尾的“+”）的位置和大小。不读任何输入：指针、键盘都交给里面的拖动排序项和外面的卡片；
// 宿主映射把它设成禁用，禁用的组件不参与路由，物体照常被带到宿主值（协议第 4.2 节）。
// 通道是平面，按卡片的单位算：x = 上边在第几个单位，y = 占几个单位。宿主值是这块区域此刻该在的地方，由卡片槽位的顺序和大小算出；
// 改大小、新增、删除、换位置时宿主值变了，由动画风格选的弹簧或缓动带到终态（springOrEase.ts 的部件 slot）：
// 流畅平滑是拖动排序复刻的弹簧（接住正在走的速度），桌面常规、终端顿挫是 --motion-medium 时长的缓动；无动画、超高性能直接到终态。新加的槽位从高度 0 出发。呈现层只按通道写这块区域的上边和高度（features/monitor/Dashboard.tsx）。

import type { Definition } from "../protocol/definition.ts";
import { carryField } from "./springOrEase.ts";
import type { Vec } from "../protocol/vec.ts";

/** 通道边界（单位）：远大于一张卡片能有的单位，宿主值总在里面。 */
export const CARD_SLOT_LAYOUT_BOUND = 64;

export const cardSlotLayoutDefinition: Definition<Vec> = {
  id: "card-slot-layout",
  name: "卡片槽位",
  modes: ["静止"],
  exclusive: [],
  held: {},
  rows: [],
  channel: {
    id: "slot",
    label: "上边和高度",
    unit: "单位",
    min: { x: 0, y: 0 },
    max: { x: CARD_SLOT_LAYOUT_BOUND, y: CARD_SLOT_LAYOUT_BOUND },
    hand: [],
    field: carryField("slot")
  },
  ports: [],
  values: {}
};
