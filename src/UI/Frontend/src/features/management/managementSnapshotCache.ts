import type { ManagedComponent, SoftwareRecord } from "../../types.ts";
import { decodeManagedComponent } from "./componentDecoder.ts";
import { decodeSoftwareRecord } from "./softwareRecordDecoder.ts";

const storageKey = "resource-manager:management-snapshot:v4";
const cacheVersion = 4;
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
  if (memoryCache !== undefined) return memoryCache;
  let raw: string | null;
  try {
    raw = localStorage.getItem(storageKey);
  } catch {
    return memoryCache = null;
  }
  if (!raw) return memoryCache = null;
  let parsed: Partial<ManagementSnapshotCache>;
  try {
    parsed = JSON.parse(raw) as Partial<ManagementSnapshotCache>;
  } catch {
    return memoryCache = null;
  }
  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)
    || parsed.version !== cacheVersion || !Array.isArray(parsed.components)
    || !Array.isArray(parsed.software)) return memoryCache = null;

  const components = parsed.components.slice(0, maximumComponents)
    .map((item, index) => decodeManagedComponent(item, `$.components[${index}]`));
  const software = parsed.software.slice(0, maximumSoftwareRecords)
    // 问题标签是实时状态，缓存不保留；旧缓存里残留的标签在解码前丢弃，不参与校验。
    .map((item, index) => decodeSoftwareRecord(withoutCachedIssues(item), `$.software[${index}]`));
  memoryCache = {
    version: cacheVersion,
    capturedAt: typeof parsed.capturedAt === "string" ? parsed.capturedAt : "",
    components,
    software
  };
  lastPayload = JSON.stringify({ components, software });
  return memoryCache;
}

export function writeManagementSnapshotCache(components: ManagedComponent[], software: SoftwareRecord[]) {
  const boundedComponents = components.slice(0, maximumComponents);
  const boundedSoftware = software.slice(0, maximumSoftwareRecords).map(withoutIssueState);
  const payload = JSON.stringify({ components: boundedComponents, software: boundedSoftware });
  if (payload === lastPayload) return;
  const next: ManagementSnapshotCache = {
    version: cacheVersion,
    capturedAt: new Date().toISOString(),
    components: boundedComponents,
    software: boundedSoftware
  };
  memoryCache = next;
  lastPayload = payload;
  try {
    localStorage.setItem(storageKey, JSON.stringify(next));
  } catch {
    // Retain the bounded in-memory cache when browser storage is unavailable.
  }
}

function withoutCachedIssues(item: unknown): unknown {
  return typeof item === "object" && item !== null && !Array.isArray(item) ? { ...item, issues: undefined } : item;
}

function withoutIssueState(record: SoftwareRecord): SoftwareRecord {
  return { ...record, issues: undefined };
}
