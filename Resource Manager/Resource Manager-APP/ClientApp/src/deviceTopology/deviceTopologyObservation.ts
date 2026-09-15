import {
  cachedObservation,
  failedObservation,
  loadingObservation,
  readyObservation,
  type ObservationState
} from "../observation/observationState.ts";
import type { DeviceTopologySnapshotState } from "../types";
import { renderBackendMessage } from "../presentation/backendMessage.ts";
import { uiText } from "../text.ts";

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
      return failedObservation(previous, uiText.deviceTopologyState.snapshotMissing);
    }
    if (state.source === "persisted") {
      return {
        ...cachedObservation(capturedAt),
        lastError: uiText.deviceTopologyState.waitingForLive
      };
    }
    return readyObservation(capturedAt);
  }

  if (state.state === "failed") {
    const diagnostic = state.attemptDiagnostics[0];
    return failedObservation(
      hasSnapshot ? lastGoodObservation(state, capturedAt) : loadingObservation(),
      diagnostic
        ? renderBackendMessage(diagnostic.messageCode, uiText.deviceTopologyState.collectionFailed)
        : uiText.deviceTopologyState.collectionFailed);
  }

  if (!hasSnapshot) {
    return loadingObservation();
  }

  return {
    ...lastGoodObservation(state, capturedAt),
    status: "stale",
    lastError: state.state === "refreshing"
      ? uiText.deviceTopologyState.refreshing
      : uiText.deviceTopologyState.preparing
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
