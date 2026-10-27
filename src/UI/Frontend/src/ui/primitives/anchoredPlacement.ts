export interface AnchorRect {
  left: number;
  right: number;
  top: number;
  bottom: number;
}

export interface AnchoredPlacement {
  /** 弹层左上角在视口里的位置（position: fixed）。 */
  left: number;
  top: number;
  vertical: "above" | "below";
}

export interface AnchoredSpacing {
  /** 弹层外框和锚点之间的距离（px）。0 是紧贴；负数是压住锚点边缘这么多。 */
  gap: number;
  /** 弹层外框和视口四边至少留的距离（px）。 */
  inset: number;
}

/** 资源条提示（ResourceTooltipOverlay）：离悬停的那一段 8 px，离视口四边至少 8 px。 */
export const RESOURCE_TOOLTIP_SPACING: AnchoredSpacing = { gap: 8, inset: 8 };

/** 问号说明（HelpHint）：离问号 6 px，离视口四边至少 8 px。 */
export const HELP_HINT_SPACING: AnchoredSpacing = { gap: 6, inset: 8 };

/**
 * 挂在页面最顶层的小弹层（资源条提示、问号说明）放在锚点的哪里：放在锚点上方或下方，不盖住锚点；整个弹层留在视口里。
 * 上方放得下就放上方，否则下方放得下就放下方；两边都放不下时放在空间大的一边，再收进视口（这时才可能压到锚点）。
 * 水平方向以锚点为中心，收进视口左右各留 inset。
 */
export function resolveAnchoredPlacement(
  anchor: AnchorRect,
  viewport: AnchorRect,
  width: number,
  height: number,
  spacing: AnchoredSpacing
): AnchoredPlacement {
  const { gap, inset } = spacing;
  const minimumLeft = viewport.left + inset;
  const maximumLeft = Math.max(minimumLeft, viewport.right - inset - width);
  const left = clamp((anchor.left + anchor.right - width) / 2, minimumLeft, maximumLeft);

  const availableAbove = anchor.top - gap - (viewport.top + inset);
  const availableBelow = viewport.bottom - inset - (anchor.bottom + gap);
  const vertical = height <= availableAbove
    ? "above"
    : height <= availableBelow || availableBelow > availableAbove
      ? "below"
      : "above";
  const preferredTop = vertical === "above"
    ? anchor.top - gap - height
    : anchor.bottom + gap;
  const minimumTop = viewport.top + inset;
  const maximumTop = Math.max(minimumTop, viewport.bottom - inset - height);
  return { left, top: clamp(preferredTop, minimumTop, maximumTop), vertical };
}

function clamp(value: number, minimum: number, maximum: number) {
  return Math.min(maximum, Math.max(minimum, value));
}
