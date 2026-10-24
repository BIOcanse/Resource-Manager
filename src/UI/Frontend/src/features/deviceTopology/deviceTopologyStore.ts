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
  failedObservation,
  loadingObservation,
  type ObservationState
} from "../../observation/observationState";
import type { SourceSnapshot } from "../../frontendRuntime/source/SourceSnapshot.ts";
import { uiText } from "../../text.ts";
import type { DeviceTopologySnapshotState } from "../../types";
import { projectDeviceTopologyObservation } from "./deviceTopologyObservation";
import {
  clearRequestedDeviceTopologyPort,
  requestDeviceTopologySelection,
  requestedDeviceTopologyPortId
} from "./deviceTopologySelection";

const subscriptionIntervalMilliseconds = 1_000;
const initialDeviceTopologyState: DeviceTopologySnapshotState = {
  schemaVersion: "4.0.0",
  state: "warming",
  snapshot: null,
  contentGeneration: 0,
  stateRevision: 0,
  source: "memory",
  lastSuccessAt: null,
  lastAttemptAt: null,
  failureCode: null,
  attemptDiagnostics: [],
  informationFields: { observedAt: null, devices: [] }
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
  const [source, setSource] = createSignal<SourceSnapshot<DeviceTopologySnapshotState> | null>(null);
  const state = () => source()?.data ?? initialDeviceTopologyState;

  createEffect(() => {
    if (!frontendWork.isNeeded(frontendWorkIds.detailsDeviceTopology)) {
      setSource(null);
      return;
    }
    const unsubscribe = runtime.sources.deviceTopology.subscribeSnapshot(
      subscriptionIntervalMilliseconds,
      setSource);
    onCleanup(() => { unsubscribe(); setSource(null); });
  });
  const observation = createMemo<ObservationState>(() => {
    const current = source();
    if (current?.status === "error" || current?.status === "unavailable") {
      return failedObservation(loadingObservation(), uiText.deviceTopologyState.collectionFailed);
    }
    return projectDeviceTopologyObservation(loadingObservation(), state());
  });

  return {
    state,
    observation,
    requestedPortId: requestedDeviceTopologyPortId,
    clearRequestedPort: clearRequestedDeviceTopologyPort
  };
}

export { requestDeviceTopologySelection };
