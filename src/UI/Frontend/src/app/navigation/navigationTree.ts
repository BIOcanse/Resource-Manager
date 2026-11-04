// The shell renders primary pages and subpages as two levels of one navigation tree.
// App owns primary selection; subpage lists delegate to their owners without duplicating state.

import type { Accessor } from "solid-js";
import type { PageId } from "../../types";
import { uiText } from "../../text.ts";

/** Subpage identity and shared display name, with an optional separate count and tab element ID. */
export interface SecondaryTab {
  readonly id: string;
  readonly name: string;
  readonly count?: number | "--";
  readonly elementId?: string;
}

/** ids fixes ordering across visibility changes; tabs lists currently visible items in order. */
export interface SecondaryNavigation {
  readonly label: Accessor<string>;
  readonly ids: readonly string[];
  readonly tabs: Accessor<readonly SecondaryTab[]>;
  readonly selected: Accessor<string>;
  readonly select: (id: string) => void;
}

export interface PrimaryPage {
  readonly id: PageId;
  readonly label: Accessor<string>;
  readonly visible: Accessor<boolean>;
  readonly secondary: SecondaryNavigation | null;
}

/** Fixed primary-page order. */
export const primaryPageOrder: readonly PageId[] = ["monitor", "components", "manage", "control", "details", "settings"];

const primaryLabels: Readonly<Record<PageId, () => string>> = {
  monitor: () => uiText.page.monitor,
  components: () => uiText.page.components,
  manage: () => uiText.page.manage,
  control: () => uiText.page.control,
  details: () => uiText.page.details,
  settings: () => uiText.page.settings
};

export interface NavigationTree {
  readonly pages: readonly PrimaryPage[];
  /** Primary page. */
  readonly page: (id: PageId) => PrimaryPage;
  /** Selected subpage name, or primary-page name when there is no subpage. */
  readonly name: (page: PageId) => string;
  /** Flattened navigation index for transition direction and indicators; each leaf occupies one position. */
  readonly order: (page: PageId, secondary: string | null) => number;
}

export function createNavigationTree(options: {
  readonly secondary: Partial<Readonly<Record<PageId, SecondaryNavigation>>>;
}): NavigationTree {
  const pages = primaryPageOrder.map((id): PrimaryPage => ({
    id,
    label: primaryLabels[id],
    // Primary pages remain visible; conditional content belongs in their subpages or body.
    visible: () => true,
    secondary: options.secondary[id] ?? null
  }));
  const byId = new Map(pages.map((page) => [page.id, page]));
  const offsets = new Map<PageId, number>();
  let next = 0;
  for (const page of pages) {
    offsets.set(page.id, next);
    next += page.secondary ? page.secondary.ids.length : 1;
  }
  return {
    pages,
    page: (id) => byId.get(id)!,
    name: (id) => {
      const entry = byId.get(id)!;
      const secondary = entry.secondary;
      if (!secondary) return entry.label();
      const selected = secondary.selected();
      return secondary.tabs().find((tab) => tab.id === selected)?.name ?? entry.label();
    },
    order: (page, secondary) => {
      const entry = byId.get(page)!;
      const offset = offsets.get(page)!;
      if (!entry.secondary || secondary === null) return offset;
      const index = entry.secondary.ids.indexOf(secondary);
      return offset + Math.max(0, index);
    }
  };
}
