// 弹簧或缓动：到终态的走法由动画风格选（docs/animation_style_2026-10-26.md「各风格的行为」）。
// 流畅平滑用弹簧（接这一刻的速度，冲过会回来，动画中再换终态从当时的位置和速度接着走）；
// 桌面常规、终端顿挫用缓动（按时长和曲线到位，不接速度、不过冲）；无动画、超高性能由运行时直接到终态。
// 选哪一种是这个场声明的参数「到终态用弹簧」（`${prefix}Spring`，1 弹簧、0 缓动），和弹簧、缓动自己的参数一样
// 在全局层由风格换掉（协议第 4.6 节“风格就是一组参数”）。组件给的默认是桌面常规（缓动）和流畅平滑的弹簧参数，
// 定义里不写这几项，所以全局层改得到（协议第 5 节“范围小的覆盖大的”）。
// 只在 Resource Manager 用的组合，放在产品这边（协议 4.6：只在一处用的不进库）。
// 各部件的参数前缀、默认值和出处都在 CARRY_PARTS 一处；风格目录（presentation/animationStyle.ts）按它写全局层。

import type { Field, Relations } from "../protocol/field.ts";
import { byCase, easeField, springField } from "../protocol/field.ts";
import { declared } from "../protocol/params.ts";
import type { Vec } from "../protocol/vec.ts";

/** 参数名：这个部件到终态用弹簧（1）还是缓动（0）。 */
export const springParam = (prefix: string) => `${prefix}Spring`;

export type Curve = readonly [number, number, number, number];
export interface EaseDefaults { readonly ms: number; readonly curve: Curve }
export interface SpringDefaults { readonly responseMs: number; readonly damping: number; readonly stopPx: number }

/** 桌面常规的减速曲线（styles/motion.css 的 --ease-decel）。 */
export const DESKTOP_DECEL: Curve = [0.1, 0.9, 0.2, 1];

/**
 * 用弹簧或缓动到终态的部件：参数前缀就是键。
 * - durationToken：桌面常规、终端顿挫按这种风格的这个动效令牌定缓动时长（styles/motion.css）；ease 是桌面常规的值，也是组件给的默认。
 * - spring：流畅平滑的弹簧，取自磁石复刻（docs/ui_interaction_replicas.md，协议第 4.6 节“现有的风格”）；停下距离按通道的单位定，不随风格变。
 */
export const CARRY_PARTS = {
  // 换页、二级分页、页签面板、设置分区：UIKit 弹簧（画中画复刻），整页这一类大的位移。通道是序号（没有几何，1 单位 = 1 px）。
  // 停下距离 0.005 个序号：画面上是 24 px 进出距离的 0.5%（0.12 px）以下；0.001 时最后几十毫秒只剩看不见的零点几像素还在动，
  // 隔几页换页要 700 ms 以上才停（page-motion 旅程 10-26 实测）。
  page: { by: "换页", durationToken: "--motion-page", ease: { ms: 260, curve: DESKTOP_DECEL },
    spring: { responseMs: 400, damping: 1, stopPx: 0.005 }, source: "UIKit 弹簧（画中画复刻）：响应 400 ms、阻尼比 1" },
  // 弹窗入场：同一个 UIKit 弹簧；Vaul 抽屉的 500 ms、Motion 布局的 450 ms 和它到位的时长相当。通道是开度 0–1，停下距离 0.005（不到 0.03 px）。
  dialog: { by: "弹窗", durationToken: "--motion-medium", ease: { ms: 180, curve: DESKTOP_DECEL },
    spring: { responseMs: 400, damping: 1, stopPx: 0.005 }, source: "UIKit 弹簧（画中画复刻）：响应 400 ms、阻尼比 1" },
  // 选中指示条（页签、二级分页、设置导航、分段选择）：分段控件复刻的 Motion 布局是 450 ms 缓动，换成到位时长相当的 UIKit 弹簧。通道是序号。
  selection: { by: "选中指示条", durationToken: "--motion-medium", ease: { ms: 180, curve: DESKTOP_DECEL },
    spring: { responseMs: 400, damping: 1, stopPx: 0.001 }, source: "分段控件复刻（Motion 布局 450 ms）→ UIKit 弹簧：响应 400 ms、阻尼比 1" },
  // 监视卡片槽位（拖动排序换位、改大小、增删）：拖动排序复刻的 Motion 回位弹簧 stiffness 200、damping 40。通道是卡片的单位。
  slot: { by: "卡片槽位", durationToken: "--motion-medium", ease: { ms: 180, curve: DESKTOP_DECEL },
    spring: { responseMs: 444.3, damping: 1.41, stopPx: 0.001 }, source: "拖动排序复刻（Motion Reorder）：响应 444.3 ms、阻尼比 1.41" },
  // 风扇曲线点、数值滑条（键盘一步、取消回位、外部改值）：平滑滑块复刻的弹簧。通道有几何，单位是屏幕 px。
  control: { by: "控件到位", durationToken: "--motion-medium", ease: { ms: 180, curve: DESKTOP_DECEL },
    spring: { responseMs: 100, damping: 1, stopPx: 0.5 }, source: "平滑滑块复刻：响应 100 ms、阻尼比 1" }
} as const satisfies Record<string, { by: string; durationToken: string; ease: EaseDefaults; spring: SpringDefaults; source: string }>;

export type CarryPart = keyof typeof CARRY_PARTS;

/**
 * 开关和折叠（同一份定义）：流畅平滑用回点弹簧（协议参数「回点时间」，没有终态时自己指向最近的一端，甩得够快越过中点就到另一端），
 * 其余用缓动，松手按位置落到近的一端。前缀 switch。
 */
export const SWITCH_PART = { prefix: "switch", by: "开关", durationToken: "--motion-medium", ease: { ms: 180, curve: DESKTOP_DECEL } } as const;

/** 按「到终态用弹簧」选 spring 或 ease，并声明这一项（默认 0：缓动）。 */
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

/**
 * 带向放置点：「到终态用弹簧」（`${prefix}Spring`）为 1 时按弹簧（`${prefix}SpringResponse`、`${prefix}SpringDamping`、`${prefix}SpringStop`），
 * 否则按缓动（`${prefix}EaseMs` 和曲线的四个数）。没有被带向的放置点时原地停（两种场都是）。
 */
export function springOrEase(by: string, prefix: string, ease: EaseDefaults, spring: SpringDefaults): Field {
  return chooseBySpring(by, prefix, springField(by, prefix, spring.responseMs, spring.damping, spring.stopPx), easeField(by, prefix, ease.ms, ease.curve));
}

/** CARRY_PARTS 里一个部件的场。 */
export function carryField(part: CarryPart): Field {
  const spec = CARRY_PARTS[part];
  return springOrEase(spec.by, part, spec.ease, spec.spring);
}

/**
 * 有输出映射点的通道（开关、折叠）：「到终态用弹簧」为 1 时按 springy（回点弹簧，没有终态时自己指向最近的点），
 * 否则按缓动；缓动没有被带向的放置点时（松手）到离出发处最近的输出映射点，所以松手按位置落到近的一端，不接速度。
 */
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
