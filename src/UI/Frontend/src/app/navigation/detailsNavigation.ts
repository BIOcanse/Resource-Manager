// Device subpages share this list and its selected state.
// Preserve the selected subpage when leaving device information.


import { createSignal } from "solid-js";
import type { Accessor } from "solid-js";
import { uiText } from "../../text.ts";
import type { SecondaryNavigation, SecondaryTab } from "./navigationTree.ts";

export type DetailsTab = "device" | "gpu" | "cpu";

export interface DetailsNavigation extends SecondaryNavigation {
  readonly selected: Accessor<DetailsTab>;
}

export function createDetailsNavigation(): DetailsNavigation {
  const [selected, setSelected] = createSignal<DetailsTab>("device");
  // Resolve labels in the current language.
  const tabs = (): readonly SecondaryTab[] => ([
    { id: "device", name: uiText.misc.detailsTab.device },
    { id: "gpu", name: uiText.misc.detailsTab.gpu },
    { id: "cpu", name: uiText.misc.detailsTab.cpu }
  ] satisfies Array<{ id: DetailsTab; name: string }>);

  return {
    label: () => uiText.misc.detailsTab.tabsLabel,
    ids: ["device", "gpu", "cpu"],
    tabs,
    selected,
    select: (id) => setSelected(id as DetailsTab)
  };
}
