// 动画风格目录：唯一来源（docs/animation_style_2026-10-26.md「归属」）。一种风格是一组参数，同时换 CSS 动效令牌和磁石（Lodestone）全局层
// （协议第 4.6 节“风格就是一组参数”、第 5 节“范围小的覆盖大的”，组件定义里写死的值不受全局层影响）。
// styles/motion.css 按这里的值写 :root（桌面常规）和 body[data-animations] 的覆盖，tests/animationStyleContract.test.ts 核对两边一致。
// 实际生效的风格写在 body[data-animations]，值就是风格标识（presentation/documentAppearance.ts 的 publishAnimationStyle）。

import type { ParamOverrides } from "../interactions/protocol/params.ts";
import type { AnimationMode } from "../interactions/protocol/runtime.ts";
import { CARRY_PARTS, springParam, SWITCH_PART } from "../interactions/components/springOrEase.ts";
import { EDGE_BAND_RUBBER, IOS_RUBBER } from "../interactions/components/edgeRubberBand.ts";
import type { Curve } from "../interactions/components/springOrEase.ts";
import type { MotionKind } from "../interactions/components/common.ts";
import { ResourceManager } from "../preferences/generated/frontendPoints.values.ts";

export type AnimationStyleId = "smooth" | "desktop" | "terminal" | "none" | "ultra";

export type MotionDurationToken = "--motion-fast" | "--motion-medium" | "--motion-page" | "--motion-panel" | "--motion-bar";
export type MotionEaseToken = "--ease-decel" | "--ease-standard" | "--ease-bar";
/** 一条曲线：CSS 的 cubic-bezier，或 steps(n)（跳 n 次，每段结束时跳，和 CSS 的 steps(n, end) 一样）。 */
export type MotionEase = { readonly kind: "cubic"; readonly points: Curve } | { readonly kind: "steps"; readonly count: number };

/** 终端顿挫的步数：CSS 的 steps(n) 和磁石的“输出步数”是同一个数。 */
export const TERMINAL_STEPS = 4;

export interface AnimationStyleDefinition {
  readonly id: AnimationStyleId;
  /** 磁石的动画档位：会动的风格是 normal；无动画、超高性能直接到终态。 */
  readonly engineMode: AnimationMode;
  readonly durationsMs: Readonly<Record<MotionDurationToken, number>>;
  readonly easings: Readonly<Record<MotionEaseToken, MotionEase>>;
  /** 磁石全局层整层换成这一组（每种风格同一组参数名）。 */
  readonly lodestone: Readonly<ParamOverrides>;
}

const cubic = (points: Curve): MotionEase => ({ kind: "cubic", points });
const LINEAR: Curve = [0, 0, 1, 1];

/** 桌面常规：现在的“标准”令牌（:root 的值）。 */
const DESKTOP_DURATIONS = { "--motion-fast": 100, "--motion-medium": 180, "--motion-page": 260, "--motion-panel": 300, "--motion-bar": 360 } as const;
const DESKTOP_EASINGS = {
  "--ease-decel": cubic([0.1, 0.9, 0.2, 1]),
  "--ease-standard": cubic([0.2, 0, 0, 1]),
  // 数据条连续更新的近线性曲线：会缓动的风格都不变（换了会让条在每次更新时起步发顿）。
  "--ease-bar": cubic([0.35, 0, 0.15, 1])
} as const;
/** 流畅平滑的 CSS 令牌：协议 4.6 风格表的 Vaul 减速曲线、Motion 布局的标准曲线，时长约 1.4 倍。 */
const SMOOTH_DURATIONS = { "--motion-fast": 150, "--motion-medium": 260, "--motion-page": 380, "--motion-panel": 420, "--motion-bar": 480 } as const;
const SMOOTH_EASINGS = { "--ease-decel": cubic([0.32, 0.72, 0, 1]), "--ease-standard": cubic([0.4, 0, 0.1, 1]), "--ease-bar": DESKTOP_EASINGS["--ease-bar"] } as const;
const TERMINAL_EASE: MotionEase = { kind: "steps", count: TERMINAL_STEPS };
const TERMINAL_EASINGS = { "--ease-decel": TERMINAL_EASE, "--ease-standard": TERMINAL_EASE, "--ease-bar": TERMINAL_EASE } as const;
const ZERO_DURATIONS = { "--motion-fast": 0, "--motion-medium": 0, "--motion-page": 0, "--motion-panel": 0, "--motion-bar": 0 } as const;

