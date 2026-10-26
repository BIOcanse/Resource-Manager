// 分页树（docs/navigation_tree_2026-10-25.md）：一级页和二级分页是一棵树的两层，外壳在上方统一画。
// 一级选中项的所有者是 App 的 activePage；二级选中项各有各的所有者，清单只读它、调用它的选择，不另存副本。

import type { Accessor } from "solid-js";
import type { PageId } from "../../types";
import { uiText } from "../../text.ts";

/**
 * 二级分页：标识（也是页签的值）和名字；名字是这一处的唯一叫法，页签、页标题、页面里的区块标题都用它。
 * count 是页签上名字后面的数量（组件与软件每一类的项数），不是名字的一部分。elementId 是页签元素的 id，不给就由页签组生成。
 */
export interface SecondaryTab {
  readonly id: string;
  readonly name: string;
  readonly count?: number | "--";
  readonly elementId?: string;
}

/** 一个一级页的二级分页清单。ids 是声明的全部项，定序号用（可见性变了序号不变）；tabs 是现在可见的项，按次序。 */
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

/** 一级次序固定（用户的布局）。 */
export const primaryPageOrder: readonly PageId[] = ["monitor", "components", "optimization", "diskUsage", "control", "details", "settings"];

const primaryLabels: Readonly<Record<PageId, () => string>> = {
  monitor: () => uiText.page.monitor,
  components: () => uiText.page.components,
  optimization: () => uiText.page.optimization,
  diskUsage: () => uiText.page.diskUsage,
  control: () => uiText.page.control,
  details: () => uiText.page.details,
  settings: () => uiText.page.settings
};

export interface NavigationTree {
  readonly pages: readonly PrimaryPage[];
  /** 一级页。 */
  readonly page: (id: PageId) => PrimaryPage;
  /** 这一处的名字：选中的二级分页的名字，没有二级分页时是一级页的名字。 */
  readonly name: (page: PageId) => string;
  /** 当前所在处的序号：树按一级次序摊平，每个二级分页一个，没有二级分页的一级页占一个。换页的方向和指示条都读它。 */
  readonly order: (page: PageId, secondary: string | null) => number;
}

export function createNavigationTree(options: {
  readonly optimizationVisible: Accessor<boolean>;
  readonly secondary: Partial<Readonly<Record<PageId, SecondaryNavigation>>>;
}): NavigationTree {
  const pages = primaryPageOrder.map((id): PrimaryPage => ({
    id,
    label: primaryLabels[id],
    visible: id === "optimization" ? options.optimizationVisible : () => true,
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
