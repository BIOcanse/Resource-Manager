import assert from "node:assert/strict";
import {
  buildHostManagerSmartStatusSubscriptionUrl,
  decodeHostManagerSmartCoordinatorStatus,
  hostManagerSmartStatusDecoder
} from "../src/data/optimization/hostManagerSmartStatusSource.ts";

// 线上形状：故障关闭原因是必有字段（null 或原因码），模式只接受后端规范化后的值。
const wire = {
  mode: "limited",
  schedulerRunning: false,
  lastRunAt: null,
  lastRestoreAt: "2026-10-22T04:10:59Z",
  pendingChangeCount: 3,
  appliedTargetCount: 92,
  faultClosedReason: "effect-scope-load-failed:UnauthorizedAccessException",
  message: "ready"
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
