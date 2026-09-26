export interface TooltipRect {
  left: number;
  right: number;
  top: number;
  bottom: number;
}

export interface ResourceTooltipPlacement {
  shiftX: number;
  vertical: "above" | "below";
}

const tooltipInset = 8;
const tooltipGap = 8;

export function resolveResourceTooltipPlacement(
  anchor: TooltipRect,
  boundary: TooltipRect,
  tooltipWidth: number,
  tooltipHeight: number,
  contentTop = boundary.top
): ResourceTooltipPlacement {
  const preferredLeft = (anchor.left + anchor.right - tooltipWidth) / 2;
  const minimumLeft = boundary.left + tooltipInset;
  const maximumLeft = Math.max(minimumLeft, boundary.right - tooltipInset - tooltipWidth);
  const clampedLeft = clamp(preferredLeft, minimumLeft, maximumLeft);
  const availableAbove = anchor.top - tooltipGap - Math.max(contentTop, boundary.top + tooltipInset);
  const availableBelow = boundary.bottom - tooltipInset - anchor.bottom - tooltipGap;
  const vertical = tooltipHeight <= availableAbove || availableAbove >= availableBelow
    ? "above"
    : "below";

  return {
    shiftX: clampedLeft - preferredLeft,
    vertical
  };
}

export function positionResourceTooltip(anchor: HTMLElement) {
  const tooltip = anchor.querySelector<HTMLElement>(":scope > .resource-tooltip");
  const boundary = anchor.closest<HTMLElement>("[data-resource-tooltip-boundary]");
  if (!tooltip || !boundary) {
    return;
  }

  const boundaryHeader = boundary.querySelector<HTMLElement>(
    ":scope > .resource-breakdown-header, :scope > .resource-process-header");
  const placement = resolveResourceTooltipPlacement(
    anchor.getBoundingClientRect(),
    boundary.getBoundingClientRect(),
    tooltip.getBoundingClientRect().width,
    tooltip.getBoundingClientRect().height,
    boundaryHeader?.getBoundingClientRect().bottom);
  tooltip.style.setProperty("--resource-tooltip-shift-x", `${placement.shiftX}px`);
  tooltip.dataset.placement = placement.vertical;
}

function clamp(value: number, minimum: number, maximum: number) {
  return Math.min(maximum, Math.max(minimum, value));
}
