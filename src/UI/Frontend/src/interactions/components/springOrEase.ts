// Animation styles select spring or easing for terminal motion.
// Springs preserve current velocity when retargeted.
// Easing uses a fixed duration without momentum or overshoot; disabled modes snap.
// The per-part Spring parameter selects the field.
// Styles override parameters at the global layer.
// Keep these parameters out of definitions so global overrides apply.

// CARRY_PARTS owns per-part prefixes and defaults used by the style catalog.

import type { Field, Relations } from "../protocol/field.ts";
import { byCase, easeField, springField } from "../protocol/field.ts";
import { declared } from "../protocol/params.ts";
import type { Vec } from "../protocol/vec.ts";

/** Parameter selecting spring (1) or easing (0). */
export const springParam = (prefix: string) => `${prefix}Spring`;

export type Curve = readonly [number, number, number, number];
export interface EaseDefaults { readonly ms: number; readonly curve: Curve }
export interface SpringDefaults { readonly responseMs: number; readonly damping: number; readonly stopPx: number }

/** Default deceleration curve, shared with --ease-decel. */
export const DESKTOP_DECEL: Curve = [0.1, 0.9, 0.2, 1];

/** Per-part easing tokens and spring defaults. Stop distance uses channel units and does not vary by style. */
export const CARRY_PARTS = {
  // Page transitions use the UIKit-style spring in order coordinates.
  // A 0.005-order stop threshold avoids invisible motion tails.

  page: { by: "换页", durationToken: "--motion-page", ease: { ms: 260, curve: DESKTOP_DECEL },
    spring: { responseMs: 400, damping: 1, stopPx: 0.005 }, source: "UIKit 弹簧（画中画复刻）：响应 400 ms、阻尼比 1" },
  // Dialog entry uses the same spring in a 0-1 openness channel.
  dialog: { by: "弹窗", durationToken: "--motion-medium", ease: { ms: 180, curve: DESKTOP_DECEL },
    spring: { responseMs: 400, damping: 1, stopPx: 0.005 }, source: "UIKit 弹簧（画中画复刻）：响应 400 ms、阻尼比 1" },
  // Selection motion uses a UIKit-style spring in order coordinates.
  selection: { by: "选中指示条", durationToken: "--motion-medium", ease: { ms: 180, curve: DESKTOP_DECEL },
    spring: { responseMs: 400, damping: 1, stopPx: 0.001 }, source: "分段控件复刻（Motion 布局 450 ms）→ UIKit 弹簧：响应 400 ms、阻尼比 1" },
  // Slot reflow uses a return spring with stiffness 200 and damping 40.
  // The 0-1 channel drives a noninteractive copy.
  slot: { by: "卡片槽位", durationToken: "--motion-medium", ease: { ms: 180, curve: DESKTOP_DECEL },
    spring: { responseMs: 444.3, damping: 1.41, stopPx: 0.001 }, source: "拖动排序复刻（Motion Reorder）：响应 444.3 ms、阻尼比 1.41" },
  // Curve points and sliders use screen-pixel geometry for spring parameters.
  control: { by: "控件到位", durationToken: "--motion-medium", ease: { ms: 180, curve: DESKTOP_DECEL },
    spring: { responseMs: 100, damping: 1, stopPx: 0.5 }, source: "平滑滑块复刻：响应 100 ms、阻尼比 1" }
} as const satisfies Record<string, { by: string; durationToken: string; ease: EaseDefaults; spring: SpringDefaults; source: string }>;

export type CarryPart = keyof typeof CARRY_PARTS;

/** Switches and disclosures share a field: springs preserve momentum, easing selects the nearest endpoint. */
export const SWITCH_PART = { prefix: "switch", by: "开关", durationToken: "--motion-medium", ease: { ms: 180, curve: DESKTOP_DECEL } } as const;

/** Select spring or easing and declare the selector, defaulting to easing. */
function chooseBySpring(by: string, prefix: string, spring: Field, ease: Field): Field {
  const id = springParam(prefix);
  const who = `组合「弹簧或缓动（${by}）」`;
  const chosen = byCase(`弹簧或缓动（${by}）`, "到终态用弹簧为 1 时", (r: Relations) => r.params[id].value === 1, spring, "否则", ease);
  return {
    ...chosen,
    params: [...chosen.params, id],
    declares: [...(chosen.declares ?? []), declared(who, id, `${by}到终态用弹簧`, "", 0, `${who}：1 用弹簧（接开始时的速度），0 用缓动（不接速度）；动画风格在全局层换`)]
  };
}

/** Carry to the placement point using the selected field; stop in place when no placement is active. */
export function springOrEase(by: string, prefix: string, ease: EaseDefaults, spring: SpringDefaults): Field {
  return chooseBySpring(by, prefix, springField(by, prefix, spring.responseMs, spring.damping, spring.stopPx), easeField(by, prefix, ease.ms, ease.curve));
}

/** Field for a CARRY_PARTS entry. */
export function carryField(part: CarryPart): Field {
  const spec = CARRY_PARTS[part];
  return springOrEase(spec.by, part, spec.ease, spec.spring);
}

/** For output-mapped channels, springs target the nearest point with momentum; easing selects the nearest point by position. */
export function springOrEaseToPoints(springy: Field): Field {
  const eased = easeField(SWITCH_PART.by, SWITCH_PART.prefix, SWITCH_PART.ease.ms, SWITCH_PART.ease.curve);
  const toNearest: Field<{ x: Vec }> = {
    ...eased,
    name: `${eased.name}，松手到最近的点`,
    begin: (r) => eased.begin(r.target !== null ? r : { ...r, target: r.nearest(r.start.x) }),
    describe: (p) => [...eased.describe(p), "没有被带向的放置点时（松手）到离出发处最近的输出映射点"]
  };
  return chooseBySpring(SWITCH_PART.by, SWITCH_PART.prefix, springy, toNearest);
}
