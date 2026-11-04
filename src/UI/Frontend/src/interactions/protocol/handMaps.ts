// Reusable hand-to-object mappings; the default channel map is identity.
// Mappings declare prefixed parameters whose defaults come from the consuming component.
// Scalar channels only. The inverse preserves continuity when grabbing a moving object.

import type { HandMap } from "./definition.ts";
import type { ParamTable } from "./params.ts";
import { cite, declared, show } from "./params.ts";

/** Clamp identity to [min, max]; positions outside that range cannot be inverted. */
export function clampHand(name: string, min: number, max: number): HandMap {
  return {
    name,
    to: (h) => Math.min(max, Math.max(min, h)),
    from: (x) => x,
    describe: () => `手的位置限在 ${min}–${max} 里，1:1`,
  };
}

/** One-sided rubber band asymptotically approaches displacement d. */
const stretch = (x: number, d: number, c: number) => (1 - 1 / ((x * c) / d + 1)) * d;
/** Inverse rubber-band displacement. */
const unstretch = (y: number, d: number, c: number) => (d / c) * (y / (d - y));

/** Outside the range, compress hand displacement asymptotically toward d; the prefixed Rubber coefficient controls resistance. */
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

/** Scale displacement beyond at by the prefixed Elastic parameter. */
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

/** Round hand position to the prefixed Step, including visual motion. The inverse is identity. */
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
