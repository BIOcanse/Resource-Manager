// Own management subpage IDs, order, visibility, and labels.
// Retain selection across visits.
// Targeted recording requires writable persistence; fall back to smart scheduling if selection becomes hidden.
// Keep smart scheduling visible; gate runtime-dependent content inside it.

import { createEffect, createSignal } from "solid-js";
import type { Accessor } from "solid-js";
import { currentSettingsText, uiText } from "../../text.ts";
import type { SecondaryNavigation, SecondaryTab } from "../../app/navigation/navigationTree.ts";

export const manageSubpageIds = [
  "smartScheduling", "memoryProtection", "cpuMonopoly", "freezeProtection", "kernelLeaks", "diskManagement",
  "targeted", "crashReports", "performanceOverlay"
] as const;

export type ManageSubpage = typeof manageSubpageIds[number];

export interface ManageNavigation extends SecondaryNavigation {
  readonly selected: Accessor<ManageSubpage>;
  readonly select: (id: ManageSubpage | string) => void;
}

/** Reuse each feature label for its management subpage. */
export function manageSubpageName(id: ManageSubpage): string {
  const sections = currentSettingsText().sections;
  switch (id) {
    case "smartScheduling": return uiText.manage.smartScheduling;
    case "memoryProtection": return sections.memoryProtection;
    case "cpuMonopoly": return sections.cpuMonopolyProtection;
    case "freezeProtection": return uiText.manage.freezeProtection;
    case "kernelLeaks": return uiText.kernelLeakReport.tab;
    case "diskManagement": return uiText.manage.diskManagement;
    case "targeted": return uiText.targetedReport.tab;
    case "crashReports": return uiText.manage.crashReports;
    case "performanceOverlay": return sections.performanceOverlay;
  }
}

export function createManageNavigation(options: {
  readonly mutablePersistenceEnabled: Accessor<boolean>;
}): ManageNavigation {
  const [selected, setSelected] = createSignal<ManageSubpage>("smartScheduling");
  const visible = (id: ManageSubpage) => id !== "targeted" || options.mutablePersistenceEnabled();
  // Resolve labels in the current language.
  const tabs = (): readonly SecondaryTab[] => manageSubpageIds
    .filter(visible)
    .map((id) => ({ id, name: manageSubpageName(id) }));

  createEffect(() => {
    if (!visible(selected())) setSelected("smartScheduling");
  });

  return {
    label: () => uiText.manage.tabsLabel,
    ids: manageSubpageIds,
    tabs,
    selected,
    select: (id) => setSelected(id as ManageSubpage)
  };
}
