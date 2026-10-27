import assert from "node:assert/strict";
import {
  buildHostManagerSmartStatusSubscriptionUrl,
  decodeHostManagerSmartCoordinatorStatus,
  hostManagerSmartStatusDecoder
} from "../src/data/optimization/hostManagerSmartStatusSource.ts";
import {
  hostManagerNativePlanStatuses,
  hostManagerSchedulingExclusionReasons,
  hostManagerSchedulingFacts
} from "../src/data/optimization/hostManagerSchedulingFactCodes.ts";

import { readFileSync } from "node:fs";

// 线上形状：故障关闭原因是必有字段（null 或原因码），模式只接受后端规范化后的值。
const wire = {
  mode: "limited",
  schedulerRunning: false,
  lastRunAt: null,
  lastRestoreAt: "2026-10-22T04:10:59Z",
  pendingChangeCount: 3,
  appliedTargetCount: 92,
  faultClosedReason: "effect-scope-load-failed:UnauthorizedAccessException",
  coordinator: { revision: "18446744073709551615", pendingCount: 3, inflightCount: 0, reasonMask: "137438953616" },
  failure: null,
  factExclusionCount: 0,
  factExclusionCounts: [],
  factExclusions: []
};
assert.deepEqual(decodeHostManagerSmartCoordinatorStatus(wire), wire);
assert.deepEqual(hostManagerSmartStatusDecoder.decode(wire), wire);
assert.equal(
  decodeHostManagerSmartCoordinatorStatus({ ...wire, faultClosedReason: null }).faultClosedReason,
  null);
for (const mode of [
  "normal", "limited", "cpu", "gpu", "memory+cpu", "memory+gpu", "cpu+gpu", "smart"
]) {
  assert.equal(decodeHostManagerSmartCoordinatorStatus({ ...wire, mode }).mode, mode);
}
assert.throws(() => decodeHostManagerSmartCoordinatorStatus({
  ...wire,
  faultClosedReason: undefined
}));
assert.throws(() => decodeHostManagerSmartCoordinatorStatus({ ...wire, mode: "turbo" }));
assert.throws(() => decodeHostManagerSmartCoordinatorStatus({ ...wire, schedulerRunning: "yes" }));
assert.throws(() => decodeHostManagerSmartCoordinatorStatus({ ...wire, appliedTargetCount: -1 }));
assert.throws(() => decodeHostManagerSmartCoordinatorStatus(null));
assert.equal(
  buildHostManagerSmartStatusSubscriptionUrl(2_500),
  "/api/optimization/smart/status/subscribe?intervalMs=2500");
assert.equal(
  buildHostManagerSmartStatusSubscriptionUrl(0),
  "/api/optimization/smart/status/subscribe?intervalMs=1");

console.log("host manager smart status source: ok");

assert.equal("message" in decodeHostManagerSmartCoordinatorStatus(wire), false);
for (const invalid of ["-1", "1.5", "18446744073709551616", 62, null]) {
  assert.throws(() => decodeHostManagerSmartCoordinatorStatus({ ...wire,
    coordinator: { ...wire.coordinator, revision: invalid } }));
}
assert.throws(() => decodeHostManagerSmartCoordinatorStatus({ ...wire,
  coordinator: { ...wire.coordinator, pendingCount: -1 } }));

// 调度段失败：直接读后端 tests/App 的 smart-status 夹具（HTTP 与订阅同一形状）的三例，原样解码（含真实的 mode）。
const fixtures = JSON.parse(readFileSync(
  new URL("../../../../tests/App/Fixtures/Optimization/smart-status.json", import.meta.url), "utf8")) as Record<string, Record<string, unknown>>;
assert.deepEqual(Object.keys(fixtures).sort(), ["cycle-failed", "native-plan-failed", "success"]);
for (const [name, fixture] of Object.entries(fixtures)) {
  assert.deepEqual(decodeHostManagerSmartCoordinatorStatus(fixture), fixture, `${name} decodes as sent`);
}
const nativePlanFailed = fixtures["native-plan-failed"];
const cycleFailed = fixtures["cycle-failed"];
const decodedNative = decodeHostManagerSmartCoordinatorStatus(nativePlanFailed);
assert.equal(decodedNative.failure?.kind, "native-plan-failed");
assert.equal(decodedNative.failure?.nativePlanStatus, "capacity-exceeded");
assert.equal(decodedNative.factExclusions[0].processStartKey, "9007199254740993", "start keys stay exact text");
assert.equal(decodedNative.factExclusions[0].adapterKey, null);

