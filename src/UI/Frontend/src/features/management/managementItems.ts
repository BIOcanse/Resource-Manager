// 组件与软件的分类和每一类列出的项。分页清单（app/navigation/managementNavigation.ts）按它数每一类的数量，
// 管理页按它列出当前分类的项；两处读同一份规则。

import { sharedBrowserRuntimeComponentId } from "../browserRuntimes/browserRuntimeTypes";
import { componentDisplayName } from "../../presentation/userFacingText";
import { isUnclassifiedSoftware } from "../../presentation/softwareClassification.ts";
import type { ManagedComponent, ManagementKind, ManagementRole, SoftwareRecord } from "../../types";
import { managementKindLabel } from "../../text.ts";
import { normalizeName, normalizeSoftwareKind } from "../../utils";
import { managementInventoryKinds } from "./managementNavigation";

// 分类名按当前语言求值，不能在模块顶层固化。
export function managementKinds(): Array<{ id: ManagementKind; label: string }> {
  return managementInventoryKinds.map((id) => ({
    id,
    label: managementKindLabel(id)
  }));
}

export interface ManagementItem {
  type: "component" | "software";
  value: ManagedComponent | SoftwareRecord;
}


export function getManagementItems(kind: ManagementKind, components: ManagedComponent[], software: SoftwareRecord[]): ManagementItem[] {
  if (kind === "Dependency" || kind === "Support") {
    return getManagementRoleItems(kind, components, software);
  }

  if (kind === "Unclassified") {
    return software.filter(isUnclassifiedSoftware).map((item) => ({ type: "software", value: item }));
  }

  if (kind === "Unconfirmed") {
    return software
      .filter(isUnconfirmedSoftware)
      .map((item) => ({ type: "software", value: item }));
  }

  return software
    .filter((item) => !isUnconfirmedSoftware(item) && !isUnclassifiedSoftware(item) && normalizeSoftwareKind(item) === kind)
    .map((item) => ({ type: "software", value: item }));
}

export function isUnconfirmedSoftware(software: SoftwareRecord) {
  return software.identityConfirmed === false || software.requiresRootPathConfirmation === true;
}

function getManagementRoleItems(
  role: "Dependency" | "Support",
  components: ManagedComponent[],
  software: SoftwareRecord[]
): ManagementItem[] {
  const wireRole: ManagementRole = role === "Dependency" ? "dependency" : "support";
  const roleComponents = components
    .filter((component) => component.definition?.managementRole === wireRole);
  const componentItems = roleComponents
    .filter((component) => component.definition?.id !== sharedBrowserRuntimeComponentId)
    .map((component) => ({ type: "component" as const, value: component }));
  // 共享 WebView2 运行时只在「运行时管理」里列；它在软件清单里的同名记录也要一起去掉，
  // 否则会以后端原名的软件卡片回到依赖列表。
  const componentIds = new Set(roleComponents.map((component) => component.definition?.id));
  const componentNameSet = new Set(roleComponents
    .map((component) => normalizeName(componentDisplayName(component.definition?.id)))
    .filter(Boolean));
  const extraSoftwareItems = software
    .filter((item) => !isUnconfirmedSoftware(item) && item.managementRole === wireRole)
    .filter((item) => !componentIds.has(item.id.replace(/^dependency:/, "")) && !componentNameSet.has(normalizeName(item.name)))
    .map((item) => ({ type: "software" as const, value: item }));
  return [...componentItems, ...extraSoftwareItems];
}
