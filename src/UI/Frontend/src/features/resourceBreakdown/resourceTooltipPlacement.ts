export interface TooltipRect {
  left: number;
  right: number;
  top: number;
  bottom: number;
}

export interface ResourceTooltipPlacement {
  /** 提示左上角在视口里的位置（position: fixed）。 */
  left: number;
  top: number;
  vertical: "above" | "below";
}

const tooltipInset = 8;
const tooltipGap = 8;

/**
 * 提示放在悬停的那一段上方或下方，不盖住这一段；整个提示留在视口里。
 * 上方放得下就放上方，否则下方放得下就放下方；两边都放不下时放在空间大的一边，再收进视口（这时才可能压到这一段）。
 * 水平方向以这一段为中心，收进视口左右各留 8 px。
 */
export function resolveResourceTooltipPlacement(
  anchor: TooltipRect,
  viewport: TooltipRect,
  tooltipWidth: number,
  tooltipHeight: number
): ResourceTooltipPlacement {
  const minimumLeft = viewport.left + tooltipInset;
  const maximumLeft = Math.max(minimumLeft, viewport.right - tooltipInset - tooltipWidth);
  const left = clamp((anchor.left + anchor.right - tooltipWidth) / 2, minimumLeft, maximumLeft);

  const availableAbove = anchor.top - tooltipGap - (viewport.top + tooltipInset);
  const availableBelow = viewport.bottom - tooltipInset - (anchor.bottom + tooltipGap);
  const vertical = tooltipHeight <= availableAbove
    ? "above"
    : tooltipHeight <= availableBelow || availableBelow > availableAbove
      ? "below"
      : "above";
  const preferredTop = vertical === "above"
    ? anchor.top - tooltipGap - tooltipHeight
    : anchor.bottom + tooltipGap;
  const minimumTop = viewport.top + tooltipInset;
  const maximumTop = Math.max(minimumTop, viewport.bottom - tooltipInset - tooltipHeight);
  return { left, top: clamp(preferredTop, minimumTop, maximumTop), vertical };
}

function clamp(value: number, minimum: number, maximum: number) {
  return Math.min(maximum, Math.max(minimum, value));
}