/**
 * 磁石全局层：每个用弹簧或缓动的部件（interactions/components/springOrEase.ts 的 CARRY_PARTS 和开关）写全
 * 「到终态用弹簧」、缓动的时长和曲线、弹簧的响应和阻尼比，再加回点时间、输出步数和滚动区边缘回弹的橡皮筋系数（只有弹簧的风格回弹）。
 * 缓动的时长取这种风格的对应令牌、曲线取它的 --ease-decel（steps 的风格取直线，跳由输出步数做）；弹簧取复刻的参数。
 */
function lodestoneLayer(spring: boolean, durations: Readonly<Record<MotionDurationToken, number>>,
  easings: Readonly<Record<MotionEaseToken, MotionEase>>, outputSteps: number): ParamOverrides {
  const decel = easings["--ease-decel"];
  const curve = decel.kind === "cubic" ? decel.points : LINEAR;
  const ease = (prefix: string, ms: number): ParamOverrides => ({
    [springParam(prefix)]: spring ? 1 : 0,
    [`${prefix}EaseMs`]: ms,
    [`${prefix}EaseX1`]: curve[0], [`${prefix}EaseY1`]: curve[1], [`${prefix}EaseX2`]: curve[2], [`${prefix}EaseY2`]: curve[3]
  });
  const layer: ParamOverrides = {
    outputSteps, springTime: durations[SWITCH_PART.durationToken], [EDGE_BAND_RUBBER]: spring ? IOS_RUBBER : 0,
    ...ease(SWITCH_PART.prefix, durations[SWITCH_PART.durationToken])
  };
  for (const [prefix, part] of Object.entries(CARRY_PARTS)) {
    Object.assign(layer, ease(prefix, durations[part.durationToken]), {
      [`${prefix}SpringResponse`]: part.spring.responseMs,
      [`${prefix}SpringDamping`]: part.spring.damping
    });
  }
  return layer;
}

const desktopLayer = lodestoneLayer(false, DESKTOP_DURATIONS, DESKTOP_EASINGS, 0);

export const animationStyles: readonly AnimationStyleDefinition[] = [
  {
    // macOS、iOS、Android 一类：磁石的部件用弹簧，带着速度，随时能接住；还用 CSS 过渡的地方用 SMOOTH 令牌。
    id: "smooth",
    engineMode: "normal",
    durationsMs: SMOOTH_DURATIONS,
    easings: SMOOTH_EASINGS,
    lodestone: lodestoneLayer(true, SMOOTH_DURATIONS, SMOOTH_EASINGS, 0)
  },
  {
    // 一般 Windows 桌面软件：短的减速过渡，不带速度，不回弹。就是 :root 的令牌。
    id: "desktop",
    engineMode: "normal",
    durationsMs: DESKTOP_DURATIONS,
    easings: DESKTOP_EASINGS,
    lodestone: desktopLayer
  },
  {
    // 按固定几步跳到终点，没有缓动：时长同桌面常规，CSS 用 steps(n)，磁石的画面按同样的步数跳。
    id: "terminal",
    engineMode: "normal",
    durationsMs: DESKTOP_DURATIONS,
    easings: TERMINAL_EASINGS,
    lodestone: lodestoneLayer(false, DESKTOP_DURATIONS, TERMINAL_EASINGS, TERMINAL_STEPS)
  },
  {
    // 真的不动：所有时长 0，物理组件当帧到终态（磁石档位 none）。磁石全局层同桌面常规：松手的落点照样按力场算。
    id: "none",
    engineMode: "none",
    durationsMs: ZERO_DURATIONS,
    easings: DESKTOP_EASINGS,
    lodestone: desktopLayer
  },
  {
    // 同无动画，再抹平阴影、高光、模糊（styles/motion.css 的 body[data-animations="ultra"] 规则，原有档不变）。
    id: "ultra",
    engineMode: "ultra",
    durationsMs: ZERO_DURATIONS,
    easings: DESKTOP_EASINGS,
    lodestone: desktopLayer
  }
];

