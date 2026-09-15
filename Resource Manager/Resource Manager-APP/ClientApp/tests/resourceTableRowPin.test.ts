import assert from "node:assert/strict";
import { pinResourceTableRow } from "../src/resourceTable/resourceTableRowPin.ts";
import type { ResourceTableRow } from "../src/types.ts";

function row(id: string): ResourceTableRow {
  return {
    id,
    parentId: null,
    depth: 0,
    kind: "software",
    name: id,
    status: "Other",
    values: {},
    sortKeys: {},
    impactScore: 0,
    processCount: 0
  } as unknown as ResourceTableRow;
}

const before = [row("a"), row("b"), row("c"), row("d")];

// 没钉住的时候原样返回。
assert.equal(pinResourceTableRow(before, null, null), before);
assert.equal(pinResourceTableRow(before, "b", null), before);

// 后端把 c 排到了最前面，但 c 被钉在第 2 位，就该回到第 2 位。
const reordered = [row("c"), row("a"), row("b"), row("d")];
assert.deepEqual(
  pinResourceTableRow(reordered, "c", 2).map((item) => item.id),
  ["a", "b", "c", "d"],
  "钉住的行回到它被右键时所在的位置");

// 其余行之间的先后仍然听后端的。
assert.deepEqual(
  pinResourceTableRow([row("d"), row("c"), row("a"), row("b")], "c", 0).map((item) => item.id),
  ["c", "d", "a", "b"],
  "只移动被钉住的那一行，其余保持后端顺序");

// 已经在原位就不用动。
assert.equal(pinResourceTableRow(before, "b", 1), before);

// 钉住的行没了就什么都不做，不为它占位。
assert.equal(pinResourceTableRow(before, "missing", 2), before);

// 位置超出当前长度时贴到末尾，不越界。
assert.deepEqual(
  pinResourceTableRow([row("b"), row("a")], "b", 9).map((item) => item.id),
  ["a", "b"],
  "位置超出范围时钉到最后一行");

console.log("resourceTableRowPin: ok");
