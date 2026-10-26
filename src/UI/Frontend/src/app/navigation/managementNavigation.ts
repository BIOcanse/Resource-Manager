// 组件与软件的二级分页清单：九类和浏览器运行时，文字带数量。选中项的所有者是管理存储（刷新调度也读它）。
// 软件类只在可写持久化开着时可见；看不见的类被选中时由 App 回到依赖组件（现有规则）。

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
        count: getManagementItems(kind.id, options.components(), options.software()).length,
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
