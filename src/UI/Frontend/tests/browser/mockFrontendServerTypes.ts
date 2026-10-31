import type { IncomingMessage, ServerResponse } from "node:http";
import type {
  BackendMessage, DeviceTopologySnapshot, DeviceTopologySnapshotState, DeviceTopologyPort,
  DeviceTopologyInformationSnapshot, DeviceTopologyTransport, DeviceTopologyDisplayConnection,
  DeviceTopologyStorageDevice, DeviceTopologyStoragePartition, DeviceTopologyStorageVolume,
  ResourceGpuEngineScope, OperationSnapshot, MetricValue, MetricSourceCandidate
} from "../../src/types.ts";
import type { CrashReport, CrashReportList } from "../../src/data/crashReports/crashReports.ts";
import type { ShowcaseSoftwareCatalogRow } from "./readmeShowcaseFixture.ts";

/** These DTOs describe the mock's wire data, before the frontend decoder projects it. */
export type Mutable<T> = { -readonly [K in keyof T]: T[K] };
export type MockCrashReport = Mutable<CrashReport>;
export type MockCrashReportList = Omit<Mutable<CrashReportList>, "reports"> & { reports: MockCrashReport[] };
export type GeneratedData = ReturnType<typeof import("../realData/generateScenario.ts").generateScenario>["generatedData"] & { frameIndex?: number };
type GeneratedDevice = ReturnType<typeof import("../realData/generatedMockData.ts").generatedFieldDevices>[number];
export type MetricFieldDevice = Omit<GeneratedDevice, "fields"> & { fields: (GeneratedDevice["fields"][number] & {
  reading?: MetricSourceCandidate["reading"];
})[] };
export interface WireMetricDefinition {
  id: string; unit: string; selectable?: boolean; candidateSources?: readonly MetricSourceCandidate[];
  hasDedicatedMemoryMetrics: boolean | null; fieldName?: string | null; deviceIdentity?: string | null;
}
export type WireMetricValue = MetricValue & { status: number };
export type InformationFields = Omit<DeviceTopologyInformationSnapshot, "devices"> & {
  devices: { deviceIdentity: string; objectType: string; fields: {
    definition: { name: string; resource?: string; vendor?: string; valueKind?: string; unit?: string;
      quantity?: string; sourceId?: string; volatility?: { kind: number; event?: string } };
    value: unknown;
  }[] }[];
};
export type TopologyState = Omit<DeviceTopologySnapshotState, "snapshot" | "informationFields"> & {
  snapshot: DeviceTopologySnapshot; informationFields: InformationFields;
};
/** The original structure fixture retains the legacy, explicitly empty sampled slot. */
type TopologyStorageVolume = DeviceTopologyStorageVolume & { freeBytes?: null };
type TopologyStoragePartition = Omit<DeviceTopologyStoragePartition, "volumes"> & { volumes: TopologyStorageVolume[] };
type TopologyStorageDevice = Omit<DeviceTopologyStorageDevice, "partitions"> & { partitions: TopologyStoragePartition[] };
export type TopologyPort = Omit<DeviceTopologyPort, "storage"> & { storage?: TopologyStorageDevice | null };
export type DisplayConnection = DeviceTopologyDisplayConnection;
export type Transport = DeviceTopologyTransport;
export type SoftwareIdentity = ShowcaseSoftwareCatalogRow;
export type SoftwareAmount = [...SoftwareIdentity, amount: number];
export type CounterLeaf = [kind: string, bytes: number, name: string];
export type ProcessRow = [processId: number, name: string, path: string | null, value: number,
  systemPercent: number, softwarePercent: number, user: string | null, architecture: string | null,
  attributionKind: string, baseScore: number, startKey: string | null];
export type UnsharedSoftwareRow = [catalogIndex: number, value: number, systemPercent: number,
  processCount: number, baseScore: number, processes: ProcessRow[]];
