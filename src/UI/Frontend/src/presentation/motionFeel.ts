// 手感目录：一种手感是一组动效参数，换手感就换掉整个界面的动画节奏。规则和出处见 docs/motion_feel_2026-10-25.md。
// styles/motion.css 按这里的值写 :root[data-motion-feel] 的覆盖，tests/motionFeelContract.test.ts 核对两边一致。
// 动画档位（自动／正常／无／极简）决定动不动，手感只决定怎么动，两者互不相干。

export type MotionFeelId = "standard" | "smooth" | "crisp";

export type MotionDurationToken = "--motion-fast" | "--motion-medium" | "--motion-page" | "--motion-panel" | "--motion-bar";
export type MotionEaseToken = "--ease-decel" | "--ease-standard";

export interface MotionFeelDefinition {
  id: MotionFeelId;
  durationsMs: Readonly<Record<MotionDurationToken, number>>;
  easings: Readonly<Record<MotionEaseToken, readonly [number, number, number, number]>>;
}

// 第一个是 :root 的值（标准，现在的样子）；其余由 motion.css 的 [data-motion-feel] 覆盖。
export const motionFeels: readonly MotionFeelDefinition[] = [
  {
    id: "standard",
    durationsMs: { "--motion-fast": 100, "--motion-medium": 180, "--motion-page": 260, "--motion-panel": 300, "--motion-bar": 360 },
    easings: { "--ease-decel": [0.1, 0.9, 0.2, 1], "--ease-standard": [0.2, 0, 0, 1] }
  },
  {
    // 磁石协议 4.6 风格表：Vaul 的减速曲线、Motion 布局的标准曲线，时长约 1.4 倍。
    id: "smooth",
    durationsMs: { "--motion-fast": 150, "--motion-medium": 260, "--motion-page": 380, "--motion-panel": 420, "--motion-bar": 480 },
    easings: { "--ease-decel": [0.32, 0.72, 0, 1], "--ease-standard": [0.4, 0, 0.1, 1] }
  },
  {
    // 标准的曲线，时长约 0.65 倍。
    id: "crisp",
    durationsMs: { "--motion-fast": 70, "--motion-medium": 120, "--motion-page": 170, "--motion-panel": 200, "--motion-bar": 240 },
    easings: { "--ease-decel": [0.1, 0.9, 0.2, 1], "--ease-standard": [0.2, 0, 0, 1] }
  }
];

export const motionFeelIds: readonly MotionFeelId[] = motionFeels.map((feel) => feel.id);

// 根元素是外观的唯一发布点；样式表按 data-motion-feel 取令牌。
export function applyMotionFeel(root: HTMLElement, feel: MotionFeelId): void {
  root.dataset.motionFeel = feel;
}
