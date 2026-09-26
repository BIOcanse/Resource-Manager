import type { ManagementKind } from "../../types";

export const managementInventoryKinds = [
  "Dependency",
  "Support",
  "Adapted",
  "Controlled",
  "Unconfirmed",
  "Game",
  "HighPerformance",
  "Other"
] as const satisfies readonly ManagementKind[];

export const migrationManagementSubpageId = "Migration" as const;
export const browserRuntimeManagementSubpageId = "BrowserRuntime" as const;

export type ManagementSubpageId = ManagementKind
  | typeof migrationManagementSubpageId
  | typeof browserRuntimeManagementSubpageId;

export function isManagementInventorySubpage(subpage: ManagementSubpageId): subpage is ManagementKind {
  return (managementInventoryKinds as readonly string[]).includes(subpage);
}
