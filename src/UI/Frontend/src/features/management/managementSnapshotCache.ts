import type { ComponentProviderState, ManagedComponent, SoftwareRecord } from "../../types";

const storageKey = "resource-manager:management-snapshot:v3";
const cacheVersion = 3;
const maximumComponents = 256;
const maximumSoftwareRecords = 2048;

interface ManagementSnapshotCache {
  version: number;
  capturedAt: string;
  components: ManagedComponent[];
  software: SoftwareRecord[];
}

let memoryCache: ManagementSnapshotCache | null | undefined;
let lastPayload = "";

export function readManagementSnapshotCache(): ManagementSnapshotCache | null {
  if (memoryCache !== undefined) {
    return memoryCache;
  }

  let raw: string | null;
  try {
    raw = localStorage.getItem(storageKey);
  } catch {
    memoryCache = null;
    return null;
  }

  if (!raw) {
    memoryCache = null;
    return null;
  }

  let parsed: Partial<ManagementSnapshotCache>;
  try {
    parsed = JSON.parse(raw) as Partial<ManagementSnapshotCache>;
  } catch {
    memoryCache = null;
    return null;
  }
  if (!isRecord(parsed)
    || parsed.version !== cacheVersion
    || !Array.isArray(parsed.components)
    || !Array.isArray(parsed.software)) {
    memoryCache = null;
    return null;
  }

  let migrated = false;
  const components = parsed.components.slice(0, maximumComponents).map((component) => {
    const result = readStoredComponent(component);
    migrated ||= result.changed;
    return result.value;
  });
  const software = parsed.software
    .slice(0, maximumSoftwareRecords)
    .map((record) => {
      const result = readStoredSoftware(record);
      migrated ||= result.changed;
      return withoutIssueState(result.value);
    });
  const next: ManagementSnapshotCache = {
    version: cacheVersion,
    capturedAt: typeof parsed.capturedAt === "string" ? parsed.capturedAt : "",
    components,
    software
  };
  memoryCache = next;
  lastPayload = serializePayload(next.components, next.software);
  if (migrated) {
    try {
      localStorage.setItem(storageKey, JSON.stringify(next));
    } catch {
      // The in-memory migrated snapshot remains available for this shell session.
    }
  }
  return next;
}

export function writeManagementSnapshotCache(
  components: ManagedComponent[],
  software: SoftwareRecord[]
) {
  const boundedComponents = components.slice(0, maximumComponents);
  const boundedSoftware = software
    .slice(0, maximumSoftwareRecords)
    .map(withoutIssueState);
  const payload = serializePayload(boundedComponents, boundedSoftware);
  if (payload === lastPayload) {
    return;
  }

  const next: ManagementSnapshotCache = {
    version: cacheVersion,
    capturedAt: new Date().toISOString(),
    components: boundedComponents,
    software: boundedSoftware
  };
  const serialized = JSON.stringify(next);
  memoryCache = next;
  lastPayload = payload;
  try {
    localStorage.setItem(storageKey, serialized);
  } catch {
    // The in-memory last-good snapshot remains available for this shell session.
  }
}

function serializePayload(components: ManagedComponent[], software: SoftwareRecord[]) {
  return JSON.stringify({ components, software });
}

function withoutIssueState(record: SoftwareRecord): SoftwareRecord {
  return { ...record, issues: undefined };
}

const componentStatusValues = [
  "active", "installed", "installed_unverified", "ready_to_install", "available",
  "manual_download_required", "provider_missing", "missing"
] as const;
const componentProviderStateValues = [
  "active", "installed", "missing", "runtime_available", "installed_unverified", "driver_missing", "runtime_active"
] as const;
const managementRoleValues = ["dependency", "support"] as const;
const componentProviderKindValues = [
  "runtime", "cpu", "fan", "hardware_monitor", "provider", "gpu", "notebook_oem"
] as const;
const softwareKindValues = [
  "adapted", "controlled", "managed", "game", "high_performance", "other", "windows_system",
  "windows_component", "windows_service", "runtime_package", "runtime_product", "runtime_root", "unattributed"
] as const;
const softwareDisplayKindValues = [...softwareKindValues, "system_residual"] as const;