export type SoftwareRow = [...UnsharedSoftwareRow, sharedValue?: number | null];
export interface DetailSelection { size: number; has(id: string): boolean; }
export type WireGpuEngine = ResourceGpuEngineScope & { adapterLuid?: number };
export interface BarFactInput {
  processSumValue: number | null; residualValue: number | null; additiveAcrossProcesses: boolean | null | undefined;
  capturedAt: string | null; quantity?: string | null; windowMilliseconds?: number | null;
  gpuEngine?: WireGpuEngine | null; engineClass?: string | null;
}
export interface ResourceBar extends Partial<BarFactInput> {
  metricId: string; unit: string; scaleMode: string; totalValue: number | null; capacityValue: number | null;
  totalSystemPercent: number | null; software: SoftwareRow[]; totalMetricId?: string | null;
  processFieldName?: string | null; sharedValue?: number | null; totalDisplay?: string;
  residualSource?: string | null; unavailableReason?: string | null;
}
export interface ResourceBreakdown {
  version: number; capturedAt: string | null; softwareCatalog: SoftwareIdentity[]; bars: ResourceBar[];
}
export interface ResourceOptions { keepMissingTotals?: boolean; ids?: string[]; }
export interface ProcessFact {
  totalMetricId: string; totalValue: number; capacityValue: number; processSum: number; unit?: string;
  windowMilliseconds?: number | null; additive?: boolean | null; quantity?: string | null;
  gpuEngine?: WireGpuEngine | null; engineClass?: string | null;
}
export interface CatalogFieldOptions {
  reading?: { resource: string; quantity: string } | null; total?: string | null; additive?: boolean | null;
  gpuEngine?: WireGpuEngine | null; engineClass?: string | null; sourceFieldName?: string | null;
}
export interface FactSoftwareIdentity { softwareId: string; name: string; kind: string; attributionKind: string; }
export interface FactProcessIdentity {
  processId: number; processStartKey: string | null; name: string; executablePath: string | null;
  userName: string | null; architecture: string | null; attributionKind: string;
}
export interface Attribution { softwareId: string; processIndex: number; baseScore: number; }
export interface FactProcessRow {
  processIndex: number; value: number; systemPercent: number; softwarePercent: number; baseScore: number; sharedValue: number | null;
}
export interface FactSoftwareRow {
  softwareId: string; value: number; systemPercent: number; processCount: number; baseScore: number;
  sharedValue: number | null; processes: FactProcessRow[];
}
export interface BalanceInput {
  metricId: string; unit: string; totalMetricId: string; processFieldName: string | null; totalValue: number;
  capacityValue: number; processSum: number; additive?: boolean | null; quantity?: string | null;
  windowMilliseconds?: number | null; gpuEngine?: WireGpuEngine | null; engineClass?: string | null;
  sharedValue?: number | null; residualRows?: FactSoftwareRow[] | null; counterLeaves?: CounterLeaf[] | null;
}
export interface DiskTile {
  nodeId: number; parentNodeId: number; depth: number; sizeBytes: number; name: string; isDirectory: boolean;
  fileCount: number; x: number; y: number; width: number; height: number;
  childSizeTotal?: number; firstChildId?: number; nextSiblingId?: number;
}
export interface Telemetry {
  physicalStarted: number; physicalCompleted: number; physicalAborted: number; physicalInFlight: number; physicalMaxInFlight: number;
  logicalStarted: number; logicalRetired: number; logicalInFlight: number; logicalMaxInFlight: number;
  started: number; completed: number; aborted: number; inFlight: number; maxInFlight: number;
  startTimes: number[]; queries: string[]; statuses: number[]; inFlightByQuery: Record<string, number>;
  maxInFlightByQuery: Record<string, number>; publications: number; publicationTimes: number[];
  publicationsByQuery: Record<string, number>; publicationTimesByQuery: Record<string, number[]>;
  subscriptionSets: unknown[][];
}
export interface EventSubscriber { response: ServerResponse; requestUrl: URL; write(value: unknown): void; }
export interface PayloadFactory { (publishedAt: string): unknown; pushOnChange?: boolean; }
export interface PeriodicSubscriber {
  response: ServerResponse; requestUrl: URL; intervalMs: number; createPayload: PayloadFactory;
  detailsReadyAt: number; lastValue: unknown; publish(publishedAt: string, options?: { force?: boolean }): boolean;
}
export interface Publisher {
  path: string; subscribers: Set<PeriodicSubscriber>; intervalMs: number;
  timer: ReturnType<typeof setTimeout> | null; generatedFrame?: number;
}
export interface SubscriptionRequest { version?: unknown; subscriptions?: { id?: unknown; path?: unknown; fields?: unknown }[]; }
export interface LogicalSubscription {
  id: string; requestUrl: URL; operation: boolean; managementInventoryRevision: boolean; productUpdateStatus: boolean;
  kernelLeakReport: boolean; memoryProtectionStatus: boolean; backendNotifications: boolean; crashReports: boolean;
  createPayload: PayloadFactory | null;
}
export interface RecordingItem {
  kind: string; id: string; source: string; intervalMs: number | null; syncPrecision: string;
}
export interface RecordingItemInput { kind: string; id: string; source?: string; intervalMs?: number | null; syncPrecision?: string; }
export interface RecordingProfile { id: string; name: string; defaultIntervalMs: number; items: RecordingItem[]; }
export interface RecordingEntry {
  header: {
    id: string; softwareId: string; softwareName: string; startedAt: string; endedAt: string | null;
    maximumDurationSeconds: number; status: string; stopReason: string | null; incomplete: boolean;
    profileId: string | null; profileName: string | null;
    items: { kind: string; id: string; unit: string; selectedSource: string; actualSources: string[];
      selectedIntervalMs: number | null; intervalOrigin: string; actualIntervalMs: number | null;
      syncPrecision: string; pointCount: number; gaps: { fromMs: number; toMs: number; reason: string }[] }[];
    processes: { processId: number; processStartKey: number; name: string; firstSeenAt: string; exitedAt: string | null }[];
    storedBytes: number; maximumReportKib: number;
  };
  selectedItems?: RecordingItem[]; defaultIntervalMs: number; frameIntervals: unknown[]; fpsDistribution: unknown[]; resourceSamples: unknown[];
}
export interface ProductUpdateStatus {
  stage: string; targetVersion: string | null; workspace: string | null; error: string | null; updatedAt: string | null;
  errorCode: string | null; errorParameters: Record<string, string> | null; lastCheckedAt: string | null;
  checkResult: string | null; availableVersion: string | null; nextCheckAt: string | null;
  onlineTimeSinceLastCheckSeconds: number | null; automaticCheckStatus: string; failedVersion: string | null;
  checkedInstalledVersion: string | null; incompatibleVersions: string[];
}
export interface StartupModuleSelection { softwareId: string; moduleIds: string[]; }
export interface MockOperationValue {
  schema: string; capturedAt: string; publicationRevision: string; configurationGeneration: string;
  ready: boolean; persistenceFaulted: boolean; faultStage: string | null; operations: OperationSnapshot[];
}
export type HttpRequest = IncomingMessage;
export type HttpResponse = ServerResponse;
export type WireMessage = BackendMessage;