// 码表逐项等于后端 SchedulingFactExclusionCodes.cs 的两个枚举（JsonStringEnumMemberName，顺序相同）。
const codesSource = readFileSync(
  new URL("../../../Core/Domain/ResourceBreakdown/SchedulingFactExclusionCodes.cs", import.meta.url), "utf8");
const enumCodes = (name: string) => {
  const body = codesSource.slice(codesSource.indexOf(`public enum ${name}`));
  return [...body.slice(0, body.indexOf("}")).matchAll(/JsonStringEnumMemberName\("([^"]+)"\)/g)].map(match => match[1]);
};
assert.deepEqual([...hostManagerSchedulingFacts], enumCodes("SchedulingFactCode"));
assert.deepEqual([...hostManagerSchedulingExclusionReasons], enumCodes("SchedulingFactExclusionReason"));
assert.deepEqual([...hostManagerSchedulingExclusionReasons].slice(-hostManagerNativePlanStatuses.length), [...hostManagerNativePlanStatuses],
  "the native plan statuses are the last reasons");

// 后端 smart-exclusions 夹具覆盖每个事实和原因（适配器标识有值也有 null）：整份作为明细原样解码，计数按（事实，原因）分组。
const exclusionRows = JSON.parse(readFileSync(
  new URL("../../../../tests/App/Fixtures/Optimization/smart-exclusions.json", import.meta.url), "utf8")) as Array<Record<string, unknown>>;
assert.deepEqual(new Set(exclusionRows.map(row => row.fact)), new Set(hostManagerSchedulingFacts), "fixture covers every fact");
assert.deepEqual(new Set(exclusionRows.map(row => row.reason)), new Set(hostManagerSchedulingExclusionReasons), "fixture covers every reason");
assert.ok(exclusionRows.some(row => row.adapterKey === null) && exclusionRows.some(row => typeof row.adapterKey === "string"));
const exclusionGroups = new Map<string, { fact: unknown; reason: unknown; count: number }>();
for (const row of exclusionRows) {
  const key = `${String(row.fact)} ${String(row.reason)}`;
  const group = exclusionGroups.get(key) ?? { fact: row.fact, reason: row.reason, count: 0 };
  group.count += 1;
  exclusionGroups.set(key, group);
}
const allExclusions = { ...nativePlanFailed, factExclusionCount: exclusionRows.length,
  factExclusionCounts: [...exclusionGroups.values()], factExclusions: exclusionRows };
const decodedAll = decodeHostManagerSmartCoordinatorStatus(allExclusions);
assert.deepEqual(decodedAll, allExclusions, "every code decodes as sent");
assert.equal(decodedAll.factExclusions.find(row => row.fact === "gpu-usage")?.adapterKey, "9007199254740995", "adapter keys stay exact text");
assert.equal(decodedAll.factExclusions.find(row => row.fact === "gpu-score")?.adapterKey, "18446744073709551615");
assert.equal(decodeHostManagerSmartCoordinatorStatus(cycleFailed).failure?.nativePlanStatus, null);

// 每个原生计划状态码都接受；未知码、ok、以及类型和状态码不配对都拒绝。
for (const status of [
  "invalid-argument", "abi-mismatch", "unavailable", "no-data", "buffer-too-small", "stale-generation",
  "out-of-memory", "capacity-exceeded", "conflicting-facts", "feedback-mismatch", "recreate-required"
]) {
  const value = { ...nativePlanFailed, failure: { kind: "native-plan-failed", exceptionType: "InvalidDataException", nativePlanStatus: status } };
  assert.equal(decodeHostManagerSmartCoordinatorStatus(value).failure?.nativePlanStatus, status);
}
const withFailure = (failure: unknown) => ({ ...nativePlanFailed, failure });
for (const failure of [
  { kind: "native-plan-failed", exceptionType: "InvalidDataException", nativePlanStatus: "ok" },
  { kind: "native-plan-failed", exceptionType: "InvalidDataException", nativePlanStatus: "CapacityExceeded" },
  { kind: "native-plan-failed", exceptionType: "InvalidDataException", nativePlanStatus: null },
  { kind: "cycle-failed", exceptionType: "IOException", nativePlanStatus: "capacity-exceeded" },
  { kind: "segment-failed", exceptionType: "IOException", nativePlanStatus: null },
  { kind: "cycle-failed", exceptionType: "", nativePlanStatus: null },
  { kind: "cycle-failed", nativePlanStatus: null },
  "cycle-failed"
]) {
  assert.throws(() => decodeHostManagerSmartCoordinatorStatus(withFailure(failure)), JSON.stringify(failure));
}
// 新字段都是必有字段。
for (const field of ["failure", "factExclusionCount", "factExclusionCounts", "factExclusions"]) {
  const value: Record<string, unknown> = { ...nativePlanFailed };
  delete value[field];
  assert.throws(() => decodeHostManagerSmartCoordinatorStatus(value), `${field} is required`);
}
assert.throws(() => decodeHostManagerSmartCoordinatorStatus({ ...nativePlanFailed, factExclusionCount: -1 }));
assert.throws(() => decodeHostManagerSmartCoordinatorStatus({ ...nativePlanFailed,
  factExclusionCounts: [{ fact: "inventory", reason: "inventory-overflow", count: 1.5 }] }));
const count = (fields: Record<string, unknown>) => ({ ...nativePlanFailed,
  factExclusionCounts: [{ fact: "inventory", reason: "inventory-overflow", count: 1, ...fields }] });
const exclusion = (fields: Record<string, unknown>) => ({ ...nativePlanFailed,
  factExclusions: [{ processId: 1, processStartKey: "2", targetId: "process:1:2", fact: "identity", reason: "invalid-or-duplicate-process", adapterKey: null, ...fields }] });
// 事实和原因只接受码表里的码：未知码、旧的 PascalCase 写法和旧的“事实:适配器”拼接写法都在边界拒绝，错误指出路径。
for (const fact of ["", "unknown-fact", "GpuUsage:11", "gpu-usage:11", "CpuUsage", "RuntimeState", null]) {
  assert.throws(() => decodeHostManagerSmartCoordinatorStatus(count({ fact })), /\$\.factExclusionCounts\[0\]\.fact/, `count fact ${fact}`);
  assert.throws(() => decodeHostManagerSmartCoordinatorStatus(exclusion({ fact })), /\$\.factExclusions\[0\]\.fact/, `detail fact ${fact}`);
}
for (const reason of ["", "unknown-reason", "ok", "CapacityExceeded", null]) {
  assert.throws(() => decodeHostManagerSmartCoordinatorStatus(count({ reason })), /\$\.factExclusionCounts\[0\]\.reason/, `count reason ${reason}`);
  assert.throws(() => decodeHostManagerSmartCoordinatorStatus(exclusion({ reason })), /\$\.factExclusions\[0\]\.reason/, `detail reason ${reason}`);
}
// 适配器标识是必有字段：null 或 ulong 的十进制文本，不接受数字。
for (const adapterKey of [undefined, 11, "", "-1", "01", "18446744073709551616", "0x0b"]) {
  assert.throws(() => decodeHostManagerSmartCoordinatorStatus(exclusion({ adapterKey })), /\$\.factExclusions\[0\]\.adapterKey/, `adapter key ${adapterKey}`);
}
assert.equal(decodeHostManagerSmartCoordinatorStatus(exclusion({ fact: "gpu-score", reason: "row-missing-or-invalid", adapterKey: "0" }))
  .factExclusions[0].adapterKey, "0");
for (const startKey of ["-1", "01", "18446744073709551616", 2]) {
  assert.throws(() => decodeHostManagerSmartCoordinatorStatus(exclusion({ processStartKey: startKey })), `start key ${startKey}`);
}
assert.equal(decodeHostManagerSmartCoordinatorStatus(exclusion({ processStartKey: "18446744073709551615" }))
  .factExclusions[0].processStartKey, "18446744073709551615");
assert.throws(() => decodeHostManagerSmartCoordinatorStatus(exclusion({ processId: "1" })));
assert.throws(() => decodeHostManagerSmartCoordinatorStatus(exclusion({ reason: null })));
// 明细最多 64 项：64 项接受，65 项拒绝。
const many = (count: number) => ({ ...nativePlanFailed, factExclusionCount: count,
  factExclusions: Array.from({ length: count }, (_, index) => ({ processId: index + 1, processStartKey: String(index + 1),
    targetId: null, fact: "identity", reason: "invalid-or-duplicate-process", adapterKey: null })) });
assert.equal(decodeHostManagerSmartCoordinatorStatus(many(64)).factExclusions.length, 64);
assert.throws(() => decodeHostManagerSmartCoordinatorStatus(many(65)));

console.log("host manager smart status failure: ok");
