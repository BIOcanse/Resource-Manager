/** Share numeric conversion rules across sliders, text input, and keyboard adjustment. */

/** Numeric capability range. */
export interface ControlNumberBounds {
  minimum: number;
  maximum: number;
  step?: number;
  unit?: string;
}

/** Invalid or missing steps use the native input default of one. */
export function effectiveStep(step: number | undefined): number {
  return step !== undefined && Number.isFinite(step) && step > 0 ? step : 1;
}

/** Derive display precision from step size so each keyboard increment remains visible. */
export function decimalsForStep(step: number | undefined): number {
  const value = effectiveStep(step);
  if (value >= 1) {
    return 0;
  }
  // Small fractional steps require enough decimal places to show an increment.
  const text = value.toFixed(10).replace(/0+$/, "");
  const dot = text.indexOf(".");
  return dot < 0 ? 0 : Math.min(text.length - dot - 1, 6);
}

/** Display settings only to the precision that can be selected. */
export function formatControlNumber(value: number, step: number | undefined): string {
  return value.toFixed(decimalsForStep(step));
}

/** Preserve observed precision independently of editable step size, removing only redundant zeros. */
export function formatActualNumber(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(1);
}

/** Show a separate reading only when it adds information beyond the numeric editor. */
export function actualRepeatsNumberEditor(setting: number | null | undefined, actual: number): boolean {
  // Unset values display the observation, matching NumberEditor.
  // Compare displayed numeric values rather than formatting differences.
  return setting === undefined || setting === null
    || Number(formatActualNumber(setting)) === Number(formatActualNumber(actual));
}

/** Snap typed values to hardware steps without clamping the advisory range. */
export function snapToStep(value: number, bounds: ControlNumberBounds): number {
  const step = effectiveStep(bounds.step);
  const snapped = bounds.minimum + Math.round((value - bounds.minimum) / step) * step;
  // Remove floating-point residue using the step's precision.
  return Number(snapped.toFixed(decimalsForStep(bounds.step)));
}

/** Clamp and snap slider values to the displayed range. */
export function snapControlNumber(value: number, bounds: ControlNumberBounds): number {
  return snapToStep(
    Math.min(bounds.maximum, Math.max(bounds.minimum, value)),
    bounds
  );
}

/** Clamp only the thumb position; typed values outside the range remain intact. */
export function sliderThumbValue(value: number, bounds: ControlNumberBounds): number {
  return Math.min(bounds.maximum, Math.max(bounds.minimum, value));
}
