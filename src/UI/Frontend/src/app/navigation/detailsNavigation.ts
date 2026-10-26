// 详细信息的二级分页清单：设备管理、GPU 调度、CPU 拓扑、报告（性能优化开着时）、定向报告（可写持久化开着时）。
// 选中项的所有者就是这份清单。每次进入详细信息都从设备管理开始（原来每次挂载新建状态）：离开详细信息时设回设备管理。
// 选中的分页变得不可见时回到设备管理（现有规则）。

import { createEffect, createSignal, on } from "solid-js";
import type { Accessor } from "solid-js";
import type { PageId } from "../../types";
import { uiText } from "../../text.ts";
import type { SecondaryNavigation, SecondaryTab } from "./navigationTree.ts";

export type DetailsTab = "device" | "gpu" | "cpu" | "report" | "targeted";

export interface DetailsNavigation extends SecondaryNavigation {
  readonly selected: Accessor<DetailsTab>;
}

export function createDetailsNavigation(options: {
  readonly activePage: Accessor<PageId>;
  readonly optimizationEnabled: Accessor<boolean>;
  readonly mutablePersistenceEnabled: Accessor<boolean>;
}): DetailsNavigation {
  const [selected, setSelected] = createSignal<DetailsTab>("device");
  const visible = (id: DetailsTab) => (id !== "report" || options.optimizationEnabled())
    && (id !== "targeted" || options.mutablePersistenceEnabled());
  // 文案按当前语言求值。
  const tabs = (): readonly SecondaryTab[] => ([
    { id: "device", name: uiText.misc.detailsTab.device },
    { id: "gpu", name: uiText.misc.detailsTab.gpu },
    { id: "cpu", name: uiText.misc.detailsTab.cpu },
    { id: "report", name: uiText.misc.detailsTab.report },
    { id: "targeted", name: uiText.targetedReport.tab }
  ] satisfies Array<{ id: DetailsTab; name: string }>).filter((tab) => visible(tab.id));

  createEffect(() => {
    if (!visible(selected())) setSelected("device");
  });
  createEffect(on(options.activePage, (page, previous) => {
    if (previous === "details" && page !== "details") setSelected("device");
  }, { defer: true }));

  return {
    label: () => uiText.misc.detailsTab.tabsLabel,
    ids: ["device", "gpu", "cpu", "report", "targeted"],
    tabs,
    selected,
    select: (id) => setSelected(id as DetailsTab)
  };
}
