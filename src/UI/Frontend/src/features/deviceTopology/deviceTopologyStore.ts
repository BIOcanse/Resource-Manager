import {
  createEffect,
  createMemo,
  createSignal,
  onCleanup,
  type Accessor
} from "solid-js";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import {
  loadingObservation,
  type ObservationState
} from "../../observation/observationState";
import type { DeviceTopologySnapshotState } from "../../types";
import { projectDeviceTopologyObservation } from "./deviceTopologyObservation";
import {
  clearRequestedDeviceTopologyPort,
  requestDeviceTopologySelection,
  requestedDeviceTopologyPortId
} from "./deviceTopologySelection";

const subscriptionIntervalMilliseconds = 1_000;
const initialDeviceTopologyState: DeviceTopologySnapshotState = {
  schemaVersion: "3.0.0",
  state: "warming",
  snapshot: null,
  contentGeneration: 0,
  stateRevision: 0,
  source: "memory",
  lastSuccessAt: null,
  lastAttemptAt: null,
  failureCode: null,
  attemptDiagnostics: []
};

export interface DeviceTopologyStoreView {
  state: Accessor<DeviceTopologySnapshotState>;
  observation: Accessor<ObservationState>;
  requestedPortId: Accessor<string | null>;
  clearRequestedPort: () => void;
}

export function useDeviceTopologyState(): DeviceTopologyStoreView {
  const runtime = useFrontendRuntime();
  const frontendWork = useFrontendWork();
  const [state, setState] = createSignal<DeviceTopologySnapshotState>(
    initialDeviceTopologyState);

  createEffect(() => {
    if (!frontendWork.isNeeded(frontendWorkIds.detailsDeviceTopology)) {
      return;
    }
    const unsubscribe = runtime.sources.deviceTopology.subscribe(
      subscriptionIntervalMilliseconds,
      setState);
    onCleanup(unsubscribe);
  });
  const observation = createMemo<ObservationState>(() =>
    projectDeviceTopologyObservation(loadingObservation(), state()));

  return {
    state,
    observation,
    requestedPortId: requestedDeviceTopologyPortId,
    clearRequestedPort: clearRequestedDeviceTopologyPort
  };
}

export { requestDeviceTopologySelection };
