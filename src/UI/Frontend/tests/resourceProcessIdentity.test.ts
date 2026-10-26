import assert from "node:assert/strict";
import type { ResourceProcessSegment } from "../src/types.ts";
import { isReservedProcess, isReservedSegment, resourceProcessKey } from "../src/features/resourceBreakdown/resourceProcessIdentity.ts";

function leaf(attributionKind: string, name = ""): ResourceProcessSegment {
  return { processId: -1, processStartKey: null, name, value: 1, systemPercent: 1, softwarePercent: 50, attributionKind };
}

// 物理内存残差的计数器叶子：都是 PID -1、名字为空，靠 attributionKind 区分，键不能相撞。
const pool = leaf("system-memory/nonpaged-pool");
const drivers = leaf("system-memory/drivers");
const tag = leaf("system-memory/nonpaged-pool/tag/4E747E46", "NtFF");
const keys = [pool, drivers, tag].map(resourceProcessKey);
assert.equal(new Set(keys).size, keys.length);

// 同一 PID 的两个进程实例（启动标识不同）也是两项。
const first = { processId: 42, processStartKey: "a", name: "app.exe", value: 1, systemPercent: 1, softwarePercent: 50 };
const second = { ...first, processStartKey: "b" };
assert.notEqual(resourceProcessKey(first), resourceProcessKey(second));

// 系统/驱动保留条目下面的每一项都按保留画，不管 attributionKind 写的是什么。
const residual = { softwareId: "resource-residual:memory.usage", displayKind: "system-residual" };
assert.equal(isReservedSegment(residual), true);
for (const process of [pool, drivers, tag, leaf("anything-else")]) {
  assert.equal(isReservedProcess(residual, process), true);
}

// 普通软件下：普通进程不是保留；后端标成 system-residual 的未拆分残差是保留。
const software = { softwareId: "software:example", displayKind: "application" };
assert.equal(isReservedSegment(software), false);
assert.equal(isReservedProcess(software, first), false);
assert.equal(isReservedProcess(software, leaf("system-residual")), true);
// 判断看所属条目和 attributionKind，不看分项种类的 system-memory/ 前缀。
assert.equal(isReservedProcess(software, pool), false);

console.log("resource process identity ok");
