// 可复用的手的映射（协议第 4.5 节“谁在带物体”）：手的位置到物体位置的映射，通道不写就是手的位置本身。
// 和可复用的场一样，常用的写一次，组件直接用，手感统一靠复用。参数是它声明的（名字带上前缀），默认值由用它的组件给。
// 都只用于一条轴的通道。from 是反推：按下时手所在处由物体的位置按它反推，所以按住正在动的物体不会跳；反推不出的写明。

import type { HandMap } from "./definition.ts";
import type { ParamTable } from "./params.ts";
import { cite, declared, show } from "./params.ts";

/** 限在范围里：手的位置 1:1，不出 [min, max]。范围外反推不出手，按住范围外的物体再拖会跳回范围里。 */
export function clampHand(name: string, min: number, max: number): HandMap {
  return {
    name,
    to: (h) => Math.min(max, Math.max(min, h)),
    from: (x) => x,
    describe: () => `手的位置限在 ${min}–${max} 里，1:1`,
  };
}

/** 橡皮筋的一边：手越过 x，位置越过 (1 − 1/(x·c/d + 1))·d，永远到不了 d。 */
const stretch = (x: number, d: number, c: number) => (1 - 1 / ((x * c) / d + 1)) * d;
/** 反推：位置越过 y，手越过 (d/c)·y/(d − y)。 */
const unstretch = (y: number, d: number, c: number) => (d / c) * (y / (d - y));

/**
 * 橡皮筋（复刻 iOS 列表时加的）：[min, max] 之外，位置 = 边缘 ± (1 − 1/(手越过的距离·c/d + 1))·d，越拉越重，永远到不了 d。
 * d 是能拉出的最远距离（iOS 是视口高），系数 c 是它声明的参数 `${prefix}Rubber`，越小越拉不动。
 */
export function rubberBand(by: string, prefix: string, min: number, max: number, d: number, c: number): HandMap {
  const id = `${prefix}Rubber`;
  const name = `橡皮筋（${by}）`;
  const who = `手的映射「${name}」`;
  const k = (p: ParamTable) => p[id].value;
  return {
    name,
    to: (h, p) => (h < min ? min - stretch(min - h, d, k(p)) : h > max ? max + stretch(h - max, d, k(p)) : h),
    from: (x, p) => (x < min ? min - unstretch(min - x, d, k(p)) : x > max ? max + unstretch(x - max, d, k(p)) : x),
    describe: (p) => `${min}–${max} 之外，位置越过 (1 − 1/(手越过的距离·${show(k(p))}/${d} + 1))·${d}，永远到不了 ${d}（${cite(p[id])}）`,
    declares: [declared(who, id, `${by}橡皮筋系数`, "", c, `${who}：越过边缘后位置的比例，越小越拉不动`)],
  };
}

/** 超过一处按比例（复刻 Ionic 横滑行时加的）：手的位置超过 at 后，多出的部分乘 k（声明的参数 `${prefix}Elastic`）。 */
export function elasticBeyond(by: string, prefix: string, at: number, k: number): HandMap {
  const id = `${prefix}Elastic`;
  const name = `弹性（${by}）`;
  const who = `手的映射「${name}」`;
  const e = (p: ParamTable) => p[id].value;
  return {
    name,
    to: (h, p) => (h > at ? at + (h - at) * e(p) : h),
    from: (x, p) => (x > at ? at + (x - at) / e(p) : x),
    describe: (p) => `超过 ${at} 后，多出的部分乘 ${show(e(p))}（${cite(p[id])}）`,
    declares: [declared(who, id, `${by}弹性系数`, "", k, `${who}：超过 ${at} 后多出的部分乘它`)],
  };
}

/**
 * 按步长取整（复刻 Radix 滑块时加的）：位置 = min + round((手的位置 − min) ÷ 步长) × 步长，步长是它声明的参数 `${prefix}Step`。
 * 画面也取整（通道的步长只管调用参数）：没有动画的吸附就是它。反推就是位置本身。
 */
export function snapToStep(by: string, prefix: string, min: number, step: number): HandMap {
  const id = `${prefix}Step`;
  const name = `按步长取整（${by}）`;
  const who = `手的映射「${name}」`;
  return {
    name,
    to: (h, p) => min + Math.round((h - min) / p[id].value) * p[id].value,
    from: (x) => x,
    describe: (p) => `位置 = ${min} + round((手的位置 − ${min}) ÷ ${show(p[id].value)}) × ${show(p[id].value)}（${cite(p[id])}）`,
    declares: [declared(who, id, `${by}步长`, "", step, `${who}：位置只停在 ${min} + 它的整数倍 上`)],
  };
}
