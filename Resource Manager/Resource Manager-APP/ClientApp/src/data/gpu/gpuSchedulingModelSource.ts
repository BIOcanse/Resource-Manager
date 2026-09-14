import type { RequestClient } from
  "../../frontendRuntime/request/RequestClient.ts";
import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireNonNegativeFiniteNumber,
  requireNonNegativeSafeInteger,
  requireOneOf,
  requireRecord,
  requireSafeInteger,
  requireString
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type {
  AppGpuPerformanceUseCase,
  GpuPerformanceScoreItem,
  GpuPerformanceScoreOverrideResult,
  GpuPerformanceScoreSnapshot,
  GpuSpecializedTelemetryAdapter,
  GpuSpecializedTelemetryCounter,
  GpuSpecializedTelemetrySnapshot
} from "../../types.ts";

export interface GpuSchedulingModelQuery {
  readonly gpuIndexes: readonly number[];
}

export interface GpuSchedulingModelSnapshot {
  readonly scoreOverrides: GpuPerformanceScoreOverrideResult;
  readonly scoreSnapshot: GpuPerformanceScoreSnapshot;
  readonly capturedAt: string | null;
}

export interface GpuSpecializedTelemetryQuery {
  readonly counterIds: readonly string[];
}

const lowIntrusionSpecializedCounterSuffixes = [
  "nvidia.rt.usage",
  "nvidia.cuda.usage",
  "amd.gcn.usage",
  "amd.rdna.usage",
  "amd.cdna.usage"
] as const;

export function normalizeGpuSchedulingModelQuery(
  query: GpuSchedulingModelQuery
): GpuSchedulingModelQuery {
  return Object.freeze({
    gpuIndexes: Object.freeze([...new Set(query.gpuIndexes
      .filter((index) => Number.isSafeInteger(index) && index >= 0))]
      .sort((left, right) => left - right))
  });
}

export function normalizeGpuSpecializedTelemetryQuery(
  query: GpuSpecializedTelemetryQuery
): GpuSpecializedTelemetryQuery {
  return Object.freeze({
    counterIds: Object.freeze([...new Set(query.counterIds
      .map((id) => id.trim())
      .filter(Boolean))].sort())
  });
}

export function buildGpuSpecializedCounterIds(
  gpuIndexes: readonly number[]
): string[] {
  return normalizeGpuSchedulingModelQuery({ gpuIndexes }).gpuIndexes
    .flatMap((index) => lowIntrusionSpecializedCounterSuffixes
      .map((suffix) => `gpu.${index}.${suffix}`));
}

export function buildGpuSpecializedTelemetrySubscriptionUrl(
  query: GpuSpecializedTelemetryQuery,
  intervalMs: number
): string {
  const normalized = normalizeGpuSpecializedTelemetryQuery(query);
  const params = new URLSearchParams();
  normalized.counterIds.forEach((id) => params.append("ids", id));
  params.set("intervalMs", String(Math.max(1, Math.round(intervalMs))));
  return `/api/metrics/gpu-specialized/subscribe?${params.toString()}`;
}

export async function getGpuSchedulingModelSource(
  requestClient: Pick<RequestClient, "execute">,
  query: GpuSchedulingModelQuery,
  signal?: AbortSignal
): Promise<GpuSchedulingModelSnapshot> {
  normalizeGpuSchedulingModelQuery(query);

  const fixedRequests = [
    requestClient.execute({
      key: "details.gpu.score-overrides",
      url: "/api/gpu/performance-overrides",
      fallbackError: "GPU 分数覆盖读取失败",
      decoder: gpuPerformanceScoreOverrideDecoder,
      signal,
      request: { method: "GET" }
    }),
    requestClient.execute({
      key: "details.gpu.performance-scores",
      url: "/api/gpu/performance-scores",
      fallbackError: "GPU 性能分读取失败",
      decoder: gpuPerformanceScoreSnapshotDecoder,
      signal,
      request: { method: "GET" }
    })
  ] as const;
  const [scoreOverrides, scoreSnapshot] = await Promise.all(fixedRequests);
  const timestamps = [
    scoreOverrides.value.updatedAt,
    scoreSnapshot.value.capturedAt
  ];
  return Object.freeze({
    scoreOverrides: scoreOverrides.value,
    scoreSnapshot: scoreSnapshot.value,
    capturedAt: latestTimestamp(timestamps)
  });
}

export const gpuPerformanceScoreOverrideDecoder =
  defineResponseDecoder<GpuPerformanceScoreOverrideResult>(
    "gpu.performance-score-overrides.v1",
    (value) => {
      const root = requireRecord(value);
      const rawScores = requireRecord(root.scoresByGpuId, "$.scoresByGpuId");
      const scoresByGpuId = Object.fromEntries(Object.entries(rawScores)
        .map(([id, score]) => [
          id,
          requireNonNegativeFiniteNumber(score, `$.scoresByGpuId.${id}`)
        ]));
      return Object.freeze({
        scoresByGpuId: Object.freeze(scoresByGpuId),
        updatedAt: requireTimestamp(root.updatedAt, "$.updatedAt"),
        storagePath: requireString(root.storagePath, "$.storagePath")
      });
    });

export const gpuPerformanceScoreSnapshotDecoder =
  defineResponseDecoder<GpuPerformanceScoreSnapshot>(
    "gpu.performance-scores.v1",
    (value) => {
      const root = requireRecord(value);
      return Object.freeze({
        capturedAt: requireTimestamp(root.capturedAt, "$.capturedAt"),
        gpus: requireArray(root.gpus, "$.gpus")
          .map(decodeGpuPerformanceScoreItem),
        storagePath: requireString(root.storagePath, "$.storagePath")
      });
    });

