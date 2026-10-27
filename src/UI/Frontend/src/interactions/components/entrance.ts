// 进场：一个刚挂上的节点从看不见到看得见（docs/animation_style_2026-10-26.md「总原则」）。
// 不读输入。通道是开度 0–1：每个进场的节点各有一个新的部件，宿主值从 0 变成 1，力场把开度带到 1；
// 呈现层（src/presentation/nodeAnimation.ts）只按开度画不透明度，不画位移、缩放（进场不能挪动命中区域）。
// 新节点不接旧节点的位置和速度：每次进场都从开度 0、静止开始。
// 到终态由动画风格选弹簧或缓动（springOrEase.ts 的部件：弹窗是 dialog，换页、切分页、页签、设置分区是 page）；
// 无动画、超高性能由运行时直接到终态。
import type { PartDefinition } from "./common.ts";
import { carryField } from "./springOrEase.ts";

/** 有进场的部件：参数前缀就是 springOrEase.ts 的部件名。 */
export type EntrancePart = "dialog" | "page";

const NAMES: Readonly<Record<EntrancePart, string>> = { dialog: "弹窗进场", page: "内容进场" };

function entranceDefinition(part: EntrancePart): PartDefinition<number, "进场退场"> {
  return {
    id: `${part}-entrance`,
    name: NAMES[part],
    motion: "进场退场",
    modes: ["静止"],
    exclusive: [],
    rows: [],
    channel: {
      id: "openness", label: "开度", unit: "", min: 0, max: 1, hand: [],
      field: carryField(part)
    },
    ports: [],
    values: {}
  };
}

export const entranceDefinitions: Readonly<Record<EntrancePart, PartDefinition<number, "进场退场">>> = {
  dialog: entranceDefinition("dialog"),
  page: entranceDefinition("page")
};
