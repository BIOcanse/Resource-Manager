// Share category membership between navigation counts
// and the management list.

import { sharedBrowserRuntimeComponentId } from "../browserRuntimes/browserRuntimeTypes.ts";
import { componentDisplayName } from "../../presentation/userFacingText.ts";
import { isUnclassifiedSoftware } from "../../presentation/softwareClassification.ts";
import type { ManagedComponent, ManagementKind, ManagementRole, SoftwareRecord } from "../../types.ts";
import { managementKindLabel } from "../../text.ts";
import { normalizeName, normalizeSoftwareKind } from "../../utils.ts";
import { managementInventoryKinds } from "./managementNavigation.ts";

// Resolve category labels in the current language.
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

  // List dependency and support records only under their owning role; omit duplicate application entries and uninstall actions.
  const appSoftware = software.filter((item) => !item.managementRole);
  if (kind === "Unclassified") {
    return appSoftware.filter(isUnclassifiedSoftware).map((item) => ({ type: "software", value: item }));
  }

  if (kind === "Unconfirmed") {
    return software
      .filter(isUnconfirmedSoftware)
      .map((item) => ({ type: "software", value: item }));
  }

  return appSoftware
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
  // Keep shared WebView2 entries exclusively in runtime management,
  // including matching software-inventory records.
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