function readStoredComponent(component: ManagedComponent) {
  if (!isRecord(component)) {
    return { value: component, changed: false };
  }
  let result = migrateProperty(component, "state", readStoredComponentStatus);
  let changed = result.changed;
  let value = result.value;
  if (isRecord(value.definition)) {
    const definition = migrateProperty(value.definition, "managementRole", readStoredManagementRole);
    let nextDefinition = definition.value;
    const originalCapabilities = nextDefinition.capabilities;
    if (Array.isArray(originalCapabilities)) {
      const capabilities = originalCapabilities.map((capability) =>
        migrateProperty(capability, "providerKind", readStoredComponentProviderKind).value);
      if (capabilities.some((capability, index) => capability !== originalCapabilities[index])) {
        nextDefinition = { ...nextDefinition, capabilities };
      }
    }
    if (nextDefinition !== value.definition) {
      value = { ...value, definition: nextDefinition };
      changed = true;
    }
  }
  const originalProviders = value.providers;
  if (Array.isArray(originalProviders)) {
    const providers = originalProviders.map((provider) => {
      const state = migrateProperty(provider as ComponentProviderState, "state", readStoredComponentProviderState);
      const kind = migrateProperty(state.value, "providerKind", readStoredComponentProviderKind);
      changed ||= state.changed || kind.changed;
      return kind.value;
    });
    if (providers.some((provider, index) => provider !== originalProviders[index])) {
      value = { ...value, providers };
    }
  }
  return { value, changed };
}

function readStoredSoftware(record: SoftwareRecord) {
  let result = migrateProperty(record, "kind", readStoredSoftwareKind);
  let value = result.value;
  let changed = result.changed;
  result = migrateProperty(value, "displayKind", readStoredSoftwareDisplayKind);
  value = result.value;
  changed ||= result.changed;
  result = migrateProperty(value, "managementRole", readStoredManagementRole);
  value = result.value;
  changed ||= result.changed;
  return { value, changed };
}

function migrateProperty<T extends object>(
  record: T,
  property: string,
  readStored: (value: unknown) => StoredToken
): { value: T; changed: boolean } {
  if (!isRecord(record)) {
    return { value: record, changed: false };
  }
  const token = readStored(record[property]);
  return token.changed
    ? { value: { ...record, [property]: token.value } as T, changed: true }
    : { value: record, changed: false };
}

interface StoredToken {
  value: unknown;
  changed: boolean;
}

function readStoredToken(
  value: unknown,
  valueSet: string,
  canonicalValues: readonly string[],
  aliases: Readonly<Record<string, string>> = {}
): StoredToken {
  if (value === undefined || value === null) {
    return { value, changed: false };
  }
  if (typeof value !== "string") {
    throw invalidStoredValue(valueSet, value);
  }
  const token = value.replaceAll("_", "").toLowerCase();
  const canonical = canonicalValues.find((candidate) => candidate.replaceAll("_", "").toLowerCase() === token)
    ?? aliases[token];
  if (!canonical) {
    throw invalidStoredValue(valueSet, value);
  }
  return { value: canonical, changed: canonical !== value };
}

function readStoredComponentStatus(value: unknown) {
  // Compatibility read for v3 cached component status; delete after supported v3 management snapshots are rewritten/retired.
  return readStoredToken(value, "component state", componentStatusValues);
}

function readStoredComponentProviderState(value: unknown) {
  // Compatibility read for v3 cached provider state; delete after supported v3 management snapshots are rewritten/retired.
  return readStoredToken(value, "component provider state", componentProviderStateValues);
}

function readStoredComponentProviderKind(value: unknown) {
  // Compatibility read for v3 cached provider kinds (capabilities and statuses); delete after supported v3 snapshots are rewritten/retired.
  return readStoredToken(value, "component provider kind", componentProviderKindValues);
}

function readStoredManagementRole(value: unknown) {
  // Compatibility read for v3 cached management roles; delete after supported v3 management snapshots are rewritten/retired.
  return readStoredToken(value, "management role", managementRoleValues);
}

function readStoredSoftwareKind(value: unknown) {
  // Compatibility read for v3 cached software kind; delete after supported v3 management snapshots are rewritten/retired.
  return readStoredToken(value, "software kind", softwareKindValues);
}

function readStoredSoftwareDisplayKind(value: unknown) {
  // Compatibility read for v3 cached software display kind; delete after supported v3 management snapshots are rewritten/retired.
  return readStoredToken(value, "software display kind", softwareDisplayKindValues, {
    general: "other",
    windowsapp: "runtime_package"
  });
}

function invalidStoredValue(valueSet: string, value: unknown) {
  const error = new Error(`InvalidDataException: unsupported stored ${valueSet} '${String(value)}'.`);
  error.name = "InvalidDataException";
  return error;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
