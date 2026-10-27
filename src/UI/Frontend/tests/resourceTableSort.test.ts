import assert from "node:assert/strict";
import { defaultResourceTableSort, nextResourceTableSort } from "../src/features/resourceTable/resourceTableSort.ts";

// 用户 10-27：点一次从高到低，点两次从低到高，再点一次回到默认的综合排序。每一列都一样，文字列也一样。
for (const columnId of ["cpu", "memory", "gpu.1.vram", "name", "status", "pid", "user", "architecture"]) {
  const first = nextResourceTableSort(defaultResourceTableSort, columnId);
  assert.deepEqual(first, { columnId, direction: "desc" }, `${columnId}: the first click sorts high to low`);
  const second = nextResourceTableSort(first, columnId);
  assert.deepEqual(second, { columnId, direction: "asc" }, `${columnId}: the second click sorts low to high`);
  const third = nextResourceTableSort(second, columnId);
  assert.equal(third, defaultResourceTableSort, `${columnId}: the third click returns to the combined order`);
  assert.deepEqual(nextResourceTableSort(third, columnId), first, `${columnId}: the cycle starts again`);
}

// 换一列从降序开始，不管上一列在哪一步。
assert.deepEqual(nextResourceTableSort({ columnId: "cpu", direction: "desc" }, "memory"), { columnId: "memory", direction: "desc" });
assert.deepEqual(nextResourceTableSort({ columnId: "cpu", direction: "asc" }, "memory"), { columnId: "memory", direction: "desc" });
assert.deepEqual(defaultResourceTableSort, { columnId: "impact", direction: "desc" });
assert.ok(Object.isFrozen(defaultResourceTableSort));
console.log("Resource table sort cycle: high to low, low to high, combined order passed");
