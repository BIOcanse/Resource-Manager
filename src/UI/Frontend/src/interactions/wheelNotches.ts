// Normalize wheel input to notches, retaining fractional trackpad movement.
// Use the same notch units for zooming and wheel selection.
// Pixel deltas use the Chromium Windows notch convention.
// Line deltas use three lines per notch; page deltas use one page.

const pixelsPerNotch = 100;
const linesPerNotch = 3;

/** Convert a signed wheel delta to notches according to deltaMode. */
export function wheelNotches(delta: number, deltaMode: number): number {
  if (deltaMode === 1) return delta / linesPerNotch;
  if (deltaMode === 2) return delta;
  return delta / pixelsPerNotch;
}

/** Accumulate whole signed steps, retaining fractions; reset the remainder on direction reversal. */
export function createWheelStepAccumulator(): (notches: number) => number {
  let total = 0;
  return (notches) => {
    if (notches === 0) return 0;
    if (total !== 0 && Math.sign(total) !== Math.sign(notches)) total = 0;
    total += notches;
    // Allow floating-point error when accumulated fractions reach a whole notch.
    const steps = Math.trunc(total + Math.sign(total) * 1e-9);
    total -= steps;
    // Normalize truncation negative zero.
    return steps === 0 ? 0 : steps;
  };
}
