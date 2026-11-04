export interface AnchorRect {
  left: number;
  right: number;
  top: number;
  bottom: number;
}

export interface AnchoredPlacement {
  /** Fixed-position layer origin in viewport coordinates. */
  left: number;
  top: number;
  vertical: "above" | "below";
}

export interface AnchoredSpacing {
  /** Anchor gap in pixels; negative values overlap its edge. */
  gap: number;
  /** Minimum viewport inset in pixels. */
  inset: number;
}

/** Resource tooltip spacing. */
export const RESOURCE_TOOLTIP_SPACING: AnchoredSpacing = { gap: 8, inset: 8 };

/** Help hint spacing. */
export const HELP_HINT_SPACING: AnchoredSpacing = { gap: 6, inset: 8 };

/** Name and full-content tooltip spacing. */
export const HOVER_TOOLTIP_SPACING: AnchoredSpacing = { gap: 6, inset: 8 };

/** Prefer above, then below. If neither fits, choose the larger side; clamp the centered layer to the viewport inset. */
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
