import type { ManagementKind } from "../../types";

export const managementInventoryKinds = [
  "Dependency",
  "Support",
  "adapted",
  "controlled",
  "Unconfirmed",
  "Unclassified",
  "game",
  "high_performance",
  "other"
] as const satisfies readonly ManagementKind[];

export const browserRuntimeManagementSubpageId = "BrowserRuntime" as const;

export type ManagementSubpageId = ManagementKind
  | typeof browserRuntimeManagementSubpageId;

export function isManagementInventorySubpage(subpage: ManagementSubpageId): subpage is ManagementKind {
  return (managementInventoryKinds as readonly string[]).includes(subpage);
}
