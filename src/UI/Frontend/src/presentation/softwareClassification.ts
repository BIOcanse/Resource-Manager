import type { SoftwareIssueTag, SoftwareRecord } from "../types.ts";
import { formatBytes } from "./byteUnits.ts";
import { uiText } from "../text.ts";

export function isUnclassifiedSoftware(software: SoftwareRecord) {
  return software.issues?.some((issue) => issue.kind === "Unclassified") === true;
}

export function softwareIssueLabel(issue: SoftwareIssueTag) {
  if (issue.kind === "Unclassified") return uiText.softwareKind.Unclassified;
  const text = uiText.management.issueText;
  switch (issue.kind) {
    case "AbnormalMemoryUsage": return text.AbnormalMemoryUsage;
    case "LongSystemInterrupts": return text.LongSystemInterrupts;
    case "ExcessiveSystemInterrupts": return text.ExcessiveSystemInterrupts;
    case "KnownSecurityVulnerability": return text.KnownSecurityVulnerability;
    case "DependencyIssue": return text.DependencyIssue;
    case "CompatibilityIssue": return text.CompatibilityIssue;
    case "KnownMalware": return text.KnownMalware;
    default: return uiText.management.issueHeading;
  }
}

export function softwareIssueMessage(issue: SoftwareIssueTag) {
  if (issue.kind === "Unclassified") return uiText.management.unclassifiedReminder;
  const text = uiText.management.issueText;
  if (issue.id === "app-notebook-fan-control-winring0x64-known-vulnerabilities") return text.winringMessage;
  const evidence = (issue.evidence ?? []).filter(item => Number.isFinite(item.value)).map(item => {
    const value = item.valueUnit === "B" ? formatBytes(item.value, "memory")
      : item.valueUnit === "count" ? uiText.optimizationReport.detail.occurrenceTimes(item.value)
      : `${Number(item.value.toFixed(3))} ${item.valueUnit}`;
    return `${item.moduleName ? `${item.moduleName}: ` : ""}${value}`;
  }).join(" · ");
  const advice = issue.kind === "AbnormalMemoryUsage" ? text.memoryAdvice
    : issue.kind === "LongSystemInterrupts" || issue.kind === "ExcessiveSystemInterrupts" ? text.interruptAdvice : "";
  return [evidence, advice].filter(Boolean).join(". ");
}

export function softwareIssueReferenceLabel(reference: { label: string; url: string }) {
  if (/^CVE-\d{4}-\d+$/.test(reference.label)) return reference.label;
  return reference.url.includes("microsoft-recommended-driver-block-rules")
    ? uiText.management.issueText.driverReference : uiText.management.issueKnownCatalog;
}

export function runtimeDiscoveryLabel(kind: string) {
  switch (kind) {
    case "runtime_package": return uiText.management.discoveredPackage;
    case "runtime_product": return uiText.management.discoveredProduct;
    case "runtime_root": return uiText.management.discoveredRoot;
    default: return "";
  }
}

export function softwareCategorySourceLabel(source: string | null | undefined) {
  return source === "Windows GameConfigStore\\Children\\MatchedExeFullPath"
    ? uiText.management.windowsGameList
    : source ?? "";
}
