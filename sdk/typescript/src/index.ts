export const SNAPSHOT_SCHEMA_VERSION = 11 as const;

export enum AdapterResourceTier {
  Vram = 0,
  PhysicalMemory = 1,
  VirtualMemory = 2,
}

export enum AdapterResourceKind {
  PrimaryData = 0,
  Cache = 1,
  Index = 2,
  ModelWeights = 3,
  MediaResource = 4,
  EditingDocumentState = 5,
  StagingBuffer = 6,
  TemporaryComputeMemory = 7,
  RuntimeOverhead = 8,
  RenderSurface = 9,
  Texture = 10,
  RenderBuffer = 11,
  ComputeBuffer = 12,
}

export enum AdapterResourceRecoveryKind {
  DiskCopy = 0,
  BuiltData = 1,
  LiveState = 2,
}

export enum AdapterResourceGranularity {
  FullyLoaded = 0,
  PartialUsable = 1,
  NotApplicable = 2,
}

export enum AdapterResourceActionRoute {
  ManagerDirect = 0,
  AdapterHandler = 1,
}

export enum AdapterSoftwareSurfaceState {
  ForegroundFocused = 0,
  ForegroundUnfocused = 1,
  BackgroundWindow = 2,
  TrayBackground = 3,
  PureBackground = 4,
}

export enum AdapterResourceActionMask {
  None = 0,
  Discard = 1 << 0,
  Trim = 1 << 1,
  MoveDown = 1 << 2,
  MoveUp = 1 << 3,
}

export enum AdapterResourceDemandMask {
  None = 0,
  RequiredNow = 1 << 0,
  ReadySoon = 1 << 1,
  PreloadEager = 1 << 2,
  PreloadOpportunistic = 1 << 3,
}

export const ALL_ACTIONS =
  AdapterResourceActionMask.Discard |
  AdapterResourceActionMask.Trim |
  AdapterResourceActionMask.MoveDown |
  AdapterResourceActionMask.MoveUp;

export const ALL_FRONTEND_DEMAND =
  AdapterResourceDemandMask.RequiredNow |
  AdapterResourceDemandMask.ReadySoon |
  AdapterResourceDemandMask.PreloadEager |
  AdapterResourceDemandMask.PreloadOpportunistic;

const MAX_RESOURCE_KIND = AdapterResourceKind.ComputeBuffer;

export const MAX_ACTION_ROUTE = AdapterResourceActionRoute.AdapterHandler;

export enum AdapterResourceActionStatus {
  Completed = 0,
  ResourceNotFound = 1,
  ActionNotSupported = 2,
  InvalidRequest = 3,
  ResourceBusy = 4,
  Failed = 5,
}

export interface AdapterResourceActionRequest {
  requestId: bigint;
  resourceKey: bigint;
  action: AdapterResourceActionMask;
  flags: number;
}

export interface AdapterResourceActionResult {
  requestId: bigint;
  resourceKey: bigint;
  action: AdapterResourceActionMask;
  status: AdapterResourceActionStatus;
  previousTier: AdapterResourceTier;
  currentTier: AdapterResourceTier;
  releasedBytes: bigint;
  residentBytes: bigint;
  detailCode: number;
}

export type AdapterResourceActionHandler = (request: AdapterResourceActionRequest) => AdapterResourceActionResult;

export function completedActionResult(
  request: AdapterResourceActionRequest,
  previousTier: AdapterResourceTier,
  currentTier: AdapterResourceTier,
  releasedBytes: bigint,
  residentBytes: bigint,
  detailCode = 0,
): AdapterResourceActionResult {
  return {
    requestId: request.requestId,
    resourceKey: request.resourceKey,
    action: request.action,
    status: AdapterResourceActionStatus.Completed,
    previousTier,
    currentTier,
    releasedBytes,
    residentBytes,
    detailCode,
  };
}

export function actionErrorResult(
  request: AdapterResourceActionRequest,
  status: AdapterResourceActionStatus,
  detailCode = 0,
): AdapterResourceActionResult {
  return {
    requestId: request.requestId,
    resourceKey: request.resourceKey,
    action: request.action,
    status,
    previousTier: AdapterResourceTier.Vram,
    currentTier: AdapterResourceTier.Vram,
    releasedBytes: 0n,
    residentBytes: 0n,
    detailCode,
  };
}