export const animationStyleIds: readonly AnimationStyleId[] = animationStyles.map((style) => style.id);

/** 会动的风格（磁石档位 normal）：CSS 里位移类的效果只在这些风格下写。 */
export const movingAnimationStyleIds: readonly AnimationStyleId[] = animationStyles
  .filter((style) => style.engineMode === "normal").map((style) => style.id);

/**
 * 样式表里的每一条过渡和动画登记它的类别（docs/animation_style_2026-10-26.md「总原则」「已裁定」），
 * 和磁石部件（components/common.ts 的 MotionKind）、呈现层槽位（presentation/nodeAnimation.ts）同一套两类。
 * - 进场退场：节点出现、消失时播的（挂上时的淡入、退场幽灵的槽位）。
 * - 平面移动：已经在场的节点的状态变化（悬停、按压、选中、位置和宽度跟着数值走、循环的呼吸和转圈）。
 * selector 和样式表里那条规则的选择器一字不差（几个选择器用“, ”连起来，空白合成一个空格），会动的风格那一段写 MOVING；
 * property 是 transition 或 animation 简写（值是 none 的不算，无动画、超高性能清零时长的那条也不算）。
 * 合同测试（tests/animationStyleContract.test.ts）核对样式表里的过渡和动画与这里一一对应。
 */
export interface CssMotion {
  readonly sheet: "styles/motion.css" | "styles/task-center.css";
  readonly selector: string;
  readonly property: "transition" | "animation";
  readonly kind: MotionKind;
  /** 平面移动让节点出现、消失的裁定例外：写用户的裁定。只有资源条的分段一处。 */
  readonly ruledAppearance?: string;
}

/** 样式表里只在会动的风格下生效的规则前缀。 */
export const MOVING = `body:is(${movingAnimationStyleIds.map((id) => `[data-animations="${id}"]`).join(", ")})`;
const planar = (property: CssMotion["property"], selector: string): CssMotion => ({ sheet: "styles/motion.css", selector, property, kind: "平面移动" });
const entering = (property: CssMotion["property"], selector: string): CssMotion => ({ sheet: "styles/motion.css", selector, property, kind: "进场退场" });
const ghost = (selector: string) => entering("animation", `${MOVING} [data-exit-ghost]${selector}`);

