import {
  cachedObservation,
  failedObservation,
  loadingObservation,
  readyObservation,
  type ObservationState
} from "../observation/observationState.ts";
import type { DeviceTopologySnapshotState } from "../types";

export function projectDeviceTopologyObservation(
  previous: ObservationState,
  state: DeviceTopologySnapshotState
): ObservationState {
  const capturedAt = state.lastSuccessAt
    ?? state.snapshot?.capturedAt
    ?? state.lastAttemptAt
    ?? undefined;
  const hasSnapshot = state.snapshot !== null && state.snapshot !== undefined;

  if (state.state === "ready") {
    if (!hasSnapshot) {
      return failedObservation(previous, "设备拓扑响应未包含可用快照。");
    }
    if (state.source === "persisted") {
      return {
        ...cachedObservation(capturedAt),
        lastError: "正在等待实时设备拓扑，以下内容来自持久化快照。"
      };
    }
    return readyObservation(capturedAt);
  }

  if (state.state === "failed") {
    const diagnosticMessage = state.attemptDiagnostics[0]?.message;
    return failedObservation(
      hasSnapshot ? lastGoodObservation(state, capturedAt) : loadingObservation(),
      diagnosticMessage ?? "设备拓扑采集失败。");
  }

  if (!hasSnapshot) {
    return loadingObservation();
  }

  return {
    ...lastGoodObservation(state, capturedAt),
    status: "stale",
    lastError: state.state === "refreshing"
      ? "设备拓扑正在刷新，以下内容为最近一次采集结果。"
      : "设备拓扑正在准备，以下内容为最近一次采集结果。"
  };
}

function lastGoodObservation(
  state: DeviceTopologySnapshotState,
  capturedAt?: string
): ObservationState {
  return state.source === "persisted"
    ? cachedObservation(capturedAt)
    : readyObservation(capturedAt);
}
