// Each animation style supplies CSS tokens and a complete Lodestone global parameter layer.
// Component-local parameters override the global layer.
// styles/motion.css mirrors these tokens for :root and body[data-animations].
// body[data-animations] holds the effective style ID published by documentAppearance.ts.

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
/** CSS cubic-bezier or steps(n, end), jumping at the end of each step. */
export type MotionEase = { readonly kind: "cubic"; readonly points: Curve } | { readonly kind: "steps"; readonly count: number };

/** Shared step count for CSS steps(n) and Lodestone output quantization. */
export const TERMINAL_STEPS = 4;

export interface AnimationStyleDefinition {
  readonly id: AnimationStyleId;
  /** Moving styles use normal; disabled and ultra modes settle immediately. */
  readonly engineMode: AnimationMode;
  readonly durationsMs: Readonly<Record<MotionDurationToken, number>>;
  readonly easings: Readonly<Record<MotionEaseToken, MotionEase>>;
  /** Replaces the entire Lodestone global layer with the same parameter names for every style. */
  readonly lodestone: Readonly<ParamOverrides>;
}

const cubic = (points: Curve): MotionEase => ({ kind: "cubic", points });
const LINEAR: Curve = [0, 0, 1, 1];

/** Standard desktop tokens, mirrored by :root. */
const DESKTOP_DURATIONS = { "--motion-fast": 100, "--motion-medium": 180, "--motion-page": 260, "--motion-panel": 300, "--motion-bar": 360 } as const;
const DESKTOP_EASINGS = {
  "--ease-decel": cubic([0.1, 0.9, 0.2, 1]),
  "--ease-standard": cubic([0.2, 0, 0, 1]),
  // Keep data bars nearly linear across moving styles to avoid restarting easing on each update.
  "--ease-bar": cubic([0.35, 0, 0.15, 1])
} as const;
/** Smooth CSS tokens use Vaul deceleration and Motion layout curves with roughly 1.4x duration. */
const SMOOTH_DURATIONS = { "--motion-fast": 150, "--motion-medium": 260, "--motion-page": 380, "--motion-panel": 420, "--motion-bar": 480 } as const;
const SMOOTH_EASINGS = { "--ease-decel": cubic([0.32, 0.72, 0, 1]), "--ease-standard": cubic([0.4, 0, 0.1, 1]), "--ease-bar": DESKTOP_EASINGS["--ease-bar"] } as const;
const TERMINAL_EASE: MotionEase = { kind: "steps", count: TERMINAL_STEPS };
const TERMINAL_EASINGS = { "--ease-decel": TERMINAL_EASE, "--ease-standard": TERMINAL_EASE, "--ease-bar": TERMINAL_EASE } as const;
const ZERO_DURATIONS = { "--motion-fast": 0, "--motion-medium": 0, "--motion-page": 0, "--motion-panel": 0, "--motion-bar": 0 } as const;

/** Complete spring/ease parameters for every carried part: mode, duration, curve, response, damping, return time, output steps, and edge rubber-banding. Ease uses style tokens; stepped styles use linear easing plus output quantization. */
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
    // Spring styles carry velocity across interruptions; CSS transitions use SMOOTH tokens.
    id: "smooth",
    engineMode: "normal",
    durationsMs: SMOOTH_DURATIONS,
    easings: SMOOTH_EASINGS,
    lodestone: lodestoneLayer(true, SMOOTH_DURATIONS, SMOOTH_EASINGS, 0)
  },
  {
    // Desktop uses short deceleration without velocity carry or rebound.
    id: "desktop",
    engineMode: "normal",
    durationsMs: DESKTOP_DURATIONS,
    easings: DESKTOP_EASINGS,
    lodestone: desktopLayer
  },
  {
    // Fixed output steps without easing; duration matches desktop and CSS uses the same step count.
    id: "terminal",
    engineMode: "normal",
    durationsMs: DESKTOP_DURATIONS,
    easings: TERMINAL_EASINGS,
    lodestone: lodestoneLayer(false, DESKTOP_DURATIONS, TERMINAL_EASINGS, TERMINAL_STEPS)
  },
  {
    // Zero-duration styles settle immediately; the global force-field parameters still determine release targets.
    id: "none",
    engineMode: "none",
    durationsMs: ZERO_DURATIONS,
    easings: DESKTOP_EASINGS,
    lodestone: desktopLayer
  },
  {
    // Ultra also removes shadows, highlights, and blur through styles/motion.css.
    id: "ultra",
    engineMode: "ultra",
    durationsMs: ZERO_DURATIONS,
    easings: DESKTOP_EASINGS,
    lodestone: desktopLayer
  }
];

export const animationStyleIds: readonly AnimationStyleId[] = animationStyles.map((style) => style.id);

/** Styles with Lodestone normal mode; CSS movement rules apply only to these. */
export const movingAnimationStyleIds: readonly AnimationStyleId[] = animationStyles
  .filter((style) => style.engineMode === "normal").map((style) => style.id);

/** Classifies CSS transitions and animations as entrance/exit or movement. Selectors match stylesheet rules with normalized whitespace; MOVING prefixes style-restricted rules. Excludes none and zero-duration overrides. */
export interface CssMotion {
  readonly sheet: "styles/motion.css" | "styles/task-center.css";
  readonly selector: string;
  readonly property: "transition" | "animation";
  readonly kind: MotionKind;
  /** Reason a movement effect may reveal or hide content; used only for resource-bar segments. */
  readonly ruledAppearance?: string;
}

/** Rule prefix for styles that animate. */
export const MOVING = `body:is(${movingAnimationStyleIds.map((id) => `[data-animations="${id}"]`).join(", ")})`;
const planar = (property: CssMotion["property"], selector: string): CssMotion => ({ sheet: "styles/motion.css", selector, property, kind: "平面移动" });
const entering = (property: CssMotion["property"], selector: string): CssMotion => ({ sheet: "styles/motion.css", selector, property, kind: "进场退场" });
const ghost = (selector: string) => entering("animation", `${MOVING} [data-exit-ghost]${selector}`);

export const CSS_MOTIONS: readonly CssMotion[] = [
  // Hover, press, focus, and selection states of mounted controls.
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
  // Mount opacity only; interactive geometry starts at the final position.
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
  // Exit-ghost slots.
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

/** Catalog style, or the appearance-animation_style point default for unknown values. */
export function animationStyle(id?: string | null): AnimationStyleDefinition {
  return animationStyles.find((style) => style.id === id)
    ?? animationStyles.find((style) => style.id === ResourceManager.FrontendPoints.AnimationStyle)!;
}

/** Whether transitions, entrances, and physics render intermediate frames. */
export function animationStyleMoves(id: AnimationStyleId): boolean {
  return animationStyle(id).engineMode === "normal";
}

/** CSS serialization of an easing curve. */
export function cssEase(ease: MotionEase): string {
  return ease.kind === "cubic" ? `cubic-bezier(${ease.points.join(", ")})` : `steps(${ease.count}, end)`;
}
