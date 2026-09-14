import assert from "node:assert/strict";
import { classifyResourceTableContentState } from
  "../src/resourceTable/resourceTableContentState.ts";

assert.equal(classifyResourceTableContentState({
  businessRowCount: 0,
  visibleBusinessRowCount: 0,
  searchActive: false
}), "ready-empty");

assert.equal(classifyResourceTableContentState({
  businessRowCount: 4,
  visibleBusinessRowCount: 0,
  searchActive: true
}), "filtered-empty");

assert.equal(classifyResourceTableContentState({
  businessRowCount: 4,
  visibleBusinessRowCount: 4,
  searchActive: false
}), null);
