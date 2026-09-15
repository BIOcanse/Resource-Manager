import { createMemo, type Accessor } from "solid-js";
import type { SourceHandle } from "../frontendRuntime/source/SourceDescriptor.ts";
import { sourceCanRender } from "../frontendRuntime/source/SourceSnapshot.ts";
import { useSource } from "../frontendRuntime/source/useSource.ts";
import {
  failedObservation,
  loadingObservation,
  readyObservation,
  type ObservationState
} from "../observation/observationState";
import { userFacingErrorMessage } from "../presentation/userFacingText";
import type { BackendStartupCapabilities } from "../types";
import { uiText } from "../text.ts";

const failClosedCapabilities: BackendStartupCapabilities = {
  profileId: "unavailable",
  readOnly: true,
  mutablePersistence: false,
  legacyPersistenceImport: false,
  gpuLaunchInterceptionReconciliation: false,
  runtimeEffectOwners: false,
  publicServiceCoordination: false,
  optimizationRuntime: false,
  sharedResourceOwnership: false
};

export interface RuntimeCapabilitiesStore {
  state: Accessor<"loading" | "ready" | "error">;
  observation: Accessor<ObservationState>;
  current: Accessor<BackendStartupCapabilities>;
  mutablePersistenceEnabled: Accessor<boolean>;
  gpuPlacementEnabled: Accessor<boolean>;
  runtimeEffectsEnabled: Accessor<boolean>;
  publicServicesEnabled: Accessor<boolean>;
  optimizationEnabled: Accessor<boolean>;
  sharedResourcesEnabled: Accessor<boolean>;
  refresh: () => Promise<boolean>;
}

export function createRuntimeCapabilitiesStore(
  handle: SourceHandle<BackendStartupCapabilities>
): RuntimeCapabilitiesStore {
  const source = useSource(handle, () => ({ active: true, refreshIntervalMs: null }));
  const state = createMemo<"loading" | "ready" | "error">(() => {
    const status = source.snapshot().status;
    if (status === "ready") {
      return "ready";
    }
    return status === "error" || status === "unavailable" || status === "disposed"
      ? "error"
      : "loading";
  });
  const current = createMemo<BackendStartupCapabilities>(() => {
    const snapshot = source.snapshot();
    return sourceCanRender(snapshot) && snapshot.data
      ? snapshot.data
      : failClosedCapabilities;
  });
  const observation = createMemo<ObservationState>(() => {
    const snapshot = source.snapshot();
    if (snapshot.status === "ready") {
      return readyObservation(snapshot.capturedAt ?? undefined);
    }
    if (snapshot.status === "error"
      || snapshot.status === "unavailable"
      || snapshot.status === "disposed") {
      return failedObservation(
        loadingObservation(),
        userFacingErrorMessage(snapshot.error, uiText.misc.runtimeCapabilityReadFailed));
    }
    return loadingObservation();
  });

  async function refresh() {
    const snapshot = await source.refresh();
    return snapshot.status === "ready";
  }

  const allows = (selector: (value: BackendStartupCapabilities) => boolean) =>
    () => state() === "ready" && selector(current());

  return {
    state,
    observation,
    current,
    mutablePersistenceEnabled: allows((value) => value.mutablePersistence),
    gpuPlacementEnabled: allows((value) => value.gpuLaunchInterceptionReconciliation),
    runtimeEffectsEnabled: allows((value) => value.runtimeEffectOwners),
    publicServicesEnabled: allows((value) => value.publicServiceCoordination),
    optimizationEnabled: allows((value) => value.optimizationRuntime),
    sharedResourcesEnabled: allows((value) => value.sharedResourceOwnership),
    refresh
  };
}