interface AdapterResourceActionRegistration {
  supportedActions: AdapterResourceActionMask;
  handler: AdapterResourceActionHandler;
}

export class AdapterResourceActionDispatcher {
  private readonly registrations = new Map<bigint, AdapterResourceActionRegistration>();

  register(resourceKey: bigint, supportedActions: AdapterResourceActionMask, handler: AdapterResourceActionHandler): void {
    if (resourceKey === 0n) {
      throw new Error("resourceKey must be nonzero.");
    }
    if (supportedActions === AdapterResourceActionMask.None || (supportedActions & ~ALL_ACTIONS) !== 0) {
      throw new Error("supportedActions contains unknown bits.");
    }
    this.registrations.set(resourceKey, { supportedActions, handler });
  }

  unregister(resourceKey: bigint): boolean {
    return this.registrations.delete(resourceKey);
  }

  execute(request: AdapterResourceActionRequest): AdapterResourceActionResult {
    if (request.resourceKey === 0n || !isSingleKnownAction(request.action)) {
      return actionErrorResult(request, AdapterResourceActionStatus.InvalidRequest);
    }
    const registration = this.registrations.get(request.resourceKey);
    if (!registration) {
      return actionErrorResult(request, AdapterResourceActionStatus.ResourceNotFound);
    }
    if ((registration.supportedActions & request.action) === 0) {
      return actionErrorResult(request, AdapterResourceActionStatus.ActionNotSupported);
    }
    return registration.handler(request);
  }
}

export interface TieredResourceEntry {
  resourceKey: bigint;
  resourceId: number;
  sizeBytes: bigint;
  tier: AdapterResourceTier;
  resourceKind: AdapterResourceKind;
  recoveryKind: AdapterResourceRecoveryKind;
  granularity: AdapterResourceGranularity;
  inapplicableActions: AdapterResourceActionMask;
  actionRoute: AdapterResourceActionRoute;
  activityScore: number;
  frontendDemandMask: AdapterResourceDemandMask;
}

export interface AdapterResourceSnapshot {
  schemaVersion: typeof SNAPSHOT_SCHEMA_VERSION;
  applicationId: string;
  applicationName: string;
  processId: number;
  surfaceState: AdapterSoftwareSurfaceState;
  sequence: bigint;
  capturedAt: string;
  resources: readonly TieredResourceEntry[];
}

export type U64DecimalString = string;

export interface WireTieredResourceEntry extends Omit<TieredResourceEntry, "resourceKey" | "sizeBytes"> {
  resourceKey: U64DecimalString;
  sizeBytes: U64DecimalString;
}

export interface WireAdapterResourceSnapshot extends Omit<AdapterResourceSnapshot, "sequence" | "resources"> {
  sequence: U64DecimalString;
  resources: readonly WireTieredResourceEntry[];
}

export interface WireAdapterResourceActionRequest extends Omit<AdapterResourceActionRequest, "requestId" | "resourceKey"> {
  requestId: U64DecimalString;
  resourceKey: U64DecimalString;
}

export interface WireAdapterResourceActionResult extends Omit<AdapterResourceActionResult, "requestId" | "resourceKey" | "releasedBytes" | "residentBytes"> {
  requestId: U64DecimalString;
  resourceKey: U64DecimalString;
  releasedBytes: U64DecimalString;
  residentBytes: U64DecimalString;
}

const U64_MAX = (1n << 64n) - 1n;

export function encodeU64(value: bigint): U64DecimalString {
  if (value < 0n || value > U64_MAX) {
    throw new RangeError("u64 value is outside 0..18446744073709551615.");
  }
  return value.toString(10);
}

export function decodeU64(value: U64DecimalString): bigint {
  if (!/^(0|[1-9][0-9]*)$/.test(value)) {
    throw new TypeError("u64 wire value must be a canonical unsigned decimal string.");
  }
  const parsed = BigInt(value);
  if (parsed > U64_MAX) {
    throw new RangeError("u64 wire value exceeds 18446744073709551615.");
  }
  return parsed;
}

