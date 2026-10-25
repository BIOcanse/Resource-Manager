// 从样式表读动效令牌：脚本画的过渡（画布、按时长计时的状态）和 CSS 过渡用同一个时长和曲线，
// 手感（:root[data-motion-feel]）和窗口缩放（body[data-window-resizing] 把时长置 0）也就一起管到。
// 令牌由 styles/motion.css 定义，读不出或格式不对是样式表写错了，直接报错，不另给一个默认值。

import { cubicBezier } from "../interactions/protocol/ease.ts";
import type { MotionDurationToken, MotionEaseToken } from "./motionFeel.ts";

/** --ease-bar 不随手感变（motion.css），不在手感目录里，但同样从样式表读。 */
export type MotionReadableEaseToken = MotionEaseToken | "--ease-bar";

/** 元素上令牌的时长，单位 ms（样式表写 ms 或 s）。 */
export function readMotionDurationMs(element: Element, token: MotionDurationToken): number {
  const text = getComputedStyle(element).getPropertyValue(token).trim();
  const match = /^(-?\d*\.?\d+)(ms|s)$/.exec(text);
  if (!match) throw new Error(`Motion token ${token} is not a duration: "${text}"`);
  const value = Number(match[1]);
  return match[2] === "s" ? value * 1000 : value;
}

/** 元素上令牌的曲线：cubic-bezier 的四个数解成进度 → 缓动后的进度。 */
export function readMotionEase(element: Element, token: MotionReadableEaseToken): (progress: number) => number {
  const text = getComputedStyle(element).getPropertyValue(token).trim();
  const match = /^cubic-bezier\(\s*([^,]+),\s*([^,]+),\s*([^,]+),\s*([^)]+)\)$/.exec(text);
  const points = match ? match.slice(1).map(Number) : [];
  if (points.length !== 4 || points.some((point) => !Number.isFinite(point))) {
    throw new Error(`Motion token ${token} is not a cubic-bezier: "${text}"`);
  }
  return cubicBezier(points[0], points[1], points[2], points[3]);
}
