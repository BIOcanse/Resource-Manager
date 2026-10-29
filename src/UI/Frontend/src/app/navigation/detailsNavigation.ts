// 详细信息的二级分页清单：设备管理、GPU 调度、CPU 拓扑、报告（性能优化开着时）、定向报告（可写持久化开着时）、内核泄漏报告、
// 内存保护记录。
// 选中项的所有者就是这份清单。离开再回到详细信息时仍是上次选中的分页（与组件与软件一致，页面保留选中状态）。
// 选中的分页变得不可见时回到设备管理（现有规则）。

import { createEffect, createSignal } from "solid-js";
import type { Accessor } from "solid-js";
import { uiText } from "../../text.ts";
import type { SecondaryNavigation, SecondaryTab } from "./navigationTree.ts";

export type DetailsTab = "device" | "gpu" | "cpu" | "report" | "targeted" | "kernelLeaks" | "memoryProtection";

export interface DetailsNavigation extends SecondaryNavigation {
  readonly selected: Accessor<DetailsTab>;
}

export function createDetailsNavigation(options: {
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
    { id: "targeted", name: uiText.targetedReport.tab },
    { id: "kernelLeaks", name: uiText.kernelLeakReport.tab },
    { id: "memoryProtection", name: uiText.memoryProtection.reportTab }
  ] satisfies Array<{ id: DetailsTab; name: string }>).filter((tab) => visible(tab.id));

  createEffect(() => {
    if (!visible(selected())) setSelected("device");
  });

  return {
    label: () => uiText.misc.detailsTab.tabsLabel,
    ids: ["device", "gpu", "cpu", "report", "targeted", "kernelLeaks", "memoryProtection"],
    tabs,
    selected,
    select: (id) => setSelected(id as DetailsTab)
  };
}
