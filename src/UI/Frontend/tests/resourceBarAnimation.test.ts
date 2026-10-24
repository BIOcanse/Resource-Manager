import assert from "node:assert/strict";
import { canAnimateResourceBar } from "../src/features/resourceBreakdown/resourceBarAnimation.ts";
import { resolveEffectiveAnimationMode } from "../src/stores/settingsStore.ts";
assert.equal(canAnimateResourceBar("normal"), true);
assert.equal(canAnimateResourceBar("none"), false);
assert.equal(canAnimateResourceBar("ultra"), false);
for (const focused of [false, true]) {
  assert.equal(canAnimateResourceBar(resolveEffectiveAnimationMode("normal", "optimize", focused)), true,
    "Explicit Normal must animate even when unfocused under optimized GPU scheduling");
  assert.equal(canAnimateResourceBar(resolveEffectiveAnimationMode("auto", "optimize", focused)), focused);
}
