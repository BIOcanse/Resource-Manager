import type { ManagedComponent, SoftwareRecord } from "../../types";

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

  try {
    const raw = localStorage.getItem(storageKey);
    if (!raw) {
      memoryCache = null;
      return null;
    }

    const parsed = JSON.parse(raw) as Partial<ManagementSnapshotCache>;
    if (parsed.version !== cacheVersion
      || !Array.isArray(parsed.components)
      || !Array.isArray(parsed.software)) {
      memoryCache = null;
      return null;
    }

    memoryCache = {
      version: cacheVersion,
      capturedAt: typeof parsed.capturedAt === "string" ? parsed.capturedAt : "",
      components: parsed.components.slice(0, maximumComponents),
      software: parsed.software
        .slice(0, maximumSoftwareRecords)
        .map(withoutIssueState)
    };
    lastPayload = serializePayload(memoryCache.components, memoryCache.software);
    return memoryCache;
  } catch {
    memoryCache = null;
    return null;
  }
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