export const CSS_MOTIONS: readonly CssMotion[] = [
  // 已在场控件的悬停、按压、聚焦、选中。
  planar("transition", "button"),
  planar("transition", ".has-selection-indicator .page-tab, .has-selection-indicator .segment"),
  planar("transition", ".has-selection-indicator .page-tab::before, .has-selection-indicator .segment::before, .page-tab-indicator, .segment-indicator"),
  planar("transition", "input, select, textarea"),
  planar("transition", ".resource-table-head-cell, .resource-table-column-resizer::after, .cpu-core, .cpu-ccd, .settings-credit-link, .settings-credit-source, .ui-disclosure-header"),
  planar("transition", ".fan-curve-point"),
  planar("transition", ".resource-segment, .resource-process-segment, .resource-segment-empty"),
  {
    ...planar("transition", `${MOVING} .resource-segment-layer.motion-segment`),
    ruledAppearance: "用户 2026-10-26「保留生长」：资源条新出现的分段从宽度 0 长出来，和旁边收缩的段是同一次数值变化，悬停与点击区域随当前画面宽度"
  },
  planar("transition", [".resource-segment", ".resource-process-segment", ".resource-segment-empty"]
    .map((item) => `${MOVING} .resource-segment-layer.motion-segment > ${item}`).join(", ")),
  planar("transition", ".resource-table-row"),
  planar("transition", `${MOVING} .resource-row-expander svg`),
  planar("transition", ".resource-table-sort-indicator"),
  planar("transition", `${MOVING} .resource-table-sort-indicator`),
  planar("animation", ".metric-pending-bar"),
  planar("transition", ".metric-card"),
  planar("transition", ".slot-actions"),
  planar("transition", ".management-card"),
  planar("transition", `${MOVING} .management-action-button:not(:disabled), ${MOVING} .management-settings-button:not(:disabled)`),
  planar("transition", ".edit-slot, .resource-breakdown-item"),
  planar("transition", ".targeted-report-list-row"),
  planar("transition", ".settings-row, .device-port-item, .optimization-report, .device-port-icon"),
  planar("transition", "::-webkit-scrollbar-thumb"),
  { sheet: "styles/task-center.css", selector: ".task-center-spinner", property: "animation", kind: "平面移动" },
  // 挂上时的淡入（只改不透明度，真实节点第一帧就在终态几何上）。
  entering("animation", ".toast"),
  entering("animation", ".standard-select-popover, .standard-context-menu, .disk-usage-context-menu, .help-hint-popover, .hover-tooltip"),
  entering("animation", ".resource-tooltip"),
  entering("animation", ".resource-process-panel"),
  entering("animation", `${MOVING} .resource-table-row[data-enter-slot="row"] > .resource-table-cell`),
  entering("animation", `${MOVING} .ui-disclosure-content[data-enter-slot="disclosure"]`),
  entering("animation", ".metric-value:not(.pending)"),
  entering("animation", ".edit-slot, .slot-add"),
  entering("animation", ".device-machine-figure"),
  entering("animation", ".targeted-report-list-row, .targeted-report-detail"),
  entering("animation", ".targeted-recording-status > p, .targeted-recording-alert, .targeted-report-note.error"),
  // 退场幽灵的槽位。
  ghost(":is(.standard-select-popover, .standard-context-menu, .disk-usage-context-menu, .help-hint-popover, .hover-tooltip)"),
  ghost(".modal-backdrop"),
  ghost(".modal-backdrop > .modal"),
  ghost(".toast"),
  ghost('[data-exit-slot="content"]'),
  ghost(".resource-table-row"),
  ghost(".resource-table-row > .resource-table-cell"),
  ghost('[data-exit-slot="disclosure"]'),
  ghost(":is(.resource-process-panel, .edit-slot, .targeted-report-list-row, .targeted-report-detail, .device-machine-figure)"),
  ghost(".resource-tooltip")
];

export function isAnimationStyleId(value: unknown): value is AnimationStyleId {
  return typeof value === "string" && (animationStyleIds as readonly string[]).includes(value);
}

/** 目录里的风格；不是目录里的值取前端自由点 appearance-animation_style 的默认值。 */
export function animationStyle(id?: string | null): AnimationStyleDefinition {
  return animationStyles.find((style) => style.id === id)
    ?? animationStyles.find((style) => style.id === ResourceManager.FrontendPoints.AnimationStyle)!;
}

/** 这种风格下界面会不会动（过渡、入场、物理组件的中间帧）。 */
export function animationStyleMoves(id: AnimationStyleId): boolean {
  return animationStyle(id).engineMode === "normal";
}

/** CSS 里一条曲线的写法。 */
export function cssEase(ease: MotionEase): string {
  return ease.kind === "cubic" ? `cubic-bezier(${ease.points.join(", ")})` : `steps(${ease.count}, end)`;
}
