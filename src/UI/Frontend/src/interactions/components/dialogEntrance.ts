// 弹窗入场：对话框和它的遮罩从看不见到在位（docs/animation_style_2026-10-26.md「各风格的行为」）。
// 不读输入。通道是开度 0–1：对话框挂上时宿主值从 0 变成 1，力场把开度带到 1，呈现层按开度画不透明度、位移和缩放。
// 到终态由动画风格选弹簧或缓动（springOrEase.ts 的部件 dialog，参数前缀 dialog）：流畅平滑是弹簧，
// 桌面常规、终端顿挫是 --motion-medium 时长的缓动；无动画、超高性能由运行时直接到终态。关闭是立刻撤掉，不走这里。

import type { Definition } from "../protocol/definition.ts";
import { carryField } from "./springOrEase.ts";

export const dialogEntranceDefinition: Definition = {
  id: "dialog-entrance",
  name: "弹窗入场",
  modes: ["静止"],
  exclusive: [],
  rows: [],
  channel: {
    id: "openness", label: "开度", unit: "", min: 0, max: 1, hand: [],
    field: carryField("dialog")
  },
  ports: [],
  values: {}
};
