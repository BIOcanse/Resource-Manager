import { createEffect, onCleanup } from "solid-js";
import type { Accessor } from "solid-js";
import type { ManagementInventoryRevisionSnapshot } from "../data/managementInventoryRevision.ts";
import type { CurrentValueSource } from "../frontendRuntime/push/PushValueSourceFamily.ts";
import type { PageId } from "../types.ts";
import {
  browserRuntimeManagementSubpageId,
  type ManagementSubpageId
} from "../features/management/managementNavigation.ts";

export interface ManagementInventoryRevisionBindingOptions {
  readonly activePage: Accessor<PageId>;
  readonly activeSubpage: Accessor<ManagementSubpageId>;
  readonly inventoryRevisionSource: CurrentValueSource<ManagementInventoryRevisionSnapshot>;
  readonly inventoryActive: Accessor<boolean>;
  readonly browserRuntimesActive: Accessor<boolean>;
  readonly mutablePersistenceEnabled: Accessor<boolean>;
  readonly refreshComponents: () => Promise<unknown>;
  readonly refreshSoftware: () => Promise<unknown>;
  readonly refreshBrowserRuntimes: () => Promise<unknown>;
}

export function bindManagementInventoryRevisions(
  options: ManagementInventoryRevisionBindingOptions
): void {
  createEffect(() => {
    const managementVisible = options.activePage() === "components";
    const subpage = options.activeSubpage();
    const inventoryVisible = managementVisible
      && options.inventoryActive()
      && subpage !== browserRuntimeManagementSubpageId;
    const browserRuntimeVisible = managementVisible
      && options.browserRuntimesActive()
      && subpage === browserRuntimeManagementSubpageId;
    if (!inventoryVisible && !browserRuntimeVisible) {
      return;
    }

    let previous: ManagementInventoryRevisionSnapshot | null = null;
    const unsubscribe = options.inventoryRevisionSource.subscribeSnapshot((snapshot) => {
      const next = snapshot.data;
      if (!next) {
        return;
      }
      const before = previous;
      previous = next;
      if (!before) {
        return;
      }

      if (inventoryVisible && options.inventoryActive()) {
        const wantsComponentList = subpage === "Dependency" || subpage === "Support";
        if (wantsComponentList
          && before.componentRevision !== next.componentRevision) {
          refreshWithoutEscapingStoreFailure(options.refreshComponents);
        }
        if (options.mutablePersistenceEnabled()
          && before.softwareRevision !== next.softwareRevision) {
          refreshWithoutEscapingStoreFailure(options.refreshSoftware);
        }
      }

      if (browserRuntimeVisible
        && options.browserRuntimesActive()
        && before.browserRuntimeRevision !== next.browserRuntimeRevision) {
        refreshWithoutEscapingStoreFailure(options.refreshBrowserRuntimes);
      }
    });
    onCleanup(unsubscribe);
  });
}

function refreshWithoutEscapingStoreFailure(refresh: () => Promise<unknown>): void {
  void refresh().catch(() => undefined);
}