export const gpuSpecializedTelemetryDecoder =
  defineResponseDecoder<GpuSpecializedTelemetrySnapshot>(
    "gpu.specialized-telemetry.v2",
    (value) => {
      const root = requireRecord(value);
      return Object.freeze({
        version: requireVersion(root.version, "$.version", 2),
        capturedAt: requireTimestamp(root.capturedAt, "$.capturedAt"),
        adapters: requireArray(root.adapters, "$.adapters")
          .map(decodeSpecializedAdapter)
      });
    });

function requireVersion(value: unknown, path: string, expected: number): 2 {
  if (requireSafeInteger(value, path) !== expected) {
    throw new Error(`Invalid response version '${path}'.`);
  }
  return 2;
}

function decodeGpuPerformanceScoreItem(
  value: unknown,
  index: number
): GpuPerformanceScoreItem {
  const path = `$.gpus[${index}]`;
  const item = requireRecord(value, path);
  const useCases = item.gpuPerformanceUseCases === undefined
    ? undefined
    : requireArray(item.gpuPerformanceUseCases, `${path}.gpuPerformanceUseCases`)
      .map((useCase, useCaseIndex) => requireOneOf(
        useCase,
        `${path}.gpuPerformanceUseCases[${useCaseIndex}]`,
        ["general", "ai", "gaming"] as const)) as AppGpuPerformanceUseCase[];
  return Object.freeze({
    gpuId: requireNonEmptyString(item.gpuId, `${path}.gpuId`),
    index: requireNonNegativeSafeInteger(item.index, `${path}.index`),
    name: requireString(item.name, `${path}.name`),
    rasterPerformanceScore: optionalNonNegativeNumber(
      item.rasterPerformanceScore,
      `${path}.rasterPerformanceScore`),
    generationBonusScore: optionalNonNegativeNumber(
      item.generationBonusScore,
      `${path}.generationBonusScore`),
    useCaseBonusScore: optionalNonNegativeNumber(
      item.useCaseBonusScore,
      `${path}.useCaseBonusScore`),
    gpuPerformanceUseCases: useCases,
    defaultPerformanceScore: requireNonNegativeFiniteNumber(
      item.defaultPerformanceScore,
      `${path}.defaultPerformanceScore`),
    performanceScore: requireNonNegativeFiniteNumber(
      item.performanceScore,
      `${path}.performanceScore`),
    hasPerformanceOverride: requireBoolean(
      item.hasPerformanceOverride,
      `${path}.hasPerformanceOverride`),
    isIntegrated: requireBoolean(item.isIntegrated, `${path}.isIntegrated`),
    source: requireString(item.source, `${path}.source`),
    matchedPreset: optionalNullableString(item.matchedPreset, `${path}.matchedPreset`)
  });
}

function decodeSpecializedAdapter(
  value: unknown,
  index: number
): GpuSpecializedTelemetryAdapter {
  const path = `$.adapters[${index}]`;
  const item = requireRecord(value, path);
  return Object.freeze({
    adapterIndex: requireNonNegativeSafeInteger(
      item.adapterIndex,
      `${path}.adapterIndex`),
    adapterName: requireString(item.adapterName, `${path}.adapterName`),
    vendorId: requireString(item.vendorId, `${path}.vendorId`),
    architecture: requireString(item.architecture, `${path}.architecture`),
    counters: requireArray(item.counters, `${path}.counters`)
      .map((counter, counterIndex) => decodeSpecializedCounter(
        counter,
        `${path}.counters[${counterIndex}]`))
  });
}

function decodeSpecializedCounter(
  value: unknown,
  path: string
): GpuSpecializedTelemetryCounter {
  const item = requireRecord(value, path);
  return Object.freeze({
    counterId: requireNonEmptyString(item.counterId, `${path}.counterId`),
    displayName: requireString(item.displayName, `${path}.displayName`),
    counterClass: requireSafeInteger(item.counterClass, `${path}.counterClass`),
    value: optionalNullableNumber(item.value, `${path}.value`),
    unit: requireString(item.unit, `${path}.unit`),
    providerId: requireString(item.providerId, `${path}.providerId`),
    isIntrusive: requireBoolean(item.isIntrusive, `${path}.isIntrusive`),
    engineName: optionalNullableString(item.engineName, `${path}.engineName`)
  });
}

function optionalNonNegativeNumber(value: unknown, path: string): number | undefined {
  return value === undefined
    ? undefined
    : requireNonNegativeFiniteNumber(value, path);
}

function optionalNullableNumber(
  value: unknown,
  path: string
): number | null | undefined {
  return value === undefined
    ? undefined
    : value === null
      ? null
      : requireFiniteNumber(value, path);
}

function optionalNullableString(
  value: unknown,
  path: string
): string | null | undefined {
  return value === undefined
    ? undefined
    : value === null
      ? null
      : requireString(value, path);
}

function optionalTimestamp(value: unknown, path: string): string | undefined {
  return value === undefined ? undefined : requireTimestamp(value, path);
}

function requireTimestamp(value: unknown, path: string): string {
  const text = requireNonEmptyString(value, path);
  if (!Number.isFinite(Date.parse(text))) {
    throw new Error(`Invalid response timestamp '${path}'.`);
  }
  return text;
}

function latestTimestamp(values: readonly (string | null | undefined)[]): string | null {
  let latest: string | null = null;
  let latestValue = Number.NEGATIVE_INFINITY;
  for (const value of values) {
    const timestamp = value ? Date.parse(value) : Number.NaN;
    if (Number.isFinite(timestamp) && timestamp > latestValue) {
      latest = value!;
      latestValue = timestamp;
    }
  }
  return latest;
}
