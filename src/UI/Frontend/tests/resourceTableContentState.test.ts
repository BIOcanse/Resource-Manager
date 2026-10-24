import assert from "node:assert/strict";
import { classifyResourceTableContentState, resourceTableCountText } from
  "../src/features/resourceTable/resourceTableContentState.ts";

assert.equal(classifyResourceTableContentState({
  hasSnapshot: true, status: "ready",
  businessRowCount: 0,
  visibleBusinessRowCount: 0,
  searchActive: false
}), "ready-empty");

assert.equal(classifyResourceTableContentState({
  hasSnapshot: true, status: "ready",
  businessRowCount: 4,
  visibleBusinessRowCount: 0,
  searchActive: true
}), "filtered-empty");

assert.equal(classifyResourceTableContentState({
  hasSnapshot: true, status: "ready",
  businessRowCount: 4,
  visibleBusinessRowCount: 4,
  searchActive: false
}), null);

for (const [hasSnapshot, status, expected, count] of [
  [false, "loading", "pending", "--"],
  [true, "ready", "ready-empty", "0"],
  [false, "ready", "unavailable", "--"],
  [false, "error", "unavailable", "--"]
] as const) {
  const input = { hasSnapshot, status, businessRowCount: 0, visibleBusinessRowCount: 0, searchActive: false };
  const state = classifyResourceTableContentState(input);
  assert.equal(state, expected);
  assert.equal(resourceTableCountText({ ...input, state }), count);
}
// The actual component uses the tested projection and existing localized pending copy.
const { readFileSync } = await import("node:fs");
const component = readFileSync(new URL("../src/features/resourceTable/ResourceTable.tsx", import.meta.url), "utf8");
assert.match(component, /hasSnapshot: props\.snapshot !== null/);
assert.match(component, /status: props\.observation\.status/);
assert.match(component, /return resourceTableCountText\(/);
assert.match(component, /case "pending": return uiText\.observationNotice\.loading/);
assert.match(component, /props\.state === "ready-empty" \? uiText\.resourceTableView\.emptyDetail : undefined/);

const breakdown = readFileSync(new URL("../src/features/resourceBreakdown/ResourceBreakdown.tsx", import.meta.url), "utf8");
assert.match(breakdown, /kind=\{props\.observation\.status === "loading" \? "waiting"/);
assert.match(breakdown, /\? undefined : props\.bars\.length === 0/);
