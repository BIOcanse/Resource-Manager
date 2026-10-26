import { createMemo, type Accessor } from "solid-js";
import type { FrontendSources } from "../../frontendRuntime/source/FrontendSources.ts";
import { useSource } from "../../frontendRuntime/source/useSource.ts";
import type { SourceSnapshot } from "../../frontendRuntime/source/SourceSnapshot.ts";
import type { OperationRegistry } from "../../frontendRuntime/operations/OperationRegistry.ts";
import { bindManagementInventoryRevisions } from "../../stores/managementInventoryRevisionBinding.ts";
import { loadingObservation, readyObservation, failedObservation, profileDisabledObservation, type ObservationState } from "../../observation/observationState.ts";
import { userFacingErrorMessage } from "../../presentation/userFacingText.ts";
import { uiText } from "../../text.ts";

export interface ManagementInventoryOptions {
  sources: Pick<FrontendSources, "managementComponents" | "managementSoftware" | "managementBrowserRuntimes" | "managementInventoryRevision">;
  operations: Pick<OperationRegistry, "subscribeInvalidations">;
  inventoryActive: Accessor<boolean>;
  browserRuntimesActive: Accessor<boolean>;
  mutablePersistenceEnabled: Accessor<boolean>;
}
export function useManagementInventory(options: ManagementInventoryOptions) {
  const componentsActive = () => options.inventoryActive() || options.browserRuntimesActive();
  const softwareActive = () => options.inventoryActive() && options.mutablePersistenceEnabled();
  const components = useSource(options.sources.managementComponents, () => ({ active: componentsActive() }));
  const software = useSource(options.sources.managementSoftware, () => ({ active: softwareActive() }));
  const browserRuntimes = useSource(options.sources.managementBrowserRuntimes, () => ({ active: options.browserRuntimesActive() }));
  bindManagementInventoryRevisions({
    inventoryRevisionSource: options.sources.managementInventoryRevision,
    operations: options.operations,
    components: { active: componentsActive, invalidate: components.invalidate },
    software: { active: softwareActive, invalidate: software.invalidate },
    browserRuntimes: { active: options.browserRuntimesActive, invalidate: browserRuntimes.invalidate }
  });
  return {
    components: createMemo(() => components.snapshot().data ?? []),
    software: createMemo(() => options.mutablePersistenceEnabled() ? software.snapshot().data ?? [] : []),
    browserRuntimes: createMemo(() => browserRuntimes.snapshot().data),
    componentsObservation: createMemo(() => inventoryObservation(components.snapshot(), uiText.stores.componentCatalogRefreshFailed)),
    softwareObservation: createMemo(() => options.mutablePersistenceEnabled()
      ? inventoryObservation(software.snapshot(), uiText.stores.softwareRegistryRefreshFailed)
      : profileDisabledObservation(uiText.stores.writableRegistryDisabled)),
    browserRuntimesObservation: createMemo(() => inventoryObservation(browserRuntimes.snapshot(), uiText.stores.browserRuntimeRefreshFailed)),
    refreshComponents: async () => { await components.refresh({ afterCurrent: true }); },
    refreshSoftware: async (force = false) => {
      if (!options.mutablePersistenceEnabled()) return;
      const result = await software.refresh({ force, afterCurrent: !force });
      // A concurrent ordinary reader does not inherit another caller's rescan failure.
      if (force && result.error) throw result.error;
    },
    refreshBrowserRuntimes: async (force = false) => { await browserRuntimes.refresh({ force, afterCurrent: !force }); }
  };
}
export type ManagementInventory = ReturnType<typeof useManagementInventory>;

export function inventoryObservation<T>(snapshot: SourceSnapshot<T>, fallback: string): ObservationState {
  if (snapshot.status === "ready" || snapshot.status === "refreshing") return readyObservation(snapshot.capturedAt ?? undefined);
  if (snapshot.status === "error" || snapshot.status === "unavailable" || snapshot.status === "disposed") {
    return failedObservation(loadingObservation(), userFacingErrorMessage(snapshot.error, fallback));
  }
  return loadingObservation();
}
