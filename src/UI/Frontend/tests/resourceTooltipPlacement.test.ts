import assert from "node:assert/strict";
import { resolveResourceTooltipPlacement } from "../src/features/resourceBreakdown/resourceTooltipPlacement.ts";

const boundary = { left: 16, right: 784, top: 120, bottom: 600 };
const first = resolveResourceTooltipPlacement(
  { left: 32, right: 72, top: 220, bottom: 244 },
  boundary,
  150,
  64,
  180);
assert.equal(first.shiftX, 47);
assert.equal(first.vertical, "below");

const middle = resolveResourceTooltipPlacement(
  { left: 360, right: 440, top: 300, bottom: 324 },
  boundary,
  150,
  64,
  180);
assert.equal(middle.shiftX, 0);
assert.equal(middle.vertical, "above");

const last = resolveResourceTooltipPlacement(
  { left: 748, right: 772, top: 300, bottom: 324 },
  boundary,
  150,
  64,
  180);
assert.equal(last.shiftX, -59);
assert.equal(last.vertical, "above");

const constrained = resolveResourceTooltipPlacement(
  { left: 360, right: 440, top: 188, bottom: 212 },
  boundary,
  150,
  64,
  180);
assert.equal(constrained.vertical, "below");
