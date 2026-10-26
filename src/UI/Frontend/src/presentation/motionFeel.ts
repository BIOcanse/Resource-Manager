// 手感目录：一种手感是一组动效参数，换手感就换掉整个界面的动画节奏。规则和出处见 docs/motion_feel_2026-10-25.md。
// styles/motion.css 按这里的值写 :root[data-motion-feel] 的覆盖，tests/motionFeelContract.test.ts 核对两边一致。
// 动画档位（自动／正常／无／极简）决定动不动，手感只决定怎么动，两者互不相干。
// 一种手感两部分一起换：CSS 动效令牌，和磁石（Lodestone）全局层的覆盖（协议第 5 节，范围小的覆盖大的，组件定义写的值不受影响）。

import type { ParamOverrides } from "../interactions/protocol/params.ts";
import { CONTENT_TRANSITION_PREFIX } from "../interactions/components/contentTransition.ts";
import { ResourceManager } from "../preferences/generated/frontendPoints.values.ts";

export type MotionFeelId = "standard" | "smooth" | "crisp";

export type MotionDurationToken = "--motion-fast" | "--motion-medium" | "--motion-page" | "--motion-panel" | "--motion-bar";
export type MotionEaseToken = "--ease-decel" | "--ease-standard";

export interface MotionFeelDefinition {
  id: MotionFeelId;
  durationsMs: Readonly<Record<MotionDurationToken, number>>;
  easings: Readonly<Record<MotionEaseToken, readonly [number, number, number, number]>>;
  /**
   * 磁石全局层整层换成这一组；空的是不覆盖，取引擎来源值和组件给的默认。
   * 到位时间 settleTime、回点时间 springTime 跟 --motion-medium 一致；
   * 换页缓动（内容过渡，pageEase*）的时长和曲线跟 --motion-page、--ease-decel 一致。
   */
  lodestone: Readonly<ParamOverrides>;
}

// 第一个是 :root 的值（标准，现在的样子）；其余由 motion.css 的 [data-motion-feel] 覆盖。
export const motionFeels: readonly MotionFeelDefinition[] = [
  {
    id: "standard",
    durationsMs: { "--motion-fast": 100, "--motion-medium": 180, "--motion-page": 260, "--motion-panel": 300, "--motion-bar": 360 },
    easings: { "--ease-decel": [0.1, 0.9, 0.2, 1], "--ease-standard": [0.2, 0, 0, 1] },
    // 引擎的来源值：到位时间取动效令牌 180，回点时间协议默认 180。
    lodestone: {}
  },
  {
    // 磁石协议 4.6 风格表：Vaul 的减速曲线、Motion 布局的标准曲线，时长约 1.4 倍。
    id: "smooth",
    durationsMs: { "--motion-fast": 150, "--motion-medium": 260, "--motion-page": 380, "--motion-panel": 420, "--motion-bar": 480 },
    easings: { "--ease-decel": [0.32, 0.72, 0, 1], "--ease-standard": [0.4, 0, 0.1, 1] },
    lodestone: { settleTime: 260, springTime: 260, ...contentEase(380, [0.32, 0.72, 0, 1]) }
  },
  {
    // 标准的曲线，时长约 0.65 倍。
    id: "crisp",
    durationsMs: { "--motion-fast": 70, "--motion-medium": 120, "--motion-page": 170, "--motion-panel": 200, "--motion-bar": 240 },
    easings: { "--ease-decel": [0.1, 0.9, 0.2, 1], "--ease-standard": [0.2, 0, 0, 1] },
    lodestone: { settleTime: 120, springTime: 120, ...contentEase(170, [0.1, 0.9, 0.2, 1]) }
  }
];

/** 内容过渡的换页缓动参数（easeField 按前缀声明的五项）。 */
function contentEase(ms: number, curve: readonly [number, number, number, number]): ParamOverrides {
  const p = CONTENT_TRANSITION_PREFIX;
  return { [`${p}EaseMs`]: ms, [`${p}EaseX1`]: curve[0], [`${p}EaseY1`]: curve[1], [`${p}EaseX2`]: curve[2], [`${p}EaseY2`]: curve[3] };
}

export const motionFeelIds: readonly MotionFeelId[] = motionFeels.map((feel) => feel.id);

/** 目录里的手感；不是目录里的值（例如旧缓存没有这一项）取前端自由点 appearance-motion_feel 的默认值。 */
export function motionFeel(id?: string | null): MotionFeelDefinition {
  return motionFeels.find((feel) => feel.id === id)
    ?? motionFeels.find((feel) => feel.id === ResourceManager.FrontendPoints.MotionFeel)!;
}

// 根元素是外观的唯一发布点；样式表按 data-motion-feel 取令牌。
export function applyMotionFeel(root: HTMLElement, feel: MotionFeelId): void {
  root.dataset.motionFeel = feel;
}
