import type {
  SoftwareRecord, SoftwareSourceFact, SoftwareRegistrationFacts, SoftwareIssueTag, SoftwareIssueEvidence, SoftwareIssueReference
} from "../../types.ts";
import {
  requireArray, requireRecord, requireString, requireStringArray, requireBoolean, requireFiniteNumber,
  requireNonNegativeSafeInteger, requireOneOf, requireNullable, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

const sourceKinds = ["dependency", "windows_registry", "self", "adapter", "controlled", "portable", "manual", "identity_catalog", "policy", "runtime"] as const;
const registrationKinds = ["self", "adapter", "controlled", "portable", "manual"] as const;
const uninstallConstraints = ["none", "empty_managed_root", "missing_uninstall_entry", "manual_registration", "missing_strategy", "self", "portable"] as const;
const softwareKinds = ["adapted", "controlled", "managed", "game", "high_performance", "other", "windows_system", "windows_component", "windows_service", "runtime_package", "runtime_product", "runtime_root", "unattributed"] as const;

function optionalString(value: unknown, path: string) {
  return value === undefined ? undefined : requireNullable(value, path, requireString);
}

function decodeSource(value: unknown, path: string): SoftwareSourceFact {
  const record = requireRecord(value, path);
  return {
    kind: requireOneOf(record.kind, `${path}.kind`, sourceKinds),
    catalogVersion: optionalString(record.catalogVersion, `${path}.catalogVersion`),
    catalogSource: optionalString(record.catalogSource, `${path}.catalogSource`),
    policyGroupId: optionalString(record.policyGroupId, `${path}.policyGroupId`),
    policyGroupName: optionalString(record.policyGroupName, `${path}.policyGroupName`)
  };
}

function decodeRegistration(value: unknown, path: string): SoftwareRegistrationFacts {
  const record = requireRecord(value, path);
  return {
    kind: requireOneOf(record.kind, `${path}.kind`, registrationKinds),
    processCount: requireNonNegativeSafeInteger(record.processCount, `${path}.processCount`),
    serviceCount: requireNonNegativeSafeInteger(record.serviceCount, `${path}.serviceCount`),
    endpointState: optionalString(record.endpointState, `${path}.endpointState`)
  };
}

function decodeIssueReference(value: unknown, path: string): SoftwareIssueReference {
  const record = requireRecord(value, path);
  return { label: requireString(record.label, `${path}.label`), url: requireString(record.url, `${path}.url`) };
}

function decodeIssueEvidence(value: unknown, path: string): SoftwareIssueEvidence {
  const record = requireRecord(value, path);
  return {
    metricKind: requireString(record.metricKind, `${path}.metricKind`),
    value: requireFiniteNumber(record.value, `${path}.value`),
    valueUnit: requireString(record.valueUnit, `${path}.valueUnit`),
    ...(record.moduleName === undefined ? {} : { moduleName: optionalString(record.moduleName, `${path}.moduleName`) })
  };
}

function decodeIssue(value: unknown, path: string): SoftwareIssueTag {
  const record = requireRecord(value, path);
  return {
    id: requireString(record.id, `${path}.id`),
    kind: requireString(record.kind, `${path}.kind`),
    severity: requireString(record.severity, `${path}.severity`),
    source: requireString(record.source, `${path}.source`),
    label: requireString(record.label, `${path}.label`),
    message: requireString(record.message, `${path}.message`),
    dynamic: requireBoolean(record.dynamic, `${path}.dynamic`),
    references: requireArray(record.references, `${path}.references`).map((item, index) => decodeIssueReference(item, `${path}.references[${index}]`)),
    reportIds: requireStringArray(record.reportIds, `${path}.reportIds`),
    ...(record.evidence === undefined ? {} : {
      evidence: requireArray(record.evidence, `${path}.evidence`).map((item, index) => decodeIssueEvidence(item, `${path}.evidence[${index}]`))
    })
  };
}

export function decodeSoftwareRecord(value: unknown, path = "$"): SoftwareRecord {
  const record = requireRecord(value, path);
  for (const field of ["sources", "message", "messageCode", "displayKind"]) {
    if (field in record) throw new ResponseDecodeError(`${path}.${field}`, "removed presentation field");
  }
  const operations = requireRecord(record.operations, `${path}.operations`);
  for (const field of ["uninstallLabel", "uninstallMessage", "uninstallLabelCode", "uninstallMessageCode"]) {
    if (field in operations) throw new ResponseDecodeError(`${path}.operations.${field}`, "removed presentation field");
  }
  return {
    id: requireString(record.id, `${path}.id`),
    name: requireString(record.name, `${path}.name`),
    kind: requireOneOf(record.kind, `${path}.kind`, softwareKinds),
    state: requireString(record.state, `${path}.state`),
    sourceFacts: requireArray(record.sourceFacts, `${path}.sourceFacts`).map((item, index) => decodeSource(item, `${path}.sourceFacts[${index}]`)),
    rootPaths: requireStringArray(record.rootPaths, `${path}.rootPaths`),
    registrationFacts: record.registrationFacts === undefined ? undefined : requireNullable(record.registrationFacts, `${path}.registrationFacts`, decodeRegistration),
    managementRole: record.managementRole === undefined ? undefined : requireNullable(record.managementRole, `${path}.managementRole`, (item, location) => requireOneOf(item, location, ["dependency", "support"])),
    publisher: optionalString(record.publisher, `${path}.publisher`),
    version: optionalString(record.version, `${path}.version`),
    categorySource: optionalString(record.categorySource, `${path}.categorySource`),
    softwareIdentityId: optionalString(record.softwareIdentityId, `${path}.softwareIdentityId`),
    suggestedRootPaths: record.suggestedRootPaths == null ? undefined : requireStringArray(record.suggestedRootPaths, `${path}.suggestedRootPaths`),
    executablePaths: record.executablePaths == null ? undefined : requireStringArray(record.executablePaths, `${path}.executablePaths`),
    requiresRootPathConfirmation: requireBoolean(record.requiresRootPathConfirmation, `${path}.requiresRootPathConfirmation`),
    identityConfirmed: requireBoolean(record.identityConfirmed, `${path}.identityConfirmed`),
    issues: record.issues == null ? undefined : requireArray(record.issues, `${path}.issues`).map((item, index) => decodeIssue(item, `${path}.issues[${index}]`)),
    operations: {
      canUninstall: requireBoolean(operations.canUninstall, `${path}.operations.canUninstall`),
      uninstallKind: requireOneOf(operations.uninstallKind, `${path}.operations.uninstallKind`, ["None", "ManagedRootCleanup", "WindowsUninstaller"]),
      uninstallConstraint: requireOneOf(operations.uninstallConstraint, `${path}.operations.uninstallConstraint`, uninstallConstraints)
    }
  };
}

export function decodeSoftwareRecords(value: unknown): SoftwareRecord[] {
  return requireArray(value, "$").map((item, index) => decodeSoftwareRecord(item, `$[${index}]`));
}
