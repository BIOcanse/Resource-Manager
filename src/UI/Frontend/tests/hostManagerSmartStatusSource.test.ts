import assert from "node:assert/strict";
import {
  buildHostManagerSmartStatusSubscriptionUrl,
  decodeHostManagerSmartCoordinatorStatus,
  hostManagerSmartStatusDecoder
} from "../src/data/optimization/hostManagerSmartStatusSource.ts";

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

// 调度段失败：直接读后端 tests/App 的 smart-status 夹具（HTTP 与订阅同一形状）的三例，解码后原样保留。
// 夹具的 mode 是 "cpu-only"，不是产品模式（后端缺口，已报告），这里换成产品模式再比较。
const fixtures = JSON.parse(readFileSync(
  new URL("../../../../tests/App/Fixtures/Optimization/smart-status.json", import.meta.url), "utf8")) as Record<string, Record<string, unknown>>;
assert.deepEqual(Object.keys(fixtures).sort(), ["cycle-failed", "native-plan-failed", "success"]);
for (const [name, fixture] of Object.entries(fixtures)) {
  const value = { ...fixture, mode: "cpu" };
  assert.deepEqual(decodeHostManagerSmartCoordinatorStatus(value), value, `${name} decodes as sent`);
}
const nativePlanFailed = { ...fixtures["native-plan-failed"], mode: "cpu" };
const cycleFailed = { ...fixtures["cycle-failed"], mode: "cpu" };
const decodedNative = decodeHostManagerSmartCoordinatorStatus(nativePlanFailed);
assert.equal(decodedNative.failure?.kind, "native-plan-failed");
assert.equal(decodedNative.failure?.nativePlanStatus, "capacity-exceeded");
assert.equal(decodedNative.factExclusions[0].processStartKey, "9007199254740993", "start keys stay exact text");
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
assert.throws(() => decodeHostManagerSmartCoordinatorStatus({ ...nativePlanFailed,
  factExclusionCounts: [{ fact: "", reason: "inventory-overflow", count: 1 }] }));
const exclusion = (fields: Record<string, unknown>) => ({ ...nativePlanFailed,
  factExclusions: [{ processId: 1, processStartKey: "2", targetId: "process:1:2", fact: "identity", reason: "invalid-or-duplicate-process", ...fields }] });
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
    targetId: null, fact: "identity", reason: "invalid-or-duplicate-process" })) });
assert.equal(decodeHostManagerSmartCoordinatorStatus(many(64)).factExclusions.length, 64);
assert.throws(() => decodeHostManagerSmartCoordinatorStatus(many(65)));

console.log("host manager smart status failure: ok");
