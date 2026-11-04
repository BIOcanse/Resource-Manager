import type { ResourceGpuEngineScope, ResourceMetricFacts } from "../types.ts";
import {
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireNullable,
  requireRecord,
  requireSafeInteger,
  ResponseDecodeError
} from "../frontendRuntime/request/ResponseDecoder.ts";

/* Each wire shape has its own required keys; never fill missing required keys with defaults. */

export function requireTimestamp(value: unknown, path: string): string {
  const timestamp = requireNonEmptyString(value, path);
  if (!Number.isFinite(Date.parse(timestamp))) {
    throw new ResponseDecodeError(path, "valid timestamp string");
  }
  return timestamp;
}

function requireGpuEngine(value: unknown, path: string): ResourceGpuEngineScope {
  const scope = requireRecord(value, path);
  const index = (key: "physicalIndex" | "engineIndex") => {
    const number = requireSafeInteger(scope[key], `${path}.${key}`);
    if (number < 0) throw new ResponseDecodeError(`${path}.${key}`, "nonnegative index");
    return number;
  };
  return { physicalIndex: index("physicalIndex"), engineIndex: index("engineIndex") };
}

/** Required key with a nullable value. */
function written<T>(record: Record<string, unknown>, key: string, path: string, decode: (value: unknown, path: string) => T): T | null {
  if (!Object.hasOwn(record, key)) throw new ResponseDecodeError(`${path}.${key}`, "written key");
  return requireNullable(record[key], `${path}.${key}`, decode);
}

/** Omitted means null; a present value must be non-null. */
function writtenWhenPresent<T>(record: Record<string, unknown>, key: string, path: string, decode: (value: unknown, path: string) => T): T | null {
  return Object.hasOwn(record, key) ? decode(record[key], `${path}.${key}`) : null;
}

function requireNonnegativeSafeInteger(value: unknown, path: string): number {
  const number = requireSafeInteger(value, path);
  if (number < 0) throw new ResponseDecodeError(path, "nonnegative integer");
  return number;
}

function totals(record: Record<string, unknown>, path: string) {
  const additiveAcrossProcesses = requireBoolean(record.additiveAcrossProcesses, `${path}.additiveAcrossProcesses`);
  const residualValue = written(record, "residualValue", path, requireFiniteNumber);
  // Non-additive measures, such as working set, must have a null residual.
  if (!additiveAcrossProcesses && residualValue !== null)
    throw new ResponseDecodeError(`${path}.residualValue`, "null residual for a non-additive metric");
  return {
    totalValue: written(record, "totalValue", path, requireFiniteNumber),
    processSumValue: written(record, "processSumValue", path, requireFiniteNumber),
    residualValue,
    additiveAcrossProcesses,
    capturedAt: written(record, "capturedAt", path, requireTimestamp)
  };
}

/** Resource-facts frames include every key. */
export function decodeFactsMetricFacts(record: Record<string, unknown>, path: string): ResourceMetricFacts {
  return {
    ...totals(record, path),
    totalMetricId: written(record, "totalMetricId", path, requireNonEmptyString),
    windowMilliseconds: written(record, "windowMilliseconds", path, requireNonnegativeSafeInteger),
    quantity: written(record, "quantity", path, requireNonEmptyString),
    unavailableReason: written(record, "unavailableReason", path, requireNonEmptyString),
    gpuEngine: written(record, "gpuEngine", path, requireGpuEngine),
    engineClass: written(record, "engineClass", path, requireNonEmptyString)
  };
}

/** Resource-bar facts omit optional null values. */
export function decodeBarMetricFacts(record: Record<string, unknown>, path: string): ResourceMetricFacts & { processFieldName: string | null } {
  const gpuEngine = writtenWhenPresent(record, "gpuEngine", path, requireGpuEngine);
  return {
    ...totals(record, path),
    totalMetricId: writtenWhenPresent(record, "totalMetricId", path, requireNonEmptyString),
    processFieldName: writtenWhenPresent(record, "processFieldName", path, requireNonEmptyString),
    windowMilliseconds: writtenWhenPresent(record, "windowMilliseconds", path, requireNonnegativeSafeInteger),
    quantity: writtenWhenPresent(record, "quantity", path, requireNonEmptyString),
    unavailableReason: writtenWhenPresent(record, "unavailableReason", path, requireNonEmptyString),
    gpuEngine,
    engineClass: gpuEngine === null ? null : written(record, "engineClass", path, requireNonEmptyString)
  };
}

/** Process-detail facts include totals, sums, residuals, additivity, time and capacity. */
export function decodeDetailsMetricFacts(record: Record<string, unknown>, path: string) {
  return {
    ...totals(record, path),
    totalMetricId: written(record, "totalMetricId", path, requireNonEmptyString),
    capacityValue: written(record, "capacityValue", path, requireFiniteNumber)
  };
}
