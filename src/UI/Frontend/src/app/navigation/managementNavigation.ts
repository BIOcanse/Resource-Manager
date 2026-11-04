// The management store owns selection; category labels include item counts.
// Software categories require writable persistence; App selects dependencies if the current category hides.

import { observationCanRender, type ObservationState } from "../../observation/observationState.ts";
import type { Accessor } from "solid-js";
import type { ManagedComponent, SoftwareRecord } from "../../types";
import { uiText } from "../../text.ts";
import { getManagementItems, managementKinds } from "../../features/management/managementItems.ts";
import {
  browserRuntimeManagementSubpageId,
  managementInventoryKinds,
  type ManagementSubpageId
} from "../../features/management/managementNavigation";
import type { SecondaryNavigation, SecondaryTab } from "./navigationTree.ts";

export function createManagementNavigation(options: {
  readonly components: Accessor<ManagedComponent[]>;
  readonly software: Accessor<SoftwareRecord[]>;
  readonly componentsObservation: Accessor<ObservationState>;
  readonly softwareObservation: Accessor<ObservationState>;
  readonly mutablePersistenceEnabled: Accessor<boolean>;
  readonly selected: Accessor<ManagementSubpageId>;
  readonly select: (subpage: ManagementSubpageId) => void;
}): SecondaryNavigation {
  const tabs = (): readonly SecondaryTab[] => [
    ...managementKinds()
      .filter((kind) => options.mutablePersistenceEnabled() || kind.id === "Dependency" || kind.id === "Support")
      .map((kind) => ({
        id: kind.id,
        name: kind.label,
        count: observationCanRender(kind.id === "Dependency" || kind.id === "Support"
          ? options.componentsObservation() : options.softwareObservation())
          ? getManagementItems(kind.id, options.components(), options.software()).length : "--" as const,
        elementId: `managementTab-${kind.id}`
      })),
    {
      id: browserRuntimeManagementSubpageId,
      name: uiText.managementPage.browserRuntimeTab,
      elementId: `managementTab-${browserRuntimeManagementSubpageId}`
    }
  ];
  return {
    label: () => uiText.management.categoryNav,
    ids: [...managementInventoryKinds, browserRuntimeManagementSubpageId],
    tabs,
    selected: options.selected,
    select: (id) => options.select(id as ManagementSubpageId)
  };
}
