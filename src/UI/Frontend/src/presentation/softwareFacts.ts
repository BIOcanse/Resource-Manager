import type { SoftwareRecord } from "../types.ts";
import { uiText, softwareDisplayKindLabel } from "../text.ts";
import { componentDisplayName } from "./userFacingText.ts";

export function softwareRecordName(record: SoftwareRecord): string {
  return record.sourceFacts?.some(source => source.kind === "dependency")
    ? componentDisplayName(record.id.replace(/^dependency:/, "")) : record.name;
}

export function softwareSourceLabels(record: SoftwareRecord): string[] {
  return (record.sourceFacts ?? []).map(source => {
    const label = uiText.softwareFacts.source[source.kind];
    return [label, source.catalogVersion, source.catalogSource, source.policyGroupName]
      .filter(Boolean).join(" · ");
  });
}

export function softwareDescription(record: SoftwareRecord): string {
  const facts = record.registrationFacts;
  const copy = uiText.softwareFacts;
  const parts: string[] = [];
  if (facts) {
    parts.push(copy.source[facts.kind]);
    if (facts.kind === "adapter" || facts.kind === "controlled") {
      parts.push(copy.processCount(facts.processCount), copy.serviceCount(facts.serviceCount));
      if (facts.endpointState) parts.push(copy.endpointState[facts.endpointState as keyof typeof copy.endpointState] ?? uiText.status.unknownState);
    } else if (facts.kind === "portable") {
      parts.push(copy.executableCount(record.executablePaths?.length ?? 0),
        record.requiresRootPathConfirmation ? uiText.managementPage.rootNeedsCheck : copy.rootConfirmed);
    } else if (facts.kind === "manual") {
      parts.push(softwareDisplayKindLabel(record.kind));
    }
  } else if (record.sourceFacts?.some(source => source.kind === "windows_registry")) {
    parts.push(record.operations?.canUninstall
      ? uiText.backendMessage.software.hasUninstallEntry : uiText.backendMessage.software.missingUninstallEntry);
  }
  for (const source of record.sourceFacts ?? []) {
    if (source.kind === "policy") parts.push(`${copy.source.policy}: ${source.policyGroupName ?? source.policyGroupId ?? ""}`);
  }
  return parts.join(" · ");
}

export function softwareUninstallDescription(record: SoftwareRecord): string {
  const operation = record.operations;
  const copy = uiText.backendMessage.software;
  switch (operation?.uninstallConstraint) {
    case "empty_managed_root": return uiText.backendMessage.dependency.managedRootEmpty;
    case "manual_registration": return copy.manualClassificationNoUninstall;
    case "missing_strategy": return record.kind === "adapted" ? copy.adaptedNoUninstall : copy.controlledNoUninstall;
    case "self": return copy.selfNoUninstall;
    case "portable": return copy.portableNoUninstall;
    case "missing_uninstall_entry": return copy.noUninstallUnknownRoot;
    case "none":
      return operation?.uninstallKind === "ManagedRootCleanup"
        ? uiText.backendMessage.dependency.managedRootRemovable : copy.windowsUninstallerDescription;
    default: return copy.cannotUninstallAction;
  }
}

export function softwareSummary(record: SoftwareRecord): string {
  return [record.publisher, record.version, softwareDescription(record)].filter(Boolean).join(" · ");
}
