import assert from "node:assert/strict";
import {
  moveResourceTableFocus,
  reconcileResourceTableFocus
} from "../src/features/resourceTable/resourceTableFocus.ts";

const rows = [
  { id: "summary", kind: "summary" },
  { id: "software-a", kind: "software" },
  { id: "process-a", kind: "process" },
  { id: "software-b", kind: "software" }
];

assert.equal(reconcileResourceTableFocus(rows, null), "software-a");
assert.equal(reconcileResourceTableFocus([...rows].reverse(), "process-a"), "process-a");
assert.equal(reconcileResourceTableFocus([
  { id: "software-a", kind: "software" },
  { id: "software-b", kind: "software" }
], "process-a", 1), "software-b");
assert.equal(reconcileResourceTableFocus([], "software-a"), null);
assert.equal(moveResourceTableFocus(rows, "software-a", "next"), "process-a");
assert.equal(moveResourceTableFocus(rows, "software-a", "previous"), "software-a");
assert.equal(moveResourceTableFocus(rows, "process-a", "last"), "software-b");
assert.equal(moveResourceTableFocus(rows, "software-b", "first"), "software-a");
