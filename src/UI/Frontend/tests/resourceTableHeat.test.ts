import assert from "node:assert/strict";
import { resourceTableCellHeat, resourceTableHeatMaxima } from "../src/features/resourceTable/resourceTableHeat.ts";
import type { ResourceTableRow } from "../src/types.ts";

function row(id: string, memory: number | null, cpu: number): ResourceTableRow {
  return {
    id, kind: id === "summary" ? "summary" : "software", depth: 0,
    name: id, status: "", processCount: 1, impactScore: 0,
    values: {
      memory: { value: memory, unit: "B", percent: 90 },
      cpu: { value: cpu, unit: "%", percent: cpu }
    }
  };
}
const heatOf = (rows: ResourceTableRow[], index: number, columnId: string) =>
  resourceTableCellHeat(rows[index].values[columnId], resourceTableHeatMaxima(rows).get(columnId));

const input = [row("summary", 5.4, 5.1), row("a", 14.3, 1.2), row("b", 8.2, 0.2)];
const saved = structuredClone(input);
assert.equal(heatOf(input, 1, "memory").heat, 100);
assert.equal(heatOf(input, 2, "memory").heat, 8.2 / 14.3 * 100);
assert.equal(heatOf(input, 0, "memory").heat, 5.4 / 14.3 * 100);
assert.equal(heatOf(input, 0, "cpu").heat, 100);
assert.equal(heatOf(input, 1, "cpu").heat, 1.2 / 5.1 * 100);
assert.deepEqual(input, saved, "heat must not touch rows or cells");
assert.equal(heatOf(input.slice(2), 0, "memory").heat, 100);
assert.equal(heatOf([...input].reverse(), 1, "memory").heat, 100);
assert.equal(resourceTableHeatMaxima([]).size, 0);
for (const index of [0, 1]) {
  const rows = [row("empty", null, 0), row("zero", 0, 0)];
  assert.deepEqual(heatOf(rows, index, "memory"), { heat: 0, privateHeat: 0 });
  assert.deepEqual(heatOf(rows, index, "cpu"), { heat: 0, privateHeat: 0 });
}
const tied = [row("a", 8, 2), row("b", 8, 2)];
assert.ok([0, 1].every((index) => heatOf(tied, index, "memory").heat === 100));
assert.equal(heatOf([row("large", Number.MAX_VALUE, 0)], 0, "memory").heat, 100, "divide before multiplying to avoid overflow");
assert.deepEqual(resourceTableCellHeat(undefined, 10), { heat: 0, privateHeat: 0 }, "an absent cell has no heat");
assert.deepEqual(resourceTableCellHeat({ value: 5, unit: "%" }, undefined), { heat: 0, privateHeat: 0 }, "a column without maximum has no heat");

const shared = [row("a", 12, 0), row("b", 16, 0)];
shared[0].values.memory.sharedValue = 4;
shared[1].values.memory.sharedValue = 6;
assert.deepEqual(heatOf(shared, 0, "memory"), { heat: 75, privateHeat: 50 });
assert.deepEqual(heatOf(shared, 1, "memory"), { heat: 100, privateHeat: 62.5 });
console.log("Resource table per-column heat passed.");
