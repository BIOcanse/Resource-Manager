import assert from "node:assert/strict";
import {
  backendRetainedObservation,
  cachedObservation,
  failedObservation,
  loadingObservation,
  observationCanRender,
  profileDisabledObservation,
  readyObservation,
  refreshingObservation
} from "../src/observation/observationState.ts";
import { projectDeviceTopologyObservation } from "../src/deviceTopology/deviceTopologyObservation.ts";
import type { DeviceTopologySnapshotState } from "../src/types.ts";

const loading = loadingObservation();
assert.deepEqual(loading, { status: "loading", source: "none" });
assert.equal(observationCanRender(loading), false);

const cached = cachedObservation("2026-08-21T01:02:03.000Z");
assert.deepEqual(cached, {
  status: "stale",
  source: "cache",
  capturedAt: "2026-08-21T01:02:03.000Z"
});
assert.equal(observationCanRender(cached), true);
assert.equal(cachedObservation("not-a-date").capturedAt, undefined);

const backendRetained = backendRetainedObservation(
  "2026-08-21T01:02:03.000Z",
  "后端保留值");
assert.deepEqual(backendRetained, {
  status: "stale",
  source: "backend-retained",
  capturedAt: "2026-08-21T01:02:03.000Z",
  lastError: "后端保留值"
});
assert.equal(observationCanRender(backendRetained), true);

const ready = readyObservation("2026-08-21T02:03:04.000Z");
assert.deepEqual(ready, {
  status: "ready",
  source: "live",
  capturedAt: "2026-08-21T02:03:04.000Z"
});
assert.equal(observationCanRender(ready), true);
assert.equal(refreshingObservation(ready), ready);
assert.deepEqual(refreshingObservation(loading), loadingObservation());

assert.deepEqual(failedObservation(loading, "首次读取失败"), {
  status: "error",
  source: "none",
  lastError: "首次读取失败"
});

assert.deepEqual(failedObservation(ready, "刷新失败"), {
  status: "stale",
  source: "live",
  capturedAt: "2026-08-21T02:03:04.000Z",
  lastError: "刷新失败"
});

assert.deepEqual(failedObservation(cached, "缓存刷新失败"), {
  status: "stale",
  source: "cache",
  capturedAt: "2026-08-21T01:02:03.000Z",
  lastError: "缓存刷新失败"
});

const disabled = profileDisabledObservation("当前运行档案未启用该数据源");
assert.deepEqual(disabled, {
  status: "profile-disabled",
  source: "profile",
  lastError: "当前运行档案未启用该数据源"
});
assert.equal(observationCanRender(disabled), false);

const topologyCapturedAt = "2026-08-21T03:04:05.000Z";
const topologySnapshot = { capturedAt: topologyCapturedAt } as NonNullable<
  DeviceTopologySnapshotState["snapshot"]>;
const topologyState: DeviceTopologySnapshotState = {
  schemaVersion: "3.0.0",
  state: "ready",
  snapshot: topologySnapshot,
  contentGeneration: 1,
  stateRevision: 1,
  source: "live",
  lastSuccessAt: topologyCapturedAt,
  lastAttemptAt: topologyCapturedAt,
  failureCode: null,
  attemptDiagnostics: []
};

assert.deepEqual(projectDeviceTopologyObservation(loading, topologyState), {
  status: "ready",
  source: "live",
  capturedAt: topologyCapturedAt
});
assert.deepEqual(projectDeviceTopologyObservation(loading, {
  ...topologyState,
  source: "persisted"
}), {
  status: "stale",
  source: "cache",
  capturedAt: topologyCapturedAt,
  lastError: "正在等待实时设备拓扑，以下内容来自持久化快照。"
});
assert.deepEqual(projectDeviceTopologyObservation(ready, {
  ...topologyState,
  state: "warming",
  snapshot: null,
  lastSuccessAt: null
}), loadingObservation());
assert.deepEqual(projectDeviceTopologyObservation(ready, {
  ...topologyState,
  state: "refreshing"
}), {
  status: "stale",
  source: "live",
  capturedAt: topologyCapturedAt,
  lastError: "设备拓扑正在刷新，以下内容为最近一次采集结果。"
});
assert.deepEqual(projectDeviceTopologyObservation(ready, {
  ...topologyState,
  state: "failed",
  attemptDiagnostics: [{
    sourceId: "display-coordinator",
    status: "required-incomplete",
    code: "device-topology-display-coordinator-incomplete",
    messageCode: { domain: 5, code: 10, args: ["Warming"] }
  }]
}), {
  status: "stale",
  source: "live",
  capturedAt: topologyCapturedAt,
  lastError: "显示协调器尚未提供完整快照：Warming"
});
assert.deepEqual(projectDeviceTopologyObservation(loading, {
  ...topologyState,
  state: "failed",
  snapshot: null,
  lastSuccessAt: null,
  failureCode: "device-topology-read-failed"
}), {
  status: "error",
  source: "none",
  lastError: "设备拓扑采集失败。"
});
assert.deepEqual(projectDeviceTopologyObservation(ready, {
  ...topologyState,
  state: "failed",
  failureCode: "device-topology-read-failed"
}), {
  status: "stale",
  source: "live",
  capturedAt: topologyCapturedAt,
  lastError: "设备拓扑采集失败。"
});
