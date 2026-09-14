import assert from "node:assert/strict";
import {
  activeDescendantOptionId,
  resolveActiveDescendantTarget
} from "../src/ui/primitives/activeDescendantListbox.ts";

const items = ["first", "second", "third"];
assert.deepEqual(resolveActiveDescendantTarget(items, null, "ArrowRight"), {
  id: "first",
  index: 0
});
assert.deepEqual(resolveActiveDescendantTarget(items, null, "ArrowLeft"), {
  id: "third",
  index: 2
});
assert.deepEqual(resolveActiveDescendantTarget(items, "second", "Home"), {
  id: "first",
  index: 0
});
assert.deepEqual(resolveActiveDescendantTarget(items, "second", "End"), {
  id: "third",
  index: 2
});
assert.deepEqual(resolveActiveDescendantTarget(items, "second", " "), {
  id: "second",
  index: 1
});
assert.equal(resolveActiveDescendantTarget(items, "second", "Escape"), null);
assert.match(activeDescendantOptionId("gpu.0", "software:示例"), /^active-option-[A-Za-z0-9_.-]+$/);