export function toWireSnapshot(snapshot: AdapterResourceSnapshot): WireAdapterResourceSnapshot {
  return {
    ...snapshot,
    sequence: encodeU64(snapshot.sequence),
    resources: snapshot.resources.map((resource) => ({
      ...resource,
      resourceKey: encodeU64(resource.resourceKey),
      sizeBytes: encodeU64(resource.sizeBytes),
    })),
  };
}

export function fromWireSnapshot(snapshot: WireAdapterResourceSnapshot): AdapterResourceSnapshot {
  if (snapshot.schemaVersion !== SNAPSHOT_SCHEMA_VERSION) {
    throw new Error(`Unsupported adapter snapshot schema ${snapshot.schemaVersion}.`);
  }
  return {
    ...snapshot,
    sequence: decodeU64(snapshot.sequence),
    resources: snapshot.resources.map((resource) => ({
      ...resource,
      resourceKey: decodeU64(resource.resourceKey),
      sizeBytes: decodeU64(resource.sizeBytes),
    })),
  };
}

export function toWireActionRequest(request: AdapterResourceActionRequest): WireAdapterResourceActionRequest {
  return {
    ...request,
    requestId: encodeU64(request.requestId),
    resourceKey: encodeU64(request.resourceKey),
  };
}

export function fromWireActionRequest(request: WireAdapterResourceActionRequest): AdapterResourceActionRequest {
  return {
    ...request,
    requestId: decodeU64(request.requestId),
    resourceKey: decodeU64(request.resourceKey),
  };
}

export function toWireActionResult(result: AdapterResourceActionResult): WireAdapterResourceActionResult {
  return {
    ...result,
    requestId: encodeU64(result.requestId),
    resourceKey: encodeU64(result.resourceKey),
    releasedBytes: encodeU64(result.releasedBytes),
    residentBytes: encodeU64(result.residentBytes),
  };
}

export function fromWireActionResult(result: WireAdapterResourceActionResult): AdapterResourceActionResult {
  return {
    ...result,
    requestId: decodeU64(result.requestId),
    resourceKey: decodeU64(result.resourceKey),
    releasedBytes: decodeU64(result.releasedBytes),
    residentBytes: decodeU64(result.residentBytes),
  };
}

export function adapterResourceKey(stableId: string): bigint {
  const trimmed = stableId.trim();
  if (!trimmed) {
    throw new Error("Stable id is required.");
  }

  let hash = 14695981039346656037n;
  for (const byte of new TextEncoder().encode(trimmed)) {
    hash ^= BigInt(byte);
    hash = BigInt.asUintN(64, hash * 1099511628211n);
  }

  return hash === 0n ? 14695981039346656037n : hash;
}

export function validateTieredResourceEntry(entry: TieredResourceEntry): void {
  if (entry.resourceKey === 0n) {
    throw new Error("Adapter resource entry requires a nonzero resourceKey.");
  }
  if (!Number.isInteger(entry.resourceId) || entry.resourceId <= 0 || entry.resourceId > 0xffffffff) {
    throw new Error("Adapter resource entry requires a nonzero uint32 resourceId.");
  }
  if (!Number.isInteger(entry.resourceKind) || entry.resourceKind < 0 || entry.resourceKind > MAX_RESOURCE_KIND) {
    throw new Error("resourceKind is outside the known byte enum range.");
  }
  if ((entry.inapplicableActions & ~ALL_ACTIONS) !== 0) {
    throw new Error("inapplicableActions contains unknown bits.");
  }
  if (entry.actionRoute === undefined || entry.actionRoute === null) {
    throw new Error("actionRoute is required.");
  }
  if (!Number.isInteger(entry.actionRoute) || entry.actionRoute < 0 || entry.actionRoute > MAX_ACTION_ROUTE) {
    throw new Error("actionRoute is outside the known byte enum range.");
  }
  if (!Number.isInteger(entry.activityScore) || entry.activityScore < 0 || entry.activityScore > 255) {
    throw new Error("activityScore must be a uint8 value.");
  }
  if ((entry.frontendDemandMask & ~ALL_FRONTEND_DEMAND) !== 0) {
    throw new Error("frontendDemandMask contains unknown bits.");
  }
}

function isSingleKnownAction(action: AdapterResourceActionMask): boolean {
  const value = action as number;
  return value !== 0 && (value & (value - 1)) === 0 && (value & ~ALL_ACTIONS) === 0;
}
