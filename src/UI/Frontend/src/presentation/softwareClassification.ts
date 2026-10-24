import type { SoftwareIssueTag, SoftwareRecord } from "../types.ts";
import { uiText } from "../text.ts";

export function isUnclassifiedSoftware(software: SoftwareRecord) {
  return software.issues?.some((issue) => issue.kind === "Unclassified") === true;
}

export function softwareIssueLabel(issue: SoftwareIssueTag) {
  return issue.kind === "Unclassified" ? uiText.softwareKind.Unclassified : issue.label;
}

export function softwareIssueMessage(issue: SoftwareIssueTag) {
  return issue.kind === "Unclassified" ? uiText.management.unclassifiedReminder : issue.message;
}

export function runtimeDiscoveryLabel(kind: string) {
  switch (kind) {
    case "runtime_package": return uiText.management.discoveredPackage;
    case "runtime_product": return uiText.management.discoveredProduct;
    case "runtime_root": return uiText.management.discoveredRoot;
    default: return "";
  }
}
