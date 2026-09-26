import assert from "node:assert/strict";
import { projectResourceTableHeat } from "../src/resourceTable/resourceTableHeat.ts";
import type { ResourceTableRow } from "../src/types.ts";

function row(id: string, memory: number | null, cpu: number): ResourceTableRow {
  return {
    id, kind: id === "summary" ? "summary" : "software", depth: 0,
    name: id, status: "", processCount: 1, impactScore: 0,
    values: {
      memory: { value: memory, displayValue: `${memory} GB`, percent: 90 },
      cpu: { value: cpu, displayValue: `${cpu}%`, percent: cpu }
    }
  };
}

const input = [row("summary", 5.4, 5.1), row("a", 14.3, 1.2), row("b", 8.2, 0.2)];
const saved = structuredClone(input);
const output = projectResourceTableHeat(input);
assert.equal(output[1].values.memory.heatPercent, 100);
assert.equal(output[2].values.memory.heatPercent, 8.2 / 14.3 * 100);
assert.equal(output[0].values.memory.heatPercent, 5.4 / 14.3 * 100);
assert.equal(output[0].values.cpu.heatPercent, 100);
assert.equal(output[1].values.cpu.heatPercent, 1.2 / 5.1 * 100);
assert.deepEqual(input, saved, "projection must not mutate raw values");
for (let index = 0; index < output.length; index++) {
  for (const key of ["memory", "cpu"]) {
    const { heatPercent, privateHeatPercent, ...cell } = output[index].values[key];
    assert.deepEqual(cell, input[index].values[key]);
  }
}
assert.equal(projectResourceTableHeat(input.slice(2))[0].values.memory.heatPercent, 100);
assert.equal(projectResourceTableHeat([...input].reverse())[1].values.memory.heatPercent, 100);
assert.deepEqual(projectResourceTableHeat([]), []);
for (const item of projectResourceTableHeat([row("empty", null, 0), row("zero", 0, 0)])) {
  assert.equal(item.values.memory.heatPercent, 0);
  assert.equal(item.values.cpu.heatPercent, 0);
}
const tied = projectResourceTableHeat([row("a", 8, 2), row("b", 8, 2)]);
assert.ok(tied.every((item) => item.values.memory.heatPercent === 100));
assert.equal(projectResourceTableHeat([row("large", Number.MAX_VALUE, 0)])[0]
  .values.memory.heatPercent, 100, "divide before multiplying to avoid overflow");
console.log("Resource table per-column heat projection passed.");

const shared = [row("a", 12, 0), row("b", 16, 0)];
shared[0].values.memory.sharedValue = 4;
shared[1].values.memory.sharedValue = 6;
const split = projectResourceTableHeat(shared);
assert.equal(split[0].values.memory.heatPercent, 75);
assert.equal(split[0].values.memory.privateHeatPercent, 50);
assert.equal(split[1].values.memory.heatPercent, 100);
assert.equal(split[1].values.memory.privateHeatPercent, 62.5);
assert.equal(projectResourceTableHeat(shared.slice(0, 1))[0].values.memory.sharedValue, 4);
