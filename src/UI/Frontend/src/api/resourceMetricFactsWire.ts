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

/*
 * 每个指标自己的整机事实（metric-source round 4，后端 ResourceMetricFacts）在三种 wire 上的写法不同，
 * 解码按各自 wire 的写法逐键要求，不补默认值：
 * - 资源事实帧（/api/resource-facts，System.Text.Json）：全部键都写，空值写 null。
 * - 资源条（ResourceBreakdownWireSnapshotJsonConverter.WriteBars）：processSumValue、residualValue、
 *   additiveAcrossProcesses、capturedAt 总写；quantity、unavailableReason、totalMetricId、processFieldName、
 *   windowMilliseconds、gpuEngine 只在有值时写，engineClass 跟 gpuEngine 一起写（可为 null）。
 * - 进程明细（ResourceProcessDetailsWireSnapshotJsonConverter）：totalMetricId、totalValue、capacityValue、
 *   processSumValue、residualValue、additiveAcrossProcesses、capturedAt 总写，不写其余几项。
 */

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

/** 必写键：缺了就是 wire 变了；值可以为 null。 */
function written<T>(record: Record<string, unknown>, key: string, path: string, decode: (value: unknown, path: string) => T): T | null {
  if (!Object.hasOwn(record, key)) throw new ResponseDecodeError(`${path}.${key}`, "written key");
  return requireNullable(record[key], `${path}.${key}`, decode);
}

/** 只在有值时写的键：没写就是 null，写了就不能是 null。 */
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
  // 后端只给可加口径算残差；不可加的口径（工作集）残差必须是 null。
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

/** 资源事实帧上的指标事实：全部键都写。 */
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

/** 资源条上的指标事实：可空的几项只在有值时写。 */
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

/** 进程明细上的指标事实：总量、之和、残差、可加性、时刻和比较基数。 */
export function decodeDetailsMetricFacts(record: Record<string, unknown>, path: string) {
  return {
    ...totals(record, path),
    totalMetricId: written(record, "totalMetricId", path, requireNonEmptyString),
    capacityValue: written(record, "capacityValue", path, requireFiniteNumber)
  };
}
